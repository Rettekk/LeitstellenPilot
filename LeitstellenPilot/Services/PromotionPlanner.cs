using System;
using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;

namespace LeitstellenPilot.Services;

public sealed record PromotionPage(bool Recognized, string CurrentRank, string? ActionPath = null, bool Post = false, string Token = "");
public enum PromotionState { NotAvailable, Promoted, Pending, Unknown, Paused }
public sealed record PromotionResult(bool Promoted, string Message, PromotionState State);
public static class PromotionPlanner
{
    private static bool HasClass(HtmlNode node, string name) => node.GetAttributeValue("class", "")
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(name);

    public static PromotionPage Read(string html, Uri origin)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        if (page.DocumentNode.SelectSingleNode("//form[contains(@action,'sign_in')]") != null)
            throw new UnauthorizedAccessException("Die Sitzung ist abgelaufen. Bitte erneut anmelden.");
        var currentRows = page.DocumentNode.Descendants("tr").Where(row => HasClass(row, "rank-earned-row")).ToArray();
        if (currentRows.Length != 1) return new(false, "");
        var cells = currentRows[0].SelectNodes("./td");
        var rank = cells?.Count >= 2 ? (HtmlEntity.DeEntitize(cells[1].InnerText) ?? "").Trim() : "";
        if (rank.Length == 0) return new(false, "");
        var actions = new List<PromotionPage>();
        foreach (var link in page.DocumentNode.Descendants("a").Where(link => HasClass(link, "btn-success") &&
                     !HasClass(link, "disabled") && link.Attributes["disabled"] == null &&
                     !link.GetAttributeValue("aria-disabled", "false").Equals("true", StringComparison.OrdinalIgnoreCase) &&
                     link.Ancestors().Any(parent => HasClass(parent, "alert-success"))))
        {
            if (!Uri.TryCreate(origin, HtmlEntity.DeEntitize(link.GetAttributeValue("href", "")), out var uri) ||
                uri.Scheme != origin.Scheme || uri.Host != origin.Host || uri.Port != origin.Port || uri.UserInfo.Length > 0 ||
                uri.AbsolutePath != "/level_upgrade" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                return new(false, rank);
            var method = link.GetAttributeValue("data-method", "get").ToLowerInvariant();
            if (method is not ("get" or "post")) return new(false, rank);
            var csrf = HtmlEntity.DeEntitize(page.DocumentNode.SelectSingleNode("//meta[@name='csrf-token']")?.GetAttributeValue("content", "") ?? "") ?? "";
            if (method == "post" && string.IsNullOrEmpty(csrf)) return new(false, rank);
            actions.Add(new(true, rank, uri.PathAndQuery, method == "post", csrf));
        }
        if (actions.Count > 1) return new(false, rank);
        return actions.SingleOrDefault() ?? new(true, rank);
    }
}
