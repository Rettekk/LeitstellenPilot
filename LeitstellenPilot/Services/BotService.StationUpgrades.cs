using System;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed partial class BotService
{
    private Task? _stationUpgradeTask;
    public event Action<StationUpgradeResult>? StationUpgradeChanged;
    private bool CanUpgradeStations()
    {
        lock (_stateSync) if (!_hasSnapshot || _snapshotFailures > 0) return false;
        return IsRunning && CanAct();
    }
    private async Task StationUpgradeLoopAsync(CancellationToken token)
    {
        if (_api is not IStationUpgradeApi upgrades) return;
        var next = DateTime.MinValue; var previous = "";
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_config.Config.StationUpgrades.Enabled && UtcNow >= next && CanUpgradeStations())
                {
                    next = UtcNow.AddMinutes(Math.Clamp(_config.Config.StationUpgrades.IntervalMinutes, 1, 1440));
                    try
                    {
                        _log?.Write(LogLevel.Info, "StationUpgrade", "started", "Wachenausbau: Prüfung gestartet.");
                        StationUpgradeChanged?.Invoke(new(StationUpgradeState.Waiting, "Wachenausbau: Prüfung läuft."));
                        var result = await upgrades.UpgradeStationAsync(token);
                        StationUpgradeChanged?.Invoke(result);
                        if (result.Message != previous || result.State == StationUpgradeState.Confirmed)
                            _log?.Write(result.State == StationUpgradeState.Pending ? LogLevel.Warning : LogLevel.Info,
                                "StationUpgrade", "result", result.Message);
                        previous = result.Message;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        var step = ex.Data["StationUpgradeStep"] as string ?? "Prüfung";
                        var reason = ex is Newtonsoft.Json.JsonException ? "Serverantwort konnte nicht gelesen werden."
                            : ex is OperationCanceledException ? "Zeitüberschreitung beim Abruf."
                            : ex.Message;
                        var actionSent = ex.Data["StationUpgradeActionSent"] is true;
                        var safety = actionSent ? "Letzten Ausbau im Spiel prüfen; weitere Ausbauten bleiben bis zur Bestätigung gesperrt."
                            : "Kein weiterer Ausbau gesendet.";
                        var message = _log?.SanitizeForDisplay($"Wachenausbau unterbrochen: {step}. {reason} {safety}")
                            ?? $"Wachenausbau unterbrochen: {ex.GetType().Name}. {safety}";
                        _log?.Write(LogLevel.Warning, "StationUpgrade", "failed", message);
                        StationUpgradeChanged?.Invoke(new(StationUpgradeState.Pending, message));
                        if (!_api.IsLoggedIn) { _cancellation?.Cancel(); break; }
                    }
                    next = UtcNow.AddMinutes(Math.Clamp(_config.Config.StationUpgrades.IntervalMinutes, 1, 1440));
                }
                if (!_config.Config.StationUpgrades.Enabled) next = DateTime.MinValue;
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
