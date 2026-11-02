using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private string? _personnelAccount;
    public Func<bool>? PersonnelActionsAllowed { get; set; }
    private void CheckPersonnelAccess(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); EnsureLoggedIn();
        if (ActionsAllowed?.Invoke() == false || PersonnelActionsAllowed?.Invoke() == false)
            throw new InvalidOperationException("Personalprüfung pausiert; keine weitere Personalaktion gesendet.");
        if (_personnelAccount != null && _config.Session.UserId != _personnelAccount)
            throw new InvalidOperationException("Spielkonto während der Personalprüfung gewechselt.");
    }
    private bool PersonnelVehicleHome(Vehicle v) => v.State == 2 && string.IsNullOrEmpty(v.TargetType) &&
        v.TargetId.GetValueOrDefault() <= 0 && v.QueuedMissionId.GetValueOrDefault() <= 0 &&
        _pendingVehicles.GetValueOrDefault(v.Id) <= UtcNow && _pendingReleases.GetValueOrDefault(v.Id) <= UtcNow &&
        _pendingTransports.GetValueOrDefault(v.Id) <= UtcNow && !PlannedReservations(0).Contains(v.Id) &&
        !OwnLoans.Any(loan => loan.VehicleId == v.Id || loan.ReservedVehicleIds?.Contains(v.Id) == true);

    private static string PersonnelLayout(int total, IEnumerable<PersonnelVehicleRule> rules, IReadOnlyList<PersonnelEquipment> equipment,
        IReadOnlyList<StationPerson> people) =>
        "balanced-v2:" + total.ToString(CultureInfo.InvariantCulture) + ":" +
        string.Join(";", rules.OrderBy(r => r.Vehicle.Id).Select(r => $"{r.Vehicle.Id}/{r.Vehicle.TypeId}/{r.Minimum}/{r.Maximum}/" +
            string.Join(",", r.Training.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Minimum}/{t.Everyone}")))) + ":" +
        string.Join(";", equipment.OrderBy(e => e.Id).Select(e => $"{e.Id}/{e.VehicleId}/{Uri.EscapeDataString(e.Name)}")) + ":" +
        string.Join(";", people.OrderBy(p => p.Id).Select(p => $"{p.Id}/{p.Available}/" + string.Join(",", p.Training.OrderBy(t => t))));
    private string PersonnelStationKey(long id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        _httpClient.BaseAddress!.Host + ":" + _config.Session.UserId))) + ":" + id.ToString(CultureInfo.InvariantCulture);

    public async Task<PersonnelRunReport> OptimizePersonnelAsync(bool onlyChanged = false, IProgress<PersonnelProgress>? progress = null,
        CancellationToken token = default)
    {
        var report = new PersonnelRunReport();
        await _dispatchGate.WaitAsync(token);
        try
        {
            CheckPersonnelAccess(token);
            if (string.IsNullOrWhiteSpace(_config.Session.UserId))
            {
                _config.Session.UserId = (await GetUserInfoAsync(token))!.user_id.ToString(CultureInfo.InvariantCulture);
                await _config.SaveSessionAsync(_config.Session);
            }
            var account = _config.Session.UserId;
            _personnelAccount = account;
            var buildings = await GetBuildingsAsync(token, refresh: true);
            var fleet = await GetVehiclesAsync(true, token);
            var stations = buildings.OfType<JObject>().Where(b => fleet.Any(v => v.BuildingId == b.Value<long?>("id")))
                .OrderBy(b => b.Value<long>("id")).ToArray();
            _config.Config.PersonnelLayouts ??= new(); _config.Config.PersonnelBackups ??= new();
            for (var index = 0; index < stations.Length; index++)
            {
                CheckPersonnelAccess(token);
                if (_config.Session.UserId != account) throw new InvalidOperationException("Spielkonto während der Personalprüfung gewechselt.");
                var building = stations[index]; var id = building.Value<long>("id");
                var name = building.Value<string>("caption") ?? $"Wache #{id}";
                progress?.Report(new($"Wache {index + 1}/{stations.Length}: {name} prüfen …"));
                var vehicles = fleet.Where(v => v.BuildingId == id).ToArray();
                void Add(string state, string message, IReadOnlyList<PersonnelVehicleResult>? results = null, string warnings = "")
                {
                    var item = new PersonnelStationReport(id, name, state, message, results ?? [], warnings);
                    report.Stations.Add(item); progress?.Report(new(message, item));
                    Logger?.Write(state == "Fehler" ? LogLevel.Warning : LogLevel.Info, "Personnel", "station", $"{name}: {message}");
                }
                if (building.Value<int?>("personal_count") is not { } total || total < 0)
                { Add("Übersprungen", "Personal-Gesamtzahl fehlt; Wache unverändert."); continue; }
                var rules = vehicles.Select(v => PersonnelAllocator.ReadRule(v, _vehicleTypes, _rules)).ToArray();
                if (rules.Any(r => r == null) || rules.Any(r => r!.Maximum == 0 && r.Training.Count > 0))
                { Add("Übersprungen", "Fahrzeug oder Anhänger mit nicht vollständig unterstütztem Ausbildungsbedarf; Wache unverändert."); continue; }
                var crewed = rules.Where(r => r!.Maximum > 0).Cast<PersonnelVehicleRule>().ToArray();
                if (crewed.Length == 0) { Add("Unverändert", "Keine Fahrzeuge mit eigener Besatzung."); continue; }
                var key = PersonnelStationKey(id);
                fleet = await GetVehiclesAsync(true, token);
                var current = fleet.Where(v => v.BuildingId == id).ToArray();
                if (!SameStation(vehicles, current) || current.Any(v => !PersonnelVehicleHome(v)))
                { Add("Übersprungen", "Fahrzeuge sind unterwegs, reserviert oder der Bestand hat sich geändert. Erst alle Fahrzeuge zur Wache zurückkehren lassen."); continue; }
                var wrote = false;
                try
                {
                    var equipment = await ReadPersonnelEquipment(id, vehicles, token);
                    var first = crewed[0].Vehicle.Id;
                    var seed = await ReadPersonnelPage(first, id, token);
                    if (seed.People.Count != total) throw new FormatException($"Personalliste unvollständig ({seed.People.Count}/{total}).");
                    if (seed.People.Any(p => p.VehicleId.HasValue && !vehicles.Any(v => v.Id == p.VehicleId)))
                        throw new FormatException("Personal ist an ein Fahrzeug außerhalb der Wache gebunden.");
                    var layout = PersonnelLayout(total, rules.Cast<PersonnelVehicleRule>(), equipment, seed.People);
                    if (onlyChanged && _config.Config.PersonnelLayouts.GetValueOrDefault(key) == layout)
                    { Add("Unverändert", "Personal, Ausbildungen, Fahrzeuge, Beladung und Besatzungsregeln unverändert."); continue; }
                    async Task CheckEquipment()
                    {
                        if (!equipment.SequenceEqual(await ReadPersonnelEquipment(id, vehicles, token)))
                            throw new InvalidOperationException("Beladung wurde während der Personalprüfung geändert. Bitte erneut prüfen.");
                    }
                    var equipmentPlan = PersonnelEquipmentPlanner.Build(crewed, seed.People, equipment);
                    var plan = equipmentPlan.Plan;
                    if (!plan.IsValid) { Add("Übersprungen", string.Join(" ", plan.Problems) + " Wache unverändert."); continue; }
                    string WithWarnings(string message) => message + (equipmentPlan.Warnings.Count > 0 ? " " + string.Join(" ", equipmentPlan.Warnings) : "");
                    var expected = seed.People.ToDictionary(p => p.Id, p => p.VehicleId);
                    var edits = new Dictionary<long, PersonnelEditForm>();
                    foreach (var rule in crewed)
                    {
                        var edit = await ReadPersonnelEdit(rule.Vehicle.Id, token);
                        if (!edit.AllowedMaximums.Contains(plan.Maximums[rule.Vehicle.Id])) throw new FormatException("Geplante Besatzung wird vom Fahrzeugformular nicht angeboten.");
                        edits[rule.Vehicle.Id] = edit;
                    }
                    var results = crewed.Select(r => new PersonnelVehicleResult(r.Vehicle.Id, r.Vehicle.Name,
                        seed.People.Count(p => p.VehicleId == r.Vehicle.Id), plan.Assignments.Values.Count(v => v == r.Vehicle.Id), plan.Maximums[r.Vehicle.Id],
                        string.Join("; ", new[] { r.Training.Count == 0 ? "" : "Fahrzeugausbildung gesichert",
                            equipmentPlan.Details.GetValueOrDefault(r.Vehicle.Id) ?? "" }.Where(s => s.Length > 0)))).ToArray();
                    if (plan.Assignments.All(p => expected[p.Key] == p.Value) && edits.All(e => e.Value.CurrentMaximum == plan.Maximums[e.Key]))
                    {
                        await RememberPersonnelLayout(key, layout);
                        Add("Unverändert", WithWarnings("Personalbindungen und Besatzungsgrenzen passen bereits."), results, string.Join(" ", equipmentPlan.Warnings)); continue;
                    }
                    // Keep one local, account-scoped recovery snapshot before the first mutation.
                    _config.Config.PersonnelBackups[key] = new(UtcNow, new(expected), edits.ToDictionary(e => e.Key, e => e.Value.CurrentMaximum));
                    _config.Config.PersonnelLayouts.Remove(key);
                    await _config.SaveConfigAsync(_config.Config);
                    foreach (var rule in crewed)
                    {
                        var vehicleId = rule.Vehicle.Id; var edit = edits[vehicleId]; var maximum = plan.Maximums[vehicleId];
                        if (edit.CurrentMaximum == maximum) continue;
                        await _pacer.WaitPersonnelAsync(token); await CheckStationBeforePersonnelWrite(id, vehicles, token);
                        await CheckEquipment();
                        edit = await ReadPersonnelEdit(vehicleId, token);
                        if (edit.CurrentMaximum != edits[vehicleId].CurrentMaximum || !edit.AllowedMaximums.Contains(maximum))
                            throw new FormatException("Besatzungsgrenze wurde zwischenzeitlich geändert.");
                        CheckPersonnelAccess(token); wrote = true;
                        await PostPersonnel(edit.Path, edit.Token, [new("_method", "patch"), new("authenticity_token", edit.Token),
                            new("utf8", "✓"), new("vehicle[personal_max]", maximum.ToString(CultureInfo.InvariantCulture))], token);
                        var saved = await ReadPersonnelEdit(vehicleId, token);
                        if (saved.CurrentMaximum != maximum)
                            throw new FormatException("Neue Besatzungsgrenze wurde vom Spiel nicht bestätigt.");
                        edits[vehicleId] = saved;
                    }
                    var changes = plan.Assignments.Where(p => p.Value != expected[p.Key]).OrderBy(p => p.Value ?? expected[p.Key]).ToArray();
                    PersonnelPage? page = null;
                    foreach (var change in changes)
                    {
                        var target = change.Value ?? expected[change.Key] ?? throw new FormatException("Fahrzeugbindung nicht erkannt.");
                        await _pacer.WaitPersonnelAsync(token);
                        if (page?.VehicleId != target)
                        {
                            await CheckStationBeforePersonnelWrite(id, vehicles, token);
                            await CheckEquipment();
                            page = await ReadPersonnelPage(target, id, token);
                        }
                        await CheckPersonnelVehicles(id, vehicles, new long?[] { target, expected[change.Key] }, token);
                        ValidatePersonnelRoster(page, seed, expected);
                        if (!page.Actions.TryGetValue(change.Key, out var path)) throw new FormatException("Personalaktion wird vom Spiel nicht angeboten.");
                        CheckPersonnelAccess(token); wrote = true;
                        var after = new Dictionary<long, long?>(expected) { [change.Key] = change.Value };
                        try { await PostPersonnel(path, page.Token, [], token); }
                        catch (HttpRequestException ex) when (ex.StatusCode is { } status && (int)status >= 500)
                        {
                            // A server error can follow a committed toggle. Never send it twice.
                            var confirmation = await ReadPersonnelPage(target, id, token);
                            try { ValidatePersonnelRoster(confirmation, seed, after); }
                            catch (FormatException) { throw new HttpRequestException(ex.Message + " Fahrzeugbindung nicht bestätigt; keine automatische Wiederholung.", ex, status); }
                            Logger?.Write(LogLevel.Warning, "Personnel", "assignment.confirmed.after.error",
                                $"{name}: Personalbindung trotz HTTP {(int)status} durch erneutes Lesen bestätigt.");
                        }
                        page = await ReadPersonnelPage(target, id, token);
                        ValidatePersonnelRoster(page, seed, after);
                        expected = after;
                        progress?.Report(new($"{name}: {changes.Count(c => expected[c.Key] == c.Value)}/{changes.Length} Personalbindungen bestätigt."));
                    }
                    var finalPage = await ReadPersonnelPage(first, id, token);
                    ValidatePersonnelRoster(finalPage, seed, expected);
                    await CheckEquipment();
                    fleet = await GetVehiclesAsync(true, token);
                    foreach (var rule in crewed)
                    {
                        var vehicle = fleet.SingleOrDefault(v => v.Id == rule.Vehicle.Id && v.BuildingId == id);
                        var defaultMax = _vehicleTypes[rule.Vehicle.TypeId.ToString()].Value<int>("maxPersonnel");
                        if (vehicle == null || vehicle.Personnel != results.Single(r => r.VehicleId == vehicle.Id).After ||
                            (vehicle.MaximumPersonnel ?? defaultMax) != plan.Maximums[vehicle.Id])
                            throw new FormatException("Fahrzeugbestand bestätigt die Personalverteilung noch nicht vollständig. Bitte erneut prüfen.");
                    }
                    await RememberPersonnelLayout(key, layout); ClearStaffing();
                    foreach (var vehicle in vehicles) _observedCrew.TryRemove(vehicle.Id, out _);
                    Add("Gespeichert", WithWarnings("Personal fest zugewiesen, Besatzungsgrenzen gespeichert und vom Spiel bestätigt."), results, string.Join(" ", equipmentPlan.Warnings));
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException or HttpRequestException or IOException or OperationCanceledException)
                {
                    _config.Config.PersonnelLayouts.Remove(key);
                    Add("Fehler", (wrote ? "Änderungen möglicherweise teilweise übernommen. " : "Wache nicht geändert. ") + ex.Message);
                    if (wrote || ex is not FormatException) { report.Interrupted = true; break; }
                }
            }
            return report;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException or FormatException or IOException)
        { report.Interrupted = true; report.StopReason = ex.Message; return report; }
        finally { _trainingRosters.Clear(); _personnelAccount = null; _dispatchGate.Release(); }
    }

    private static bool SameStation(IEnumerable<Vehicle> before, IEnumerable<Vehicle> after) =>
        before.OrderBy(v => v.Id).Select(v => (v.Id, v.TypeId, v.BuildingId)).SequenceEqual(after.OrderBy(v => v.Id).Select(v => (v.Id, v.TypeId, v.BuildingId)));
    private async Task CheckStationBeforePersonnelWrite(long id, Vehicle[] initial, CancellationToken token)
    {
        CheckPersonnelAccess(token);
        var current = (await GetVehiclesAsync(true, token)).Where(v => v.BuildingId == id).ToArray();
        if (!SameStation(initial, current) || current.Any(v => !PersonnelVehicleHome(v)))
            throw new InvalidOperationException("Fahrzeugbestand geändert oder ein Fahrzeug ist nicht mehr zu Hause. Personalprüfung angehalten.");
    }
    private async Task CheckPersonnelVehicles(long buildingId, Vehicle[] initial, IEnumerable<long?> ids, CancellationToken token)
    {
        CheckPersonnelAccess(token);
        // A full account inventory can span hundreds of pages. Between individual
        // bindings only the affected source and destination need a fresh vehicle read.
        foreach (var id in ids.Where(id => id.HasValue).Select(id => id!.Value).Distinct())
        {
            var current = await GetVehicleAsync(id, token);
            if (current.BuildingId != buildingId || !initial.Any(v => v.Id == id && v.TypeId == current.TypeId) || !PersonnelVehicleHome(current))
                throw new InvalidOperationException("Betroffenes Fahrzeug ist nicht mehr auf seiner Heimatwache. Personalprüfung angehalten.");
        }
    }
    private async Task<PersonnelPage> ReadPersonnelPage(long vehicleId, long buildingId, CancellationToken token)
    {
        var page = PersonnelPageParser.Read(await GetTextAsync($"/vehicles/{vehicleId}/zuweisung", token), vehicleId, buildingId, _httpClient.BaseAddress!);
        // Rails form tokens can be scoped to /vehicles/:id PATCH. The assignment AJAX
        // endpoint instead uses the global meta token, just like the game's jQuery.
        if (string.IsNullOrWhiteSpace(page.Token)) throw new FormatException("Sicherheitstoken der Personalzuweisungsseite fehlt; keine Personalaktion gesendet.");
        Logger?.RegisterSecret(page.Token); return page;
    }
    private async Task<PersonnelEditForm> ReadPersonnelEdit(long vehicleId, CancellationToken token)
    {
        var form = PersonnelPageParser.ReadEdit(await GetTextAsync($"/vehicles/{vehicleId}/edit", token), vehicleId, _httpClient.BaseAddress!);
        Logger?.RegisterSecret(form.Token); return form;
    }
    private async Task<IReadOnlyList<PersonnelEquipment>> ReadPersonnelEquipment(long buildingId, Vehicle[] vehicles, CancellationToken token) =>
        PersonnelPageParser.ReadAssignedEquipment(await GetTextAsync($"/buildings/{buildingId}", token), buildingId,
            vehicles.Select(v => v.Id).ToArray(), _httpClient.BaseAddress!);
    private static void ValidatePersonnelRoster(PersonnelPage page, PersonnelPage seed, Dictionary<long, long?> expected)
    {
        if (page.People.Count != seed.People.Count || page.People.Any(person => !expected.TryGetValue(person.Id, out var binding) ||
            person.VehicleId != binding || !seed.People.Any(old => old.Id == person.Id && old.Available == person.Available && old.Training.SequenceEqual(person.Training))))
            throw new FormatException("Personalbestand oder Bindungen wurden zwischenzeitlich geändert / nicht bestätigt.");
    }
    private async Task PostPersonnel(string path, string csrf, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, path.Contains("/zuweisungDo/", StringComparison.Ordinal)
            ? path.Split("/zuweisungDo/", StringSplitOptions.None)[0] + "/zuweisung" : path + "/edit");
        request.Headers.Add("X-CSRF-Token", csrf); request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("Origin", _httpClient.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var response = await SendAsync(request, token, requireAccess: true, requestAllowed: () => IsLoggedIn &&
            _config.Session.UserId == _personnelAccount && PersonnelActionsAllowed?.Invoke() != false);
        CheckAuthentication(response);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode is not (302 or 303))
            throw new HttpRequestException($"Personalaktion: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        // Toggle endpoints are never retried. The next fresh page must confirm the exact binding.
    }
    private async Task RememberPersonnelLayout(string key, string layout)
    {
        _config.Config.PersonnelLayouts[key] = layout;
        try { await _config.SaveConfigAsync(_config.Config); }
        catch (IOException) { _config.Config.PersonnelLayouts.Remove(key); Logger?.Write(LogLevel.Warning, "Personnel", "layout.save.failed", "Prüfstand konnte nicht gespeichert werden."); }
    }
}
