using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

// A snapshot belongs to one reference-data version. Never change global planner
// rules when a different account, test or background refresh installs a catalog.
public sealed class DispatchRuleCatalog
{
    private static readonly Lazy<JObject> BundledDocument = new(() => ReadAsset("dispatch-rules.json"));
    private static readonly Lazy<DispatchRuleCatalog> DefaultCatalog = new(() => Create(BundledDocument.Value,
        ReadAsset("vehicle-types.json").ToObject<Dictionary<string, JObject>>()!));
    public static DispatchRuleCatalog Bundled => DefaultCatalog.Value;
    public static JObject BundledDefinition() => (JObject)BundledDocument.Value.DeepClone();
    public long Revision { get; }
    public IReadOnlyDictionary<string, int[]> Types { get; }
    public IReadOnlyDictionary<string, string> Labels { get; }
    public IReadOnlyDictionary<string, string[]> Aliases { get; }
    public IReadOnlyDictionary<string, TrainedPersonnelRule> Trainings { get; }
    public JObject CrewOverrides { get; }
    public IReadOnlyList<string> DiscoveredCourses { get; }
    public IReadOnlyList<int> ExactVehicleTypes { get; }

    private DispatchRuleCatalog(long revision, Dictionary<string, int[]> types, Dictionary<string, string> labels,
        Dictionary<string, string[]> aliases, Dictionary<string, TrainedPersonnelRule> trainings, JObject crewOverrides,
        string[] discoveredCourses, int[] exactVehicleTypes)
    {
        Revision = revision; Types = types; Labels = labels; Aliases = aliases; Trainings = trainings;
        CrewOverrides = crewOverrides; DiscoveredCourses = discoveredCourses; ExactVehicleTypes = exactVehicleTypes;
    }

    private static JObject ReadAsset(string name)
    {
        using var stream = typeof(DispatchRuleCatalog).Assembly.GetManifestResourceStream("LeitstellenPilot.Assets." + name)!;
        using var reader = new StreamReader(stream); return JObject.Parse(reader.ReadToEnd());
    }
    public static string TrainingKey(string course) => "_trained_" + course;
    private static bool Key(string value) => Regex.IsMatch(value, @"^[a-z_][a-z0-9_]{0,99}$");
    private static bool Text(JToken? value, int maximum = 160) => value?.Type == JTokenType.String &&
        value.ToString().Length is > 0 && value.ToString().Length <= maximum && !value.ToString().Any(char.IsControl);
    private static bool Number(JToken? value, int min, int max) => value?.Type == JTokenType.Integer &&
        long.TryParse(value.ToString(), out var number) && number >= min && number <= max;
    private static bool Strings(JToken? value) => value is JArray values && values.Count <= 100 && values.All(item => Text(item));
    private static bool Fields(JObject value, params string[] allowed) => value.Properties().All(p => allowed.Contains(p.Name));

