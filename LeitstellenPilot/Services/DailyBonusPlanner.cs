using System;
using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;

namespace LeitstellenPilot.Services;

public sealed record DailyBonusPage(bool Recognized, bool Available, List<KeyValuePair<string, string>> Fields,
    string? ActionPath = null, int? Day = null, int? ClaimedThroughDay = null, IReadOnlySet<int>? DisabledDays = null);
public enum DailyBonusState { Unknown, Available, NotAvailable, AlreadyClaimed, Claimed, Pending, Paused }
public sealed record DailyBonusResult(bool Claimed, string Message, DailyBonusState State = DailyBonusState.Unknown, int? Day = null);

public static class DailyBonusPlanner
{
    public static DailyBonusPage Read(string html, Uri origin)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        var forms = doc.DocumentNode.SelectNodes("//form[@action]")?.Where(node =>
        {
            if (!Uri.TryCreate(origin, HtmlEntity.DeEntitize(node.GetAttributeValue("action", "")), out var uri)) return false;
            return uri.Scheme == origin.Scheme && uri.Host == origin.Host && uri.Port == origin.Port &&
                uri.AbsolutePath == "/daily_bonuses/collect" && node.GetAttributeValue("method", "").Equals("post", StringComparison.OrdinalIgnoreCase);
        }).ToArray() ?? [];
        if (forms.Length == 0) return new(false, false, new());
        var available = new List<DailyBonusPage>();
        var seenDays = new HashSet<int>();
        var disabledDays = new HashSet<int>();
        int? claimedThrough = null;
        foreach (var form in forms)
        {
            var action = new Uri(origin, HtmlEntity.DeEntitize(form.GetAttributeValue("action", ""))!);
            if (!TryReadDay(action, out var day)) return new(false, false, new());
            if (day.HasValue && !seenDays.Add(day.Value)) return new(false, false, new());
            var buttons = form.Descendants().Where(IsSubmit).ToArray();
            if (buttons.Length == 0) return new(false, false, new());
            if (day.HasValue && buttons.Any(button => IsDisabled(button) && Classes(button).Contains("btn-default")))
                claimedThrough = Math.Max(claimedThrough ?? 0, day.Value);
            var enabled = buttons.Where(button => !IsDisabled(button)).ToArray();
            if (enabled.Length == 0) { if (day.HasValue) disabledDays.Add(day.Value); continue; }
            if (enabled.Length != 1) return new(false, false, new());
            var fields = ReadFields(form, enabled[0]);
            if (fields == null) return new(false, false, new());
            available.Add(new(true, true, fields, action.PathAndQuery, day));
        }
        if (available.Count > 1) return new(false, false, new());
        return available.Count == 1 ? available[0] with { ClaimedThroughDay = claimedThrough, DisabledDays = disabledDays } :
            new(true, false, new(), ClaimedThroughDay: claimedThrough, DisabledDays: disabledDays);
    }

    private static bool TryReadDay(Uri action, out int? day)
    {
        day = null;
        if (!string.IsNullOrEmpty(action.Fragment) || !string.IsNullOrEmpty(action.UserInfo)) return false;
        if (string.IsNullOrEmpty(action.Query)) return true; // Legacy single-form response.
        var pairs = action.Query[1..].Split('&');
        if (pairs.Length != 1) return false;
        var pair = pairs[0].Split('=', 2);
        if (pair.Length != 2 || Uri.UnescapeDataString(pair[0]) != "day" ||
            !int.TryParse(Uri.UnescapeDataString(pair[1]), out var value) || value is < 1 or > 7) return false;
        day = value;
        return true;
    }

    private static IEnumerable<string> Classes(HtmlNode node) => node.GetAttributeValue("class", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static bool IsSubmit(HtmlNode node) =>
        node.Name == "button" && node.GetAttributeValue("type", "submit").Equals("submit", StringComparison.OrdinalIgnoreCase) ||
        node.Name == "input" && node.GetAttributeValue("type", "").Equals("submit", StringComparison.OrdinalIgnoreCase);
    private static bool IsDisabled(HtmlNode node) => node.Attributes["disabled"] != null ||
        node.GetAttributeValue("aria-disabled", "false").Equals("true", StringComparison.OrdinalIgnoreCase) ||
        Classes(node).Contains("disabled") || node.Ancestors("fieldset").Any(parent => parent.Attributes["disabled"] != null);

    private static List<KeyValuePair<string, string>>? ReadFields(HtmlNode form, HtmlNode button)
    {
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var field in form.SelectNodes(".//input[@type='hidden' and @name]") ?? Enumerable.Empty<HtmlNode>())
        {
            var name = field.GetAttributeValue("name", "");
            // The observed free collect form only needs these two fields.
            if (name is not ("utf8" or "authenticity_token") || field.Attributes["disabled"] != null) return null;
            fields.Add(new(name, HtmlEntity.DeEntitize(field.GetAttributeValue("value", "")) ?? ""));
        }
        if (fields.Count(f => f.Key == "authenticity_token") != 1 || !fields.Any(f => f.Key == "authenticity_token" && f.Value.Length > 0)) return null;
        var buttonName = button.GetAttributeValue("name", "");
        if (buttonName.Length > 0)
        {
            if (buttonName is not ("button" or "commit")) return null;
            fields.Add(new(buttonName, HtmlEntity.DeEntitize(button.GetAttributeValue("value", "")) ?? ""));
        }
        return fields;
    }
}
