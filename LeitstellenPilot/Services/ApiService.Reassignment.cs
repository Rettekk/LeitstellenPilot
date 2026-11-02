using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using LeitstellenPilot.Models;
using Newtonsoft.Json;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private DateTime _nextReassignmentTry = DateTime.MinValue;
    private string LoanAccount => string.IsNullOrEmpty(_config.Session.UserId) ? _config.Session.UserName : _config.Session.UserId;
    private bool ReassignmentAllowed => _config.Config.BotSettings.ReassignBlockedMissions &&
        _config.Config.BotSettings.AutoAlarm && AutomaticActionsAllowed?.Invoke() != false;
    private IEnumerable<VehicleLoan> OwnLoans => _config.Config.VehicleLoans.Where(loan => loan.Account == LoanAccount);
    private HashSet<long> ReservedForMission(long missionId) => _pendingVehicles.Keys.Concat(StaffingReservations()).Concat(PlannedReservations(missionId)).Concat(
        _config.Config.BotSettings.ReassignBlockedMissions ? OwnLoans.Where(loan =>
            (loan.ReturningToSource ? loan.SourceMissionId : loan.TargetMissionId) != missionId)
            .SelectMany(loan => new[] { loan.VehicleId }.Concat(!loan.ReturningToSource ? loan.ReservedVehicleIds ?? [] : [])) : []).ToHashSet();

    private bool Borrowable(Vehicle vehicle, IReadOnlyList<Vehicle> fleet, bool ownPackage = false)
    {
        if (vehicle.State != 4 || vehicle.TargetType == "building" || vehicle.QueuedMissionId.GetValueOrDefault() > 0 ||
            !ownPackage && OwnLoans.Any(loan => loan.VehicleId == vehicle.Id) || fleet.Any(child => child.TowingVehicleId == vehicle.Id) ||
            !_vehicleTypes.TryGetValue(vehicle.TypeId.ToString(), out var type)) return false;
        var buildings = type["possibleBuildings"]?.ToObject<int[]>() ?? [];
        return buildings.Length > 0 && buildings.All(building => building is 0 or 18) &&
            type.Value<bool?>("isTrailer") != true && (type.Value<int?>("equipmentCapacity") ?? 0) == 0 && vehicle.TowingVehicleId.GetValueOrDefault() <= 0;
    }

    private static Vehicle WithMission(Vehicle vehicle, long missionId)
    {
        var copy = JsonConvert.DeserializeObject<Vehicle>(JsonConvert.SerializeObject(vehicle))!;
        copy.TargetType = "mission"; copy.TargetId = missionId; return copy;
    }

    private async Task<Vehicle> ResolveSceneMissionAsync(Vehicle vehicle, CancellationToken token)
    {
        if (vehicle.State != 4 || vehicle.TargetType == "mission" && vehicle.TargetId > 0) return vehicle;
        var missionId = GamePageParser.ReadCurrentVehicleMissionId(await GetTextAsync($"/vehicles/{vehicle.Id}", token));
        return missionId.HasValue ? WithMission(vehicle, missionId.Value) : vehicle;
    }

    private static Vehicle SimulatedFreeVehicle(Vehicle vehicle, MissionDetails sourceDetails)
    {
        var copy = JsonConvert.DeserializeObject<Vehicle>(JsonConvert.SerializeObject(vehicle))!;
        copy.State = 1; copy.TargetType = "building"; copy.TargetId = copy.BuildingId;
        copy.Personnel = sourceDetails.CrewByVehicleId.GetValueOrDefault(vehicle.Id, vehicle.Personnel);
        if (copy.Personnel <= 0) return copy;
        copy.MaximumPersonnel = copy.Personnel; // Transfer the known crew, never assume a freshly staffed full vehicle.
        return copy;
    }

    private Uri? BackalarmLink(string html, long vehicleId) => BackalarmLinkForPath(html, $"/vehicles/{vehicleId}/backalarm");

    private Uri? BackalarmLinkForPath(string html, string path)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        return doc.DocumentNode.Descendants("a").Where(link => link.Attributes["disabled"] == null &&
            !link.GetAttributeValue("class", "").Split(' ').Contains("disabled") &&
            link.GetAttributeValue("aria-disabled", "false") != "true" && link.GetAttributeValue("data-method", "get") == "get")
            .Select(link => Uri.TryCreate(_httpClient.BaseAddress, HtmlEntity.DeEntitize(link.GetAttributeValue("href", "")), out var uri) ? uri : null)
            .FirstOrDefault(uri => uri != null && SameOrigin(uri) && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
                SafeBackalarmQuery(uri.Query) && uri.AbsolutePath == path);
    }

    private static bool SafeBackalarmQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).All(part =>
    {
        var pair = part.Split('=', 2); var key = WebUtility.UrlDecode(pair[0]); var value = pair.Length == 2 ? WebUtility.UrlDecode(pair[1]) : "";
        return key switch { "return" => value is "mission" or "vehicle", "sd" => value is "a" or "d", "sk" => value is "a" or "g" or "c" or "t", _ => false };
    });

    public async Task<ReassignmentResult?> TryReassignAsync(Mission target, IReadOnlyList<Mission> board, CancellationToken token = default)
    {
        var observations = Interlocked.Read(ref _crewObservationVersion);
        var hadStationBudget = _buildings != null;
        var result = await TryConsolidateAsync(target, board, token);
        if (result != null || !hadStationBudget || observations == Interlocked.Read(ref _crewObservationVersion) || !ReassignmentAllowed) return result;
        // Reading donor pages can replace conservative capacity estimates with actual crews.
        // If that frees enough existing staff, dispatch the full packet without needless recalls.
        var dispatch = await DispatchCoreAsync(target, false, token, true, requireReassignment: true, requireComplete: true);
        return dispatch.Sent ? new(true, dispatch.Message, target.Id, dispatch) : null;
    }
    public async Task<ReassignmentResult?> RestoreLoanAsync(IReadOnlyList<Mission> board, CancellationToken token = default)
    {
        EnsureLoggedIn();
        if (!ReassignmentAllowed) return null;
        foreach (var package in OwnLoans.Where(loan => loan.PackageId.Length > 0).GroupBy(loan => loan.PackageId).Select(group => group.ToArray()).ToArray())
        {
            var result = await ResumeConsolidationAsync(package, board, token);
            if (result != null) return result;
        }
        foreach (var loan in OwnLoans.Where(loan => loan.PackageId.Length == 0).ToArray())
        {
            var source = board.FirstOrDefault(mission => mission.Id == loan.SourceMissionId);
            if (source == null || source.FleetBlockReason.Length > 0 || !source.NeedsDispatch)
            { _config.Config.VehicleLoans.Remove(loan); await _config.SaveConfigAsync(_config.Config); continue; }
            var current = await GetVehicleAsync(loan.VehicleId, token);
            if (loan.ReturningToSource && current.TargetType == "mission" && current.TargetId == loan.SourceMissionId)
            { _config.Config.VehicleLoans.Remove(loan); await _config.SaveConfigAsync(_config.Config); continue; }
            if (current.TargetType == "mission")
            {
                if (current.TargetId == loan.TargetMissionId) { loan.SentToTarget = true; continue; }
                if (current.TargetId == loan.SourceMissionId && UtcNow - loan.CreatedUtc < TimeSpan.FromMinutes(2)) continue;
                _config.Config.VehicleLoans.Remove(loan); await _config.SaveConfigAsync(_config.Config); continue;
            }
            if (!current.IsAvailable) continue; // Never interrupt a journey to hospital, prisoner transport or another active mission.
            if (UtcNow < loan.NextActionAfterUtc) continue;
            var target = board.FirstOrDefault(mission => mission.Id == loan.TargetMissionId);
            if (!loan.ReturningToSource && target != null && loan.SentToTarget && UtcNow - loan.CreatedUtc < TimeSpan.FromMinutes(2)) continue;
            if (!loan.ReturningToSource && target != null && !loan.SentToTarget)
            {
                var dispatch = await DispatchCoreAsync(target, false, token, true, requireReassignment: true);
                loan.SentToTarget = dispatch.Sent || dispatch.Success;
                if (loan.SentToTarget) { await _config.SaveConfigAsync(_config.Config); return new(dispatch.Sent, dispatch.Message, loan.SourceMissionId, dispatch); }
            }
            loan.ReturningToSource = true;
            await _config.SaveConfigAsync(_config.Config);
            return await ReturnVehicleToSourceAsync(loan, source, token);
        }
        return null;
    }

    private async Task<ReassignmentResult?> ReturnVehicleToSourceAsync(VehicleLoan loan, Mission source, CancellationToken token)
    {
        await _dispatchGate.WaitAsync(token);
        try
        {
            await _pacer.WaitAsync(token);
            if (!ReassignmentAllowed) return null;
            var details = await ParseDetailsWithRefreshAsync(await GetTextAsync($"/missions/{source.Id}", token), source, token);
            var current = await GetVehicleAsync(loan.VehicleId, token);
            if (!current.IsAvailable || !details.HasMissingRequirementsFromPage || !details.VehicleOrder.ContainsKey(current.Id) || details.DisabledVehicleIds.Contains(current.Id)) return null;
            var catalog = await GetCatalogAsync(token, details.TypeKey);
            var specification = details.TypeKey != null ? catalog.GetValueOrDefault(details.TypeKey) : null;
            var fleet = await GetVehiclesAsync(true, token);
            foreach (var vehicle in fleet) vehicle.Order = details.VehicleOrder.GetValueOrDefault(vehicle.Id, int.MaxValue);
            await PrepareApproachesAsync(source, specification, details, fleet, token);
            var returnPlan = CreateVehiclePlanner().Build(source, specification, details, PlanningFleet(fleet), ReservedForMission(source.Id), StationCrewBudgets(fleet, source, details));
            if (!ApproachPolicy.Allows(current.Id, details, ApproachLimit))
            {
                loan.NextActionAfterUtc = UtcNow.AddSeconds(30); await _config.SaveConfigAsync(_config.Config);
                return new(false, "Rückversorgung wartet: " + ApproachPolicy.Exclusion(current.Id, details, ApproachLimit) + ".", source.Id);
            }
            if (loan.PackageId.Length > 0 && !returnPlan.IsComplete)
            {
                _config.Config.VehicleLoans.Remove(loan); await _config.SaveConfigAsync(_config.Config);
                return new(false, $"Fahrzeug {loan.VehicleId} bleibt frei: ursprünglicher Einsatz wartet auf eine vollständige Einsatzgruppe.", source.Id);
            }
            if (returnPlan.SelectedVehicles.All(vehicle => vehicle.Id != loan.VehicleId))
            {
                _config.Config.VehicleLoans.Remove(loan); await _config.SaveConfigAsync(_config.Config);
                var skipped = $"Fahrzeug {loan.VehicleId}: ursprünglicher Einsatz benötigt dieses Fahrzeug nicht zusätzlich; Rückversorgung entfällt.";
                Logger?.Write(LogLevel.Info, "Reassignment", "return.unnecessary", skipped);
                return new(false, skipped, source.Id);
            }
            var validation = new DispatchPlan(); var action = ValidateAlarmForm(source, details, validation);
            if (validation.Problems.Count > 0) return new(false, "Rückversorgung: Alarmierungsformular nicht erkannt.", source.Id);
            foreach (var field in details.HiddenFields.Where(field => field.Key == "authenticity_token")) Logger?.RegisterSecret(field.Value);
            var fields = details.HiddenFields.Where(field => field.Key is not ("commit" or "alarm")).ToList();
            fields.Add(new("commit", "Alarmieren")); fields.Add(new("vehicle_ids[]", current.Id.ToString()));
            if (details.EquipmentFieldsByVehicleId.TryGetValue(current.Id, out var equipment)) fields.AddRange(equipment.Distinct());
            loan.NextActionAfterUtc = UtcNow.AddMinutes(2);
            await _config.SaveConfigAsync(_config.Config);
            using var request = new HttpRequestMessage(HttpMethod.Post, action) { Content = new FormUrlEncodedContent(fields) };
            request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, $"/missions/{source.Id}");
            using var response = await SendAsync(request, token, automaticAction: true, actionAllowed: () =>
                _config.Config.BotSettings.ReassignBlockedMissions && _config.Config.BotSettings.AutoAlarm &&
                ApproachPolicy.Allows(current.Id, details, ApproachLimit));
            CheckAuthentication(response);
            var after = await GetVehicleAsync(current.Id, token);
            var confirmed = (int)response.StatusCode is 200 or 302 or 303 && after.TargetType == "mission" && after.TargetId == source.Id;
            if (confirmed) { _inventoryTime = DateTime.MinValue; _config.Config.VehicleLoans.Remove(loan); await _config.SaveConfigAsync(_config.Config); }
            var message = $"Fahrzeug {current.Id}: " + (confirmed ? $"Rückversorgung zu ursprünglichem Einsatz {source.Id} bestätigt." : "Rückversorgung gesendet, Bestätigung ausstehend.");
            Logger?.Write(confirmed ? LogLevel.Info : LogLevel.Warning, "Reassignment", "return.result", message);
            return new(true, message, source.Id);
        }
        catch (AutomaticActionPausedException)
        {
            loan.NextActionAfterUtc = DateTime.MinValue; await _config.SaveConfigAsync(_config.Config);
            return new(false, "Umverteilung pausiert; keine Rückversorgung gesendet.", source.Id);
        }
        finally { _dispatchGate.Release(); }
    }
}
