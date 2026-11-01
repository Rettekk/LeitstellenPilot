using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private sealed record Donor(Vehicle Vehicle, Mission Source, MissionDetails Details);
    public long? FocusMissionId => !_config.Config.BotSettings.ReassignBlockedMissions ? null : OwnLoans.Where(loan => loan.PackageId.Length > 0 && !loan.ReturningToSource && !loan.TargetConfirmed)
        .OrderBy(loan => loan.CreatedUtc).Select(loan => (long?)loan.TargetMissionId).FirstOrDefault();
    private static bool KnownShortage(MissionDetails details) => details.HasMissingRequirementsFromPage && details.MissingRequirements.Count > 0 &&
        details.UnparsedRequirements.Count == 0 && !details.HasPrisonerTransportRequest;
    private static bool KnownBlockedFireMission(MissionDetails details) => KnownShortage(details) && !details.HasPatients;

    private async Task<ReassignmentResult?> TryConsolidateAsync(Mission target, IReadOnlyList<Mission> board, CancellationToken token)
    {
        EnsureLoggedIn();
        if (!ReassignmentAllowed || UtcNow < _nextReassignmentTry || target.sw || target.prisoners_count > 0 ||
            OwnLoans.Any(loan => loan.SourceMissionId == target.Id || loan.TargetMissionId == target.Id)) return null;
        List<VehicleLoan>? package = null;
        await _dispatchGate.WaitAsync(token);
        try
        {
            if (!ReassignmentAllowed) return null;
            var catalog = await GetCatalogAsync(token);
            var details = await ParseDetailsWithRefreshAsync(await GetTextAsync($"/missions/{target.Id}", token), target, token);
            if (details.TypeKey != null && !catalog.ContainsKey(details.TypeKey)) catalog = await GetCatalogAsync(token, details.TypeKey);
            if (!KnownShortage(details)) return null;
            var fleet = await GetVehiclesAsync(true, token);
            foreach (var vehicle in fleet) vehicle.Order = details.VehicleOrder.GetValueOrDefault(vehicle.Id, int.MaxValue);
            var planner = CreateVehiclePlanner();
            var spec = details.TypeKey == null ? null : catalog.GetValueOrDefault(details.TypeKey);
            await PrepareApproachesAsync(target, spec, details, fleet, token);
            var reserved = ReservedForMission(target.Id);
            var budget = StationCrewBudgets(fleet, target, details);
            if (planner.Build(target, spec, details, PlanningFleet(fleet), reserved, budget).IsComplete) return null;
            var donors = new List<Donor>();
            foreach (var source in board.Where(source => source.Id != target.Id && !source.sw && source.FleetBlockReason.Length == 0 &&
                         source.patients_count == 0 && source.prisoners_count == 0 &&
                         !OwnLoans.Any(loan => loan.SourceMissionId == source.Id || loan.TargetMissionId == source.Id) &&
                         fleet.Any(vehicle => Borrowable(vehicle, fleet)) &&
                         (source.vehicle_state > 0 || source.HasMissingVehicles || source.HasLiveRequirements || source.PlanningProblem.Length > 0))
                     .OrderBy(source => source.average_credits).ThenByDescending(source => source.Id).Take(12))
            {
                var sourceDetails = await ParseDetailsWithRefreshAsync(await GetTextAsync($"/missions/{source.Id}", token), source, token);
                ObserveCrew(source, sourceDetails);
                if (!KnownBlockedFireMission(sourceDetails)) continue;
                fleet = fleet.Select(vehicle => vehicle.State == 4 && string.IsNullOrEmpty(vehicle.TargetType) &&
                    sourceDetails.AssignedVehicleIds.Contains(vehicle.Id) ? WithMission(vehicle, source.Id) : vehicle).ToList();
                var sourceSpec = sourceDetails.TypeKey == null ? null : catalog.GetValueOrDefault(sourceDetails.TypeKey);
                await PrepareApproachesAsync(source, sourceSpec, sourceDetails, fleet, token);
                if (planner.Build(source, sourceSpec, sourceDetails, PlanningFleet(fleet), ReservedForMission(source.Id),
                        StationCrewBudgets(fleet, source, sourceDetails)).IsComplete) continue;
                donors.AddRange(fleet.Where(vehicle => vehicle.TargetId == source.Id && Borrowable(vehicle, fleet) &&
                    !reserved.Contains(vehicle.Id) && sourceDetails.AssignedVehicleIds.Contains(vehicle.Id) &&
                    sourceDetails.CrewByVehicleId.GetValueOrDefault(vehicle.Id) > 0).Select(vehicle => new Donor(vehicle, source, sourceDetails)));
            }
            if (planner.Build(target, spec, details, PlanningFleet(fleet), reserved, StationCrewBudgets(fleet, target, details)).IsComplete) return null;
            var copies = donors.ToDictionary(donor => donor.Vehicle.Id, donor => SimulatedFreeVehicle(donor.Vehicle, donor.Details));
            var virtualFleet = fleet.Select(vehicle => copies.GetValueOrDefault(vehicle.Id, vehicle)).ToArray();
            await PrepareApproachesAsync(target, spec, details, virtualFleet, token, SceneOrigins(donors));
            var transfer = planner.Build(target, spec, details, virtualFleet, reserved, StationCrewBudgets(virtualFleet, target, details));
            var chosen = donors.Where(donor => transfer.SelectedVehicles.Any(vehicle => vehicle.Id == donor.Vehicle.Id)).ToList();
            var forStaffing = false;
            if (!transfer.IsComplete || chosen.Count == 0)
            {
                var recipients = fleet.Where(vehicle => vehicle.IsAvailable && vehicle.Personnel == 0 &&
                    (StaffingReservations().Contains(vehicle.Id) || budget.TryGetValue(vehicle.BuildingId, out var available) && available <
                        (vehicle.MaximumPersonnel ?? _vehicleTypes.GetValueOrDefault(vehicle.TypeId.ToString())?.Value<int?>("maxPersonnel") ?? 1)) &&
                    _vehicleTypes.TryGetValue(vehicle.TypeId.ToString(), out var type) &&
                    type["schooling"] is not { HasValues: true } && !details.EquipmentFieldsByVehicleId.ContainsKey(vehicle.Id)).ToArray();
                if (recipients.Length == 0) return null;
                var unblocked = new HashSet<long>(reserved); unblocked.ExceptWith(recipients.Select(vehicle => vehicle.Id));
                var returnable = donors.Where(donor => donor.Vehicle.Personnel == 0).ToDictionary(donor => donor.Vehicle.Id);
                var afterReturn = fleet.Select(vehicle =>
                {
                    if (!returnable.ContainsKey(vehicle.Id)) return vehicle;
                    var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<Vehicle>(Newtonsoft.Json.JsonConvert.SerializeObject(vehicle))!;
                    copy.State = 2; copy.TargetType = null; copy.TargetId = null; return copy;
                }).ToArray();
                await PrepareApproachesAsync(target, spec, details, afterReturn, token, fromHome: returnable.Keys.ToHashSet());
                transfer = planner.Build(target, spec, details, afterReturn, unblocked, StationCrewBudgets(afterReturn, target, details));
                if (!transfer.IsComplete) return null;
                var needed = transfer.SelectedVehicles.Where(vehicle => recipients.Any(recipient => recipient.Id == vehicle.Id))
                    .GroupBy(vehicle => vehicle.BuildingId).ToDictionary(group => group.Key, group => group.Sum(vehicle =>
                        Math.Max(1, transfer.AutomaticCrewAllocations.GetValueOrDefault(vehicle.Id,
                            vehicle.MaximumPersonnel ?? _vehicleTypes[vehicle.TypeId.ToString()].Value<int?>("maxPersonnel") ?? 1))));
                if (needed.Count == 0) return null;
                await GetBuildingsAsync(token);
                chosen.Clear();
                foreach (var (station, staff) in needed)
                {
                    var count = 0;
                    var ranked = await Task.WhenAll(donors.Where(donor => donor.Vehicle.BuildingId == station && donor.Vehicle.Personnel == 0)
                        .Select(async donor => (Donor: donor, Time: await HomeTravelTimeAsync(donor, token))));
                    foreach (var entry in ranked.Where(entry => ApproachLimit == 0 || entry.Time <= ApproachLimit * 60)
                        .OrderBy(entry => entry.Time).ThenBy(entry => entry.Donor.Source.average_credits).ThenBy(entry => entry.Donor.Vehicle.Id))
                    {
                        var donor = entry.Donor;
                        chosen.Add(donor); count += donor.Details.CrewByVehicleId[donor.Vehicle.Id];
                        if (count >= staff) break;
                    }
                    if (count < staff) return null;
                }
                forStaffing = true;
            }
            if (chosen.Count is 0 or > 12) return null;
            foreach (var donor in chosen)
                if (BackalarmLink(await GetTextAsync($"/vehicles/{donor.Vehicle.Id}", token), donor.Vehicle.Id) == null) return null;
            var packageId = Guid.NewGuid().ToString("N");
            package = chosen.Select(donor => new VehicleLoan
            {
                Account = LoanAccount, PackageId = packageId, VehicleId = donor.Vehicle.Id,
                SourceMissionId = donor.Source.Id, TargetMissionId = target.Id, CreatedUtc = UtcNow,
                KnownCrew = donor.Details.CrewByVehicleId[donor.Vehicle.Id],
                StaffingBuildingId = forStaffing ? donor.Vehicle.BuildingId : 0,
                RecallForStaffing = forStaffing, ReservedVehicleIds = transfer.SelectedVehicles.Select(vehicle => vehicle.Id).ToArray()
            }).ToList();
            _config.Config.VehicleLoans.AddRange(package);
            await _config.SaveConfigAsync(_config.Config);
            Logger?.Write(LogLevel.Info, "Reassignment", "package.prepared", $"Vollständige Einsatzgruppe für Einsatz {target.Id} vorbereitet: {package.Count} Rückrufe" +
                (forStaffing ? " damit Personal zur Wache zurückkehrt." : " zur gezielten Umverteilung."));
            _nextReassignmentTry = UtcNow.AddSeconds(30);
        }
        finally { _dispatchGate.Release(); }
        return package == null ? null : await ResumeConsolidationAsync(package, board, token);
    }

    private double HomeDistance(Donor donor)
    {
        var station = _buildings?.FirstOrDefault(building => building.Value<long?>("id") == donor.Vehicle.BuildingId);
        var lat = station?.Value<double?>("latitude"); var lon = station?.Value<double?>("longitude");
        if (donor.Details.Latitude is not { } sourceLat || donor.Details.Longitude is not { } sourceLon ||
            lat is not { } homeLat || lon is not { } homeLon || !double.IsFinite(homeLat) || !double.IsFinite(homeLon) ||
            homeLat is < -90 or > 90 || homeLon is < -180 or > 180) return double.MaxValue;
        var radians = Math.PI / 180;
        var a = Math.Pow(Math.Sin((homeLat - sourceLat) * radians / 2), 2) +
            Math.Cos(sourceLat * radians) * Math.Cos(homeLat * radians) * Math.Pow(Math.Sin((homeLon - sourceLon) * radians / 2), 2);
        return 6371 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1))); // Distance estimate, not a road travel time.
    }

    private async Task<double> HomeTravelTimeAsync(Donor donor, CancellationToken token)
    {
        var distance = HomeDistance(donor);
        if (distance == double.MaxValue) return distance;
        var station = _buildings!.First(building => building.Value<long?>("id") == donor.Vehicle.BuildingId);
        var road = await _routing.SecondsAsync(donor.Details.Latitude!.Value, donor.Details.Longitude!.Value,
            station.Value<double>("latitude"), station.Value<double>("longitude"), token);
        if (ApproachLimit > 0) return road ?? double.MaxValue;
        // The game's routing script caps an excessively long road route with its direct-distance fallback.
        return road is { } seconds ? distance / 20 * 3600 < seconds ? distance / 30 * 3600 : seconds : distance / 30 * 3600;
    }

    private async Task<ReassignmentResult?> ResumeConsolidationAsync(IReadOnlyList<VehicleLoan> package, IReadOnlyList<Mission> board, CancellationToken token)
    {
        var first = package[0]; var target = board.FirstOrDefault(mission => mission.Id == first.TargetMissionId);
        var approachSources = new Dictionary<long, MissionDetails>();
        if (target == null || target.FleetBlockReason.Length > 0)
            foreach (var loan in package) loan.ReturningToSource = true;
        var actionSent = false;
        var waitingForStaff = false;
        foreach (var loan in package)
        {
            var current = await ResolveSceneMissionAsync(await GetVehicleAsync(loan.VehicleId, token), token);
            if (current.State == 4 && current.TargetType != "mission" && !loan.WithdrawalSent)
            {
                foreach (var item in package) item.ReturningToSource = true;
                await _config.SaveConfigAsync(_config.Config);
                return new(false, $"Aktueller Einsatz von Fahrzeug {loan.VehicleId} nicht verifiziert; keine Rückalarmierung.", loan.SourceMissionId);
            }
            if (current.TargetType == "mission" && current.TargetId != loan.SourceMissionId && current.TargetId != loan.TargetMissionId ||
                loan.RecallForStaffing && loan.StaffingBuildingId > 0 && current.BuildingId != loan.StaffingBuildingId)
            {
                foreach (var item in package) item.ReturningToSource = true;
                _config.Config.VehicleLoans.Remove(loan); await _config.SaveConfigAsync(_config.Config);
                return new(false, "Einheit wurde anderweitig zugeordnet; keine automatische Umleitung aus diesem Einsatz.", loan.SourceMissionId);
            }
            if (loan.ReturningToSource || loan.TargetConfirmed)
            {
                if (loan.TargetConfirmed && target != null && !current.IsAvailable) continue;
                if (loan.TargetConfirmed && loan.RecallForStaffing && target != null) continue;
                var source = board.FirstOrDefault(mission => mission.Id == loan.SourceMissionId);
                if (source == null || !source.NeedsDispatch || current.TargetType == "mission" && current.TargetId == loan.SourceMissionId)
                { _config.Config.VehicleLoans.Remove(loan); await _config.SaveConfigAsync(_config.Config); continue; }
                if (!current.IsAvailable || UtcNow < loan.NextActionAfterUtc) continue;
                loan.ReturningToSource = true;
                await _config.SaveConfigAsync(_config.Config);
                return await ReturnVehicleToSourceAsync(loan, source, token);
            }
            if (current.TargetType == "mission" && current.TargetId == loan.SourceMissionId)
            {
                if (loan.WithdrawalSent)
                {
                    if (UtcNow < loan.NextActionAfterUtc) return new(false, "Rückalarmierung wird noch bestätigt; keine erneute Sendung.", loan.SourceMissionId);
                    foreach (var item in package) item.ReturningToSource = true;
                    await _config.SaveConfigAsync(_config.Config);
                    return new(false, "Rückalarmierung nicht bestätigt; Umverteilung wird zurückgenommen.", loan.SourceMissionId);
                }
                await _dispatchGate.WaitAsync(token);
                try
                {
                    await _pacer.WaitAsync(token);
                    if (!ReassignmentAllowed)
                    {
                        if (package.All(item => !item.WithdrawalSent))
                        { foreach (var item in package) _config.Config.VehicleLoans.Remove(item); await _config.SaveConfigAsync(_config.Config); }
                        return null;
                    }
                    var fleet = await GetVehiclesAsync(true, token);
                    current = await ResolveSceneMissionAsync(await GetVehicleAsync(loan.VehicleId, token), token);
                    foreach (var item in package.Where(item => item.VehicleId != loan.VehicleId))
                    {
                        var index = fleet.FindIndex(vehicle => vehicle.Id == item.VehicleId);
                        if (index >= 0) fleet[index] = await ResolveSceneMissionAsync(fleet[index], token);
                    }
                    var ownIndex = fleet.FindIndex(vehicle => vehicle.Id == current.Id);
                    if (ownIndex >= 0) fleet[ownIndex] = current;
                    var source = board.FirstOrDefault(mission => mission.Id == loan.SourceMissionId);
                    if (source == null || !Borrowable(current, fleet, ownPackage: true) || current.TargetType != "mission" || current.TargetId != loan.SourceMissionId)
                    {
                        foreach (var item in package) item.ReturningToSource = true;
                        await _config.SaveConfigAsync(_config.Config);
                        return new(actionSent, "Ein ausgewähltes Fahrzeug ist nicht mehr verfügbar. Die Einsatzgruppe wird nicht teilweise zurückgerufen.", loan.SourceMissionId);
                    }
                    var sourceDetails = await ParseDetailsWithRefreshAsync(await GetTextAsync($"/missions/{source.Id}", token), source, token);
                    approachSources[source.Id] = sourceDetails;
                    ObserveCrew(source, sourceDetails);
                    var targetDetails = await ParseDetailsWithRefreshAsync(await GetTextAsync($"/missions/{first.TargetMissionId}", token), target!, token);
                    var catalog = await GetCatalogAsync(token, sourceDetails.TypeKey); var sourceSpec = sourceDetails.TypeKey == null ? null : catalog.GetValueOrDefault(sourceDetails.TypeKey);
                    await PrepareApproachesAsync(source, sourceSpec, sourceDetails, fleet, token);
                    if (!KnownBlockedFireMission(sourceDetails) || !KnownShortage(targetDetails) ||
                        CreateVehiclePlanner().Build(source, sourceSpec, sourceDetails, PlanningFleet(fleet), ReservedForMission(source.Id),
                            StationCrewBudgets(fleet, source, sourceDetails)).IsComplete)
                    {
                        foreach (var item in package) item.ReturningToSource = true;
                        await _config.SaveConfigAsync(_config.Config); return null;
                    }
                    loan.KnownCrew = sourceDetails.CrewByVehicleId.GetValueOrDefault(current.Id);
                    var virtualFleet = fleet.Select(vehicle =>
                    {
                        var item = package.FirstOrDefault(item => item.VehicleId == vehicle.Id);
                        if (item == null) return vehicle;
                        var atSource = vehicle.TargetType == "mission" && vehicle.TargetId == item.SourceMissionId;
                        if (item.RecallForStaffing && (atSource || item.WithdrawalSent && vehicle.State == 1 &&
                            vehicle.TargetType == "building" && vehicle.TargetId == item.StaffingBuildingId))
                        {
                            var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<Vehicle>(Newtonsoft.Json.JsonConvert.SerializeObject(vehicle))!;
                            copy.State = 2; copy.TargetType = null; copy.TargetId = null; return copy;
                        }
                        if (!atSource) return vehicle;
                        var known = new MissionDetails(); known.CrewByVehicleId[vehicle.Id] = item.KnownCrew;
                        return SimulatedFreeVehicle(vehicle, known);
                    }).ToArray();
                    var reserved = ReservedForMission(target!.Id);
                    if (loan.RecallForStaffing) reserved.ExceptWith(first.ReservedVehicleIds);
                    var targetSpec = targetDetails.TypeKey == null ? null : catalog.GetValueOrDefault(targetDetails.TypeKey);
                    await PrepareApproachesAsync(target, targetSpec, targetDetails, fleet, token);
                    await PreparePackageApproachesAsync(target, targetSpec, targetDetails, fleet, virtualFleet, package, board, approachSources, token);
                    var transferPlan = CreateVehiclePlanner().Build(target, targetSpec, targetDetails, virtualFleet, reserved,
                        StationCrewBudgets(virtualFleet, target, targetDetails));
                    var existingPlan = CreateVehiclePlanner().Build(target, targetSpec, targetDetails, PlanningFleet(fleet), ReservedForMission(target.Id),
                        StationCrewBudgets(fleet, target, targetDetails));
                    if (loan.KnownCrew <= 0 || !transferPlan.IsComplete || existingPlan.IsComplete ||
                        !loan.RecallForStaffing && transferPlan.SelectedVehicles.All(vehicle => vehicle.Id != loan.VehicleId))
                    {
                        foreach (var item in package) item.ReturningToSource = true;
                        await _config.SaveConfigAsync(_config.Config); return new(actionSent, "Bedarf oder Verfügbarkeit geändert; keine unvollständige Einsatzgruppe umverteilen.", loan.SourceMissionId);
                    }
                    var link = BackalarmLink(await GetTextAsync($"/vehicles/{current.Id}", token), current.Id);
                    if (link == null) return null;
                    if (!ApproachAllowsSend(transferPlan, targetDetails)) return new(actionSent, "Umverteilung wartet: maximale Anfahrt kann nicht eingehalten werden.", loan.SourceMissionId);
                    var homeSeconds = loan.RecallForStaffing ? ApproachLimit > 0 ? await HomeTravelTimeAsync(new(current, source, sourceDetails), token) : double.MaxValue : 0;
                    if (ApproachLimit > 0 && homeSeconds > ApproachLimit * 60)
                        return new(actionSent, "Personalrückruf wartet: Rückfahrt zur Wache überschreitet die maximale Anfahrt oder ist noch unbekannt.", loan.SourceMissionId);
                    loan.WithdrawalSent = true; loan.NextActionAfterUtc = UtcNow.AddMinutes(2);
                    await _config.SaveConfigAsync(_config.Config);
                    using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, link);
                    request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, $"/vehicles/{current.Id}");
                    using var response = await SendAsync(request, token, automaticAction: true, actionAllowed: () => ReassignmentAllowed &&
                        ApproachAllowsSend(transferPlan, targetDetails) && (ApproachLimit == 0 || homeSeconds <= ApproachLimit * 60));
                    actionSent = true; CheckAuthentication(response);
                    Logger?.Write(LogLevel.Info, "Reassignment", "withdraw.sent", $"Rückalarmierung: {current.Name} (#{current.Id}) aus Einsatz {loan.SourceMissionId}" +
                        (loan.RecallForStaffing ? $" zur eigenen Wache für Personal in Einsatz {loan.TargetMissionId}." : $" für Einsatz {loan.TargetMissionId}.") + " Zuordnung wird geprüft.");
                    if ((int)response.StatusCode is not (200 or 302 or 303)) return new(true, "Rückalarmierung wartet auf Bestätigung.", loan.SourceMissionId);
                    current = await GetVehicleAsync(current.Id, token);
                    if (current.TargetType == "mission") return new(true, "Rückalarmierung wartet auf Bestätigung.", loan.SourceMissionId);
                    loan.NextActionAfterUtc = DateTime.MinValue;
                    await _config.SaveConfigAsync(_config.Config);
                }
                catch (AutomaticActionPausedException)
                {
                    loan.WithdrawalSent = false; loan.NextActionAfterUtc = DateTime.MinValue;
                    await _config.SaveConfigAsync(_config.Config); return new(actionSent, "Umverteilung pausiert; keine weitere Rückalarmierung.", loan.SourceMissionId);
                }
                finally { _dispatchGate.Release(); }
            }
            if (loan.RecallForStaffing && (current.State != 2 || !string.IsNullOrEmpty(current.TargetType)))
            { waitingForStaff = true; continue; }
            if (!loan.RecallForStaffing && !current.IsAvailable && !(current.TargetType == "mission" && current.TargetId == first.TargetMissionId))
                return new(actionSent, "Umverteilung wartet auf verfügbare Fahrzeuge.", loan.SourceMissionId);
        }
        if (waitingForStaff) return new(actionSent, "Personal kehrt zur Wache zurück; Freigabe erst nach bestätigter Ankunft.", first.SourceMissionId);
        if (package.Any(loan => loan.ReturningToSource || loan.TargetConfirmed) || target == null) return null;
        var dispatch = await DispatchCoreAsync(target, false, token, true, requireReassignment: true, requireComplete: true);
        foreach (var loan in package)
        {
            loan.SentToTarget |= dispatch.Sent && dispatch.Plan?.SelectedVehicles.Any(vehicle => vehicle.Id == loan.VehicleId) == true;
            loan.TargetConfirmed = dispatch.Success;
        }
        await _config.SaveConfigAsync(_config.Config);
        return new(actionSent || dispatch.Sent, $"Kräfte für Einsatz {target.Id} zusammengeführt. " + dispatch.Message, first.SourceMissionId, dispatch);
    }
}
