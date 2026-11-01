using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private DateTime _pendingPromotionUntil = DateTime.MinValue;

    public async Task<PromotionResult> CollectPromotionAsync(CancellationToken token = default)
    {
        await _dispatchGate.WaitAsync(token);
        try
        {
            var result = await CollectPromotionCoreAsync(token);
            Logger?.Write(result.State is PromotionState.Unknown or PromotionState.Pending ? LogLevel.Warning : LogLevel.Info,
                "Promotion", "result", result.Message);
            return result;
        }
        finally { _dispatchGate.Release(); }
    }

    private async Task<PromotionResult> CollectPromotionCoreAsync(CancellationToken token)
    {
        EnsureLoggedIn();
        bool Allowed() => _config.Config.BotSettings.AutoPromotion && AutomaticActionsAllowed?.Invoke() != false;
        if (!Allowed()) return new(false, "Beförderungsautomatik pausiert / ausgeschaltet.", PromotionState.Paused);
        if (_pendingPromotionUntil > UtcNow) return new(false, "Letzte Beförderung wartet auf Bestätigung; keine erneute Sendung.", PromotionState.Pending);
        var page = PromotionPlanner.Read(await GetTextAsync("/level", token), _httpClient.BaseAddress!);
        if (!page.Recognized) return new(false, "Dienstgradseite nicht eindeutig erkannt; im Spiel prüfen.", PromotionState.Unknown);
        if (page.ActionPath == null) return new(false, $"Dienstgrad: {page.CurrentRank}. Noch keine Beförderung verfügbar.", PromotionState.NotAvailable);
        var selectedRank = page.CurrentRank;
        Logger?.RegisterSecret(page.Token);
        await _pacer.WaitAsync(token);
        if (!Allowed()) return new(false, "Beförderungsautomatik pausiert / ausgeschaltet.", PromotionState.Paused);
        page = PromotionPlanner.Read(await GetTextAsync("/level", token), _httpClient.BaseAddress!);
        if (!page.Recognized) return new(false, "Dienstgradseite während der Pause geändert; keine Aktion gesendet.", PromotionState.Unknown);
        if (page.CurrentRank != selectedRank || page.ActionPath == null)
            return new(false, $"Dienstgrad inzwischen geändert / Beförderung nicht mehr verfügbar: {page.CurrentRank}.", PromotionState.NotAvailable);
        Logger?.RegisterSecret(page.Token);
        try
        {
            _pendingPromotionUntil = UtcNow.AddMinutes(5);
            using var request = new HttpRequestMessage(page.Post ? HttpMethod.Post : HttpMethod.Get, page.ActionPath);
            request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, "/level");
            if (page.Post) request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("authenticity_token", page.Token) });
            using var response = await SendAsync(request, token, automaticAction: true, actionAllowed: () => _config.Config.BotSettings.AutoPromotion);
            CheckAuthentication(response);
            if ((int)response.StatusCode is not (200 or 302 or 303))
                return new(false, $"Beförderung abgelehnt (HTTP {(int)response.StatusCode}).", PromotionState.Unknown);
            var after = PromotionPlanner.Read(await GetTextAsync("/level", token), _httpClient.BaseAddress!);
            if (!after.Recognized || after.CurrentRank == page.CurrentRank)
                return new(false, "Beförderung ausgelöst; Dienstgradänderung noch nicht bestätigt.", PromotionState.Pending);
            _pendingPromotionUntil = DateTime.MinValue;
            return new(true, $"Beförderung zu {after.CurrentRank} bestätigt; Belohnung abgeholt.", PromotionState.Promoted);
        }
        catch (AutomaticActionPausedException)
        {
            _pendingPromotionUntil = DateTime.MinValue;
            return new(false, "Automatik pausiert; Beförderung wurde nicht ausgelöst.", PromotionState.Paused);
        }
    }
}