    public static void Validate(JObject document)
    {
        if (!Fields(document, "kind", "schema", "revision", "capabilities", "labels", "aliases", "courses", "crewOverrides") ||
            document.Value<string>("kind") != "leitstellenpilot.dispatch-rules" || document["schema"]?.Type != JTokenType.Integer || document.Value<int>("schema") != 1 ||
            document["revision"]?.Type != JTokenType.Integer || !long.TryParse(document["revision"]!.ToString(), out var revision) || revision < 1 ||
            document["capabilities"] is not JObject capabilities || capabilities.Count is < 1 or > 3000 ||
            document["labels"] is not JObject labels || labels.Count > 5000 || document["aliases"] is not JObject aliases || aliases.Count > 5000 ||
            document["courses"] is not JObject courses || courses.Count is < 1 or > 1000 || document["crewOverrides"] is not JObject overrides || overrides.Count > 5000)
            throw new FormatException("Regelkatalog: Format oder Version nicht unterstützt.");
        foreach (var pair in capabilities.Properties())
            if (!Key(pair.Name) || pair.Name.StartsWith("_trained_", StringComparison.Ordinal) || pair.Value is not JArray ids || ids.Count is < 1 or > 1000 || ids.Any(id => !Number(id, 0, 10000)) || ids.Distinct(JToken.EqualityComparer).Count() != ids.Count)
                throw new FormatException("Regelkatalog: Ungültige Fahrzeugfähigkeit.");
        foreach (var pair in labels.Properties())
            if (!Key(pair.Name) || !Text(pair.Value, 512)) throw new FormatException("Regelkatalog: Ungültige Bezeichnung.");
        foreach (var pair in courses.Properties())
            if (!Key(pair.Name) || pair.Value is not JObject course || !Fields(course, "name", "aliases", "allowOtherCarriers") ||
                !Text(course["name"]) || !Strings(course["aliases"]) || course["allowOtherCarriers"]?.Type != JTokenType.Boolean)
                throw new FormatException("Regelkatalog: Ungültige Ausbildungsregel.");
        foreach (var pair in aliases.Properties())
            if (!Key(pair.Name) || !Strings(pair.Value) || !capabilities.ContainsKey(pair.Name) && !labels.ContainsKey(pair.Name) &&
                !(pair.Name.StartsWith("_trained_", StringComparison.Ordinal) && courses.ContainsKey(pair.Name[9..])))
                throw new FormatException("Regelkatalog: Unbekanntes Aliasziel.");
        foreach (var pair in overrides.Properties())
        {
            if (!int.TryParse(pair.Name, out var id) || id < 0 || id > 10000 || pair.Value is not JObject rule ||
                !Fields(rule, "minimum", "maximum", "training") || rule["minimum"] != null && !Number(rule["minimum"], 0, 100) ||
                rule["maximum"] != null && !Number(rule["maximum"], 0, 100) || rule["training"] != null && rule["training"] is not JObject)
                throw new FormatException("Regelkatalog: Ungültige Besatzungsregel.");
            foreach (var training in (rule["training"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                if (!courses.ContainsKey(training.Name) || training.Value is not JObject value || !Fields(value, "minimum", "everyone") ||
                    !Number(value["minimum"], 0, 100) || value["everyone"]?.Type != JTokenType.Boolean)
                    throw new FormatException("Regelkatalog: Ungültige Besatzungsausbildung.");
        }
    }

    public static DispatchRuleCatalog Create(JObject definition, IReadOnlyDictionary<string, JObject> metadata)
    {
        Validate(definition);
        var types = ((JObject)definition["capabilities"]!).ToObject<Dictionary<string, int[]>>()!;
        var labels = ((JObject)definition["labels"]!).ToObject<Dictionary<string, string>>()!;
        var aliases = ((JObject)definition["aliases"]!).ToObject<Dictionary<string, string[]>>()!;
        var courses = (JObject)definition["courses"]!;
        var crewOverrides = (JObject)definition["crewOverrides"]!;
        var facts = new Dictionary<string, List<(int Id, bool Everyone, int Minimum, int Scene, string[] Names, int[] Buildings)>>();
        var exact = new List<int>();
        foreach (var pair in metadata)
        {
            if (!int.TryParse(pair.Key, out var id)) continue;
            var data = pair.Value;
            // Literal offered names can cover a new type before it joins a known
            // replacement family. A similar name never grants extra capabilities.
            var caption = data.Value<string>("caption");
            if (!string.IsNullOrWhiteSpace(caption))
            {
                var key = "_vehicle_" + id; types[key] = [id]; labels[key] = caption;
                exact.Add(id);
            }
            var training = data["staff"]?["training"] as JObject;
            var schoolNames = (data["schooling"] as JObject)?.Properties().SelectMany(s => (s.Value as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                .Select(p => Regex.Replace(p.Name, @"[ -]*(?:Lehrgang|Ausbildung|Fortbildung)$", "", RegexOptions.IgnoreCase)).Distinct().ToArray() ?? [];
            var entries = training?.Properties().SelectMany(s => (s.Value as JObject)?.Properties() ?? Enumerable.Empty<JProperty>()).ToArray() ?? [];
            foreach (var entry in entries)
            {
                if (!Key(entry.Name) || entry.Value is not JObject rule) throw new FormatException("Fahrzeugkatalog: Ausbildungsdaten nicht lesbar.");
                if (!facts.TryGetValue(entry.Name, out var records)) facts[entry.Name] = records = [];
                // A confirmed mixed-crew correction may relax an old "all" flag.
                // Functional reservation minima (e.g. six Dekon staff) still do
                // not turn departure alone into proof of those qualifications.
                var mixed = crewOverrides[pair.Key]?["training"]?[entry.Name]?["everyone"]?.Value<bool>() == false;
                records.Add((id, rule.Value<bool?>("all") == true && !mixed, rule.Value<int?>("min") ?? data.Value<int?>("minPersonnel") ?? 0,
                    entries.Select(e => e.Name).Distinct().Count() == 1 ? data["staff"]?.Value<int?>("trainingAtScene") ?? 0 : 0,
                    entries.Select(e => e.Name).Distinct().Count() == 1 ? schoolNames : [], data["possibleBuildings"]?.Values<int>().ToArray() ?? []));
            }
        }
        var trainings = new Dictionary<string, TrainedPersonnelRule>(); var discovered = new List<string>();
        var knownNames = courses.Properties().SelectMany(p => new[] { p.Value.Value<string>("name")! }.Concat(p.Value["aliases"]!.Values<string>()!))
            .Select(text => RequirementCatalog.Normalize(text!)).ToHashSet();
        var unambiguousNames = facts.SelectMany(pair => pair.Value.SelectMany(r => r.Names).Select(name => (Course: pair.Key, Name: name)))
            .GroupBy(pair => RequirementCatalog.Normalize(pair.Name)).Where(group => group.Select(pair => pair.Course).Distinct().Count() == 1)
            .Select(group => group.Key).ToHashSet();
        foreach (var course in courses.Properties().Select(p => p.Name).Concat(facts.Keys).Distinct())
        {
            var records = facts.GetValueOrDefault(course) ?? [];
            var known = courses[course] as JObject;
            var names = records.SelectMany(r => r.Names).Distinct().Where(name => unambiguousNames.Contains(RequirementCatalog.Normalize(name)) &&
                (known != null || !knownNames.Contains(RequirementCatalog.Normalize(name)))).ToArray();
            var name = known?.Value<string>("name") ?? (names.Length == 1 ? names[0] : course);
            if (known == null) discovered.Add(course);
            var carriers = records.Where(r => metadata[r.Id.ToString()].Value<int?>("maxPersonnel") > 0).ToArray();
            var planned = crewOverrides.Properties().Where(pair => pair.Value["training"]?[course] is JObject &&
                    metadata.TryGetValue(pair.Name,out var type) && type.Value<int?>("maxPersonnel") > 0)
                .Select(pair => (Id:int.Parse(pair.Name), Rule:(JObject)pair.Value["training"]![course]!, Crew:(JObject)pair.Value)).ToArray();
            var sceneMinimum = records.Where(r => r.Scene > 0).GroupBy(r => r.Id).ToDictionary(g => g.Key,g => g.Max(r => r.Scene));
            foreach (var plan in planned)
            {
                var minimum=plan.Rule.Value<int>("minimum");
                if (plan.Rule.Value<bool>("everyone")) minimum=Math.Max(minimum,plan.Crew.Value<int?>("minimum") ?? metadata[plan.Id.ToString()].Value<int?>("minPersonnel") ?? 1);
                sceneMinimum[plan.Id]=Math.Max(sceneMinimum.GetValueOrDefault(plan.Id),minimum);
            }
            var key = TrainingKey(course);
            trainings[key] = new(course, name, carriers.Select(r => r.Id).Concat(planned.Select(r=>r.Id)).Distinct().ToArray(),
                EntireCrewTrained: carriers.Length > 0 && carriers.All(r => r.Everyone), AllowOtherCarriers: known?.Value<bool?>("allowOtherCarriers") ?? true)
            {
                EntireCrewVehicleTypes = carriers.Where(r => r.Everyone).Select(r => r.Id).ToHashSet(),
                MinimumQualifiedByType = carriers.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Max(r => r.Everyone
                    ? Math.Max(1, metadata[r.Id.ToString()].Value<int?>("minPersonnel") ?? 1) : r.Minimum)),
                SceneMinimumByType = sceneMinimum,
                CarrierBuildingTypes = records.SelectMany(r => r.Buildings).Concat(planned.SelectMany(r=>metadata[r.Id.ToString()]["possibleBuildings"]?.Values<int>()??[])).ToHashSet()
            };
            labels[key] = "Personen mit " + name + "-Ausbildung";
            var courseAliases = new[] { name, course }.Concat(known?["aliases"]?.Values<string>() ?? []).Concat(names).Distinct().ToArray();
            aliases[key] = aliases.GetValueOrDefault(key, []).Concat(courseAliases.SelectMany(alias => new[] { "Person", "Personen", "Personal", "Einsatzkräfte" }
                .SelectMany(person => new[] { $"{person} mit {alias}-Ausbildung", $"{person} mit der {alias}-Ausbildung", $"{person} mit Ausbildung {alias}", $"{person} mit der Ausbildung {alias}" }))).Distinct().ToArray();
        }
        return new(definition.Value<long>("revision"), types, labels, aliases, trainings, (JObject)definition["crewOverrides"]!.DeepClone(), discovered.ToArray(), exact.ToArray());
    }

    public string Label(string key, IReadOnlyDictionary<string, string>? translations = null) =>
        translations != null && translations.TryGetValue(key, out var text) ? text.Split('|')[0].Trim() : Labels.GetValueOrDefault(key, key);
}
