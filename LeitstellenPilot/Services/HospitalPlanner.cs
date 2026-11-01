using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LeitstellenPilot.Services;

public sealed record HospitalOffer(TransportAction Action, string Name, int FreeBeds, int Capacity,
    double Distance, bool? DepartmentMatches, bool Available);
public sealed record PatientTransportRequest(string? Department, IReadOnlyList<HospitalOffer> Hospitals,
    TransportAction? Release, bool Complete)
{
    public IReadOnlyList<string> ParseIssues { get; init; } = [];
}
public sealed record HospitalBedRelease(long PatientId, DateTime At);
public sealed record HospitalDetails(long BuildingId, IReadOnlySet<string> Departments, bool DepartmentsKnown,
    IReadOnlyList<HospitalBedRelease> Releases, bool AllReleaseTimesKnown)
{
    public int? FreeBeds { get; init; }
    public int? ReportedPatients { get; init; }
    public int TimerElements { get; init; }
    public string? ParseIssue { get; init; }
}
public sealed record HospitalWaitSlot(long BuildingId, long PatientId, DateTime At);
public sealed record HospitalDecision(TransportAction? Action, HospitalWaitSlot? Wait, string Message);

public static class HospitalPageParser
{
    private static string Text(HtmlNode? node) => Regex.Replace(HtmlEntity.DeEntitize(node?.InnerText ?? "") ?? "", @"\s+", " ").Trim();
    public static string DepartmentKey(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
    private static bool Class(HtmlNode node, string name) => node.GetAttributeValue("class", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(name);
    private static bool Enabled(HtmlNode node) => !Class(node, "disabled") && node.Attributes["disabled"] == null && node.GetAttributeValue("aria-disabled", "false") != "true";

    private static bool ReadTreatmentSeconds(HtmlNode timer, long patientId, out long total)
    {
        const long maximum = 31 * 24 * 3600;
        var parts = Text(timer).Split(':');
        if (parts.Length is 2 or 3 && parts.All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)) &&
            int.TryParse(parts[^1], out var seconds) && seconds <= 59 && int.TryParse(parts[^2], out var minutes) && (parts.Length == 2 || minutes <= 59))
        {
            var hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
            total = hours * 3600L + minutes * 60L + seconds;
            if (total <= maximum) return true;
        }
        // Initial HTTP responses can contain empty spans. The game fills them
        // with this numeric initializer; read only a complete literal call for
        // this patient in the same row. Never execute or interpret JavaScript.
        var values = new HashSet<long>();
        foreach (var script in timer.Ancestors("tr").FirstOrDefault()?.SelectNodes(".//script") ?? Enumerable.Empty<HtmlNode>())
        {
            var source = script.InnerHtml.Trim();
            if (source.StartsWith("//<![CDATA[", StringComparison.Ordinal) && source.EndsWith("//]]>", StringComparison.Ordinal)) source = source[11..^5].Trim();
            else if (source.StartsWith("<![CDATA[", StringComparison.Ordinal) && source.EndsWith("]]>", StringComparison.Ordinal)) source = source[9..^3].Trim();
            var call = Regex.Match(source, @"^\s*patientTreatingCountdown\s*\(\s*(-?\d{1,10})\s*,\s*(\d{1,19})\s*\)\s*;?\s*$", RegexOptions.CultureInvariant);
            if (call.Success && long.TryParse(call.Groups[2].Value, out var id) && id == patientId &&
                long.TryParse(call.Groups[1].Value, out var remaining) && remaining <= maximum) values.Add(Math.Max(0, remaining));
        }
        total = values.Count == 1 ? values.Single() : 0;
        return values.Count == 1;
    }

