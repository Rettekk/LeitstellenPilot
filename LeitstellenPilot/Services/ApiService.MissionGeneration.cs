using System;
using System.Threading;
using System.Threading.Tasks;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private readonly MissionGenerationWatchdog _generationWatchdog = new();
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    public Func<bool>? GenerationRecoveryAllowed { get; set; }
    public event Action<string>? MissionGenerationStatusChanged;
    private bool GenerationAllowed => IsLoggedIn &&
        ActionsAllowed?.Invoke() != false && GenerationRecoveryAllowed?.Invoke() == true;

    public void ResetMissionGenerationWatchdog() => _generationWatchdog.Reset();

    private void GenerationStatus(string message, string eventName)
    {
        MissionGenerationStatusChanged?.Invoke(message);
        Logger?.Write(LogLevel.Info, "Generation", eventName, message);
    }

    public async Task CheckMissionGenerationAsync(CancellationToken token = default)
    {
        if (!GenerationAllowed) { ResetMissionGenerationWatchdog(); return; }
        if (!await _generationGate.WaitAsync(0, token)) return;
        try
        {
            var revision = _generationWatchdog.BeginCheck(UtcNow);
            if (!revision.HasValue) return;
            GenerationStatus("Stillstand erkannt · mindestens zehn Minuten ohne neue eigene Einsatz-ID. Spieleinstellungen werden geprüft.", "stalled");
            bool StillNeeded() => GenerationAllowed && _generationWatchdog.StillQuiet(revision.Value,
                UtcNow, MissionGenerationWatchdog.MinimumQuietPeriod, int.MaxValue);
            var html = await GetTextAsync("/", token, automaticAction: true, actionAllowed: StillNeeded);
            var page = MissionGenerationPage.Read(html);
            if (page == null)
            { GenerationStatus("Generierung nicht angestoßen · Spieleinstellungen nicht eindeutig lesbar.", "unrecognized"); return; }
            if (page.Speed == 6)
            { GenerationStatus("Einsatzgenerierung im Spiel pausiert · kein Anstoß.", "paused"); return; }
            if (_generationWatchdog.MissionCount >= page.Maximum)
            { GenerationStatus("Einsatzlimit erreicht · kein Anstoß.", "at-limit"); return; }
            if (!_generationWatchdog.StillQuiet(revision.Value, UtcNow, page.RequiredQuiet, page.Maximum))
            { GenerationStatus("Weiter beobachten · neue Einsätze oder langsame Spielgeschwindigkeit.", "waiting"); return; }
            Logger?.RegisterSecret(page.CsrfToken);
            bool MaySend() => GenerationAllowed && _generationWatchdog.StillQuiet(revision.Value,
                UtcNow, page.RequiredQuiet, page.Maximum);
            var timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            // Same authenticated connection/proxy; no HTML or response script is executed.
            _ = await GetTextAsync($"/mission-generate?_={timestamp}", token, automaticAction: true,
                actionAllowed: MaySend, csrfToken: page.CsrfToken);
            _generationWatchdog.Requested(UtcNow);
            GenerationStatus("Anstoß gesendet · neue Einsätze werden in den normalen Abfragen beobachtet.", "requested");
        }
        catch (AutomaticActionPausedException)
        { GenerationStatus("Generierungsprüfung übersprungen · Automatik oder Einsatzlage hat sich geändert.", "cancelled"); }
        catch (AutomationUnavailableException)
        { GenerationStatus("Generierungsprüfung angehalten · Zugang nicht freigegeben.", "access-denied"); }
        catch (Exception ex) when (ex is not UnauthorizedAccessException && (ex is not OperationCanceledException || !token.IsCancellationRequested))
        {
            GenerationStatus("Generierungsprüfung ohne Bestätigung · frühestens in zehn Minuten erneut prüfen.", "unconfirmed");
            Logger?.Write(LogLevel.Warning, "Generation", "failed", "Generierungsprüfung fehlgeschlagen.");
        }
        finally { _generationGate.Release(); }
    }
}
