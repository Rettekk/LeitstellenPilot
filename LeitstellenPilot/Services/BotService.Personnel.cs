using System;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed partial class BotService
{
    private readonly SemaphoreSlim _personnelGate = new(1, 1);
    private int _personnelBusy;
    private bool _personnelAfterNightPending;
    private CancellationTokenSource? _personnelCancellation;
    public bool PersonnelBusy => Volatile.Read(ref _personnelBusy) != 0;
    public event Action<bool>? PersonnelBusyChanged;
    public event Action<PersonnelProgress>? PersonnelProgressChanged;
    public event Action<PersonnelRunReport>? PersonnelRunFinished;
    private sealed class PersonnelProgressRelay(Action<PersonnelProgress> changed) : IProgress<PersonnelProgress>
    { public void Report(PersonnelProgress value) => changed(value); }

    public Task<PersonnelRunReport> OptimizePersonnelAsync(CancellationToken token = default) => RunPersonnelAsync(false, false, token);
    public void CancelPersonnel() => _personnelCancellation?.Cancel();
    private async Task<PersonnelRunReport> RunPersonnelAsync(bool onlyChanged, bool automatic, CancellationToken token)
    {
        await _personnelGate.WaitAsync(token);
        Interlocked.Exchange(ref _personnelBusy, 1);
        PersonnelBusyChanged?.Invoke(true);
        using var linked = IsRunning && _cancellation != null
            ? CancellationTokenSource.CreateLinkedTokenSource(token, _cancellation.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(token);
        _personnelCancellation = linked;
        var acquired = false;
        var service = _api as ApiService;
        bool Allowed() => !linked.IsCancellationRequested && ActionsAllowed?.Invoke() != false &&
            (!automatic || _config.Config.BotSettings.AutoPersonnelAfterNightRest &&
                (!_config.Config.BotSettings.NightRestEnabled || NightRestPolicy.HasValidWindow(_config.Config.BotSettings) &&
                    !NightRestPolicy.RestUntil(_config.Config.BotSettings, UtcNow, _timeProvider.LocalTimeZone).HasValue));
        try
        {
            await _cycleGate.WaitAsync(linked.Token); acquired = true;
            if (!Allowed()) throw new OperationCanceledException("Personalprüfung nicht mehr freigegeben.");
            if (_api is not IPersonnelApi personnel) throw new InvalidOperationException("Personalprüfung wird nicht unterstützt.");
            if (service != null) service.PersonnelActionsAllowed = Allowed;
            PersonnelProgressChanged?.Invoke(new("Personalprüfung gestartet · Alarmierung pausiert. Alle Wachen mit Fahrzeugen werden geprüft."));
            var report = await personnel.OptimizePersonnelAsync(onlyChanged,
                new PersonnelProgressRelay(update => PersonnelProgressChanged?.Invoke(update)), linked.Token);
            if (report.Interrupted && IsRunning)
            {
                _cancellation?.Cancel();
                report.StopReason += (report.StopReason.Length > 0 ? " " : "") + "Automatik gestoppt. Personalprüfung erneut durchführen und den Bot danach manuell starten.";
            }
            PersonnelRunFinished?.Invoke(report); StatusChanged?.Invoke(report.Message);
            return report;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (IsRunning) _cancellation?.Cancel();
            var report = new PersonnelRunReport { Interrupted = true, StopReason = "Personalprüfung abgebrochen. Bereits bestätigte Änderungen bleiben gespeichert." };
            PersonnelRunFinished?.Invoke(report); StatusChanged?.Invoke(report.Message); return report;
        }
        catch
        {
            if (IsRunning) _cancellation?.Cancel();
            throw;
        }
        finally
        {
            if (service != null) service.PersonnelActionsAllowed = null;
            _personnelCancellation = null;
            lock (_stateSync)
            {
                if (automatic) _personnelAfterNightPending = false;
                _unchangedPlans.Clear(); _nextAutomaticPoll = DateTime.MinValue; _pendingWork = true;
            }
            // Queued scans must see the completed maintenance state when released.
            Interlocked.Exchange(ref _personnelBusy, 0);
            if (acquired) _cycleGate.Release();
            PersonnelBusyChanged?.Invoke(false);
            NextActionChanged?.Invoke(IsRunning && _cancellation?.IsCancellationRequested != true ? "Personalprüfung beendet · Automatik wird fortgesetzt." : "Bot gestoppt");
            _personnelGate.Release();
        }
    }
    private async Task<bool> TryPersonnelAfterNightAsync(CancellationToken token)
    {
        lock (_stateSync)
        {
            if (!_personnelAfterNightPending || PersonnelBusy) return false;
            if (!_config.Config.BotSettings.AutoPersonnelAfterNightRest || !_config.Config.BotSettings.NightRestEnabled)
            { _personnelAfterNightPending = false; return false; }
        }
        if (ActionsAllowed?.Invoke() == false || !NightRestPolicy.HasValidWindow(_config.Config.BotSettings) ||
            NightRestPolicy.RestUntil(_config.Config.BotSettings, UtcNow, _timeProvider.LocalTimeZone).HasValue) return false;
        try { await RunPersonnelAsync(true, true, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log?.Write(LogLevel.Warning, "Personnel", "night.failed", "Personalprüfung nach der Nachtruhe unterbrochen.");
            StatusChanged?.Invoke("Personalprüfung nach der Nachtruhe unterbrochen: " + ex.Message);
        }
        return true;
    }
}
