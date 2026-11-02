using System;
using System.Collections.Generic;
using System.Linq;
using LeitstellenPilot.Models;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed record TrainedPersonnelRule(string Course, string Name, int[] VehicleTypes,
    bool EntireCrewTrained = true, bool AllowOtherCarriers = false)
{
    public IReadOnlySet<int>? EntireCrewVehicleTypes { get; init; }
    public IReadOnlyDictionary<int, int> MinimumQualifiedByType { get; init; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, int> SceneMinimumByType { get; init; } = new Dictionary<int, int>();
    public IReadOnlySet<int> CarrierBuildingTypes { get; init; } = new HashSet<int> { 0, 18 };
    public bool RequiresEveryone(int type) => EntireCrewVehicleTypes?.Contains(type) ?? EntireCrewTrained;
}

public static class TrainedPersonnelRequirements
{
    // Qualified people are counted separately from the vehicle requirement.
    // Some vehicles require every rider to be trained; others (Dekon-P) do not.
    public static IReadOnlyDictionary<string, TrainedPersonnelRule> Rules => DispatchRuleCatalog.Bundled.Trainings;

    public static bool TryCourse(string name, out string key, DispatchRuleCatalog? catalog = null)
    {
        var normalized = RequirementCatalog.Normalize(name);
        key = (catalog?.Trainings ?? Rules).FirstOrDefault(pair => RequirementCatalog.Normalize(pair.Value.Course) == normalized ||
            RequirementCatalog.Normalize(pair.Value.Name) == normalized).Key ?? "";
        return key.Length > 0;
    }

    public static IReadOnlyDictionary<string, int> Demands(MissionDetails details, EinsatzInfo? specification, DispatchRuleCatalog? catalog = null)
    {
        var rules = catalog?.Trainings ?? Rules;
        if (details.HasMissingRequirements && details.MissingRequirements.Count > 0)
            return details.MissingRequirements.Where(d => rules.ContainsKey(d.Key)).GroupBy(d => d.Key)
                .ToDictionary(group => group.Key, group => group.Sum(d => d.Count));
        var result = new Dictionary<string, int>();
        var education = specification?.Requirements.GetValueOrDefault("personnel_educations") as JObject ??
            specification?.Additional["personnel_educations"] as JObject;
        foreach (var course in education?.Properties() ?? Enumerable.Empty<JProperty>())
            if (TryCourse(course.Name, out var key, catalog) && course.Value.Type == JTokenType.Integer &&
                int.TryParse(course.Value.ToString(), out var count) && count > 0) result[key] = count;
        foreach (var demand in specification?.Requirements ?? new Dictionary<string, JToken>())
        {
            if (demand.Value.Type != JTokenType.Integer || !int.TryParse(demand.Value.ToString(), out var count) || count is < 1 or > 500 ||
                !(catalog?.Types ?? DispatchRuleCatalog.Bundled.Types).TryGetValue(demand.Key, out var types)) continue;
            foreach (var training in rules)
            {
                var minimum = types.Min(type => training.Value.SceneMinimumByType.GetValueOrDefault(type));
                if (minimum > 0) result[training.Key] = Math.Max(result.GetValueOrDefault(training.Key), count * minimum);
            }
        }
        return result;
    }

    public static int ConfirmedCrew(string key, Vehicle vehicle, MissionDetails details, DispatchRuleCatalog? catalog = null)
    {
        if (!(catalog?.Trainings ?? Rules).TryGetValue(key, out var rule) || !rule.VehicleTypes.Contains(vehicle.TypeId) && !rule.AllowOtherCarriers) return 0;
        if (details.TrainedCrewByVehicleId.TryGetValue(vehicle.Id, out var training))
            return Math.Max(0, training.GetValueOrDefault(rule.Course));
        // assigned_personnel_count alone does not prove the training of bound staff.
        if (!rule.VehicleTypes.Contains(vehicle.TypeId) || !details.AssignedVehicleIds.Contains(vehicle.Id) ||
            !details.CrewByVehicleId.TryGetValue(vehicle.Id, out var actual)) return 0;
        return rule.RequiresEveryone(vehicle.TypeId) ? Math.Max(0, actual) :
            Math.Min(Math.Max(0, actual), rule.MinimumQualifiedByType.GetValueOrDefault(vehicle.TypeId));
    }
}
