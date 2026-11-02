using System;
using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;

namespace LeitstellenPilot.Services;

public sealed record TaskRewardsPage(bool Recognized, IReadOnlySet<long> ClaimableIds,
    List<KeyValuePair<string, string>> Fields, string? ActionPath = null);
public enum TaskRewardsState { NotAvailable, Claimed, Pending, Unknown, Paused }
public sealed record TaskRewardsResult(int ClaimedCount, string Message, TaskRewardsState State);

public static class TaskRewardsPlanner
{
    private static bool HasClass(HtmlNode node, string name) => node.GetAttributeValue("class", "")
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(name);
    private static bool Disabled(HtmlNode node) => node.Attributes["disabled"] != null || HasClass(node, "disabled") ||
        node.GetAttributeValue("aria-disabled", "false").Equals("true", StringComparison.OrdinalIgnoreCase) ||
        node.Ancestors("fieldset").Any(fieldset => fieldset.Attributes["disabled"] != null);
    private static HtmlNode? EnabledSubmit(HtmlNode form) => form.Descendants().FirstOrDefault(node =>
        !Disabled(node) && HasClass(node, "btn-success") &&
        (node.Name == "input" && node.GetAttributeValue("type", "").Equals("submit", StringComparison.OrdinalIgnoreCase) ||
         node.Name == "button" && node.GetAttributeValue("type", "submit").Equals("submit", StringComparison.OrdinalIgnoreCase)));
    private static Uri? Action(HtmlNode form, Uri origin)
    {
        if (!form.GetAttributeValue("method", "").Equals("post", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(origin, HtmlEntity.DeEntitize(form.GetAttributeValue("action", "")), out var uri) ||
            uri.Scheme != origin.Scheme || uri.Host != origin.Host || uri.Port != origin.Port || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return null;
        return uri;
    }

    public static TaskRewardsPage Read(string html, Uri origin)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        if (page.DocumentNode.SelectSingleNode("//form[contains(@action,'sign_in')]") != null)
            throw new UnauthorizedAccessException("Die Sitzung ist abgelaufen. Bitte erneut anmelden.");
        var all = page.GetElementbyId("all");
        if (all == null || !HasClass(all, "task-tab-content")) return new(false, new HashSet<long>(), new());
        var ids = new HashSet<long>();
        foreach (var form in all.Descendants("form"))
        {
            if (!form.Ancestors().Any(panel => HasClass(panel, "task_panel") && HasClass(panel, "panel-success")) || EnabledSubmit(form) == null) continue;
            var uri = Action(form, origin);
            if (uri?.AbsolutePath != "/tasks/claim_reward") return new(false, ids, new());
            long id = 0;
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2) return new(false, ids, new());
                var key = Uri.UnescapeDataString(pair[0]);
                if (key == "task_progress_id" && id == 0 && long.TryParse(Uri.UnescapeDataString(pair[1]), out var value) && value > 0) id = value;
                else if (key != "return_to_category") return new(false, ids, new());
            }
            if (id == 0) return new(false, ids, new());
            ids.Add(id); // Desktop/mobile and category duplicates represent the same reward.
        }
        if (ids.Count == 0) return new(true, ids, new());
        foreach (var form in page.DocumentNode.Descendants("form"))
        {
            var uri = Action(form, origin);
            var submit = EnabledSubmit(form);
            if (uri?.AbsolutePath != "/tasks/claim_all_rewards" || uri.Query.Length > 0 || submit == null) continue;
            var fields = new List<KeyValuePair<string, string>>();
            foreach (var input in form.Descendants("input").Where(input => !Disabled(input) && input.GetAttributeValue("type", "") == "hidden"))
            {
                var name = input.GetAttributeValue("name", "");
                if (name is not ("authenticity_token" or "utf8")) return new(false, ids, new());
                fields.Add(new(name, HtmlEntity.DeEntitize(input.GetAttributeValue("value", "")) ?? ""));
            }
            if (fields.Count(field => field.Key == "authenticity_token" && field.Value.Length > 0) != 1) continue;
            var submitName = submit.GetAttributeValue("name", "");
            if (submitName.Length > 0)
            {
                if (submitName != "commit") return new(false, ids, new());
                fields.Add(new(submitName, HtmlEntity.DeEntitize(submit.GetAttributeValue("value", submit.InnerText)) ?? ""));
            }
            return new(true, ids, fields, uri.PathAndQuery);
        }
        return new(false, ids, new());
    }
}
