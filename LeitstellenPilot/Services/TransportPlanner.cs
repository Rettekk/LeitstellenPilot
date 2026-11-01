using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed record TransportAction(Uri Uri, long BuildingId, string Kind, bool Post, string Token)
{
    public bool IsPrisonerRelease => Kind == "prisoner-release";
    public bool IsPatientRelease => Kind == "patient-release";
}

public static class TransportPlanner
{
    public static TransportAction? Choose(string html, long vehicleId, Uri origin, ISet<long> ownBuildingIds,
        bool allowPrisonerRelease = false, long? missionId = null, bool prisonersOnly = false)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        var csrf = HtmlEntity.DeEntitize(page.DocumentNode.SelectSingleNode("//meta[@name='csrf-token']")?.GetAttributeValue("content", "") ??
            page.DocumentNode.SelectSingleNode("//input[@name='authenticity_token']")?.GetAttributeValue("value", "") ?? "") ?? "";
        TransportAction? release = null;
        foreach (var link in page.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var classes = link.GetAttributeValue("class", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (classes.Contains("disabled") || link.Attributes["disabled"] != null ||
                link.GetAttributeValue("aria-disabled", "false") == "true") continue;
            if (!Uri.TryCreate(origin, HtmlEntity.DeEntitize(link.GetAttributeValue("href", "")), out var uri)) continue;
            if (uri.Scheme != origin.Scheme || uri.Host != origin.Host || uri.Port != origin.Port) continue;
            if (allowPrisonerRelease && missionId > 0 && csrf.Length > 0 &&
                uri.AbsolutePath == $"/missions/{missionId}/gefangene/entlassen" &&
                uri.Query == $"?vehicle_id={vehicleId}" && uri.Fragment.Length == 0 &&
                link.GetAttributeValue("data-method", "post") == "post")
                release = new(uri, 0, "prisoner-release", true, csrf);
            if (!classes.Contains("btn-success") || uri.Query.Length > 0 || uri.Fragment.Length > 0) continue;
            var match = Regex.Match(uri.AbsolutePath, $"^/vehicles/{vehicleId}/(patient|gefangener)/(\\d+)$");
            if (!match.Success || !long.TryParse(match.Groups[2].Value, out var building) || building <= 0 || !ownBuildingIds.Contains(building)) continue;
            if (prisonersOnly && match.Groups[1].Value != "gefangener") continue;
            var method = link.GetAttributeValue("data-method", "get");
            if (method is not ("get" or "post")) continue;
            if (method == "post" && string.IsNullOrEmpty(csrf)) continue;
            // Only destinations offered as eligible by the server, in the server's distance order.
            return new(uri, building, match.Groups[1].Value, method == "post", csrf);
        }
        // Prison cells are also supplied as JSON and rendered by the game's JavaScript.
        // Read only the server's own erb_prisons list, never execute script or use alliance cells.
        var cells = new List<(long Id, double Distance)>();
        foreach (var script in page.DocumentNode.SelectNodes("//script") ?? Enumerable.Empty<HtmlNode>())
            foreach (Match call in Regex.Matches(script.InnerHtml, @"\berb_prisons\s*\.\s*push\s*\(\s*\{"))
            {
                var start = script.InnerHtml.IndexOf('{', call.Index);
                var cell = JObject.Parse(GamePageParser.BalancedJson(script.InnerHtml, start));
                var id = cell.Value<long?>("id") ?? throw new FormatException("Zellendaten enthalten keine Gebäude-ID.");
                var free = cell.Value<int?>("free_cells") ?? throw new FormatException("Freie Zellen konnten nicht gelesen werden.");
                if (free > 0 && ownBuildingIds.Contains(id)) cells.Add((id, cell.Value<double?>("distance_in_km") ?? double.MaxValue));
            }
        if (cells.Count > 0)
        {
            var cell = cells.OrderBy(c => c.Distance).First();
            // The game's destination action uses the ID from its server-provided cell list.
            return new(new Uri(origin, $"/vehicles/{vehicleId}/gefangener/{cell.Id}"), cell.Id, "gefangener", false, "");
        }
        return release;
    }
}

public sealed record TransportResult(bool Success, string Message)
{
    public bool Sent { get; init; }
    public bool Waiting { get; init; }
}
