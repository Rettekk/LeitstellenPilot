using System;
using System.Collections.Generic;
using System.Linq;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed record PollSchedule(int Seconds, string Reason);

public static class PollingPolicy
{
    public static PollSchedule Next(BotSettings settings, IReadOnlyList<Mission> missions,
        IEnumerable<DateTime?> completionTimes, DateTime now, bool hasSnapshot = true,
        bool hasTransportRequests = false, Func<int, int, int>? random = null)
    {
        var fast = Math.Clamp(settings.ScanIntervalSeconds, 5, 300);
        var seconds = fast;
        var reason = "Einsatzliste / neue Einsätze aktualisieren";
        int? completionLimit = null;
        if (settings.AdaptivePolling && hasSnapshot && !hasTransportRequests && !missions.Any(m => m.NeedsDispatch &&
            (!m.sw || settings.AutoPlannedMissions && (!m.NextScheduledReviewAt.HasValue || m.NextScheduledReviewAt <= now))))
        {
            if (missions.Count == 0)
            {
                seconds = Math.Clamp(settings.IdlePollingSeconds, fast, 600);
                reason = "Keine Einsätze; nach neuen Einsätzen suchen";
            }
            else
            {
                seconds = Math.Clamp(settings.ActivePollingSeconds, fast, 600);
                reason = "Alle Einsätze laufen; neue Einsätze / Änderungen prüfen";
                var completions = completionTimes.ToArray();
                if (completions.Length < missions.Count || completions.Any(t => !t.HasValue))
                {
                    seconds = Math.Min(seconds, fast);
                    reason = "Restzeit unbekannt; Einsatzänderungen zeitnah prüfen";
                }
                var nextCompletion = completions.Where(t => t.HasValue).Select(t => t!.Value)
                    .DefaultIfEmpty(DateTime.MaxValue).Min();
                if (nextCompletion != DateTime.MaxValue)
                {
                    // Check just after the expected finish, not shortly before it
                    // followed by another full interval. Overdue timers use the
                    // normal fast interval so a stuck mission never causes one-second polling.
                    completionLimit = nextCompletion <= now ? fast :
                        Math.Max(2, (int)Math.Clamp(Math.Ceiling((nextCompletion - now).TotalSeconds) + 1, 0, 600));
                    if (completionLimit.Value <= seconds) reason = "Zum nächsten erwarteten Einsatzabschluss prüfen";
                    seconds = Math.Min(seconds, completionLimit.Value);
                }
            }
        }
        var plannedDeadline = settings.AutoPlannedMissions ? missions.Where(m => m.sw && m.NextScheduledReviewAt > now)
            .Select(m => m.NextScheduledReviewAt!.Value).DefaultIfEmpty(DateTime.MaxValue).Min() : DateTime.MaxValue;
        if (plannedDeadline != DateTime.MaxValue)
        {
            var deadlineLimit = Math.Max(2, (int)Math.Clamp(Math.Ceiling((plannedDeadline - now).TotalSeconds), 0, 600));
            completionLimit = Math.Min(completionLimit ?? 600, deadlineLimit);
            if (deadlineLimit <= seconds) { seconds = deadlineLimit; reason = "Geplanten Einsatz rechtzeitig vorbereiten / alarmieren"; }
        }
        if (hasTransportRequests) reason = "Sprechwünsche / Transporte prüfen";
        var variation = Math.Clamp(settings.PollingVariationPercent, 0, 25);
        var spread = seconds * variation / 100;
        if (settings.MaxScanIntervalSeconds > 0)
        {
            random ??= Random.Shared.Next;
            var maximum = Math.Clamp(settings.MaxScanIntervalSeconds, fast, 300);
            // Adaptive idle/active intervals keep the same relative range; deadlines still cap the result below.
            var upper = (int)Math.Clamp(Math.Ceiling(seconds * (double)maximum / fast), seconds, 600);
            seconds = random(seconds, upper + 1);
        }
        else if (spread > 0)
        {
            random ??= Random.Shared.Next;
            seconds += random(-spread, spread + 1);
        }
        seconds = Math.Clamp(seconds, completionLimit.HasValue ? Math.Min(fast, completionLimit.Value) : fast, 600);
        if (completionLimit.HasValue) seconds = Math.Min(seconds, completionLimit.Value);
        return new(seconds, reason);
    }
}
