using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using LeitstellenPilot.Models;
using Newtonsoft.Json;

namespace LeitstellenPilot.Services;

public static class GamePageParser
{
    public static long? ReadCurrentVehicleMissionId(string html)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        var section = doc.GetElementbyId("vehicle-attr-current-mission");
        foreach (var link in section?.Descendants("a") ?? Enumerable.Empty<HtmlNode>())
        {
            var match = Regex.Match(HtmlEntity.DeEntitize(link.GetAttributeValue("href", "")) ?? "", @"^/missions/(\d+)(?:[/?#]|$)");
            if (match.Success && long.TryParse(match.Groups[1].Value, out var id) && id > 0) return id;
        }
        return null;
    }

    public static IReadOnlyList<string> ReadAlarmNotices(string html)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        return doc.DocumentNode.Descendants().Where(node =>
                node.GetAttributeValue("class", "").Split(' ').Contains("alert-danger") &&
                !node.AncestorsAndSelf().Any(parent => parent.Attributes["hidden"] != null ||
                    parent.GetAttributeValue("class", "").Split(' ').Contains("hidden") ||
                    Regex.IsMatch(parent.GetAttributeValue("style", ""), @"(?:^|;)\s*display\s*:\s*none\b", RegexOptions.IgnoreCase)) &&
                !node.Ancestors().Any(parent => parent.GetAttributeValue("class", "").Split(' ').Contains("mission_patient")))
            .Select(node => Regex.Replace(HtmlEntity.DeEntitize(node.InnerText) ?? "", @"\s+", " ").Trim())
            .Where(text => text.Length > 0 && !Regex.IsMatch(text,
                @"^(?:Fehlende[sr]?\s+(?:Fahrzeuge|Kräfte|Einheiten|Personal)\s*:|Uns\s+fehlt\s*:|Wir\s+(?:benötigen|brauchen)\s*:|Ein\s+Fahrzeug\s+hat\s+einen\s+Sprechwunsch)", RegexOptions.IgnoreCase))
            .Select(text => text.Length > 500 ? text[..500] : text).Distinct().Take(3).ToArray();
    }

    public static IReadOnlyDictionary<long, string> ReadStaffingProblems(string html)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        return ReadStaffingProblems(doc);
    }

    private static IReadOnlyDictionary<long, string> ReadStaffingProblems(HtmlDocument doc)
    {
        var problems = new Dictionary<long, string>();
        var alerts = doc.DocumentNode.Descendants().Where(node => node.GetAttributeValue("class", "").Split(' ').Contains("alert"));
        foreach (var alert in alerts)
        {
            // Associate each notice with its vehicle link, even when several notices share one alert.
            var fragments = Regex.Split(alert.InnerHtml, @"<br\s*/?>|</(?:p|li|div)>", RegexOptions.IgnoreCase);
            foreach (var fragment in fragments)
            {
                var part = new HtmlDocument(); part.LoadHtml(fragment);
                var text = Regex.Replace(HtmlEntity.DeEntitize(part.DocumentNode.InnerText) ?? "", @"\s+", " ").Trim();
                if (!Regex.IsMatch(text, @"(?:hat|haben)\s+nicht\s+(?:genügend|genug)\s+Personal|(?:Personal|Besatzung).*(?:nicht\s+die\s+richtige\s+Ausbildung)", RegexOptions.IgnoreCase)) continue;
                foreach (var link in part.DocumentNode.Descendants("a"))
                {
                    var match = Regex.Match(link.GetAttributeValue("href", ""), @"^/vehicles/(\d+)(?:[/?#]|$)");
                    if (match.Success && long.TryParse(match.Groups[1].Value, out var id) && id > 0)
                        problems[id] = text.Length > 400 ? text[..400] : text;
                }
            }
        }
        return problems;
    }

    public static List<Mission> ParseMissions(string script)
    {
        var list = Regex.Match(script, @"\b(?:const|let|var)\s+mList\s*=\s*\[");
        if (list.Success)
        {
            var start = script.IndexOf('[', list.Index);
            return JsonConvert.DeserializeObject<List<Mission>>(BalancedJson(script, start)) ??
                throw new FormatException("Leere Einsatzantwort.");
        }
        var missions = new List<Mission>();
        foreach (Match call in Regex.Matches(script, @"\bmissionMarkerAdd\s*\(\s*\{"))
        {
            var start = script.IndexOf('{', call.Index);
            var mission = JsonConvert.DeserializeObject<Mission>(BalancedJson(script, start));
            if (mission != null) missions.Add(mission);
        }
        if (missions.Count == 0) throw new FormatException("Einsatzliste nicht erkannt. Die Schnittstelle hat sich möglicherweise geändert.");
        return missions;
    }

    internal static string BalancedJson(string input, int start)
    {
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = start; index < input.Length; index++)
        {
            var c = input[index];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c is '[' or '{') depth++;
            else if (c is ']' or '}' && --depth == 0) return input[start..(index + 1)];
        }
        throw new FormatException("Unvollständige Einsatzliste.");
    }

    public static MissionDetails ParseDetails(string html, Mission mission, IReadOnlyDictionary<string, string> aliases)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        if (doc.DocumentNode.SelectSingleNode("//form[contains(@action,'sign_in')]") != null)
            throw new UnauthorizedAccessException("Die Sitzung ist abgelaufen. Bitte erneut anmelden.");
        if (doc.DocumentNode.SelectSingleNode("//*[contains(@class,'missionNotFound')]") != null)
            throw new InvalidOperationException("Der Einsatz existiert nicht mehr.");
        var details = new MissionDetails { TypeKey = mission.TypeKey,
            HasPatients = doc.DocumentNode.SelectSingleNode("//*[contains(concat(' ',normalize-space(@class),' '),' mission_patient ')]") != null };
        foreach (var failure in ReadStaffingProblems(doc)) details.VehicleStaffingProblems[failure.Key] = failure.Value;
        var help = doc.GetElementbyId("mission_help");
        var general = doc.GetElementbyId("mission_general_info");
        if (double.TryParse(general?.GetAttributeValue("data-latitude", ""), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var latitude) && double.IsFinite(latitude) && latitude is >= -90 and <= 90)
            details.Latitude = latitude;
        if (double.TryParse(general?.GetAttributeValue("data-longitude", ""), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var longitude) && double.IsFinite(longitude) && longitude is >= -180 and <= 180)
            details.Longitude = longitude;
        var typeMatch = Regex.Match(help?.GetAttributeValue("href", "") ?? "", @"/einsaetze/(\d+)");
        if (typeMatch.Success)
        {
            var overlay = general?.GetAttributeValue("data-overlay-index", mission.overlay_index?.ToString() ?? "") ?? "";
            var additive = general?.GetAttributeValue("data-additive-overlays", mission.additive_overlays ?? "") ?? "";
            details.TypeKey = typeMatch.Groups[1].Value +
                (!string.IsNullOrEmpty(overlay) && overlay != "null" ? "-" + overlay : "") +
                (!string.IsNullOrEmpty(additive) && additive != "null" ? "/" + additive : "");
        }
        var form = doc.GetElementbyId("mission-form") ??
            doc.DocumentNode.SelectSingleNode("//form[.//input[@name='vehicle_ids[]']]");
        if (form != null)
        {
            details.AlarmPath = HtmlEntity.DeEntitize(form.GetAttributeValue("action", "")) ?? "";
            foreach (var input in form.SelectNodes(".//input[@type='hidden' and @name]") ?? Enumerable.Empty<HtmlNode>())
            {
                // Disabled controls are not submitted by a browser, even when they have a value.
                if (IsDisabledFormControl(input)) continue;
                var name = input.GetAttributeValue("name", "");
                var value = HtmlEntity.DeEntitize(input.GetAttributeValue("value", "")) ?? "";
                if (name.StartsWith("equipments[", StringComparison.Ordinal))
                {
                    var equipment = Regex.Match(name, @"^equipments\[(\d+)\]\[\]$");
                    if (equipment.Success && long.TryParse(equipment.Groups[1].Value, out var vehicleId) && vehicleId > 0)
                    {
                        if (!details.EquipmentFieldsByVehicleId.TryGetValue(vehicleId, out var fields))
                            details.EquipmentFieldsByVehicleId[vehicleId] = fields = new();
                        fields.Add(new(name, value));
                    }
                }
                else if (name != "vehicle_ids[]") details.HiddenFields.Add(new(name, value));
            }
            if (!details.HiddenFields.Any(p => p.Key == "authenticity_token"))
            {
                var csrf = doc.DocumentNode.SelectSingleNode("//meta[@name='csrf-token']");
                if (csrf != null) details.HiddenFields.Add(new("authenticity_token", HtmlEntity.DeEntitize(csrf.GetAttributeValue("content", "")) ?? ""));
            }
        }
        var order = 0;
        foreach (var input in doc.DocumentNode.SelectNodes("//input[@name='vehicle_ids[]']") ?? Enumerable.Empty<HtmlNode>())
        {
            if (!long.TryParse(input.GetAttributeValue("value", ""), out var id)) continue;
            details.VehicleOrder[id] = order++;
            var candidateRow = input.Ancestors("tr").FirstOrDefault();
            if (double.TryParse(candidateRow?.GetAttributeValue("data-lat", ""), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var originLat) && double.IsFinite(originLat) && originLat is >= -90 and <= 90 &&
                double.TryParse(candidateRow?.GetAttributeValue("data-lon", ""), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var originLon) && double.IsFinite(originLon) && originLon is >= -180 and <= 180)
            {
                var timeCell = candidateRow?.SelectSingleNode("./td[@start_delay]");
                int.TryParse(timeCell?.GetAttributeValue("start_delay", "0"), out var startDelay);
                details.VehicleTravelOrigins[id] = new(originLat, originLon, candidateRow?.GetAttributeValue("data-direct", "0") == "1", Math.Clamp(startDelay, 0, 86400));
            }
            foreach (var cell in candidateRow?.SelectNodes("./td") ?? Enumerable.Empty<HtmlNode>())
                if (TryReadTravelSeconds(HtmlEntity.DeEntitize(cell.InnerText) ?? "", out var seconds))
                { details.VehicleTravelSeconds[id] = seconds; break; }
            if (IsDisabledFormControl(input)) details.DisabledVehicleIds.Add(id);
        }
        foreach (var node in doc.DocumentNode.SelectNodes("//*[contains(@id,'mission_vehicle') or @id='vehicle_at_mission']//a[contains(@href,'/vehicles/')]") ?? Enumerable.Empty<HtmlNode>())
        {
            if (node.Ancestors("tr").Any(row => row.SelectSingleNode(".//input[@name='vehicle_ids[]']") != null)) continue;
            var match = Regex.Match(node.GetAttributeValue("href", ""), @"/vehicles/(\d+)");
            if (match.Success)
            {
                var vehicleId = long.Parse(match.Groups[1].Value);
                details.AssignedVehicleIds.Add(vehicleId);
                if (int.TryParse(node.GetAttributeValue("vehicle_type_id", ""), out var typeId) && typeId >= 0)
                    details.AssignedVehicleTypeIds[vehicleId] = typeId;
                if (node.Ancestors().Any(parent => parent.Id == "mission_vehicle_driving")) details.EnRouteVehicleIds.Add(vehicleId);
                var row = node.Ancestors("tr").FirstOrDefault();
                var table = row?.Ancestors("table").FirstOrDefault();
                var cells = row?.SelectNodes("./td");
                var headers = table?.SelectNodes(".//thead/tr/th");
                var crewColumn = headers == null ? -1 : headers.ToList().FindIndex(header =>
                    Regex.IsMatch(header.InnerHtml, "users|personnel|personal|besatzung", RegexOptions.IgnoreCase));
                if (crewColumn < 0) crewColumn = details.EnRouteVehicleIds.Contains(vehicleId) ? 4 : 3;
                if (cells != null && crewColumn < cells.Count)
                {
                    var cell = cells[crewColumn];
                    var countText = cell.GetAttributeValue("sortvalue", cell.InnerText.Trim());
                    if (int.TryParse(countText, out var crew) && crew >= 0) details.CrewByVehicleId[vehicleId] = crew;
                }
            }
        }

        var missingCandidates = (doc.DocumentNode.SelectNodes("//*[@id='missing_text' or contains(concat(' ',normalize-space(@class),' '),' alert-missing-vehicles ') or contains(concat(' ',normalize-space(@class),' '),' alert-missing-personnel ') or contains(concat(' ',normalize-space(@class),' '),' alert-missing-other ')]") ?? Enumerable.Empty<HtmlNode>())
            .Concat(doc.DocumentNode.Descendants("div").Where(node => node.GetAttributeValue("class", "").Split(' ').Contains("alert") &&
                Regex.IsMatch((HtmlEntity.DeEntitize(node.InnerText) ?? "").Trim(), @"^(?:Fehlende[sr]?\s+(?:Fahrzeuge|Kräfte|Einheiten|Personal)|Uns\s+fehlt)\s*:", RegexOptions.IgnoreCase)))
            .Distinct().ToArray();
        var missingNodes = missingCandidates.Where(node => !node.Ancestors().Any(missingCandidates.Contains)).ToArray();
        foreach (var missing in missingNodes.Where(node => !string.IsNullOrWhiteSpace(node.InnerText)))
        {
            details.HasMissingRequirements = true;
            details.HasMissingRequirementsFromPage = true;
            var groups = missing.SelectNodes(".//*[@data-requirement-type]");
            foreach (var group in groups ?? new HtmlNodeCollection(missing) { missing })
            {
                var clone = group.CloneNode(true);
                foreach (var heading in clone.SelectNodes(".//b") ?? Enumerable.Empty<HtmlNode>()) heading.Remove();
                var text = Regex.Replace(clone.InnerHtml, @"<br\s*/?>|</(?:li|div|p)>", ";", RegexOptions.IgnoreCase);
                text = HtmlEntity.DeEntitize(Regex.Replace(text, "<[^>]+>", " ")) ?? "";
                ParseMissingText(text, aliases, details);
            }
        }
        if (missingNodes.Length == 0 && (mission.HasMissingVehicles || mission.HasPrisonerTransportRequest))
        {
            details.HasMissingRequirements = true;
            ParseMissingText(mission.MissingVehiclesText, aliases, details);
        }
        if (details.HasPrisonerTransportRequest && details.MissingRequirements.Count == 0 && details.UnparsedRequirements.Count == 0)
            details.HasMissingRequirements = false;
        ReadPatientRequirements(doc, aliases, details);
        return details;
    }

    private static bool IsDisabledFormControl(HtmlNode node)
    {
        if (node.Attributes["disabled"] != null) return true;
        foreach (var fieldset in node.Ancestors("fieldset").Where(parent => parent.Attributes["disabled"] != null))
        {
            // The first legend of a disabled fieldset is exempt from the inherited disabled state.
            var legend = fieldset.Elements("legend").FirstOrDefault();
            if (legend == null || !node.Ancestors().Contains(legend)) return true;
        }
        return false;
    }

    private static void ReadPatientRequirements(HtmlDocument doc, IReadOnlyDictionary<string, string> aliases, MissionDetails details)
    {
        var patients = doc.DocumentNode.SelectNodes("//*[contains(concat(' ',normalize-space(@class),' '),' mission_patient ')]") ??
            doc.DocumentNode.SelectNodes("//*[starts-with(@id,'mission_patients_')]");
        var totals = new Dictionary<string, int>(); var signatures = new List<string>(); var index = 0;
        foreach (var patient in patients ?? Enumerable.Empty<HtmlNode>())
        {
            if (patient.Ancestors().Any(parent => parent.GetAttributeValue("class", "").Split(' ').Contains("mission_patient"))) continue;
            details.HasPatients = true;
            var individual = new Dictionary<string, int>(); var unknown = new HashSet<string>();
            foreach (var alert in patient.SelectNodes(".//*[contains(concat(' ',normalize-space(@class),' '),' alert-danger ')]") ?? Enumerable.Empty<HtmlNode>())
            {
                var text = Regex.Replace(HtmlEntity.DeEntitize(alert.InnerText) ?? "", @"\s+", " ").Trim();
                var required = Regex.Match(text, @"^Wir\s+(?:benötigen|brauchen)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (!required.Success) continue;
                details.HasMissingRequirements = details.HasMissingRequirementsFromPage = true;
                foreach (var description in Regex.Split(required.Groups[1].Value, @"[,;]|\s+und\s+", RegexOptions.IgnoreCase).Select(part => part.Trim()).Where(part => part.Length > 0))
                {
                    var parsed = new MissionDetails();
                    ParseMissingText(Regex.IsMatch(description, @"^\d") ? description : "1 " + description, aliases, parsed);
                    foreach (var demand in parsed.MissingRequirements) individual[demand.Key] = Math.Max(individual.GetValueOrDefault(demand.Key), demand.Count);
                    foreach (var unresolved in parsed.UnparsedRequirements) unknown.Add(unresolved);
                }
            }
            if (individual.Count + unknown.Count == 0) { index++; continue; }
            signatures.Add(patient.GetAttributeValue("id", "patient-" + index++) + ":" +
                JsonConvert.SerializeObject(individual.OrderBy(pair => pair.Key)) + ":" + JsonConvert.SerializeObject(unknown.Order()));
            foreach (var (key, count) in individual)
                totals[key] = key is "kdow_lna" or "kdow_orgl" ? Math.Max(totals.GetValueOrDefault(key), count) : totals.GetValueOrDefault(key) + count;
            details.UnparsedRequirements.AddRange(unknown.Select(text => "Patientennachforderung: " + text));
        }
        foreach (var (key, count) in totals)
        {
            var general = details.MissingRequirements.Where(demand => demand.Key == key).Sum(demand => demand.Count);
            details.MissingRequirements.RemoveAll(demand => demand.Key == key);
            details.MissingRequirements.Add(new(key, Math.Max(general, count)));
        }
        details.PatientRequirementSignature = string.Join(";", signatures.Order());
    }

    public static void ParseMissingText(string text, IReadOnlyDictionary<string, string> aliases, MissionDetails details)
    {
        details.HasPrisonerTransportRequest |= TransportStatus.HasPrisoners(text);
        text = TransportStatus.WithoutPrisoners(text);
        text = Regex.Replace(text, @"(?:Fehlende[sr]?\s+(?:Fahrzeuge|Kräfte|Einheiten|Personal)|Uns\s+fehlt)\s*:", "", RegexOptions.IgnoreCase);
        foreach (var part in SplitRequirements(text).Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var match = Regex.Match(part, @"^(\d[\d.]*)\s*(?:[x×]\s*)?(.+)$", RegexOptions.IgnoreCase);
            var colon = !match.Success ? Regex.Match(part, @"^(.+?):\s*(\d[\d.]*)$") : null;
            var number = match.Success ? match.Groups[1].Value : colon?.Success == true ? colon.Groups[2].Value : "";
            var description = match.Success ? match.Groups[2].Value : colon?.Success == true ? colon.Groups[1].Value : "";
            if (!int.TryParse(number.Replace(".", ""), out var count) || count <= 0)
            { details.UnparsedRequirements.Add(part); continue; }
            description = Regex.Replace(description.Trim(), @"\s+(?:fehlt|fehlen)\.?$", "", RegexOptions.IgnoreCase);
            if (RequirementCatalog.TryResolve(description, aliases, out var key)) details.MissingRequirements.Add(new(key, count));
            else details.UnparsedRequirements.Add(part);
        }
    }

    private static IEnumerable<string> SplitRequirements(string text)
    {
        var start = 0; var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '(') depth++;
            if (text[index] == ')') depth--;
            if (depth != 0) continue;
            var separator = text[index] is ';' or '\r' or '\n' || text[index] == ',' &&
                Regex.IsMatch(text[(index + 1)..], @"^\s*(?:\d|[^,;:\r\n]+:\s*\d)");
            if (!separator) continue;
            yield return text[start..index]; start = index + 1;
        }
        yield return text[start..];
    }

    internal static bool TryReadTravelSeconds(string text, out int seconds)
    {
        seconds = 0; text = Regex.Replace(text.Trim(), @"\s+", " ");
        if (text.Equals("Sofort", StringComparison.OrdinalIgnoreCase)) return true;
        var time = Regex.Match(text, @"^(?:(\d+)\s*(?:Std\.?|Stunden?|h)\s*)?(?:(\d+)\s*(?:Min\.?|Minuten?)\s*)?(?:(\d+)\s*(?:Sek\.?|Sekunden?|s)\s*)?$", RegexOptions.IgnoreCase);
        if (!time.Success || !time.Groups.Cast<Group>().Skip(1).Any(group => group.Success)) return false;
        var values = new long[3];
        for (var index = 0; index < 3; index++)
            if (time.Groups[index + 1].Success && (!long.TryParse(time.Groups[index + 1].Value, out values[index]) || values[index] > int.MaxValue)) return false;
        var total = values[0] * 3600 + values[1] * 60 + values[2];
        if (total > int.MaxValue) return false;
        seconds = (int)total; return true;
    }
}
