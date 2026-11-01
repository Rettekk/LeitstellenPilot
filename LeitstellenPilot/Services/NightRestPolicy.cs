using System;
using System.Globalization;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public static class NightRestPolicy
{
    public static bool HasValidWindow(BotSettings settings) => TryReadTime(settings.NightRestStart, out var start) &&
        TryReadTime(settings.NightRestEnd, out var end) && start != end;

    public static DateTime? RestUntil(BotSettings settings, DateTime utcNow, TimeZoneInfo timeZone)
    {
        if (!settings.NightRestEnabled || !TryReadTime(settings.NightRestStart, out var start) ||
            !TryReadTime(settings.NightRestEnd, out var end) || start == end) return null;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone);
        foreach (var day in new[] { local.Date.AddDays(-1), local.Date })
        {
            var from = ToUtc(day + start.ToTimeSpan(), timeZone, endOfWindow: false);
            var until = ToUtc(day.AddDays(start > end ? 1 : 0) + end.ToTimeSpan(), timeZone, endOfWindow: true);
            if (utcNow >= from && utcNow < until) return until;
        }
        return null;
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone, bool endOfWindow)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // At the spring clock change, wake at the first existing local minute.
        while (timeZone.IsInvalidTime(local)) local = local.AddMinutes(1);
        // At the autumn clock change, keep resting until the second occurrence.
        if (timeZone.IsAmbiguousTime(local))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(local);
            var earlier = offsets[0] > offsets[1] ? offsets[0] : offsets[1];
            var later = offsets[0] < offsets[1] ? offsets[0] : offsets[1];
            return new DateTimeOffset(local, endOfWindow ? later : earlier).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }

    public static bool TryReadTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