    private static TransportAction? Action(HtmlNode link, long vehicleId, Uri origin, ISet<long> own, string csrf, bool release = false, bool metadataOnly = false)
    {
        if (!metadataOnly && !Enabled(link) || !Uri.TryCreate(origin, HtmlEntity.DeEntitize(link.GetAttributeValue("href", "")), out var uri) ||
            uri.Scheme != origin.Scheme || uri.Host != origin.Host || uri.Port != origin.Port || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        var match = Regex.Match(uri.AbsolutePath, $"^/vehicles/{vehicleId}/patient/(-?\\d+)$");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var id) || (release ? id != -1 : id <= 0 || !own.Contains(id))) return null;
        var method = link.GetAttributeValue("data-method", "get");
        if (method is not ("get" or "post") || method == "post" && csrf.Length == 0) return null;
        return new(uri, release ? 0 : id, release ? "patient-release" : "patient", method == "post", csrf);
    }

    public static PatientTransportRequest? ReadRequest(string html, long vehicleId, Uri origin, ISet<long> own)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        var request = page.DocumentNode.SelectSingleNode("//*[@data-transport-request-type='patient']");
        var table = page.DocumentNode.SelectSingleNode("//table[@id='own-hospitals']");
        if (request == null && table == null) return null;
        var csrf = HtmlEntity.DeEntitize(page.DocumentNode.SelectSingleNode("//meta[@name='csrf-token']")?.GetAttributeValue("content", "") ??
            page.DocumentNode.SelectSingleNode("//input[@name='authenticity_token']")?.GetAttributeValue("value", "") ?? "") ?? "";
        var info = request?.SelectSingleNode(".//*[contains(@class,'alert-info')]");
        var department = Regex.IsMatch(Text(info), @"mit folgender Erweiterung", RegexOptions.IgnoreCase) ? Text(info?.SelectSingleNode(".//b")) : null;
        if (string.IsNullOrWhiteSpace(department)) department = null;
        var releaseLink = (request ?? page.DocumentNode).SelectSingleNode(".//a[@id='leave_without_transport_no_compensation']");
        var release = releaseLink == null ? null : Action(releaseLink, vehicleId, origin, own, csrf, release: true);
        var offers = new List<HospitalOffer>(); var complete = table != null; var issues = new List<string>();
        if (table == null) issues.Add("Liste eigener Krankenhäuser fehlt");
        var headers = table?.SelectNodes(".//thead/tr/th")?.Select(Text).ToArray() ?? [];
        var freeColumn = Array.FindIndex(headers, text => text == "Freie Betten");
        var departmentColumn = Array.FindIndex(headers, text => text == "Fachabteilung");
        var distanceColumn = Array.FindIndex(headers, text => text == "Entfernung");
        if (freeColumn < 0) { complete = false; issues.Add("Spalte für freie Betten fehlt"); }
        foreach (var row in table?.SelectNodes(".//tr[td]") ?? Enumerable.Empty<HtmlNode>())
        {
            var links = row.SelectNodes(".//a[@href]") ?? Enumerable.Empty<HtmlNode>();
            var offered = links.Select(link => (Link: link, Action: Action(link, vehicleId, origin, own, csrf, metadataOnly: true))).FirstOrDefault(item => item.Action != null);
            if (offered.Action == null) { complete = false; issues.Add("Krankenhauszeile ohne gültiges eigenes Transportziel"); continue; }
            var cells = row.SelectNodes("./td")?.ToArray() ?? [];
            var beds = freeColumn >= 0 && freeColumn < cells.Length ? Regex.Match(Text(cells[freeColumn]), @"^(\d+)\s*/\s*(\d+)$") : Match.Empty;
            if (!beds.Success || !int.TryParse(beds.Groups[1].Value, out var free) || !int.TryParse(beds.Groups[2].Value, out var capacity) || capacity < 1 || free > capacity)
            { complete = false; issues.Add($"Bettenangabe für Krankenhaus {offered.Action.BuildingId} nicht lesbar"); continue; }
            var matchText = departmentColumn >= 0 && departmentColumn < cells.Length ? Text(cells[departmentColumn]) : "";
            bool? matches = matchText == "Ja" ? true : matchText == "Nein" ? false : null;
            var distanceText = distanceColumn >= 0 && distanceColumn < cells.Length ? Text(cells[distanceColumn]).Replace(" km", "").Replace(',', '.') : "";
            var distance = double.TryParse(distanceText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var km) && km >= 0 ? km : double.MaxValue;
            var name = Text(cells[0].SelectSingleNode("./text()"));
            offers.Add(new(offered.Action, name.Length > 0 ? name : $"Krankenhaus {offered.Action.BuildingId}", free, capacity, distance, matches,
                free > 0 && Class(offered.Link, "btn-success") && Enabled(offered.Link)));
        }
        if (offers.Select(offer => offer.Action.BuildingId).Distinct().Count() != offers.Count) { complete = false; issues.Add("Doppelte Krankenhauszeilen"); }
        return new(department, offers.DistinctBy(offer => offer.Action.BuildingId).ToArray(), release, complete) { ParseIssues = issues.Distinct().Take(3).ToArray() };
    }

    public static HospitalDetails ReadHospital(string html, long buildingId, DateTime now)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        var patients = page.DocumentNode.SelectSingleNode("//*[@id='patienten']");
        var occupancy = Regex.Match(Text(patients?.SelectSingleNode(".//*[contains(@class,'alert-info')]")), @"Derzeit liegen (\d+) Patienten.*?maximal (\d+) Patienten", RegexOptions.Singleline);
        var patientTable = patients?.SelectNodes(".//th")?.Any(header => Text(header) == "Entlassung in") == true;
        if (page.DocumentNode.SelectSingleNode("//h1[@building_type='4']") == null && !(patientTable && occupancy.Success))
            throw new FormatException("Krankenhausseite oder Patientenabschnitt nicht erkannt.");
        var departments = new HashSet<string>(StringComparer.Ordinal);
        var extensions = page.DocumentNode.SelectSingleNode("//*[@id='ausbauten' or @id='extensions']");
        foreach (var row in extensions?.SelectNodes(".//tr[td]") ?? Enumerable.Empty<HtmlNode>())
        {
            var name = Text(row.SelectSingleNode("./td[1]/b"));
            var status = Text(row.SelectSingleNode("./td[last()]"));
            if (name.Length > 0 && name != "Großkrankenhaus" && status.Contains("Bereits fertig gebaut", StringComparison.Ordinal) &&
                !Regex.IsMatch(status, @"deaktiviert|nicht einsatzbereit", RegexOptions.IgnoreCase)) departments.Add(DepartmentKey(name));
        }
        var releases = new List<HospitalBedRelease>(); var allKnown = occupancy.Success;
        var timers = patients?.SelectNodes(".//*[starts-with(@id,'patient_treating_')]")?.ToArray() ?? [];
        foreach (var timer in timers)
        {
            if (!long.TryParse(timer.GetAttributeValue("id", "")["patient_treating_".Length..], out var patientId) || patientId <= 0) { allKnown = false; continue; }
            if (!ReadTreatmentSeconds(timer, patientId, out var total)) { allKnown = false; continue; }
            releases.Add(new(patientId, now.AddSeconds(total)));
        }
        allKnown &= occupancy.Success && int.TryParse(occupancy.Groups[1].Value, out var occupied) && occupied == releases.Count &&
            releases.Select(release => release.PatientId).Distinct().Count() == releases.Count;
        int? freeBeds = occupancy.Success && int.TryParse(occupancy.Groups[1].Value, out var used) && int.TryParse(occupancy.Groups[2].Value, out var capacity) && capacity > 0 && used <= capacity
            ? capacity - used : null;
        int? reported = occupancy.Success && int.TryParse(occupancy.Groups[1].Value, out var count) ? count : null;
        var issue = allKnown ? null : patients == null ? "Patientenabschnitt fehlt" : !occupancy.Success ? "Belegungszähler nicht lesbar" :
            $"{releases.Count} von {reported} Entlassungszeiten gelesen; {timers.Length} Timerfelder gefunden";
        return new(buildingId, departments, extensions != null, releases.DistinctBy(release => release.PatientId).OrderBy(release => release.At).ToArray(), allKnown)
            { FreeBeds = freeBeds, ReportedPatients = reported, TimerElements = timers.Length, ParseIssue = issue };
    }
}

