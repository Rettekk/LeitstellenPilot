using System;
using System.Collections.Generic;
using System.Linq;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed record PersonnelEquipmentPlan(PersonnelStationPlan Plan,
    IReadOnlyDictionary<long, string> Details, IReadOnlyList<string> Warnings);

public static class PersonnelEquipmentPlanner
{
    // https://wiki.leitstellenspiel.de/index.php?title=Rollcontainer
    // Since May 2025, only the module's required crew need its qualification,
    // rather than everyone riding on the carrier (official game update).
    private sealed record Requirement(int People, string? Course = null);
    private static readonly IReadOnlyDictionary<string, Requirement> Requirements = new Dictionary<string, Requirement>(StringComparer.Ordinal)
    {
        ["RC-Modul: Atemschutz"] = new(3),
        ["RC-Modul: Öl"] = new(2),
        ["RC-Modul: Höhenrettung"] = new(2, "gw_hoehenrettung"),
        ["RC-Modul: Schlauch/Wasser"] = new(3),
        ["RC-Modul: Schlauch/Wasser (klein)"] = new(2),
        ["RC-Modul: Rüst"] = new(3),
        ["RC-Modul: Gefahrgut"] = new(3, "gw_gefahrgut"),
        ["RC-Modul: Dekon P"] = new(3, "dekon_p"),
        ["RC-Modul: Unwetter/Starkregen"] = new(2),
        ["RC-Modul: Taucher"] = new(2, "gw_taucher")
    };

    public static PersonnelEquipmentPlan Build(IReadOnlyList<PersonnelVehicleRule> vehicles, IReadOnlyList<StationPerson> people,
        IReadOnlyList<PersonnelEquipment> equipment)
    {
        var rules = vehicles.ToArray();
        var plan = PersonnelAllocator.Build(rules, people);
        if (!plan.IsValid) return new(plan, new Dictionary<long, string>(), []);
        var loaded = equipment.Select(e => (Equipment: e, Requirement: Requirements.TryGetValue(e.Name, out var rule) ? rule :
            throw new FormatException($"{e.Name}: Besatzungsregel noch nicht unterstützt; Wache unverändert."))).ToArray();
        foreach (var group in loaded.GroupBy(e => e.Equipment.VehicleId).OrderBy(g => g.Key))
        {
            var index = Array.FindIndex(rules, r => r.Vehicle.Id == group.Key);
            if (index < 0) throw new FormatException("Beladung auf Fahrzeug ohne unterstützte eigene Besatzung; Wache unverändert.");
            // Keep all base vehicles operable even if a loaded module cannot yet be staffed.
            void TryRule(PersonnelVehicleRule candidate)
            {
                if (candidate.Minimum > candidate.Maximum) return;
                var trial = rules.ToArray(); trial[index] = candidate;
                var candidatePlan = PersonnelAllocator.Build(trial, people);
                if (!candidatePlan.IsValid) return;
                rules = trial; plan = candidatePlan;
            }
            var minimum = Math.Min(rules[index].Maximum, group.Max(e => e.Requirement.People));
            for (var count = rules[index].Minimum + 1; count <= minimum; count++)
                TryRule(rules[index] with { Minimum = count });
            foreach (var training in group.Where(e => e.Requirement.Course != null).GroupBy(e => e.Requirement.Course!))
            {
                var needed = training.Max(e => e.Requirement.People);
                for (var count = 1; count <= Math.Min(needed, rules[index].Maximum); count++)
                {
                    var old = rules[index].Training.FirstOrDefault(t => t.Key == training.Key);
                    if (old?.Everyone == true || old?.Minimum >= count) continue;
                    var courses = rules[index].Training.Where(t => t.Key != training.Key).Append(new(training.Key, count, false)).ToArray();
                    TryRule(rules[index] with { Minimum = Math.Max(rules[index].Minimum, courses.Where(t => !t.Everyone).Sum(t => t.Minimum)), Training = courses });
                }
            }
        }
        var warnings = new List<string>(); var details = new Dictionary<long, string>();
        foreach (var item in loaded)
        {
            var crew = people.Where(p => plan.Assignments.GetValueOrDefault(p.Id) == item.Equipment.VehicleId).ToArray();
            var trained = item.Requirement.Course == null ? crew.Length : crew.Count(p => p.Training.Contains(item.Requirement.Course));
            var detail = item.Equipment.Name + $": {Math.Min(trained, item.Requirement.People)}/{item.Requirement.People} " +
                (item.Requirement.Course == null ? "Personen" : "ausgebildet");
            if (trained < item.Requirement.People)
                warnings.Add($"{rules.Single(r => r.Vehicle.Id == item.Equipment.VehicleId).Vehicle.Name}: {detail}. " +
                    "Modul noch nicht einsatzbereit; Personal oder Ausbildung fehlt.");
            details[item.Equipment.VehicleId] = details.TryGetValue(item.Equipment.VehicleId, out var previous) ? previous + "; " + detail : detail;
        }
        return new(plan, details, warnings);
    }
}
