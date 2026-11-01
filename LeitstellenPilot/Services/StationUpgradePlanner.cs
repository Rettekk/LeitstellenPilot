using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public static class StationUpgradePlanner
{
    public static string AccountKey(int userId) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(userId.ToString(CultureInfo.InvariantCulture))));
    public static void Normalize(StationUpgradeSettings settings)
    {
        settings.Order = settings.Order is >= 0 and <= 2 ? settings.Order : 0;
        settings.TargetLevel = Math.Clamp(settings.TargetLevel, 0, 30); // 0 = maximum offered by the game for this building
        settings.ReserveCredits = Math.Clamp(settings.ReserveCredits, 0, 1000000000);
        settings.DailyBudgetCredits = Math.Clamp(settings.DailyBudgetCredits, 0, 1000000000);
        settings.IntervalMinutes = Math.Clamp(settings.IntervalMinutes, 1, 1440);
        settings.Ledgers ??= new();
    }

    public static bool TypeEnabled(StationUpgradeSettings settings, int type) => type switch
    {
        0 or 18 => settings.FireStations,
        6 or 19 => settings.PoliceStations,
        2 or 20 => settings.RescueStations,
        _ => false
    };

    private static int Family(int type) => type switch { 0 or 18 => 0, 6 or 19 => 6, 2 or 20 => 2, _ => -1 };
    private static bool HasClass(HtmlNode node, string value) => node.GetAttributeValue("class", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(value);
    private static string Text(HtmlNode node) => Regex.Replace(HtmlEntity.DeEntitize(node.InnerText) ?? "", @"\s+", " ").Trim();
    private static bool Disabled(HtmlNode node) => node.AncestorsAndSelf().Any(parent =>
        parent.Attributes["disabled"] != null || parent.GetAttributeValue("aria-disabled", "").Equals("true", StringComparison.OrdinalIgnoreCase) ||
        HasClass(parent, "disabled") || parent.Attributes["hidden"] != null);
    private static Uri? Address(string raw, Uri origin)
    {
        if (!Uri.TryCreate(origin, HtmlEntity.DeEntitize(raw), out var uri) || uri.Scheme != origin.Scheme ||
            uri.Host != origin.Host || uri.Port != origin.Port || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return null;
        return uri;
    }

    public static bool HasNextLevelLink(string html, StationUpgradeSite site, Uri origin)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        if (page.DocumentNode.SelectSingleNode("//form[contains(@action,'sign_in')]") != null)
            throw new UnauthorizedAccessException("Die Sitzung ist abgelaufen. Bitte erneut anmelden.");
        var header = page.DocumentNode.SelectSingleNode("//h1[@building_type]");
        if (header == null || !int.TryParse(header.GetAttributeValue("building_type", ""), out var type) ||
            Family(type) < 0 || Family(type) != Family(site.TypeId)) return false;
        var label = page.DocumentNode.Descendants("dt").FirstOrDefault(node => Text(node).TrimEnd(':').Equals("Stufe", StringComparison.OrdinalIgnoreCase));
        var level = label?.SelectSingleNode("following-sibling::dd[1]");
        if (level == null) return false;
        var number = Regex.Match(Text(level), @"^(\d+)(?:\s|$)");
        if (!number.Success || !int.TryParse(number.Groups[1].Value, out var actualLevel) || actualLevel != site.Level) return false;
        return level.Descendants("a").Any(link => !Disabled(link) &&
            !link.AncestorsAndSelf().Any(node => Regex.IsMatch(node.GetAttributeValue("style", ""), @"display\s*:\s*none", RegexOptions.IgnoreCase)) &&
            link.GetAttributeValue("data-method", "get").Equals("get", StringComparison.OrdinalIgnoreCase) &&
            Text(link).Equals("Ausbauen", StringComparison.OrdinalIgnoreCase) &&
            Address(link.GetAttributeValue("href", ""), origin)?.PathAndQuery == $"/buildings/{site.Id}/expand");
    }

    // The game also renders hidden cumulative offers. Accept only the immediate next level.
    public static StationUpgradeOffer? Read(string html, StationUpgradeSite site, Uri origin)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        if (page.DocumentNode.SelectSingleNode("//form[contains(@action,'sign_in')]") != null)
            throw new UnauthorizedAccessException("Die Sitzung ist abgelaufen. Bitte erneut anmelden.");
        var back = page.GetElementbyId("back_to_building");
        if (back == null || Address(back.GetAttributeValue("href", ""), origin)?.PathAndQuery != $"/buildings/{site.Id}") return null;
        var header = page.DocumentNode.SelectSingleNode("//h1[@building_type]");
        if (header == null || !int.TryParse(header.GetAttributeValue("building_type", ""), out var type) || Family(type) < 0 || Family(type) != Family(site.TypeId)) return null;
        var offers = new List<StationUpgradeOffer>();
        foreach (var link in page.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var method = link.GetAttributeValue("data-method", "get");
            if (Disabled(link) || !method.Equals("get", StringComparison.OrdinalIgnoreCase)) continue;
            var uri = Address(link.GetAttributeValue("href", ""), origin);
            if (uri?.AbsolutePath != $"/buildings/{site.Id}/expand_do/credits" || uri.Query != $"?level={site.Level}") continue;
            var block = link.Ancestors("div").FirstOrDefault(node => HasClass(node, "price_level"));
            if (block == null || !HasClass(block, "level_" + site.Level.ToString(CultureInfo.InvariantCulture)) ||
                Regex.IsMatch(block.GetAttributeValue("style", ""), @"display\s*:\s*none", RegexOptions.IgnoreCase)) continue;
            var label = block.Descendants("dt").FirstOrDefault(node => Text(node).TrimEnd(':').Equals("Ausbauen auf Stufe", StringComparison.OrdinalIgnoreCase));
            var targetNode = label?.SelectSingleNode("following-sibling::dd[1]");
            if (targetNode == null || !int.TryParse(Text(targetNode), out var target) || target != site.Level + 1) continue;
            var price = Regex.Match(Text(link), @"^Ausbauen\s*\(\s*([1-9]\d*|[1-9]\d{0,2}(?:[. ]\d{3})+)\s+Credits\s*\)$", RegexOptions.IgnoreCase);
            if (!price.Success || !long.TryParse(Regex.Replace(price.Groups[1].Value, "[. ]", ""), out var credits) || credits is < 1 or > 1000000000) continue;
            offers.Add(new(site, target, credits, uri.PathAndQuery));
        }
        var distinct = offers.Distinct().ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    public static bool Affordable(StationUpgradeSettings settings, StationUpgradeOffer offer, long credits, long spent) =>
        settings.Enabled && TypeEnabled(settings, offer.Site.TypeId) && offer.TargetLevel == offer.Site.Level + 1 &&
        offer.Site.Level >= 0 && (settings.TargetLevel == 0 || offer.TargetLevel <= settings.TargetLevel) && offer.Credits > 0 && spent >= 0 &&
        credits >= settings.ReserveCredits && offer.Credits <= credits - settings.ReserveCredits &&
        (settings.DailyBudgetCredits == 0 || spent <= settings.DailyBudgetCredits && offer.Credits <= settings.DailyBudgetCredits - spent);

    public static StationUpgradeOffer? Select(StationUpgradeSettings settings, IEnumerable<StationUpgradeOffer> offers,
        long credits, long spent, Func<int, int, int>? random = null)
    {
        var eligible = offers.Where(offer => Affordable(settings, offer, credits, spent)).ToArray();
        if (eligible.Length == 0) return null;
        if (settings.Order == 2) return eligible[(random ?? Random.Shared.Next)(0, eligible.Length)];
        return settings.Order == 1
            ? eligible.OrderBy(offer => offer.Site.Level).ThenBy(offer => offer.Credits).ThenBy(offer => offer.Site.Id).First()
            : eligible.OrderBy(offer => offer.Credits).ThenBy(offer => offer.Site.Level).ThenBy(offer => offer.Site.Id).First();
    }
}
