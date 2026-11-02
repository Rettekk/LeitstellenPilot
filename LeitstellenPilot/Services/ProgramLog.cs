using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LeitstellenPilot.Services;

public enum LogLevel { Debug, Info, Warning, Error }

// The compact protocol exists only in memory for the current application run.
public sealed class ProgramLog : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly Queue<string> _recent = new();
    private readonly AsyncLocal<long?> _mission = new();
    private readonly TimeProvider _clock;
    private readonly Func<IEnumerable<string>>? _secretProvider;
    private bool _disposed;
    private static readonly Regex UrlQuery = new(@"((?:https?://|/(?:api|missions|vehicles|users|buildings)/)[^\s?""'<>]+)\?[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretAssignment = new(@"((?:password|passwd|authenticity_token|csrf(?:_token)?|access_token|authorization|cookie|_session_id)\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public event Action? Changed;
    public ProgramLog(Func<IEnumerable<string>>? secretProvider = null, TimeProvider? timeProvider = null)
    { _secretProvider = secretProvider; _clock = timeProvider ?? TimeProvider.System; }

    public void RegisterSecret(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) lock (_sync) _secrets.Add(value);
    }
    public IDisposable BeginMission(long missionId)
    {
        var previous = _mission.Value; _mission.Value = missionId;
        return new MissionScope(() => _mission.Value = previous);
    }
    private sealed class MissionScope(Action restore) : IDisposable { public void Dispose() => restore(); }

    public void Write(LogLevel level, string source, string eventName, string message)
    {
        if (level == LogLevel.Debug || level == LogLevel.Info && (source == "Catalog" || source == "Settings" && eventName == "saved")) return;
        lock (_sync)
        {
            if (_disposed) return;
            var now = _clock.GetLocalNow();
            var clean = Sanitize(message).Replace('\r', ' ').Replace('\n', ' ');
            if (clean.Length > 500) clean = clean[..500] + " …";
            var severity = level switch { LogLevel.Warning => "Warnung: ", LogLevel.Error => "Fehler: ", _ => "" };
            var mission = _mission.Value is { } id ? $"[Einsatz #{id}] " : "";
            _recent.Enqueue($"[{now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {severity}{mission}{clean}");
            while (_recent.Count > 200) _recent.Dequeue();
        }
        NotifyChanged();
    }
    public string RecentText { get { lock (_sync) return string.Join(Environment.NewLine, _recent); } }
    public string SanitizeForDisplay(string message)
    {
        var clean = Sanitize(message).Replace('\r', ' ').Replace('\n', ' ');
        return clean.Length > 450 ? clean[..450] + " …" : clean;
    }
    private string Sanitize(string text)
    {
        string[] secrets;
        lock (_sync) secrets = _secrets.Concat(_secretProvider?.Invoke() ?? []).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s.Length).ToArray();
        foreach (var secret in secrets)
        {
            text = text.Replace(secret, "[entfernt]", StringComparison.Ordinal);
            text = text.Replace(Uri.EscapeDataString(secret), "[entfernt]", StringComparison.Ordinal);
            text = text.Replace(System.Net.WebUtility.UrlEncode(secret), "[entfernt]", StringComparison.Ordinal);
        }
        text = UrlQuery.Replace(text, "$1?[entfernt]");
        return SecretAssignment.Replace(text, "$1[entfernt]");
    }

    private void NotifyChanged()
    {
        if (Changed == null) return;
        foreach (Action handler in Changed.GetInvocationList())
            try { handler(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Loganzeige: " + ex.GetType().Name); }
    }


    public ValueTask DisposeAsync()
    { lock (_sync) _disposed = true; return ValueTask.CompletedTask; }
}
