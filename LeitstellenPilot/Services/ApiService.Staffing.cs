using System;
using System.Collections.Generic;
using System.Linq;
using LeitstellenPilot.Models;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private sealed record StaffingHold(long BuildingId, string Signature, DateTime Until, string Message, bool AutomaticPool);
    private readonly object _staffingSync = new();
    private readonly Dictionary<long, StaffingHold> _staffingHolds = new();
    private readonly Dictionary<long, string> _seenStaffingNotices = new();

    private string StaffingSignature(Vehicle rejected, IReadOnlyList<Vehicle> fleet)
    {
        var total = _buildings?.FirstOrDefault(building => building.Value<long?>("id") == rejected.BuildingId)?["personal_count"]?.ToString() ?? "?";
        // Arrival at an incident does not release staff. Returning to the station does.
        return total + "|" + rejected.State + "|" + string.Join(";", fleet.Where(vehicle => vehicle.BuildingId == rejected.BuildingId).OrderBy(vehicle => vehicle.Id)
            .Select(vehicle => $"{vehicle.Id}:{vehicle.State == 2 && string.IsNullOrEmpty(vehicle.TargetType)}:{vehicle.Personnel}:{vehicle.MaximumPersonnel}:{vehicle.QueuedMissionId}"));
    }

    private HashSet<long> StaffingReservations()
    {
        lock (_staffingSync)
        {
            foreach (var pair in _staffingHolds.ToArray())
            {
                var vehicle = _inventory.FirstOrDefault(vehicle => vehicle.Id == pair.Key);
                if (vehicle == null || pair.Value.Until <= UtcNow || pair.Value.Signature != StaffingSignature(vehicle, _inventory))
                    _staffingHolds.Remove(pair.Key);
            }
            return _staffingHolds.Keys.Concat(_inventory.Where(vehicle => vehicle.IsAvailable && vehicle.Personnel == 0 &&
                _staffingHolds.Values.Any(hold => hold.AutomaticPool && hold.BuildingId == vehicle.BuildingId)).Select(vehicle => vehicle.Id)).ToHashSet();
        }
    }

    private void ObserveStaffing(IReadOnlyDictionary<long, string> failures, IReadOnlyList<Vehicle> fleet, bool freshResponse = false, ISet<long>? equipmentVehicleIds = null)
    {
        lock (_staffingSync)
        {
            foreach (var (id, notice) in failures)
            {
                var vehicle = fleet.FirstOrDefault(vehicle => vehicle.Id == id);
                if (vehicle == null) continue; // Never reserve another player's vehicle.
                if (!freshResponse && _seenStaffingNotices.GetValueOrDefault(id) == notice) continue;
                _seenStaffingNotices[id] = notice;
                var trainingRequired = !_vehicleTypes.TryGetValue(vehicle.TypeId.ToString(), out var type) || type["schooling"] is { HasValues: true };
                var reason = trainingRequired ? "Personal / passende Ausbildung" : "Personal";
                var caption = _buildings?.FirstOrDefault(building => building.Value<long?>("id") == vehicle.BuildingId)?["caption"]?.ToString();
                var station = string.IsNullOrWhiteSpace(caption) ? $"Wache #{vehicle.BuildingId}" : "Wache " + caption;
                var message = $"Wartet auf {reason}: {vehicle.Name} ({station}). Neue Prüfung bei Änderung auf der Wache, spätestens nach 5 Min.";
                var automaticPool = freshResponse && vehicle.Personnel == 0 && !trainingRequired && equipmentVehicleIds?.Contains(id) == false;
                _staffingHolds[id] = new(vehicle.BuildingId, StaffingSignature(vehicle, fleet), UtcNow.AddMinutes(5), message, automaticPool);
                Logger?.Write(LogLevel.Warning, "Dispatch", "staffing.wait", message);
            }
        }
    }

    private void AddStaffingWait(DispatchPlan plan, IReadOnlyList<Vehicle> fleet)
    {
        if (plan.IsComplete || plan.WaitingForPersonnel.Length > 0) return;
        var blocked = StaffingReservations();
        var relevantTypes = plan.Requirements.Where(demand => _rules.Types.ContainsKey(demand.Key))
            .SelectMany(demand => _rules.Types[demand.Key]).Concat(plan.Requirements.Where(d => _rules.Trainings.ContainsKey(d.Key))
                .SelectMany(d => _rules.Trainings[d.Key].VehicleTypes)).ToHashSet();
        lock (_staffingSync)
        {
            var messages = fleet.Where(vehicle => vehicle.IsAvailable && blocked.Contains(vehicle.Id) && relevantTypes.Contains(vehicle.TypeId))
                .Select(vehicle => _staffingHolds.GetValueOrDefault(vehicle.Id)?.Message ??
                    _staffingHolds.Values.FirstOrDefault(hold => hold.AutomaticPool && hold.BuildingId == vehicle.BuildingId)?.Message)
                .Where(message => message != null).Distinct().Take(3).ToArray();
            if (messages.Length == 0) return;
            plan.WaitingForPersonnel = string.Join(" ", messages);
            plan.Notes.Add(plan.WaitingForPersonnel);
        }
    }

    private void ClearStaffing()
    {
        lock (_staffingSync) { _staffingHolds.Clear(); _seenStaffingNotices.Clear(); }
    }
}