public static class HospitalPlanner
{
    public static int NormalizeWait(int minutes) => minutes is 5 or 10 or 15 ? minutes : 10;
    private static int MatchRank(PatientTransportRequest request, HospitalOffer offer, IReadOnlyDictionary<long, HospitalDetails> details)
    {
        if (request.Department == null) return 0;
        var matches = offer.DepartmentMatches;
        if (!matches.HasValue && details.TryGetValue(offer.Action.BuildingId, out var hospital) && hospital.DepartmentsKnown)
            matches = hospital.Departments.Contains(HospitalPageParser.DepartmentKey(request.Department));
        return matches == true ? 0 : matches == null ? 1 : 2;
    }

    public static HospitalDecision Choose(PatientTransportRequest request, IReadOnlyDictionary<long, HospitalDetails> details,
        DateTime now, DateTime deadline, ISet<(long BuildingId, long PatientId)> reserved, bool allowRelease, ISet<long>? recheckedFullHospitals = null,
        IReadOnlyDictionary<long, int>? protectedFreeBeds = null)
    {
        int Protected(long id) => protectedFreeBeds != null ? protectedFreeBeds.GetValueOrDefault(id) : reserved.Count(slot => slot.BuildingId == id);
        var available = request.Hospitals.Select(offer => (Offer: offer, Beds: offer.FreeBeds - Protected(offer.Action.BuildingId)))
            .Where(item => item.Offer.Available && item.Beds > 0)
            .OrderBy(item => MatchRank(request, item.Offer, details)).ThenBy(item => item.Offer.Distance)
            .ThenByDescending(item => (double)item.Beds / item.Offer.Capacity)
            .ThenBy(item => request.Department == null && details.TryGetValue(item.Offer.Action.BuildingId, out var hospital) && hospital.DepartmentsKnown ? hospital.Departments.Count : int.MaxValue)
            .ThenByDescending(item => item.Beds).ThenBy(item => item.Offer.Action.BuildingId).ToArray();
        if (available.Length > 0) return new(available[0].Offer.Action, null, $"Freies eigenes Krankenhaus: {available[0].Offer.Name}.");
        var candidates = request.Hospitals.Where(offer => offer.FreeBeds == 0 && details.ContainsKey(offer.Action.BuildingId))
            .SelectMany(offer => details[offer.Action.BuildingId].Releases.Where(release => now <= deadline && release.At <= deadline && !reserved.Contains((offer.Action.BuildingId, release.PatientId)))
                .Select(release => (Offer: offer, Release: release)))
            .OrderBy(item => MatchRank(request, item.Offer, details)).ThenBy(item => item.Release.At).ThenBy(item => item.Offer.Distance).ToArray();
        if (candidates.Length > 0)
        {
            var next = candidates[0]; var remaining = Math.Max(1, (int)Math.Ceiling((next.Release.At - now).TotalMinutes));
            return new(null, new(next.Offer.Action.BuildingId, next.Release.PatientId, next.Release.At), $"Wartet auf ein Bett in {next.Offer.Name}, voraussichtlich in {remaining} Min.");
        }
        if (request.Hospitals.Any(offer => offer.Available && offer.FreeBeds > 0 && Protected(offer.Action.BuildingId) >= offer.FreeBeds))
            return new(null, null, "Freie Betten sind für bereits wartende RTW vorgemerkt. Wird erneut geprüft.");
        if (!request.Complete)
            return new(null, null, "Transportliste nicht vollständig lesbar: " + string.Join("; ", request.ParseIssues.DefaultIfEmpty("Krankenhausangebote nicht eindeutig")) + ". Wird erneut geprüft.");
        foreach (var offer in request.Hospitals)
        {
            string? issue = offer.FreeBeds > 0 ? "Freie Betten gemeldet, Anfahren aber nicht freigegeben" :
                !details.TryGetValue(offer.Action.BuildingId, out var hospital) ? "Krankenhausdaten fehlen oder konnten nicht abgerufen werden" :
                !hospital.AllReleaseTimesKnown ? hospital.ParseIssue ?? "Entlassungszeiten nicht vollständig lesbar" :
                hospital.FreeBeds > 0 && recheckedFullHospitals?.Contains(offer.Action.BuildingId) != true ? "Belegungsangaben widersprechen sich; Transportangebot erneut prüfen" : null;
            if (issue != null) return new(null, null, $"Krankenhaus {offer.Action.BuildingId}: {issue}. Wird erneut geprüft.");
        }
        return allowRelease && request.Release != null
            ? new(request.Release, null, "Innerhalb der eingestellten Wartefrist wird kein freies Bett erwartet; ohne Transport entlassen.")
            : new(null, null, "Kein rechtzeitig freies Krankenhausbett; Patientenentlassung ist ausgeschaltet oder wird nicht angeboten.");
    }
}
