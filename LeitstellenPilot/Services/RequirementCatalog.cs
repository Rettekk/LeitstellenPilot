using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed class RequirementAliasIndex : Dictionary<string, string>
{
    internal Dictionary<string, HashSet<string>> Clarifications { get; } = new();
}

public static class RequirementCatalog
{
    public static IReadOnlyDictionary<string, int[]> Types => DispatchRuleCatalog.Bundled.Types;
    public static IReadOnlyDictionary<string, string> Labels => DispatchRuleCatalog.Bundled.Labels;

    public static string Label(string key, IReadOnlyDictionary<string, string>? translations = null) =>
        translations != null && translations.TryGetValue(key, out var text) ? text.Split('|')[0].Trim() :
        Labels.TryGetValue(key, out var label) ? label : key;

    public static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");

    public static RequirementAliasIndex BuildAliases(IReadOnlyDictionary<string, JObject> metadata,
        IReadOnlyDictionary<string, string>? translations = null, JArray? missionFields = null,
        IReadOnlyDictionary<string, string[]>? shortageLabels = null, DispatchRuleCatalog? catalog = null)
    {
        catalog ??= DispatchRuleCatalog.Create(DispatchRuleCatalog.BundledDefinition(), metadata);
        var types = catalog.Types; var labels = catalog.Labels;
        var aliases = new RequirementAliasIndex();
        foreach (var key in types.Keys.Concat(labels.Keys).Distinct()) aliases[Normalize(key)] = key;
        foreach (var pair in labels.Where(pair => !pair.Key.StartsWith("_vehicle_", StringComparison.Ordinal))) aliases[Normalize(pair.Value)] = pair.Key;
        if (translations != null)
            foreach (var pair in translations)
                foreach (var label in pair.Value.Split('|')) aliases[Normalize(label)] = pair.Key;
        foreach (var pair in types)
        {
            var clarifications = new HashSet<string>();
            foreach (var type in pair.Value)
                if (metadata.TryGetValue(type.ToString(), out var data) && data.Value<string>("caption") is { } caption)
                {
                    clarifications.Add(Normalize(caption));
                    var abbreviation = Regex.Match(caption, @"^([\p{L}-]+)\s+\d");
                    if (abbreviation.Success) clarifications.Add(Normalize(abbreviation.Groups[1].Value));
                }
            aliases.Clarifications[pair.Key] = clarifications;
        }
        aliases["löschfahrzeug"] = "firetrucks";
        aliases["streifenwagen"] = "police_cars";
        aliases["feuerwehrleute"] = "_personnel_fire";
        aliases["feuerwehrmann"] = aliases["feuerwehrfrau"] = "_personnel_fire";
        aliases["polizisten"] = "_personnel_police";
        aliases["polizist"] = aliases["polizistin"] = "_personnel_police";
        aliases["thwhelfer"] = aliases["thwhelferin"] = "_personnel_thw";
        aliases["rettungsdienstpersonal"] = aliases["rettungsdienstler"] = aliases["rettungsdienstlerin"] = "_personnel_ambulance";
        aliases["notärzte"] = "nef";
        aliases["notarzt"] = "nef";
        aliases["wasser"] = "water_needed";
        aliases["lwasser"] = "water_needed";
        aliases["sonderlöschmittel"] = "foam_needed";
        aliases["drehleiterdlk23"] = aliases["drehleiterndlk23"] = "platform_trucks";
        aliases["löschfahrzeuglf"] = aliases["löschfahrzeugelf"] = "firetrucks";
        // Patient cards request carrying assistance with this concrete fire-engine example.
        // Match the known wording exactly; other equipment qualifiers remain unresolved.
        aliases["tragehilfe"] = aliases[Normalize("Tragehilfe (z.B. durch ein LF)")] = "firetrucks";
        aliases["rüstwagenoderhlf"] = "heavy_rescue_vehicles";
        foreach (var field in missionFields?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            var fieldKey = field.Value<string>("key") ?? "";
            var key = FieldRequirementKey(fieldKey, catalog);
            var caption = field["caption"]?.Type == JTokenType.String ? field.Value<string>("caption") : null;
            if (key == null || string.IsNullOrWhiteSpace(caption)) continue;
            var label = Regex.Replace(caption, @"^Benötigte\s+", "", RegexOptions.IgnoreCase);
            aliases.TryAdd(Normalize(label), key);
        }
        if (shortageLabels != null)
            foreach (var pair in shortageLabels.Where(pair => types.ContainsKey(pair.Key) || labels.ContainsKey(pair.Key)))
                foreach (var text in pair.Value) aliases[Normalize(text)] = pair.Key;
        foreach (var pair in catalog.Aliases)
            foreach (var text in pair.Value)
            {
                var normalized = Normalize(text);
                if (aliases.TryGetValue(normalized, out var previous) && previous != pair.Key)
                    throw new FormatException("Regelkatalog: Mehrdeutige Bezeichnung: " + text);
                aliases[normalized] = pair.Key;
            }
        foreach (var group in metadata.Where(pair => pair.Value["caption"]?.Type == JTokenType.String)
                     .GroupBy(pair => Normalize(pair.Value.Value<string>("caption")!)))
        {
            if (aliases.ContainsKey(group.Key)) continue;
            var entries = group.ToArray();
            if (entries.Length == 1 && types.ContainsKey("_vehicle_" + entries[0].Key)) aliases[group.Key] = "_vehicle_" + entries[0].Key;
        }
        return aliases;
    }

    public static string? FieldRequirementKey(string field, DispatchRuleCatalog? catalog = null)
    {
        if (field.EndsWith("_chance", StringComparison.Ordinal) || !field.StartsWith("need_", StringComparison.Ordinal)) return null;
        var key = field[5..];
        key = key switch
        {
            "lf" => "firetrucks", "elw1" => "battalion_chief_vehicles", "dlk" => "platform_trucks",
            "rw" => "heavy_rescue_vehicles", "elw2" => "mobile_command_vehicles", "gwl2wasser" => "water_tankers",
            "gwa" => "mobile_air_vehicles", "dekon_p" => "hazmat_dekon", "gwgefahrgut" => "hazmat_vehicles",
            "streifenwagen" => "police_cars", "gwhoehenrettung" => "height_rescue_units", "rtw" => "ambulances",
            "polizeihubschrauber" => "police_helicopters", "boot" => "boats", "gwtaucher" => "diver_units", _ => key
        };
        return (catalog?.Types ?? Types).ContainsKey(key) || (catalog?.Labels ?? Labels).ContainsKey(key) ? key : null;
    }

    public static bool TryResolve(string text, IReadOnlyDictionary<string, string> aliases, out string key)
    {
        if (aliases.TryGetValue(Normalize(text), out key!)) return true;
        var match = Regex.Match(text.Trim(), @"^([^()]+)\(([^()]+)\)$");
        if (aliases is RequirementAliasIndex index && match.Success &&
            aliases.TryGetValue(Normalize(match.Groups[1].Value), out var baseKey) && index.Clarifications.TryGetValue(baseKey, out var allowed))
        {
            var descriptions = Regex.Split(match.Groups[2].Value, @",|\s+(?:oder|bzw\.?)\s+", RegexOptions.IgnoreCase);
            if (descriptions.All(description => allowed.Contains(Normalize(description)))) { key = baseKey; return true; }
        }
        key = ""; return false;
    }
}
