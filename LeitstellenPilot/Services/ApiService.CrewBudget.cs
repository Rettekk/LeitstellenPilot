using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LeitstellenPilot.Models;
using Newtonsoft.Json;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private readonly ConcurrentDictionary<long, (long Mission, int Count, DateTime Seen)> _observedCrew = new();
    private long _crewObservationVersion;
    private void ObserveCrew(Mission mission, MissionDetails details)
    {
        foreach (var (id, count) in details.CrewByVehicleId)
        {
            if (!_observedCrew.TryGetValue(id, out var previous) || previous.Mission != mission.Id || previous.Count != count ||
                UtcNow - previous.Seen >= TimeSpan.FromMinutes(5)) System.Threading.Interlocked.Increment(ref _crewObservationVersion);
            _observedCrew[id] = (mission.Id, count, UtcNow);
        }
        foreach (var pair in _observedCrew.Where(pair => UtcNow - pair.Value.Seen > TimeSpan.FromMinutes(5))) _observedCrew.TryRemove(pair.Key, out _);
    }
    private int KnownOrMaximumCrew(Vehicle vehicle)
    {
        if (_observedCrew.TryGetValue(vehicle.Id, out var observed) && UtcNow - observed.Seen < TimeSpan.FromMinutes(5) &&
            (vehicle.TargetId == observed.Mission && vehicle.TargetType == "mission" || vehicle.State == 1 ||
                vehicle.State == 4 && string.IsNullOrEmpty(vehicle.TargetType))) return observed.Count;
        var loan = OwnLoans.FirstOrDefault(loan => loan.VehicleId == vehicle.Id && loan.KnownCrew > 0);
        return loan?.KnownCrew ?? Math.Max(0, vehicle.MaximumPersonnel ?? _vehicleTypes.GetValueOrDefault(vehicle.TypeId.ToString())?.Value<int?>("maxPersonnel") ?? 1);
    }
    private Dictionary<long, int> StationCrewBudgets(IReadOnlyCollection<Vehicle> fleet, Mission mission, MissionDetails details)
    {
        ObserveCrew(mission, details);
        var budgets = new Dictionary<long, int>();
        if (_buildings == null) return budgets;
        foreach (var station in _buildings)
        {
            if (!long.TryParse(station["id"]?.ToString(), out var id) || !int.TryParse(station["personal_count"]?.ToString(), out var count) || count < 0) continue;
            var vehicles = fleet.Where(vehicle => vehicle.BuildingId == id).ToArray();
            var bound = vehicles.Sum(vehicle => Math.Max(0, vehicle.Personnel));
            var busyAutomatic = vehicles.Where(vehicle => vehicle.Personnel == 0 && (vehicle.State is 1 or 3 or 4 or 5 or 7 ||
                vehicle.QueuedMissionId > 0 || vehicle.TargetType == "mission")).Sum(KnownOrMaximumCrew);
            budgets[id] = Math.Max(0, count - bound - busyAutomatic);
        }
        return budgets;
    }
    private IReadOnlyCollection<Vehicle> PlanningFleet(IReadOnlyCollection<Vehicle> fleet) => fleet.Select(vehicle =>
    {
        if (vehicle.State != 1 || vehicle.Personnel > 0) return vehicle;
        var copy = JsonConvert.DeserializeObject<Vehicle>(JsonConvert.SerializeObject(vehicle))!;
        var known = _observedCrew.TryGetValue(vehicle.Id, out var observed) && UtcNow - observed.Seen < TimeSpan.FromMinutes(5) ? observed.Count :
            OwnLoans.FirstOrDefault(loan => loan.VehicleId == vehicle.Id)?.KnownCrew;
        copy.Personnel = known ?? _vehicleTypes.GetValueOrDefault(vehicle.TypeId.ToString())?.Value<int?>("minPersonnel") ?? 1;
        copy.MaximumPersonnel = copy.Personnel;
        return copy;
    }).ToArray();
}
