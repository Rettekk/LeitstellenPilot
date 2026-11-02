using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private sealed record TrainedTrip(long MissionId,int TypeId,long BuildingId,Dictionary<string,int> Crew);
    private readonly ConcurrentDictionary<long,TrainedTrip> _trainedTrips = new();
    private sealed record TrainingRoster(DateTime ReadAt, string SessionId, string Layout,
        HashSet<(long Id, int State)> AvailableVehicles, IReadOnlyList<StationPerson> People);
    private readonly ConcurrentDictionary<long, TrainingRoster> _trainingRosters = new();

    private async Task<IReadOnlyList<StationPerson>> ReadTrainingRosterAsync(Vehicle vehicle,
        IReadOnlyList<Vehicle> fleet, bool forceRefresh, CancellationToken token)
    {
        var session = _config.Session.SessionId;
        var total = _buildings?.FirstOrDefault(building => building.Value<long?>("id") == vehicle.BuildingId)?.Value<int?>("personal_count");
        var station = fleet.Where(candidate => candidate.BuildingId == vehicle.BuildingId).OrderBy(candidate => candidate.Id).ToArray();
        var layout = total + "|" + string.Join(";", station.Select(candidate =>
            $"{candidate.Id}:{candidate.TypeId}:{candidate.Personnel}:{candidate.MaximumPersonnel}"));
        var available = station.Where(candidate => candidate.IsAvailable).Select(candidate => (candidate.Id, candidate.State)).ToHashSet();
        if (!forceRefresh && _trainingRosters.TryGetValue(vehicle.BuildingId, out var cached) &&
            cached.SessionId == session && cached.Layout == layout && UtcNow >= cached.ReadAt && UtcNow - cached.ReadAt < TimeSpan.FromMinutes(2) &&
            available.IsSubsetOf(cached.AvailableVehicles))
            return cached.People;

        var page = PersonnelPageParser.Read(await GetTextAsync($"/vehicles/{vehicle.Id}/zuweisung", token),
            vehicle.Id, vehicle.BuildingId, _httpClient.BaseAddress!);
        if (!string.IsNullOrEmpty(page.Token)) Logger?.RegisterSecret(page.Token);
        if (total.HasValue && total != page.People.Count)
            throw new FormatException("Personalliste für die Ausbildungsprüfung unvollständig; keine Ausbildungsbesatzung geschätzt.");
        var people = page.People.ToArray();
        _trainingRosters[vehicle.BuildingId] = new(UtcNow, session, layout, available, people);
        return people;
    }

    private void ForgetEndedTrainedTrips(IReadOnlyCollection<Vehicle> fleet)
    {
        var current=fleet.ToDictionary(vehicle=>vehicle.Id);
        foreach (var pair in _trainedTrips)
            if (!current.TryGetValue(pair.Key,out var vehicle) || vehicle.IsAvailable || vehicle.TargetType!="mission" ||
                vehicle.TargetId!=pair.Value.MissionId || vehicle.TypeId!=pair.Value.TypeId || vehicle.BuildingId!=pair.Value.BuildingId)
                _trainedTrips.TryRemove(pair.Key,out _);
    }

    private void RememberConfirmedTraining(long missionId,MissionDetails details,IReadOnlyCollection<Vehicle> fleet,ISet<long> accepted)
    {
        foreach (var vehicle in fleet.Where(vehicle=>accepted.Contains(vehicle.Id)))
            if (details.TrainedCrewByVehicleId.TryGetValue(vehicle.Id,out var training) && training.Any(pair=>pair.Value>0))
                _trainedTrips[vehicle.Id]=new(missionId,vehicle.TypeId,vehicle.BuildingId,new(training));
    }

    private async Task ReadTrainedPersonnelAsync(Mission mission, EinsatzInfo? specification, MissionDetails details,
        IReadOnlyList<Vehicle> fleet, CancellationToken token, bool forceRefresh = true)
    {
        var demands = TrainedPersonnelRequirements.Demands(details, specification, _rules);
        if (demands.Count == 0) return;
        if (_buildings == null) await GetBuildingsAsync(token);
        var missingOnly = details.HasMissingRequirements && details.MissingRequirements.Count > 0;
        var rules = demands.Keys.Select(key => _rules.Trainings[key]).ToArray();
        bool Preferred(Vehicle vehicle) => rules.Any(rule => rule.VehicleTypes.Contains(vehicle.TypeId));
        bool CanCarry(TrainedPersonnelRule rule, Vehicle vehicle) => rule.VehicleTypes.Contains(vehicle.TypeId) ||
            rule.AllowOtherCarriers && _vehicleTypes.TryGetValue(vehicle.TypeId.ToString(), out var metadata) &&
            metadata.Value<int?>("maxPersonnel") > 0 && metadata["possibleBuildings"] is JArray buildings && buildings.Count > 0 &&
            buildings.Values<int>().Any(rule.CarrierBuildingTypes.Contains);
        var incoming = fleet.Where(v => missingOnly ? details.IsEnRouteTo(v,mission.Id) :
                v.TargetType == "mission" && v.TargetId == mission.Id || details.AssignedVehicleIds.Contains(v.Id)).ToArray();
        foreach (var vehicle in incoming)
            if (_trainedTrips.TryGetValue(vehicle.Id,out var trip) && trip.MissionId==mission.Id && trip.TypeId==vehicle.TypeId && trip.BuildingId==vehicle.BuildingId)
                details.TrainedCrewByVehicleId[vehicle.Id]=trip.Crew.ToDictionary(pair=>pair.Key,pair=>
                    details.CrewByVehicleId.TryGetValue(vehicle.Id,out var actual)?Math.Min(pair.Value,actual):pair.Value);
        foreach (var vehicle in incoming.Where(v => v.State == 3 && v.TargetType == "mission" && v.TargetId == mission.Id &&
                     !details.CrewByVehicleId.ContainsKey(v.Id) && !details.TrainedCrewByVehicleId.ContainsKey(v.Id)))
        {
            // Departure of these dedicated vehicles proves at least one qualified
            // crew member. Do not mistake a missing driving table for an empty car.
            var courses = rules.Where(rule => rule.MinimumQualifiedByType.GetValueOrDefault(vehicle.TypeId) > 0)
                .ToDictionary(rule => rule.Course, rule => rule.MinimumQualifiedByType[vehicle.TypeId]);
            if (courses.Count > 0) details.TrainedCrewByVehicleId[vehicle.Id] = courses;
        }
        var reserved = ReservedForMission(mission.Id);
        var candidates = fleet.Where(v => v.IsAvailable && !reserved.Contains(v.Id) && !details.DisabledVehicleIds.Contains(v.Id) &&
                rules.Any(rule => CanCarry(rule, v)))
            .OrderByDescending(Preferred).ThenBy(v => details.VehicleTravelSeconds.GetValueOrDefault(v.Id, int.MaxValue)).ThenBy(v => v.Order).ThenBy(v => v.Id).ToArray();
        bool Covered() => demands.All(demand => incoming.Concat(candidates.Where(v => details.TrainedCrewByVehicleId.ContainsKey(v.Id))).DistinctBy(v => v.Id)
            .Sum(v => TrainedPersonnelRequirements.ConfirmedCrew(demand.Key, v, details, _rules)) >= demand.Value);
        var pages=new Dictionary<long,IReadOnlyList<StationPerson>>();
        async Task<IReadOnlyList<StationPerson>> ReadStation(Vehicle vehicle)
        {
            if (pages.TryGetValue(vehicle.BuildingId,out var cached)) return cached;
            return pages[vehicle.BuildingId]=await ReadTrainingRosterAsync(vehicle, fleet, forceRefresh, token);
        }
        foreach (var vehicle in incoming.Where(vehicle=>rules.Any(rule=>CanCarry(rule,vehicle))))
        {
            if (Covered()) break;
            var applicable=demands.Where(demand=>CanCarry(_rules.Trainings[demand.Key],vehicle)).ToArray();
            if (applicable.All(demand=>TrainedPersonnelRequirements.ConfirmedCrew(demand.Key,vehicle,details,_rules)>=demand.Value)) continue;
            var page=await ReadStation(vehicle);
            var bound=page.Where(person=>person.VehicleId==vehicle.Id).ToArray();
            var hasActual=details.CrewByVehicleId.TryGetValue(vehicle.Id,out var actual);
            var maximum=Math.Max(0,hasActual?actual:vehicle.MaximumPersonnel??_vehicleTypes[vehicle.TypeId.ToString()].Value<int?>("maxPersonnel")??0);
            var confirmed=new Dictionary<string,int>(); var uncertain=new Dictionary<string,int>();
            foreach (var demand in applicable)
            {
                var rule=_rules.Trainings[demand.Key];
                var previous=TrainedPersonnelRequirements.ConfirmedCrew(demand.Key,vehicle,details,_rules);
                var qualified=bound.Count(person=>person.Available && person.Training.Contains(rule.Course));
                // Bindings do not identify which subset boarded. An unavailable
                // bound specialist might be travelling or attending a course.
                var lower=hasActual?Math.Max(0,Math.Min(maximum,bound.Length)-(bound.Length-qualified)):0;
                lower=Math.Min(maximum,Math.Max(previous,lower));
                var upper=hasActual?Math.Min(maximum,bound.Count(person=>person.Training.Contains(rule.Course))+Math.Max(0,maximum-bound.Length)):maximum;
                confirmed[rule.Course]=lower;
                uncertain[rule.Course]=Math.Max(0,upper-lower);
            }
            details.TrainedCrewByVehicleId[vehicle.Id]=confirmed;
            details.UnconfirmedTrainedCrewByVehicleId[vehicle.Id]=uncertain;
            if (hasActual) RememberConfirmedTraining(mission.Id,details,[vehicle],new HashSet<long>{vehicle.Id});
        }
        foreach (var group in candidates.GroupBy(v => v.BuildingId))
        {
            if (Covered()) break;
            var page = await ReadStation(group.First());
            var free = page.Where(p => p.Available && p.VehicleId == null).ToDictionary(p => p.Id);
            foreach (var vehicle in group)
            {
                var maximum = Math.Max(0, vehicle.MaximumPersonnel ?? _vehicleTypes[vehicle.TypeId.ToString()].Value<int?>("maxPersonnel") ?? 0);
                var counts = new Dictionary<string, int>();
                foreach (var rule in rules.Where(r => CanCarry(r, vehicle)))
                {
                    var boundPeople = page.Where(p => p.Available && p.VehicleId == vehicle.Id).ToArray();
                    var bound = boundPeople.Count(p => p.Training.Contains(rule.Course));
                    if (!rule.RequiresEveryone(vehicle.TypeId))
                    {
                        // A crew limit can leave any of the bound people behind. Count
                        // only the qualification guaranteed even if untrained staff board first.
                        counts[rule.Course] = Math.Max(0, Math.Min(maximum, boundPeople.Length) - (boundPeople.Length - bound));
                        continue;
                    }
                    // Unbound specialists are shared once across automatic crews at this station.
                    // Never count personnel bound to a different vehicle or attending a course.
                    var automatic = vehicle.Personnel == 0 ? free.Values.Where(p => p.Training.Contains(rule.Course)).Take(maximum).ToArray() : [];
                    foreach (var person in automatic) free.Remove(person.Id);
                    counts[rule.Course] = Math.Min(maximum, bound + automatic.Length);
                }
                details.TrainedCrewByVehicleId[vehicle.Id] = counts;
            }
        }
    }
}
