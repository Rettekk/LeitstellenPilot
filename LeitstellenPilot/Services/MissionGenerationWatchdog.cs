using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LeitstellenPilot.Services;

public interface IMissionGenerationApi
{
    void ResetMissionGenerationWatchdog();
    System.Threading.Tasks.Task CheckMissionGenerationAsync(System.Threading.CancellationToken token = default);
}

/// <summary>Observes successful own-mission snapshots; it never schedules a regular generator.</summary>
public sealed class MissionGenerationWatchdog
{
    public static readonly TimeSpan MinimumQuietPeriod = TimeSpan.FromMinutes(10);
    private readonly object _sync = new();
    private HashSet<long>? _previousIds;
    private DateTime _quietSince, _lastSnapshot;
    private DateTime _nextCheck;
    private DateTime? _requestedAt;
    private int _newSnapshotsAfterRequest;
    private long _revision;
    public int MissionCount { get { lock (_sync) return _previousIds?.Count ?? 0; } }

    public void Reset(bool clearCooldown = false)
    {
        lock (_sync)
        {
            _revision++;
            _previousIds = null;
            _requestedAt = null;
            _newSnapshotsAfterRequest = 0;
            if (clearCooldown) _nextCheck = default;
        }
    }

    public string? Observe(IEnumerable<long> ids, DateTime now)
    {
        lock (_sync)
        {
            var current = ids.Where(id => id > 0).ToHashSet();
            var first = _previousIds == null;
            var newCount = first ? 0 : current.Except(_previousIds!).Count();
            var newIds = newCount > 0;
            _previousIds = current;
            _lastSnapshot = now;
            if (first || newIds)
            {
                _quietSince = now;
                _revision++;
                if (newIds && _requestedAt.HasValue)
                {
                    var seconds = (now - _requestedAt.Value).TotalSeconds;
                    if (++_newSnapshotsAfterRequest >= 2)
                    {
                        _requestedAt = null;
                        return $"Mehrfach neue Einsätze nach dem Anstoß erkannt · zuletzt {newCount} neue IDs nach {seconds:0} s.";
                    }
                    return $"Neue Einsätze nach dem Anstoß erkannt: {newCount} · nach {seconds:0} s. Weiterlauf wird beobachtet.";
                }
                return first ? "Stillstandprüfung aktiv · mindestens zehn Minuten beobachten." : null;
            }
            if (_requestedAt.HasValue && now - _requestedAt.Value >= MinimumQuietPeriod)
            {
                _requestedAt = null;
                return _newSnapshotsAfterRequest == 0
                    ? "Nach dem Anstoß keine neuen Einsätze erkannt · Wirkung nicht bestätigt."
                    : "Nach dem Anstoß bisher nur einmal neue Einsätze erkannt.";
            }
            return null;
        }
    }

    public long? BeginCheck(DateTime now)
    {
        lock (_sync)
        {
            if (now < _nextCheck || !StillQuietCore(_revision, now, MinimumQuietPeriod, int.MaxValue)) return null;
            // Includes failed checks, unreadable pages and unconfirmed requests.
            _nextCheck = now + MinimumQuietPeriod;
            return _revision;
        }
    }

    public bool StillQuiet(long revision, DateTime now, TimeSpan requiredQuiet, int maximum)
    {
        lock (_sync) return StillQuietCore(revision, now, requiredQuiet, maximum);
    }

    private bool StillQuietCore(long revision, DateTime now, TimeSpan requiredQuiet, int maximum) =>
        _previousIds != null && revision == _revision && _previousIds.Count < maximum &&
        now >= _lastSnapshot && now - _lastSnapshot <= TimeSpan.FromSeconds(90) && now - _quietSince >= requiredQuiet;

    public void Requested(DateTime now)
    {
        lock (_sync) { _requestedAt = now; _newSnapshotsAfterRequest = 0; }
    }
}

public sealed record MissionGenerationPage(int Speed, int Maximum, string CsrfToken)
{
    // Slow modes must not be mistaken for a broken generator after ten minutes.
    public TimeSpan RequiredQuiet => TimeSpan.FromSeconds(Speed switch
    { 4 => 700, 5 or 8 => 1400, _ => 600 });

    public static MissionGenerationPage? Read(string html)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        var scripts = string.Join("\n", doc.DocumentNode.Descendants("script")
            .Where(node => string.IsNullOrEmpty(node.GetAttributeValue("src", ""))).Select(node => node.InnerText));
        var speeds = Regex.Matches(scripts, @"\bmissionSpeed\s*\(\s*([0-8])\s*\)")
            .Select(match => int.Parse(match.Groups[1].Value)).Distinct().ToArray();
        // Word boundaries exclude timed_mission_count_max and other lookalike fields.
        var maxima = Regex.Matches(scripts, @"\bmission_count_max\s*=\s*(\d+)\s*(?:;|$)")
            .Select(match => int.TryParse(match.Groups[1].Value, out var maximum) ? maximum : -1).Distinct().ToArray();
        var csrf = HtmlEntity.DeEntitize(doc.DocumentNode.SelectSingleNode("//meta[@name='csrf-token']")?.GetAttributeValue("content", "") ?? "");
        if (speeds.Length != 1 || maxima.Length != 1 || maxima[0] is < 1 or > 1_000_000 ||
            string.IsNullOrWhiteSpace(csrf) || csrf.Length > 1024 || csrf.Contains('\r') || csrf.Contains('\n')) return null;
        // If no explicit event flag is supplied, retain the conservative base limit.
        var eventFlags = Regex.Matches(scripts, @"\beventRunning\s*=\s*(true|false)\s*(?:;|$)")
            .Select(match => match.Groups[1].Value).Distinct().ToArray();
        var maximum = maxima[0] * (eventFlags.Length == 1 && eventFlags[0] == "true" ? 2 : 1);
        return new(speeds[0], maximum, csrf);
    }
}
