using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private HashSet<long> _eventItemOwnMissions = new();
    private readonly ConcurrentDictionary<long, string> _eventItemOffers = new();
    private long _lastEventItemInspection;
    private string _eventItemSession = "";

    private void ObserveEventItem(string path, string html)
    {
        if (!_config.Config.BotSettings.AutoCollectEventItems || !html.Contains("easter-egg-link", StringComparison.Ordinal)) return;
        var match = Regex.Match(path, @"^/missions/(\d+)$");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var id) || !_eventItemOwnMissions.Contains(id)) return;
        var link = EventItemPlanner.PumpkinLink(html, id, _httpClient.BaseAddress!);
        if (link != null && _eventItemSession == _config.Session.SessionId) _eventItemOffers[id] = link;
    }

    public async Task<EventItemResult?> CollectEventItemAsync(CancellationToken token = default)
    {
        bool Enabled() => IsLoggedIn && _config.Config.BotSettings.AutoCollectEventItems &&
            ActionsAllowed?.Invoke() != false && AutomaticActionsAllowed?.Invoke() == true;
        if (!Enabled() || _eventItemOwnMissions.Count == 0) return null;
        await _dispatchGate.WaitAsync(token);
        try
        {
            var session = _config.Session.SessionId;
            bool Allowed() => Enabled() && session == _config.Session.SessionId && session == _eventItemSession;
            if (!Allowed()) return null;
            var user = await GetUserInfoAsync(token) ?? throw new UnauthorizedAccessException("Anmeldung fehlt.");
            var prefix = StationUpgradePlanner.AccountKey(user.user_id) + ":";
            bool Due(long id) => !_config.Config.EventItemClaims.TryGetValue(prefix + id, out var until) || UtcNow >= until;
            var candidates = _eventItemOwnMissions.Where(Due).OrderBy(id => id).ToArray();
            if (candidates.Length == 0) return null;
            var id = candidates.FirstOrDefault(candidate => _eventItemOffers.ContainsKey(candidate));
            if (id == 0) id = candidates.FirstOrDefault(candidate => candidate > _lastEventItemInspection, candidates[0]);
            _lastEventItemInspection = id;
            // One extra mission read at most per maintenance pass; otherwise use offers observed in normal mission checks.
            var html = await GetTextAsync($"/missions/{id}", token);
            var link = EventItemPlanner.PumpkinLink(html, id, _httpClient.BaseAddress!);
            if (link == null) { _eventItemOffers.TryRemove(id, out _); return null; }
            await _pacer.WaitAsync(token);
            if (!Allowed() || !_eventItemOwnMissions.Contains(id)) return null;
            html = await GetTextAsync($"/missions/{id}", token);
            link = EventItemPlanner.PumpkinLink(html, id, _httpClient.BaseAddress!);
            if (link == null) { _eventItemOffers.TryRemove(id, out _); return null; }
            user = await GetUserInfoAsync(token) ?? throw new UnauthorizedAccessException("Anmeldung fehlt.");
            if (!Allowed() || prefix != StationUpgradePlanner.AccountKey(user.user_id) + ":") return null;
            async Task Remember(DateTime until)
            {
                var previous = _config.Config.EventItemClaims;
                var claims = previous.Where(entry => entry.Value > UtcNow).OrderByDescending(entry => entry.Value)
                    .Take(9999).ToDictionary(entry => entry.Key, entry => entry.Value);
                claims[prefix + id] = until;
                _config.Config.EventItemClaims = claims;
                try { await _config.SaveConfigAsync(_config.Config); }
                catch { _config.Config.EventItemClaims = previous; throw; }
            }
            // Keep an uncertain attempt across restarts. Never immediately repeat an action after a lost response.
            await Remember(UtcNow.AddMinutes(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, link);
            request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, $"/missions/{id}");
            using var response = await SendAsync(request, token, automaticAction: true,
                actionAllowed: () => Allowed() && _eventItemOwnMissions.Contains(id));
            CheckAuthentication(response);
            html = await response.Content.ReadAsStringAsync(token);
            // The sync route may redirect to the mission. Follow only that read-only page, never another action.
            if ((int)response.StatusCode is 302 or 303 && response.Headers.Location is { } location &&
                Uri.TryCreate(_httpClient.BaseAddress, location, out var redirect) && redirect.GetLeftPart(UriPartial.Authority) == _httpClient.BaseAddress!.GetLeftPart(UriPartial.Authority) &&
                redirect.UserInfo.Length == 0 && redirect.Fragment.Length == 0 && redirect.PathAndQuery == $"/missions/{id}")
                html = await GetTextAsync($"/missions/{id}", token);
            _eventItemOffers.TryRemove(id, out _);
            var confirmed = (response.IsSuccessStatusCode || (int)response.StatusCode is 302 or 303) && EventItemPlanner.PumpkinConfirmed(html);
            if (confirmed) await Remember(UtcNow.AddDays(7));
            return new(confirmed, confirmed ? $"Kürbis bei Einsatz #{id} eingesammelt." : $"Kürbis bei Einsatz #{id}: Abholung nicht bestätigt; erneute Prüfung frühestens in zehn Minuten.");
        }
        finally { _dispatchGate.Release(); }
    }
}
