using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LeitstellenPilot.Models;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed record PlannedTiming(DateTime Start, DateTime ReserveFrom, DateTime DepartAt, bool TravelKnown);
public static class PlannedMissionPolicy
{
    public static int NormalizeDurationLimit(int minutes) => minutes is 0 or 60 or 120 or 180 ? minutes : 60;

    public static bool IsPlanned(Mission mission, EinsatzInfo? specification) => mission.sw ||
        specification?.Additional["guard_mission"] is { Type: JTokenType.Boolean } guard && guard.Value<bool>();

    public static int? DurationSeconds(EinsatzInfo? specification) =>
        specification?.Additional["duration"] is { Type: JTokenType.Integer } duration &&
        int.TryParse(duration.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : null;

    public static string? DurationExclusionReason(EinsatzInfo? specification, BotSettings settings)
    {
        var limit = NormalizeDurationLimit(settings.PlannedMaxDurationMinutes);
        if (limit == 0) return null;
        var duration = DurationSeconds(specification);
        var maximum = FormatDuration(limit * 60);
        if (duration == null) return $"Übersprungen: Einsatzdauer unbekannt · eingestellt maximal {maximum}.";
        return duration > limit * 60
            ? $"Übersprungen: Einsatzdauer {FormatDuration(duration.Value)} · eingestellt maximal {maximum}." : null;
    }

    private static string FormatDuration(int seconds)
    {
        var minutes = (seconds + 59L) / 60; var hours = minutes / 60; var rest = minutes % 60;
        var hourText = hours == 1 ? "1 Stunde" : $"{hours} Stunden";
        var minuteText = rest == 1 ? "1 Minute" : $"{rest} Minuten";
        return hours == 0 ? minuteText : rest == 0 ? hourText : hourText + " " + minuteText;
    }

    public static bool CanArriveBeforeEnd(Mission mission, DispatchPlan plan, MissionDetails details, BotSettings settings, DateTime now)
    {
        if (mission.sw_start_in > 0 || mission.EstimatedCompletion is not { } end) return true;
        var travel = plan.SelectedVehicles.Select(vehicle => details.VehicleTravelSeconds.GetValueOrDefault(vehicle.Id, 900)).DefaultIfEmpty(0).Max();
        return now.AddSeconds(travel + Math.Clamp(settings.MaxActionDelaySeconds, 0, 300)) < end;
    }

    public static PlannedTiming Timing(DateTime start, DispatchPlan plan, MissionDetails details, BotSettings settings)
    {
        var known = plan.SelectedVehicles.Count > 0 && plan.SelectedVehicles.All(vehicle => details.VehicleTravelSeconds.ContainsKey(vehicle.Id));
        var travel = plan.SelectedVehicles.Select(vehicle => details.VehicleTravelSeconds.GetValueOrDefault(vehicle.Id, 900)).DefaultIfEmpty(900).Max();
        var lead = travel + Math.Clamp(settings.PlannedBufferMinutes, 1, 15) * 60 + Math.Clamp(settings.MaxActionDelaySeconds, 0, 300);
        var depart = start.AddSeconds(-lead);
        return new(start, depart.AddMinutes(-5), depart, known);
    }

    public static bool HasSpareCapacity(DispatchPlan plan, MissionDetails details, IReadOnlyList<Vehicle> fleet, Mission mission,
        BotSettings settings, Func<Vehicle, bool> sameService)
    {
        var serviceFleet = fleet.Where(sameService).Where(vehicle => vehicle.State != 6).ToArray();
        if (serviceFleet.Length == 0) return false;
        var assigned = serviceFleet.Count(vehicle => details.AssignedVehicleIds.Contains(vehicle.Id) || vehicle.TargetType == "mission" && vehicle.TargetId == mission.Id);
        var additional = plan.SelectedVehicles.Count(sameService);
        var allowance = serviceFleet.Length * (100 - Math.Clamp(settings.PlannedFleetReservePercent, 0, 90)) / 100;
        return assigned + additional <= allowance;
    }
}
