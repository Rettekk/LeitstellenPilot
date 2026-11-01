using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private sealed class StationUpgradeOperation
    {
        public string Step = "Kontodaten lesen";
        public bool ActionSent;
    }
    private readonly SemaphoreSlim _stationUpgradeGate = new(1, 1);
    private bool StationUpgradeAllowed() => IsLoggedIn && _config.Config.StationUpgrades.Enabled &&
        ActionsAllowed?.Invoke() != false && AutomaticActionsAllowed?.Invoke() == true && StationUpgradesAllowed?.Invoke() != false;
    public Func<bool>? StationUpgradesAllowed { get; set; }
    private string StationUpgradeDay()
    {
        var zone = string.IsNullOrWhiteSpace(_config.Config.TimeZone) ? _timeProvider.LocalTimeZone : LaunchOptions.ResolveTimeZone(_config.Config);
        return TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
    private static long? Number(JToken item, string field) => long.TryParse(item[field]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
    private static StationUpgradeSite? UpgradeSite(JToken building)
    {
        var id = Number(building, "id"); var type = Number(building, "building_type"); var level = Number(building, "level");
        return id is > 0 && type is >= 0 and <= 100 && level is >= 0 and <= 100
            ? new(id.Value, (int)type.Value, (int)level.Value, building.Value<string>("caption") ?? "Wache") : null;
    }
    private static string UpgradeSettingsKey(StationUpgradeSettings settings) =>
        $"{settings.Enabled}|{settings.Order}|{settings.TargetLevel}|{settings.ReserveCredits}|{settings.DailyBudgetCredits}|{settings.FireStations}|{settings.PoliceStations}|{settings.RescueStations}";
    private async Task SaveUpgradeLedgerAsync(string account, StationUpgradeLedger ledger)
    {
        var settings = _config.Config.StationUpgrades; var previous = settings.Ledgers;
        settings.Ledgers = new(previous) { [account] = ledger };
        try { await _config.SaveConfigAsync(_config.Config); }
        catch { settings.Ledgers = previous; throw; }
    }

    public async Task<StationUpgradeResult> UpgradeStationAsync(CancellationToken token = default)
    {
        await _stationUpgradeGate.WaitAsync(token);
        var operation = new StationUpgradeOperation();
        try { return await UpgradeStationCoreAsync(operation, token); }
        catch (Exception ex) when (ex is AutomaticActionPausedException or AutomationUnavailableException)
        { return new(StationUpgradeState.Disabled, "Wachenausbau pausiert; keine weitere Aktion gesendet."); }
        catch (Exception ex)
        {
            ex.Data["StationUpgradeStep"] = operation.Step;
            ex.Data["StationUpgradeActionSent"] = operation.ActionSent;
            throw;
        }
        finally { _stationUpgradeGate.Release(); }
    }

    private async Task<StationUpgradeResult> UpgradeStationCoreAsync(StationUpgradeOperation operation, CancellationToken token)
    {
        var settings = _config.Config.StationUpgrades;
        if (!StationUpgradeAllowed()) return new(StationUpgradeState.Disabled, "Automatischer Wachenausbau ist ausgeschaltet oder pausiert.");
        var settingsKey = UpgradeSettingsKey(settings); var session = _config.Session.SessionId;
        bool Allowed() => StationUpgradeAllowed() && _config.Session.SessionId == session && UpgradeSettingsKey(settings) == settingsKey;
        var user = await GetUserInfoAsync(token) ?? throw new UnauthorizedAccessException("Anmeldung fehlt.");
        var account = StationUpgradePlanner.AccountKey(user.user_id);
        var ledger = settings.Ledgers.GetValueOrDefault(account) ?? new(StationUpgradeDay(), 0);
        operation.Step = "Eigene Wachen lesen";
        var buildings = await GetBuildingsAsync(token, refresh: true);
        if (!Allowed()) return new(StationUpgradeState.Disabled, "Wachenausbau pausiert.");
        var sites = buildings.Select(UpgradeSite).Where(site => site != null).Cast<StationUpgradeSite>().ToArray();
        if (ledger.Pending is { } pending)
        {
            var actual = sites.FirstOrDefault(site => site.Id == pending.BuildingId);
            if (actual != null && actual.Level >= pending.TargetLevel)
            {
                await SaveUpgradeLedgerAsync(account, ledger with { Pending = null });
                return new(StationUpgradeState.Confirmed, $"{actual.Name}: Ausbau auf Stufe {pending.TargetLevel} nachträglich bestätigt.");
            }
            return new(StationUpgradeState.Pending, "Letzter Wachenausbau noch nicht bestätigt. Keine weiteren Ausbauten; bitte die Wache im Spiel prüfen.");
        }
        var day = StationUpgradeDay();
        var spent = ledger.Day == day ? Math.Max(0, ledger.SpentCredits) : 0;
        if (settings.DailyBudgetCredits > 0 && spent >= settings.DailyBudgetCredits)
            return new(StationUpgradeState.Waiting, "Wachenausbau: Tagesbudget erreicht.");
        if (user.CurrentCredits <= settings.ReserveCredits)
            return new(StationUpgradeState.Waiting, "Wachenausbau: Credit-Reserve bleibt erhalten.");
        var candidates = sites.Where(site => StationUpgradePlanner.TypeEnabled(settings, site.TypeId) && (settings.TargetLevel == 0 || site.Level < settings.TargetLevel)).ToArray();
        var offers = new List<StationUpgradeOffer>();
        foreach (var site in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!Allowed()) return new(StationUpgradeState.Disabled, "Wachenausbau pausiert.");
            try
            {
                operation.Step = $"Ausbaumöglichkeit für {site.Name} (#{site.Id}) prüfen";
                var offer = await ReadStationUpgradeOfferAsync(site, token);
                if (offer != null) offers.Add(offer);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { }
        }
        var selected = StationUpgradePlanner.Select(settings, offers, user.CurrentCredits, spent);
        if (selected == null) return new(StationUpgradeState.Skipped, candidates.Length == 0
            ? "Wachenausbau: Zielstufe für die gewählten Wachen erreicht."
            : offers.Count == 0 ? "Wachenausbau: Für die gewählten Wachen wird kein weiterer Stufenausbau angeboten."
            : "Wachenausbau: Kein passendes, bezahlbares Angebot innerhalb der Vorgaben.");

        // Scanning never holds the action gate. Alarmierung and transports keep running.
        await _dispatchGate.WaitAsync(token);
        try
        {
            if (!Allowed()) return new(StationUpgradeState.Disabled, "Wachenausbau pausiert.");
            await _pacer.WaitAsync(token);
            if (!Allowed()) return new(StationUpgradeState.Disabled, "Wachenausbau pausiert.");
            operation.Step = "Wachen vor dem Ausbau erneut prüfen";
            var actualBuildings = await GetBuildingsAsync(token, refresh: true);
            var actual = actualBuildings.Select(UpgradeSite).FirstOrDefault(site => site?.Id == selected.Site.Id);
            if (actual == null || actual.Level != selected.Site.Level || actual.TypeId != selected.Site.TypeId || !StationUpgradePlanner.TypeEnabled(settings, actual.TypeId))
                return new(StationUpgradeState.Changed, "Wache während der Prüfung geändert; kein Ausbau gesendet.");
            operation.Step = $"Ausbauangebot für {actual.Name} (#{actual.Id}) erneut prüfen";
            var fresh = await ReadStationUpgradeOfferAsync(actual, token);
            operation.Step = "Guthaben und Spielkonto erneut prüfen";
            user = await GetUserInfoAsync(token) ?? throw new UnauthorizedAccessException("Anmeldung fehlt.");
            if (!Allowed() || StationUpgradePlanner.AccountKey(user.user_id) != account)
                return new(StationUpgradeState.Disabled, "Wachenausbau nach Änderung von Anmeldung oder Einstellungen pausiert.");
            day = StationUpgradeDay(); ledger = settings.Ledgers.GetValueOrDefault(account) ?? new(day, 0);
            spent = ledger.Day == day ? Math.Max(0, ledger.SpentCredits) : 0;
            if (fresh == null || fresh.Credits != selected.Credits || fresh.ActionPath != selected.ActionPath ||
                !StationUpgradePlanner.Affordable(settings, fresh, user.CurrentCredits, spent))
                return new(StationUpgradeState.Changed, "Ausbauangebot oder verfügbares Budget geändert; kein Ausbau gesendet.");
            var prepared = new StationUpgradeLedger(day, checked(spent + fresh.Credits),
                new(actual.Id, actual.Level, fresh.TargetLevel, fresh.Credits, UtcNow));
            // Persist the reservation before sending: timeout, crash or cancellation cannot cause a duplicate purchase.
            operation.Step = "Ausbauvormerkung lokal speichern";
            await SaveUpgradeLedgerAsync(account, prepared);
            var sent = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, fresh.ActionPath);
                request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, $"/buildings/{actual.Id}/expand");
                operation.Step = $"{actual.Name} (#{actual.Id}) auf Stufe {fresh.TargetLevel} ausbauen";
                using var response = await SendAsync(request, token, automaticAction: true, actionAllowed: Allowed,
                    requestAllowed: () => { sent = true; operation.ActionSent = true; return true; });
                CheckAuthentication(response);
                if ((int)response.StatusCode is not (200 or 302 or 303))
                    Logger?.Write(LogLevel.Warning, "StationUpgrade", "response.unconfirmed", $"Wachenausbau: HTTP {(int)response.StatusCode}; Zustand wird nachgelesen.");
                operation.Step = $"Ausbau für {actual.Name} (#{actual.Id}) bestätigen";
                var after = (await GetBuildingsAsync(token, refresh: true)).Select(UpgradeSite).FirstOrDefault(site => site?.Id == actual.Id);
                if (after == null || after.Level < fresh.TargetLevel)
                    return new(StationUpgradeState.Pending, $"{actual.Name}: Ausbau noch nicht bestätigt. Bitte im Spiel prüfen; keine erneute Ausgabe.");
                operation.Step = "Bestätigten Ausbau lokal speichern";
                await SaveUpgradeLedgerAsync(account, prepared with { Pending = null });
                return new(StationUpgradeState.Confirmed, $"{actual.Name}: Stufe {fresh.TargetLevel} bestätigt · {fresh.Credits:N0} Credits.");
            }
            catch
            {
                if (!sent) await SaveUpgradeLedgerAsync(account, ledger);
                throw;
            }
        }
        finally { _dispatchGate.Release(); }
    }

    private async Task<StationUpgradeOffer?> ReadStationUpgradeOfferAsync(StationUpgradeSite site, CancellationToken token)
    {
        // API levels alone do not reveal the individual cap of a small/normal station.
        // Only open the expand page if this station's live level row actually offers it.
        var overview = await GetTextAsync($"/buildings/{site.Id}", token);
        if (!StationUpgradePlanner.HasNextLevelLink(overview, site, _httpClient.BaseAddress!)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/buildings/{site.Id}/expand");
        request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, $"/buildings/{site.Id}");
        using var response = await SendAsync(request, token);
        CheckAuthentication(response);
        // Fully expanded buildings may send the browser back to their own overview.
        // No offer is available in that case; never follow an arbitrary redirect or action.
        if ((int)response.StatusCode is 302 or 303 && response.Headers.Location is { } location &&
            Uri.TryCreate(_httpClient.BaseAddress, location, out var redirect) && SameOrigin(redirect) &&
            redirect.UserInfo.Length == 0 && redirect.Fragment.Length == 0 && redirect.PathAndQuery == $"/buildings/{site.Id}")
            return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Abruf /buildings/{site.Id}/expand: HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
        try { return StationUpgradePlanner.Read(await response.Content.ReadAsStringAsync(token), site, _httpClient.BaseAddress!); }
        catch (UnauthorizedAccessException) { Logout(); throw; }
    }

    public async Task AcknowledgePendingStationUpgradeAsync(CancellationToken token = default)
    {
        await _stationUpgradeGate.WaitAsync(token);
        try
        {
            if (_config.Config.StationUpgrades.Enabled) throw new InvalidOperationException("Automatischen Wachenausbau zum Freigeben zuerst ausschalten.");
            var user = await GetUserInfoAsync(token) ?? throw new UnauthorizedAccessException("Anmeldung fehlt.");
            var account = StationUpgradePlanner.AccountKey(user.user_id);
            var ledger = _config.Config.StationUpgrades.Ledgers.GetValueOrDefault(account);
            if (ledger?.Pending != null) await SaveUpgradeLedgerAsync(account, ledger with { Pending = null });
        }
        finally { _stationUpgradeGate.Release(); }
    }
}
