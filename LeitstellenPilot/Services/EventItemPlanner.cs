using System;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LeitstellenPilot.Services;

public static class EventItemPlanner
{
    public static string? PumpkinLink(string html, long missionId, Uri origin)
    {
        if (missionId <= 0 || !html.Contains("easter-egg-link", StringComparison.Ordinal)) return null;
        var page = new HtmlDocument(); page.LoadHtml(html);
        var links = page.DocumentNode.SelectNodes("//a[@id='easter-egg-link']");
        if (links?.Count != 1) return null;
        var link = links[0];
        if (link.AncestorsAndSelf().Any(node => node.Attributes["disabled"] != null || node.Attributes["hidden"] != null ||
            node.GetAttributeValue("aria-disabled", "") == "true" ||
            node.GetAttributeValue("class", "").Split(' ').Contains("disabled") ||
            Regex.IsMatch(node.GetAttributeValue("style", ""), @"display\s*:\s*none", RegexOptions.IgnoreCase))) return null;
        if (!link.GetAttributeValue("data-method", "get").Equals("get", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(origin, HtmlEntity.DeEntitize(link.GetAttributeValue("href", "")), out var uri) ||
            uri.Scheme != origin.Scheme || uri.Host != origin.Host || uri.Port != origin.Port || uri.UserInfo.Length > 0 ||
            uri.Fragment.Length > 0 || uri.PathAndQuery != $"/missions/{missionId}/claim_found_object_sync") return null;
        var image = link.SelectSingleNode(".//img[@src]");
        if (image == null || !Uri.TryCreate(origin, image.GetAttributeValue("src", ""), out var picture) ||
            picture.Scheme != origin.Scheme || picture.Host != origin.Host || picture.Port != origin.Port ||
            picture.Query.Length > 0 || picture.Fragment.Length > 0 || picture.UserInfo.Length > 0 ||
            !Regex.IsMatch(picture.AbsolutePath, @"^/images/pumpkin_\d+\.png$")) return null;
        return uri.PathAndQuery;
    }

    public static bool PumpkinConfirmed(string html)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        return page.DocumentNode.SelectNodes("//*[contains(concat(' ',normalize-space(@class),' '),' alert-success ')]")?
            .Any(node => Regex.Replace(HtmlEntity.DeEntitize(node.InnerText) ?? "", @"\s+", " ").Trim().TrimStart('×').Trim() ==
                "Du hast einen Kürbis gefunden! Glückwunsch!") == true;
    }
}
