using System;
using System.Collections.Generic;
using System.Linq;
using LeitstellenPilot.Models;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

// Pure, deterministic planning. No game requests and no dependency on dispatch planning.
public static class PersonnelAllocator
{
    public static PersonnelVehicleRule? ReadRule(Vehicle vehicle, IReadOnlyDictionary<string, JObject> metadata, DispatchRuleCatalog? catalog = null)
    {
        if (!metadata.TryGetValue(vehicle.TypeId.ToString(), out var type) ||
            type.Value<int?>("minPersonnel") is not { } min || type.Value<int?>("maxPersonnel") is not { } max || min < 0 || max < min) return null;
        var requirements = new Dictionary<string, CrewTraining>(StringComparer.Ordinal);
        if (type["staff"]?["training"] is JObject schools)
            foreach (var school in schools.Properties())
            {
                if (school.Value is not JObject courses) return null;
                foreach (var course in courses.Properties())
                {
                    if (course.Value is not JObject rule || rule.Value<bool?>("all") != true && rule.Value<int?>("min") == null) return null;
                    var training = new CrewTraining(course.Name, rule.Value<int?>("min") ?? min, rule.Value<bool?>("all") == true);
                    // Schools are alternatives for the same qualification, not additional people.
                    if (requirements.TryGetValue(course.Name, out var previous) && previous != training) return null;
                    requirements[course.Name] = training;
                }
            }
        else if (type["schooling"] is { HasValues: true }) return null;

        if (max == 0) return new(vehicle, 0, 0, requirements.Values.ToArray());
        min = Math.Max(1, min);
        if (type["staff"]?.Value<int?>("trainingAtScene") is > 0 and var scene && requirements.Count == 1 && scene <= max)
        {
            min = Math.Max(min, scene);
            var requirement = requirements.Single().Value;
            requirements[requirement.Key] = requirement with { Minimum = Math.Max(requirement.Minimum, scene), Everyone = requirement.Everyone || scene == max };
        }
        // Reserve the functional crew at the station as well as at the incident.
        if ((catalog ?? DispatchRuleCatalog.Bundled).CrewOverrides[vehicle.TypeId.ToString()] is JObject custom)
        {
            min = custom.Value<int?>("minimum") ?? min; max = custom.Value<int?>("maximum") ?? max;
            foreach (var course in (custom["training"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                requirements[course.Name] = new(course.Name, course.Value.Value<int>("minimum"), course.Value.Value<bool>("everyone"));
        }
        if (min > max || requirements.Values.Any(t => t.Minimum < 0 || t.Minimum > max)) return null;
        return new(vehicle, min, max, requirements.Values.ToArray());
    }

    private sealed record Seat(PersonnelVehicleRule Rule, string? Course);
    public static PersonnelStationPlan Build(IReadOnlyList<PersonnelVehicleRule> vehicles, IReadOnlyList<StationPerson> people)
    {
        var plan = new PersonnelStationPlan();
        var active = people.Where(p => p.Available).OrderBy(p => p.Id).ToArray();
        if (people.Select(p => p.Id).Distinct().Count() != people.Count || vehicles.Select(v => v.Vehicle.Id).Distinct().Count() != vehicles.Count)
        { plan.Problems.Add("Doppelte Personal- oder Fahrzeugdaten."); return plan; }
        if (people.Any(p => !p.Available && p.VehicleId.HasValue))
        { plan.Problems.Add("Gebundenes Personal ist nicht verfügbar, zum Beispiel wegen eines Lehrgangs. Wache später erneut prüfen."); return plan; }
        var seats = new List<Seat>();
        foreach (var vehicle in vehicles.OrderBy(v => v.Vehicle.Id))
        {
            if (vehicle.Maximum == 0) continue;
            // Crew members occupy distinct seats, even if they hold multiple qualifications.
            var specialists = vehicle.Training.Where(t => !t.Everyone).SelectMany(t => Enumerable.Repeat(t.Key, t.Minimum)).ToArray();
            if (specialists.Length > vehicle.Maximum) { plan.Problems.Add($"{vehicle.Vehicle.Name}: Ausbildungsregeln passen nicht zur Besatzung."); continue; }
            seats.AddRange(specialists.Select(t => new Seat(vehicle, t)));
            seats.AddRange(Enumerable.Repeat(new Seat(vehicle, null), Math.Max(0, vehicle.Minimum - specialists.Length)));
        }
        bool Fits(StationPerson person, Seat seat) => seat.Rule.CanRide(person) && (seat.Course == null || person.Training.Contains(seat.Course));
        var ordered = seats.OrderBy(s => active.Count(p => Fits(p, s)))
            .ThenByDescending(s => s.Course != null || s.Rule.Training.Count > 0).ThenBy(s => s.Rule.Vehicle.Id).ToList();
        var owner = new Dictionary<long, int>();
        bool Useful(StationPerson person, PersonnelVehicleRule rule) => rule.Training.Any(training => person.Training.Contains(training.Key));
        int ExtraPreference(StationPerson person, Seat seat)
        {
            var useful = Useful(person, seat.Rule);
            if (!owner.TryGetValue(person.Id, out var previous))
                return useful ? (person.VehicleId == seat.Rule.Vehicle.Id ? 0 : 1) : person.VehicleId == seat.Rule.Vehicle.Id ? 3 : 4;
            return useful && !Useful(person, ordered[previous].Rule) ? 2 : 5;
        }
        var required = ordered.Count;
        bool Match(int index, HashSet<long> visited)
        {
            var seat = ordered[index];
            foreach (var person in active.Where(p => Fits(p, seat))
                         .OrderBy(p => index < required ? 0 : ExtraPreference(p, seat))
                         .ThenBy(p => p.Training.Length).ThenByDescending(p => p.VehicleId == seat.Rule.Vehicle.Id).ThenBy(p => p.Id))
            {
                if (!visited.Add(person.Id)) continue;
                if (!owner.TryGetValue(person.Id, out var previous) || Match(previous, visited))
                { owner[person.Id] = index; return true; }
            }
            return false;
        }
        for (var index = 0; index < required; index++)
            if (!Match(index, new()))
            {
                var seat = ordered[index];
                plan.Problems.Add($"{seat.Rule.Vehicle.Name}: zu wenig verfügbares Personal" +
                    (seat.Course != null ? $" mit Ausbildung {seat.Course}." : seat.Rule.Training.Count > 0 ? " mit passender Ausbildung." : "."));
            }
        if (!plan.IsValid) return plan; // Never dismantle a working station for an infeasible plan.
        var counts = vehicles.ToDictionary(rule => rule.Vehicle.Id, rule => ordered.Count(seat => seat.Rule.Vehicle.Id == rule.Vehicle.Id));
        var blocked = new HashSet<long>();
        // Allocate one shared station budget. Larger carriers receive a moderate
        // share of spare staff; nobody fills nine seats before other minimums work.
        // Augmenting paths can move a specialist out of an ordinary seat and put
        // an untrained colleague there, without removing another required crew.
        while (owner.Count < active.Length)
        {
            var rule = vehicles.Where(rule => counts[rule.Vehicle.Id] < rule.Maximum && !blocked.Contains(rule.Vehicle.Id))
                .OrderBy(rule => counts[rule.Vehicle.Id] / Math.Sqrt(rule.Maximum)).ThenBy(rule => rule.Vehicle.Id).FirstOrDefault();
            if (rule == null) break;
            ordered.Add(new(rule, null));
            if (Match(ordered.Count - 1, new())) counts[rule.Vehicle.Id]++;
            else { ordered.RemoveAt(ordered.Count - 1); blocked.Add(rule.Vehicle.Id); }
        }
        foreach (var person in active) plan.Assignments[person.Id] = owner.TryGetValue(person.Id, out var seat) ? ordered[seat].Rule.Vehicle.Id : null;
        foreach (var rule in vehicles.Where(rule => rule.Maximum > 0)) plan.Maximums[rule.Vehicle.Id] = counts[rule.Vehicle.Id];
        return plan;
    }
}
