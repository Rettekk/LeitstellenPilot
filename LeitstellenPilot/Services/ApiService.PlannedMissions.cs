using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private sealed class PlannedAlarm
    {
        public DateTime Start, NextReview, ReserveFrom, DepartAt;
        public long[] VehicleIds = [];
        public string RequirementSignature = "";
        public string Status = "";
        public bool HasAssignedVehicles;
        public bool HasStartTime;
        public bool WasReserved;
        public int DurationLimitMinutes = -1;
        public int ApproachLimitMinutes = -1;
        public string TypeKey = "";
        public bool DurationExcluded;
    }
    private readonly Dictionary<long, PlannedAlarm> _plannedAlarms = new();
    public int ReservedPlannedSlots => _config.Config.BotSettings.AutoPlannedMissions
        ? _plannedAlarms.Values.Count(plan => plan.VehicleIds.Length > 0 && PlannedReservationAllowed(plan)) : 0;
    private IEnumerable<long> PlannedReservations(long missionId) => _config.Config.BotSettings.AutoPlannedMissions
        ? _plannedAlarms.Where(pair => pair.Key != missionId && PlannedReservationAllowed(pair.Value)).SelectMany(pair => pair.Value.VehicleIds).ToArray() : [];

    private bool PlannedReservationAllowed(PlannedAlarm alarm) => !alarm.DurationExcluded && alarm.ApproachLimitMinutes == ApproachLimit &&
        PlannedMissionPolicy.DurationExclusionReason(_catalog.GetValueOrDefault(alarm.TypeKey), _config.Config.BotSettings) == null;

    private string? PlannedAutomationWait(Mission mission, EinsatzInfo? specification) => !PlannedMissionPolicy.IsPlanned(mission, specification) ? null :
        !_config.Config.BotSettings.AutoPlannedMissions ? "Geplanter Einsatz · Automatik ausgeschaltet." :
        PlannedMissionPolicy.DurationExclusionReason(specification, _config.Config.BotSettings);

    private void SkipPlannedMission(Mission mission, EinsatzInfo? specification, string reason)
    {
        if (!_plannedAlarms.TryGetValue(mission.Id, out var alarm))
            _plannedAlarms[mission.Id] = alarm = new() { Start = mission.PlannedStartAt ?? UtcNow, HasStartTime = mission.PlannedStartAt.HasValue };
        var changed = alarm.Status != reason;
        alarm.VehicleIds = []; alarm.DurationExcluded = true; alarm.Status = reason;
        alarm.TypeKey = specification?.Id ?? mission.TypeKey ?? "";
        alarm.DurationLimitMinutes = PlannedMissionPolicy.NormalizeDurationLimit(_config.Config.BotSettings.PlannedMaxDurationMinutes);
        alarm.RequirementSignature = mission.CoverageSignature; alarm.NextReview = UtcNow.AddMinutes(5);
        mission.ScheduledStatus = reason; mission.NextScheduledReviewAt = alarm.NextReview;
        if (changed) Logger?.Write(LogLevel.Info, "Scheduled", "duration.skipped", reason);
    }
    private string ServiceOf(Vehicle vehicle)
    {
        if (!_vehicleTypes.TryGetValue(vehicle.TypeId.ToString(), out var type)) return "unknown";
        var buildings = type["possibleBuildings"]?.ToObject<int[]>() ?? [];
        if (buildings.Length > 0 && buildings.All(building => building is 0 or 18)) return "fire";
        if (buildings.Length > 0 && buildings.All(building => building is 6 or 11 or 13 or 17 or 19 or 22 or 24 or 29)) return "police";
        if (buildings.Length > 0 && buildings.All(building => building == 9)) return "thw";
        if (vehicle.TypeId is 28 or 29 or 31 or 38 or 55 or 56 or 58 or 59 or 60 or 73 or 74 or 97) return "rescue";
        return "type:" + vehicle.TypeId;
    }
    private bool PlannedBudgetAllows(DispatchPlan plan, MissionDetails details, IReadOnlyList<Vehicle> fleet, Mission mission)
    {
        var settings = _config.Config.BotSettings;
        var used = plan.SelectedVehicles.Concat(fleet.Where(vehicle => details.AssignedVehicleIds.Contains(vehicle.Id) ||
            vehicle.TargetType == "mission" && vehicle.TargetId == mission.Id)).ToArray();
        foreach (var service in used.Select(ServiceOf).Distinct())
            if (!PlannedMissionPolicy.HasSpareCapacity(plan, details, fleet, mission, settings, vehicle => ServiceOf(vehicle) == service)) return false;
        foreach (var key in plan.Requirements.Select(demand => demand.Key).Where(key => !key.StartsWith("_personnel_") && _rules.Types.ContainsKey(key)))
            if (!PlannedMissionPolicy.HasSpareCapacity(plan, details, fleet, mission, settings, vehicle => _rules.Types[key].Contains(vehicle.TypeId))) return false;
        return true;
    }
    private HashSet<long> PlannedReserveIds(Mission mission, EinsatzInfo? specification, MissionDetails details, IReadOnlyList<Vehicle> fleet)
    {
        var reserved = ReservedForMission(mission.Id);
        var keys = details.HasMissingRequirements && details.MissingRequirements.Count > 0
            ? details.MissingRequirements.Select(demand => demand.Key) : specification?.Requirements.Keys ?? Enumerable.Empty<string>();
        foreach (var key in keys.Distinct().Where(key => !key.StartsWith("_personnel_") && _rules.Types.ContainsKey(key)))
        {
            var types = _rules.Types[key];
            var owned = fleet.Where(vehicle => types.Contains(vehicle.TypeId) && vehicle.State != 6).ToArray();
            var alreadyAssigned = owned.Count(vehicle => details.AssignedVehicleIds.Contains(vehicle.Id) || vehicle.TargetType == "mission" && vehicle.TargetId == mission.Id);
            var allowance = Math.Max(0, owned.Length * (100 - Math.Clamp(_config.Config.BotSettings.PlannedFleetReservePercent, 0, 90)) / 100 - alreadyAssigned);
            var candidates = owned.Where(vehicle => vehicle.IsAvailable && !reserved.Contains(vehicle.Id) && !details.DisabledVehicleIds.Contains(vehicle.Id))
                .OrderBy(vehicle => details.VehicleTravelSeconds.GetValueOrDefault(vehicle.Id, int.MaxValue)).ThenBy(vehicle => vehicle.Order).ThenBy(vehicle => vehicle.Id);
            foreach (var vehicle in candidates.Skip(allowance)) reserved.Add(vehicle.Id);
        }
        return reserved;
    }
    private DispatchPlan BuildPlannedPlan(Mission mission, EinsatzInfo? specification, MissionDetails details, IReadOnlyList<Vehicle> fleet, out bool reserveLimited)
    {
        var planner = CreateVehiclePlanner();
        var budget = StationCrewBudgets(fleet, mission, details);
        var planningFleet = PlanningFleet(fleet).ToArray();
        var unrestricted = planner.Build(mission, specification, details, planningFleet, ReservedForMission(mission.Id), budget);
        AddStaffingWait(unrestricted, fleet);
        if (PlannedBudgetAllows(unrestricted, details, fleet, mission))
        { reserveLimited = false; return unrestricted; }
        var constrained = planner.Build(mission, specification, details, planningFleet, PlannedReserveIds(mission, specification, details, fleet), budget);
        AddStaffingWait(constrained, fleet);
        reserveLimited = !constrained.IsComplete && unrestricted.IsComplete;
        return constrained;
    }
    private string? CheckPlannedDispatch(Mission mission, DispatchPlan plan, MissionDetails details, IReadOnlyList<Vehicle> fleet)
    {
        if (!PlannedMissionPolicy.CanArriveBeforeEnd(mission, plan, details, _config.Config.BotSettings, UtcNow))
            return "Geplanter Einsatz: Fahrzeuge würden erst nach dem bekannten Ende ankommen.";
        if (!PlannedBudgetAllows(plan, details, fleet, mission)) return "Geplanter Einsatz: Reserve für normale Einsätze ist inzwischen nicht ausreichend.";
        if (mission.PlannedStartAt is not { } start) return null;
        var timing = PlannedMissionPolicy.Timing(start, plan, details, _config.Config.BotSettings);
        if (UtcNow >= timing.DepartAt) return null;
        if (_plannedAlarms.TryGetValue(mission.Id, out var alarm))
        {
            alarm.DepartAt = timing.DepartAt; alarm.ReserveFrom = timing.ReserveFrom;
            alarm.VehicleIds = UtcNow >= timing.ReserveFrom ? plan.SelectedVehicles.Select(vehicle => vehicle.Id).ToArray() : [];
        }
        return $"Geplanter Einsatz: aktuelle Anfahrt neu berechnet; Alarmierung erst {timing.DepartAt.ToLocalTime():HH:mm}.";
    }

    public async Task<PlannedMissionResult?> PlanScheduledMissionsAsync(IReadOnlyList<Mission> board, int occupiedSlots, CancellationToken token = default)
    {
        EnsureLoggedIn();
        var settings = _config.Config.BotSettings;
        if (!settings.AutoPlannedMissions || !settings.AutoAlarm)
        {
            _plannedAlarms.Clear();
            foreach (var mission in board.Where(mission => mission.sw)) mission.ScheduledStatus = "Geplanter Einsatz · Automatik ausgeschaltet.";
            return null;
        }
        foreach (var removed in _plannedAlarms.Keys.Where(id => board.All(mission => mission.Id != id || mission.FleetBlockReason.Length > 0)).ToArray()) _plannedAlarms.Remove(removed);
        if (AutomaticActionsAllowed?.Invoke() == false) return null;
        PlannedMissionResult? covered = null;
        foreach (var mission in board.Where(mission => mission.sw && mission.FleetBlockReason.Length == 0)
            .OrderBy(mission => mission.PlannedStartAt ?? DateTime.MinValue).ThenByDescending(mission => mission.average_credits))
        {
            token.ThrowIfCancellationRequested();
            var durationLimit = PlannedMissionPolicy.NormalizeDurationLimit(settings.PlannedMaxDurationMinutes);
            var cachedSpec = _catalog.GetValueOrDefault(mission.TypeKey ?? "");
            var cachedExclusion = cachedSpec == null ? null : PlannedMissionPolicy.DurationExclusionReason(cachedSpec, settings);
            if (_plannedAlarms.TryGetValue(mission.Id, out var excluded) && excluded.DurationExcluded && excluded.NextReview > UtcNow &&
                excluded.DurationLimitMinutes == durationLimit && excluded.RequirementSignature == mission.CoverageSignature &&
                (cachedSpec == null || cachedExclusion == excluded.Status))
            { mission.ScheduledStatus = excluded.Status; mission.NextScheduledReviewAt = excluded.NextReview; continue; }
            if (cachedExclusion != null) { SkipPlannedMission(mission, cachedSpec, cachedExclusion); continue; }
            var start = mission.PlannedStartAt ?? (_plannedAlarms.TryGetValue(mission.Id, out var previous) && previous.Start < UtcNow ? previous.Start : UtcNow);
            if (mission.sw_start_in > 0 && !mission.PlannedStartAt.HasValue)
            { mission.ScheduledStatus = "Geplanter Einsatz · Startzeit nicht eindeutig erkannt."; mission.NextScheduledReviewAt = UtcNow.AddMinutes(5); continue; }
            if (start - UtcNow > TimeSpan.FromHours(1))
            { mission.ScheduledStatus = $"Geplant: Beginn {start.ToLocalTime():HH:mm} · Planung ab einer Stunde vorher."; mission.NextScheduledReviewAt = start.AddHours(-1); continue; }
            if (!_plannedAlarms.TryGetValue(mission.Id, out var alarm))
                _plannedAlarms[mission.Id] = alarm = new() { Start = start, HasStartTime = mission.PlannedStartAt.HasValue };
            if (Math.Abs((alarm.Start - start).TotalSeconds) > 5 && mission.sw_start_in > 0)
            { alarm.NextReview = DateTime.MinValue; alarm.VehicleIds = []; alarm.Start = start; alarm.HasStartTime = true; }
            var startText = alarm.HasStartTime ? "Beginn " + alarm.Start.ToLocalTime().ToString("HH:mm") : "läuft bereits";
            var fleet = await GetVehiclesAsync(token: token);
            var reservationLost = alarm.VehicleIds.Any(id => !fleet.Any(vehicle => vehicle.Id == id && vehicle.IsAvailable));
            if (!alarm.DurationExcluded && alarm.DurationLimitMinutes == durationLimit && alarm.ApproachLimitMinutes == ApproachLimit && alarm.NextReview > UtcNow &&
                alarm.RequirementSignature == mission.CoverageSignature && !reservationLost)
            { mission.ScheduledStatus = alarm.Status; mission.NextScheduledReviewAt = alarm.NextReview; continue; }
            var previouslyReserved = alarm.VehicleIds.Length > 0 || alarm.HasAssignedVehicles;
            alarm.WasReserved |= previouslyReserved;
            alarm.VehicleIds = [];
            var catalog = await GetCatalogAsync(token);
            var details = await ParseDetailsWithRefreshAsync(await GetTextAsync($"/missions/{mission.Id}", token), mission, token);
            if (details.TypeKey != null && !catalog.ContainsKey(details.TypeKey)) catalog = await GetCatalogAsync(token, details.TypeKey);
            ObserveStaffing(details.VehicleStaffingProblems, fleet);
            foreach (var vehicle in fleet) vehicle.Order = details.VehicleOrder.GetValueOrDefault(vehicle.Id, int.MaxValue);
            var spec = details.TypeKey != null ? catalog.GetValueOrDefault(details.TypeKey) : null;
            if (PlannedMissionPolicy.DurationExclusionReason(spec, settings) is { } durationWait)
            { SkipPlannedMission(mission, spec, durationWait); continue; }
            alarm.DurationExcluded = false; alarm.DurationLimitMinutes = durationLimit; alarm.TypeKey = spec?.Id ?? details.TypeKey ?? "";
            await PrepareApproachesAsync(mission, spec, details, fleet, token);
            alarm.ApproachLimitMinutes = ApproachLimit;
            var plan = BuildPlannedPlan(mission, spec, details, fleet, out var reserveLimited);
            if (!PlannedMissionPolicy.CanArriveBeforeEnd(mission, plan, details, settings, UtcNow))
            {
                alarm.Status = "Geplanter Einsatz: Anfahrt länger als verbleibende Laufzeit; keine weitere Alarmierung.";
                alarm.NextReview = UtcNow.AddMinutes(5);
                mission.ScheduledStatus = alarm.Status; mission.NextScheduledReviewAt = alarm.NextReview;
                continue;
            }
            alarm.HasAssignedVehicles = details.AssignedVehicleIds.Any(id => fleet.Any(vehicle => vehicle.Id == id));
            var timing = PlannedMissionPolicy.Timing(alarm.Start, plan, details, settings);
            alarm.ReserveFrom = timing.ReserveFrom; alarm.DepartAt = timing.DepartAt; alarm.RequirementSignature = mission.CoverageSignature;
            alarm.NextReview = UtcNow.AddMinutes(5);
            if (mission.vehicle_state > 0 || UtcNow >= timing.ReserveFrom) alarm.NextReview = UtcNow.AddMinutes(1);
            if (timing.ReserveFrom > UtcNow) alarm.NextReview = new[] { alarm.NextReview, timing.ReserveFrom }.Min();
            if (timing.DepartAt > UtcNow) alarm.NextReview = new[] { alarm.NextReview, timing.DepartAt }.Min();
            mission.NextScheduledReviewAt = alarm.NextReview;
            if (plan.IsComplete && plan.SelectedVehicles.Count == 0)
            {
                alarm.Status = mission.sw_start_in > 0 ? $"Geplant: Verstärkung bereits eingeplant · {startText}." : "Geplanter Einsatz: Anforderungen abgedeckt.";
                mission.ScheduledStatus = alarm.Status;
                covered ??= new(mission.Id, new(true, false, alarm.Status, plan));
                continue;
            }
            var activePlannedIds = board.Where(other => other.sw && other.Id != mission.Id && (other.vehicle_state > 0 ||
                fleet.Any(vehicle => vehicle.TargetType == "mission" && vehicle.TargetId == other.Id))).Select(other => other.Id)
                .Concat(_plannedAlarms.Where(pair => pair.Key != mission.Id &&
                    (pair.Value.HasAssignedVehicles || pair.Value.VehicleIds.Length > 0 && PlannedReservationAllowed(pair.Value))).Select(pair => pair.Key)).ToHashSet();
            var anotherActive = activePlannedIds.Count;
            var slotsAvailable = mission.vehicle_state > 0 || occupiedSlots + ReservedPlannedSlots < Math.Clamp(settings.MaxConcurrentMissions, 1, 100);
            var plannedSlotAvailable = mission.vehicle_state > 0 || anotherActive < Math.Clamp(settings.MaxPlannedMissions, 1, 20);
            var budget = plan.SelectedVehicles.Count > 0 && PlannedBudgetAllows(plan, details, fleet, mission);
            var reservationExpired = !plan.IsComplete && UtcNow > alarm.Start.AddMinutes(5);
            if (UtcNow >= timing.ReserveFrom && slotsAvailable && plannedSlotAvailable && budget && !reserveLimited && !reservationExpired && details.UnparsedRequirements.Count == 0)
                alarm.VehicleIds = plan.SelectedVehicles.Select(vehicle => vehicle.Id).ToArray();
            var issue = reserveLimited ? "Reserve für normale Einsätze nicht ausreichend" : !plan.IsComplete ?
                (plan.ApproachLimited ? $"Bedarf innerhalb der maximalen Anfahrt von {ApproachLimit} Min. noch nicht vollständig verfügbar: " : "Bedarf noch nicht vollständig verfügbar: ") + string.Join("; ", plan.Problems) :
                !slotsAvailable || !plannedSlotAvailable ? "Parallelgrenze erreicht" : !budget ? "Reserve für normale Einsätze nicht ausreichend" : "";
            alarm.Status = $"Geplant: {startText} · " + (issue.Length > 0 ? issue :
                alarm.VehicleIds.Length > 0 ? $"{alarm.VehicleIds.Length} Fahrzeuge reserviert · Alarmierung {timing.DepartAt.ToLocalTime():HH:mm}" :
                $"Reservierung {timing.ReserveFrom.ToLocalTime():HH:mm} · Alarmierung {timing.DepartAt.ToLocalTime():HH:mm}");
            if (!timing.TravelKnown) alarm.Status += " · Fahrzeit teilweise unbekannt, 15 Min. vorsichtig angenommen";
            if (reservationExpired) alarm.Status += " · Start überschritten, unvollständige Reservierung freigegeben";
            mission.ScheduledStatus = alarm.Status;
            Logger?.Write(issue.Length > 0 ? LogLevel.Warning : LogLevel.Info, "Scheduled", "plan", alarm.Status);
            if (UtcNow < timing.DepartAt || !plan.IsComplete || !slotsAvailable || !plannedSlotAvailable || !budget) continue;
            if (AutomaticActionsAllowed?.Invoke() == false || !settings.AutoPlannedMissions || !settings.AutoAlarm) return null;
            var dispatch = await DispatchCoreAsync(mission, false, token, true, requirePlanned: true);
            if (dispatch.Sent || dispatch.Success) alarm.VehicleIds = [];
            alarm.NextReview = UtcNow.AddMinutes(1);
            mission.NextScheduledReviewAt = alarm.NextReview;
            alarm.Status = dispatch.Success ? $"Geplant: Fahrzeuge alarmiert · {startText}." : dispatch.Message;
            mission.ScheduledStatus = alarm.Status;
            return new(mission.Id, dispatch);
        }
        return covered;
    }
}
