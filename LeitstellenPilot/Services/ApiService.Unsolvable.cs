using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private DateTime _nextUnsolvableReview = DateTime.MinValue;
    private string _stockSignature = "";
    private readonly Dictionary<long, DateTime> _pendingReleases = new();
    private readonly Dictionary<long, HashSet<long>> _blockedSceneVehicles = new();
    private readonly Dictionary<long, Uri> _releaseBackalarmLinks = new();
    private readonly HashSet<long> _bulkRecallMissions = new();
    private readonly Dictionary<long, string> _releaseStatuses = new();
    private bool ReleaseAllowed => _config.Config.BotSettings.ReleaseUnsolvableMissions &&
        _config.Config.BotSettings.AutoAlarm && AutomaticActionsAllowed?.Invoke() != false;

    private static bool HasRollContainerAlternative(string key) => key is
        "mobile_air_vehicles" or "gwoil" or "height_rescue_units" or "water_tankers" or
        "heavy_rescue_vehicles" or "hazmat_vehicles" or "hazmat_dekon" or "diver_units" or "drone" or
        "oneof_fire_engine_or_rescue" or "oneof_fire_engine_or_rescue_or_oil_equipment";

    private bool StockHasRequirement(string key, IReadOnlyList<Vehicle> fleet, EinsatzInfo? specification)
    {
        if (!_rules.Types.TryGetValue(key, out var types) || key.StartsWith("_personnel_")) return true;
        if (fleet.Any(vehicle => types.Contains(vehicle.TypeId))) return true; // Busy/unavailable vehicles still exist in the stock.
        // An unknown type may supply an equivalent capability. A known carrier only
        // leaves requirements with actual roll-container alternatives unresolved;
        // its loading space cannot replace a GW-Messtechnik, ladder or command unit.
        if (fleet.Any(vehicle => !_vehicleTypes.ContainsKey(vehicle.TypeId.ToString()))) return true;
        if (HasRollContainerAlternative(key) && fleet.Any(vehicle =>
                _vehicleTypes.TryGetValue(vehicle.TypeId.ToString(), out var type) &&
                (type.Value<int?>("equipmentCapacity") ?? 0) > 0 &&
                type["possibleBuildings"]?.Values<int>().Any(building => building is 0 or 18) == true)) return true;
        var additional = specification?.Additional;
        if (key == "ambulances" && (additional?.Value<bool?>("allow_ktw_instead_of_rtw") == true && fleet.Any(v => v.TypeId == 38) ||
            additional?.Value<bool?>("allow_ktw_b_instead_of_rtw") == true && fleet.Any(v => v.TypeId == 58))) return true;
        if (key == "firetrucks" && additional?.Value<bool?>("allow_arff_instead_of_lf") == true && fleet.Any(v => v.TypeId == 75)) return true;
        if (key == "police_cars" && ((additional?.Value<int?>("max_civil_patrol_replacing_police_cars") ?? 0) > 0 && fleet.Any(v => v.TypeId == 98) ||
            ((additional?.Value<int?>("max_police_motorcycle_replacing_police_cars") ?? 0) > 0 || additional?.Value<bool?>("allow_police_motorcycle_instead_of_fustw") == true) && fleet.Any(v => v.TypeId == 95))) return true;
        return false;
    }

    public async Task<ReassignmentResult?> ReleaseUnsolvableAsync(IReadOnlyList<Mission> board, CancellationToken token = default)
    {
        EnsureLoggedIn();
        if (!ReleaseAllowed) return null;
        long[] attemptedReleases = [];
        var sentCount = 0;
        await _dispatchGate.WaitAsync(token);
        try
        {
            var fleet = await GetVehiclesAsync(token: token);
            var signature = string.Join(";", fleet.Select(vehicle => vehicle.TypeId).Distinct().Order());
            var review = UtcNow >= _nextUnsolvableReview || signature != _stockSignature;
            var blocks = _config.Config.FleetBlockedMissions;
            var changed = blocks.RemoveAll(block => block.Account == LoanAccount && board.All(mission => mission.Id != block.MissionId)) > 0;
            if (review)
            {
                _nextUnsolvableReview = UtcNow.AddMinutes(1); _stockSignature = signature;
                var catalog = await GetCatalogAsync(token);
                foreach (var mission in board.Where(mission => mission.HasMissingVehicles || mission.HasLiveRequirements ||
                             blocks.Any(block => block.Account == LoanAccount && block.MissionId == mission.Id)))
                {
                    var block = blocks.FirstOrDefault(item => item.Account == LoanAccount && item.MissionId == mission.Id);
                    var missionHtml = await GetTextAsync($"/missions/{mission.Id}", token);
                    var details = await ParseDetailsWithRefreshAsync(missionHtml, mission, token);
                    if (details.TypeKey != null && !catalog.ContainsKey(details.TypeKey)) catalog = await GetCatalogAsync(token, details.TypeKey);
                    var specification = details.TypeKey != null ? catalog.GetValueOrDefault(details.TypeKey) : null;
                    var absent = details.MissingRequirements.FirstOrDefault(demand => !StockHasRequirement(demand.Key, fleet, specification));
                    if (absent == null || details.UnparsedRequirements.Count > 0 || !details.HasMissingRequirementsFromPage ||
                        mission.patients_count > 0 || mission.prisoners_count > 0 || details.HasPatients || details.HasPrisonerTransportRequest)
                    {
                        if (block != null) { blocks.Remove(block); changed = true; mission.FleetBlockReason = ""; }
                        continue;
                    }
                    if (block == null)
                    {
                        block = new() { Account = LoanAccount, MissionId = mission.Id, RequirementKey = absent.Key };
                        blocks.Add(block); changed = true;
                    }
                    block.Reason = $"Mit aktuellem Fuhrpark nicht lösbar: {_rules.Label(absent.Key, _labels)} fehlt vollständig.";
                    mission.FleetBlockReason = block.Reason;
                    RememberReleaseLinks(mission, details, missionHtml);
                    Logger?.Write(LogLevel.Warning, "Fleet", "mission.unsolvable", block.Reason);
                }
            }
            foreach (var id in _blockedSceneVehicles.Keys.Where(id => !blocks.Any(block => block.Account == LoanAccount && block.MissionId == id)).ToArray())
            { _blockedSceneVehicles.Remove(id); _releaseStatuses.Remove(id); _bulkRecallMissions.Remove(id); }
            foreach (var id in _releaseBackalarmLinks.Keys.Where(id => !_blockedSceneVehicles.Values.Any(vehicles => vehicles.Contains(id))).ToArray())
                _releaseBackalarmLinks.Remove(id);
            if (changed) await _config.SaveConfigAsync(_config.Config);
            foreach (var block in blocks.Where(block => block.Account == LoanAccount).ToArray())
            {
                var mission = board.FirstOrDefault(mission => mission.Id == block.MissionId);
                if (mission == null) continue;
                mission.FleetBlockReason = block.Reason;
                var sceneIds = _blockedSceneVehicles.GetValueOrDefault(mission.Id) ?? [];
                var candidates = fleet.Where(vehicle => CanReleaseVehicle(vehicle, mission.Id, sceneIds)).ToArray();
                if (candidates.Length == 0)
                {
                    var observed = fleet.Where(vehicle => _blockedSceneVehicles.GetValueOrDefault(mission.Id)?.Contains(vehicle.Id) == true).ToArray();
                    SetReleaseStatus(mission, block, observed.Length > 0 && observed.All(vehicle => vehicle.IsAvailable)
                        ? "Eigene Fahrzeuge sind frei oder auf Rückfahrt."
                        : "Rückruf wartet: keine eindeutig zugeordneten, rückrufbaren eigenen Fahrzeuge erkannt.");
                }
                // The game's own bulk action releases a whole mission in one request.
                // Do not use it when it would also touch a protected or unconfirmed unit.
                if (_bulkRecallMissions.Contains(mission.Id) && CanReleaseAll(fleet, mission.Id, sceneIds))
                {
                    await _pacer.WaitAsync(token);
                    if (!ReleaseAllowed) return null;
                    fleet = await GetVehiclesAsync(true, token);
                    var missionHtml = await GetTextAsync($"/missions/{mission.Id}", token);
                    var details = await ParseDetailsWithRefreshAsync(missionHtml, mission, token);
                    var catalog = await GetCatalogAsync(token, details.TypeKey);
                    var spec = details.TypeKey != null ? catalog.GetValueOrDefault(details.TypeKey) : null;
                    if (!StillUnsolvable(block, mission, details, fleet, spec))
                    { blocks.Remove(block); mission.FleetBlockReason = ""; await _config.SaveConfigAsync(_config.Config); return null; }
                    RememberReleaseLinks(mission, details, missionHtml);
                    sceneIds = details.AssignedVehicleIds;
                    candidates = fleet.Where(vehicle => CanReleaseVehicle(vehicle, mission.Id, sceneIds)).ToArray();
                    var bulkLink = BackalarmLinkForPath(missionHtml, $"/missions/{mission.Id}/backalarmAll");
                    if (bulkLink != null && CanReleaseAll(fleet, mission.Id, sceneIds))
                    {
                        attemptedReleases = candidates.Select(vehicle => vehicle.Id).ToArray();
                        foreach (var id in attemptedReleases) _pendingReleases[id] = UtcNow.AddMinutes(2);
                        using var request = new HttpRequestMessage(HttpMethod.Get, bulkLink);
                        request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, $"/missions/{mission.Id}");
                        using var response = await SendAsync(request, token, automaticAction: true, actionAllowed: () => ReleaseAllowed);
                        sentCount = attemptedReleases.Length;
                        CheckAuthentication(response);
                        SetReleaseStatus(mission, block, $"Sammelrückruf gesendet: {sentCount} eigene Fahrzeuge · Bestätigung ausstehend.");
                        var after = await GetVehiclesAsync(true, token);
                        var confirmed = attemptedReleases.Where(id => (int)response.StatusCode is 200 or 302 or 303 &&
                            after.Any(vehicle => vehicle.Id == id && vehicle.IsAvailable)).ToArray();
                        foreach (var id in confirmed) _pendingReleases.Remove(id);
                        ReportReleaseBatch(mission, block, sentCount, confirmed.Length, candidates.Length - confirmed.Length);
                        return new(true, ReleaseMessage(mission, sentCount, confirmed.Length), mission.Id);
                    }
                }
                var confirmedCount = 0;
                var ownAssignedCount = fleet.Count(vehicle => TargetsMission(vehicle, mission.Id) || sceneIds.Contains(vehicle.Id));
                foreach (var vehicle in candidates)
                {
                    if (_pendingReleases.GetValueOrDefault(vehicle.Id) > UtcNow)
                    { SetReleaseStatus(mission, block, "Rückruf gesendet · Bestätigung ausstehend."); continue; }
                    var link = _releaseBackalarmLinks.GetValueOrDefault(vehicle.Id) ?? BackalarmLink(await GetTextAsync($"/vehicles/{vehicle.Id}", token), vehicle.Id);
                    if (link == null)
                    { SetReleaseStatus(mission, block, $"Rückruf wartet: Für {vehicle.Name} wurde kein gültiger Rückruflink angeboten."); continue; }
                    await _pacer.WaitAsync(token);
                    if (!ReleaseAllowed) break;
                    var current = await GetVehicleAsync(vehicle.Id, token);
                    var freshFleet = await GetVehiclesAsync(true, token);
                    var missionHtml = await GetTextAsync($"/missions/{mission.Id}", token);
                    var details = await ParseDetailsWithRefreshAsync(missionHtml, mission, token);
                    current = WithVerifiedSceneMission(current, mission, details);
                    var catalog = await GetCatalogAsync(token, details.TypeKey);
                    var spec = details.TypeKey != null ? catalog.GetValueOrDefault(details.TypeKey) : null;
                    if (!StillUnsolvable(block, mission, details, freshFleet, spec))
                    {
                        blocks.Remove(block); mission.FleetBlockReason = ""; await _config.SaveConfigAsync(_config.Config);
                        return sentCount > 0 ? new(true, ReleaseMessage(mission, sentCount, confirmedCount), mission.Id) : null;
                    }
                    if (current.State is not (3 or 4) || !TargetsMission(current, mission.Id) || current.QueuedMissionId.GetValueOrDefault() > 0)
                    { SetReleaseStatus(mission, block, $"Rückruf von {vehicle.Name} übersprungen · Zuordnung oder Verfügbarkeit geändert."); continue; }
                    var missionLink = BackalarmLink(missionHtml, vehicle.Id);
                    link = missionLink ?? BackalarmLink(await GetTextAsync($"/vehicles/{vehicle.Id}", token), vehicle.Id);
                    if (link == null)
                    { SetReleaseStatus(mission, block, $"Rückruf wartet: Für {vehicle.Name} wurde kein gültiger Rückruflink angeboten."); continue; }
                    _pendingReleases[vehicle.Id] = UtcNow.AddMinutes(2);
                    attemptedReleases = [vehicle.Id];
                    using var request = new HttpRequestMessage(HttpMethod.Get, link);
                    request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, missionLink != null ? $"/missions/{mission.Id}" : $"/vehicles/{vehicle.Id}");
                    using var response = await SendAsync(request, token, automaticAction: true, actionAllowed: () =>
                        _config.Config.BotSettings.ReleaseUnsolvableMissions && _config.Config.BotSettings.AutoAlarm);
                    sentCount++;
                    CheckAuthentication(response);
                    var after = await GetVehicleAsync(vehicle.Id, token);
                    var confirmed = (int)response.StatusCode is 200 or 302 or 303 && after.IsAvailable;
                    if (confirmed) { confirmedCount++; _pendingReleases.Remove(vehicle.Id); _inventoryTime = DateTime.MinValue; }
                    var message = $"Einsatz {mission.Id}: Fahrzeug {vehicle.Id} " + (confirmed ? "freigegeben; notwendiger Fahrzeugtyp fehlt im gesamten Fuhrpark." : "Rückalarmierung gesendet, Bestätigung ausstehend.");
                    Logger?.Write(confirmed ? LogLevel.Info : LogLevel.Warning, "Fleet", "vehicle.release", message);
                }
                if (sentCount > 0)
                {
                    ReportReleaseBatch(mission, block, sentCount, confirmedCount, ownAssignedCount - confirmedCount);
                    return new(true, ReleaseMessage(mission, sentCount, confirmedCount), mission.Id);
                }
            }
            return null;
        }
        catch (AutomaticActionPausedException)
        {
            foreach (var id in attemptedReleases) _pendingReleases.Remove(id);
            return new(sentCount > 0, sentCount > 0 ? $"Automatik pausiert; {sentCount} Fahrzeugrückrufe bereits gesendet." : "Automatik pausiert; keine Fahrzeuge freigegeben.");
        }
        finally { _dispatchGate.Release(); }
    }

    private void RememberReleaseLinks(Mission mission, MissionDetails details, string html)
    {
        _blockedSceneVehicles[mission.Id] = details.AssignedVehicleIds.ToHashSet();
        if (BackalarmLinkForPath(html, $"/missions/{mission.Id}/backalarmAll") != null) _bulkRecallMissions.Add(mission.Id);
        else _bulkRecallMissions.Remove(mission.Id);
        foreach (var id in details.AssignedVehicleIds)
            if (BackalarmLink(html, id) is { } recall) _releaseBackalarmLinks[id] = recall;
            else _releaseBackalarmLinks.Remove(id);
    }

    private bool StillUnsolvable(FleetBlockedMission block, Mission mission, MissionDetails details, IReadOnlyList<Vehicle> fleet, EinsatzInfo? spec) =>
        !StockHasRequirement(block.RequirementKey, fleet, spec) && details.HasMissingRequirementsFromPage &&
        details.UnparsedRequirements.Count == 0 && mission.patients_count <= 0 && mission.prisoners_count <= 0 &&
        !details.HasPatients && !details.HasPrisonerTransportRequest &&
        details.MissingRequirements.Any(demand => demand.Key == block.RequirementKey);

    private static bool TargetsMission(Vehicle vehicle, long missionId) =>
        string.Equals(vehicle.TargetType, "mission", StringComparison.OrdinalIgnoreCase) && vehicle.TargetId == missionId;

    private static bool CanReleaseVehicle(Vehicle vehicle, long missionId, HashSet<long> sceneIds) =>
        vehicle.State is 3 or 4 && vehicle.QueuedMissionId.GetValueOrDefault() == 0 &&
        (TargetsMission(vehicle, missionId) || vehicle.State == 4 && !HasOtherMission(vehicle, missionId) && sceneIds.Contains(vehicle.Id));

    private bool CanReleaseAll(IReadOnlyList<Vehicle> fleet, long missionId, HashSet<long> sceneIds)
    {
        var affected = fleet.Where(vehicle => TargetsMission(vehicle, missionId) || sceneIds.Contains(vehicle.Id)).ToArray();
        return affected.Length > 0 && affected.All(vehicle => CanReleaseVehicle(vehicle, missionId, sceneIds) &&
            _pendingReleases.GetValueOrDefault(vehicle.Id) <= UtcNow);
    }

    private void ReportReleaseBatch(Mission mission, FleetBlockedMission block, int sent, int confirmed, int remaining)
    {
        var status = confirmed == 0
            ? $"Rückruf gesendet: {sent} eigene Fahrzeuge · Bestätigung ausstehend."
            : confirmed == sent && remaining == 0
            ? $"Rückruf bestätigt: alle {confirmed} eigenen Fahrzeuge frei oder auf Rückfahrt."
            : $"Rückruf bestätigt: {confirmed} eigene Fahrzeuge · {remaining} noch nicht freigegeben." +
                (confirmed < sent ? " Bestätigung ausstehend." : "");
        SetReleaseStatus(mission, block, status);
        Logger?.Write(confirmed == sent ? LogLevel.Info : LogLevel.Warning, "Fleet", "mission.release", ReleaseMessage(mission, sent, confirmed));
    }

    private static string ReleaseMessage(Mission mission, int sent, int confirmed) =>
        $"Einsatz {mission.Id}: {confirmed} von {sent} eigenen Fahrzeugen freigegeben." +
        (confirmed < sent ? $" Für {sent - confirmed} Fahrzeuge ist die Bestätigung noch ausstehend." : "");

    private void SetReleaseStatus(Mission mission, FleetBlockedMission block, string status)
    {
        mission.FleetBlockReason = block.Reason + " " + status;
        if (_releaseStatuses.GetValueOrDefault(mission.Id) == status) return;
        _releaseStatuses[mission.Id] = status;
        Logger?.Write(LogLevel.Info, "Fleet", "release.status", status);
    }

    private static bool HasOtherMission(Vehicle vehicle, long missionId) =>
        string.Equals(vehicle.TargetType, "mission", StringComparison.OrdinalIgnoreCase) && vehicle.TargetId is > 0 && vehicle.TargetId != missionId;

    private static Vehicle WithVerifiedSceneMission(Vehicle vehicle, Mission mission, MissionDetails details) =>
        vehicle.State == 4 && !HasOtherMission(vehicle, mission.Id) && details.AssignedVehicleIds.Contains(vehicle.Id)
            ? WithMission(vehicle, mission.Id) : vehicle;
}
