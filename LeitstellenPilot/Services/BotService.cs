using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed partial class BotService
{
    private readonly IGameApi _api;
    private readonly ConfigService _config;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan, CancellationToken, Task> _monitorDelay;
    private readonly object _stateSync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, int, int> _random;
    private readonly ProgramLog? _log;
    private readonly WorkRestPolicy _workRest = new();
    private bool _resting;
    private DateTime? _announcedRestUntil;
    private string _announcedRestReason = "";
    private DateTime _nextAutomaticPoll = DateTime.MinValue;
    private DateTime _lastStatusSnapshotAt = DateTime.MinValue;
    private DateTime _nextDetailReviewBatch = DateTime.MinValue;
    private PollSchedule _pollSchedule = new(10, "Erste Statusabfrage");
    private string _pollInputs = "";
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<long, StartedMission> _running = new();
    private readonly HashSet<long> _statisticsTracked = new();
    private readonly Dictionary<long, DateTime> _attempts = new();
    private readonly Dictionary<long, (string Signature, DateTime Confirmed)> _coverage = new();
    private readonly Dictionary<long, (string Signature, string Problem, string StaffingWait, bool HasUnrecognizedRequirements)> _shortfalls = new();
    private readonly Dictionary<long, DateTime> _detailReviewDue = new();
    private sealed record WaitingPlan(string Signature, string Fleet, DateTime Until, DateTime PriorityReviewAt,
        int ApproachMinutes, IReadOnlySet<string> AvailableVehicles);
    private readonly Dictionary<long, WaitingPlan> _unchangedPlans = new();
    private List<Mission> _missions = new();
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private Task? _monitorTask;
    private bool _hasSnapshot;
    private int _snapshotFailures;
    private bool _hasTransportRequests;
    private bool _pendingWork;
    private string _boardSignature = "";
    private string _fleetSignature = "";
    private DateTime _completionDiscoveryUntil = DateTime.MinValue;
    private DateTime _nextBonusCheck = DateTime.MinValue;
    private DateTime _lastBonusDate = DateTime.MinValue;
    private int _bonusAttemptsToday;
    private DateTime _nextTaskRewardsCheck = DateTime.MinValue;
    private DateTime _nextPromotionCheck = DateTime.MinValue;
    private DateTime _nextEventItemCheck = DateTime.MinValue;
    public bool IsRunning { get; private set; }
    public bool IsResting { get { lock (_stateSync) return _resting; } }
    public IReadOnlyList<Mission> CurrentMissions { get { lock (_stateSync) return _missions.ToArray(); } }
    public Func<bool>? ActionsAllowed { get; set; }
    public string RestReason { get { lock (_stateSync) return _workRest.RestReason; } }
    public event Action<string>? StatusChanged;
    public event Action<long>? MissionFinished;
    public event Action<string>? LastActionChanged;
    public event Action<string>? NextActionChanged;
    public event Action<bool>? RunningChanged;
    public event Action<IReadOnlyList<Mission>>? MissionsChanged;
    public event Action<IReadOnlyList<StartedMission>>? RunningMissionsChanged;
    public event Action<string>? DailyBonusStatusChanged;
    public event Action? DailyBonusClaimed;
    public event Action<string>? TaskRewardsStatusChanged;
    public event Action? TaskRewardsClaimed;
    public event Action<string>? PromotionStatusChanged;
    public event Action? PromotionClaimed;
    public event Action<PollSchedule, DateTime>? StatusPollScheduled;
    public event Action<bool, DateTime?>? RestChanged;

    public sealed record StartedMission(long MissionId, string MissionName, DateTime StartTime,
        DateTime? Completion, string State);

    public BotService(IGameApi api, ConfigService config, Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan, CancellationToken, Task>? monitorDelay = null, TimeProvider? timeProvider = null, ProgramLog? logger = null,
        Func<int, int, int>? random = null)
    {
        _api = api; _config = config; _delay = delay ?? Task.Delay; _monitorDelay = monitorDelay ?? Task.Delay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = random ?? Random.Shared.Next;
        _log = logger;
        _workRest.Start(UtcNow);
        if (api is ApiService service)
        {
            service.AutomaticActionsAllowed = CanAct;
            service.StationUpgradesAllowed = CanUpgradeStations;
            service.GenerationRecoveryAllowed = () => IsRunning && CanAct();
            service.PartialTopupsAllowed = mission =>
            {
                lock (_stateSync) return !_config.Config.BotSettings.ReassignBlockedMissions ||
                    (_api as IReassignmentApi)?.FocusMissionId == null && !_missions.Any(other => other.Id != mission.Id &&
                        other.vehicle_state > 0 && other.FleetBlockReason.Length == 0 && other.NeedsDispatch);
            };
        }
    }

    public void StartBot()
    {
        if (IsRunning || _runTask is { IsCompleted: false } || _monitorTask is { IsCompleted: false } || _stationUpgradeTask is { IsCompleted: false }) return;
        if (!_api.IsLoggedIn) { StatusChanged?.Invoke("Bitte zuerst anmelden."); return; }
        if (ActionsAllowed?.Invoke() == false) { StatusChanged?.Invoke("Spielaktionen sind derzeit pausiert."); return; }
        _cancellation?.Dispose();
        _cancellation = new();
        lock (_stateSync) { _workRest.Start(UtcNow); _resting = false; _personnelAfterNightPending = false; }
        IsRunning = true;
        (_api as IMissionGenerationApi)?.ResetMissionGenerationWatchdog();
        _log?.Write(LogLevel.Info, "Bot", "started", "Automatik gestartet.");
        RunningChanged?.Invoke(true);
        _monitorTask = MonitorAsync(_cancellation.Token);
        _runTask = RunAsync(_cancellation.Token);
        _stationUpgradeTask = StationUpgradeLoopAsync(_cancellation.Token);
    }

    public async Task StopAsync()
    {
        _cancellation?.Cancel();
        _personnelCancellation?.Cancel();
        try { await Task.WhenAll(_runTask ?? Task.CompletedTask, _monitorTask ?? Task.CompletedTask, _stationUpgradeTask ?? Task.CompletedTask); }
        catch (OperationCanceledException) { }
        (_api as IMissionGenerationApi)?.ResetMissionGenerationWatchdog();
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!CanAct())
                {
                    (_api as IMissionGenerationApi)?.ResetMissionGenerationWatchdog();
                    await _monitorDelay(TimeSpan.FromSeconds(5), token); continue;
                }
                PollSchedule schedule;
                lock (_stateSync)
                {
                    schedule = GetStatusPollSchedule();
                    StatusPollScheduled?.Invoke(schedule, _nextAutomaticPoll != DateTime.MinValue ? _nextAutomaticPoll : UtcNow.AddSeconds(schedule.Seconds));
                }
                await _monitorDelay(TimeSpan.FromSeconds(schedule.Seconds), token);
                if (!CanAct()) continue;
                try
                {
                    await RefreshMissionsAsync(token, force: false);
                    lock (_stateSync) if (_snapshotFailures > 0) continue;
                    if (IsRunning && CanAct() && _api is IMissionGenerationApi generation)
                        await generation.CheckMissionGenerationAsync(token);
                    if (CanAct() && _api is IFleetApi fleet)
                    {
                        var vehicles = await fleet.GetVehiclesAsync(token: token);
                        lock (_stateSync)
                        {
                            var signature = PlanningFleetSignature(vehicles);
                            _hasTransportRequests = vehicles.Any(v => v.State == 5);
                            if (signature != _fleetSignature && (_hasTransportRequests || _missions.Any(m => m.NeedsDispatch))) _pendingWork = true;
                            _fleetSignature = signature;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log?.Write(LogLevel.Error, "Bot", "monitor.failed", "Statusaktualisierung fehlgeschlagen.");
                    StatusChanged?.Invoke("Statusaktualisierung fehlgeschlagen: " + ex.Message);
                    if (!_api.IsLoggedIn) { _cancellation?.Cancel(); break; }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var canAct = CanAct();
                if (await TryPersonnelAfterNightAsync(token)) continue;
                if (!canAct)
                {
                    var waitingForAccess = ActionsAllowed?.Invoke() == false;
                    lock (_stateSync) NextActionChanged?.Invoke(PersonnelBusy ? "Personalprüfung läuft · Alarmierung pausiert."
                        : waitingForAccess
                        ? "Spielaktionen sind vorübergehend pausiert."
                        : _workRest.RestUntil.HasValue ? $"{_workRest.RestReason} bis {_workRest.RestUntil.Value.ToLocalTime():HH:mm}" : _workRest.RestReason);
                    await _delay(TimeSpan.FromSeconds(waitingForAccess ? 1 : 5), token);
                    continue;
                }
                try { await ScanOnceAsync(token, forceRefresh: false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log?.Write(LogLevel.Error, "Bot", "scan.failed", "Automatischer Durchlauf fehlgeschlagen.");
                    StatusChanged?.Invoke("Scan fehlgeschlagen: " + ex.Message);
                    if (!_api.IsLoggedIn) break;
                }
                var seconds = GetWorkSchedule().Seconds;
                if (HasPendingWork()) continue;
                NextActionChanged?.Invoke("Bereit · warte auf aktualisierten Status.");
                for (var remaining = seconds; remaining > 0; remaining--)
                {
                    // A new open mission seen by the monitor can bring the next
                    // scan forward while the action worker is counting down.
                    remaining = Math.Min(remaining, GetWorkSchedule().Seconds);
                    if (HasPendingWork() || !CanAct()) break;
                    await _delay(TimeSpan.FromSeconds(1), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _cancellation?.Cancel();
            IsRunning = false;
            _log?.Write(LogLevel.Info, "Bot", "stopped", "Automatik gestoppt.");
            lock (_stateSync) { _resting = false; RestChanged?.Invoke(false, null); }
            RunningChanged?.Invoke(false);
            NextActionChanged?.Invoke("Bot gestoppt");
        }
    }

    private bool HasPendingWork()
    {
        lock (_stateSync) return _pendingWork && _snapshotFailures == 0;
    }

    public async Task RefreshMissionsAsync(CancellationToken token = default, bool force = true)
    {
        await _refreshGate.WaitAsync(token);
        try
        {
            lock (_stateSync)
                if (!force && UtcNow < _nextAutomaticPoll) return;
            if (!force && !CanAct()) return;
            try { ApplySnapshot(await _api.GetMissionsAsync(token)); }
            catch (Exception ex) when (ex is not UnauthorizedAccessException &&
                                      (ex is not OperationCanceledException || !token.IsCancellationRequested))
            {
                lock (_stateSync)
                {
                    _snapshotFailures = Math.Min(_snapshotFailures + 1, 5);
                    _pollSchedule = new(Math.Min(300, 30 << (_snapshotFailures - 1)), "Abruf fehlgeschlagen; mit Abstand erneut prüfen");
                    _nextAutomaticPoll = UtcNow.AddSeconds(_pollSchedule.Seconds);
                    StatusPollScheduled?.Invoke(_pollSchedule, _nextAutomaticPoll);
                    _log?.Write(LogLevel.Error, "Bot", "snapshot.failed", "Einsatzliste konnte nicht aktualisiert werden.");
                }
                throw;
            }
        }
        finally { _refreshGate.Release(); }
    }

    private PollSchedule GetStatusPollSchedule()
    {
        lock (_stateSync) return _nextAutomaticPoll != DateTime.MinValue
            ? new PollSchedule(Math.Clamp((int)Math.Ceiling((_nextAutomaticPoll - UtcNow).TotalSeconds), 1, 600), _pollSchedule.Reason)
            : CalculatePollSchedule();
    }

    private PollSchedule GetWorkSchedule()
    {
        lock (_stateSync)
        {
            var schedule = GetStatusPollSchedule();
            if (_api is IActiveMissionApi && _config.Config.BotSettings.AutoAlarm && _snapshotFailures == 0)
            {
                var due = _missions.Where(NeedsActiveReview).Select(mission => _detailReviewDue.GetValueOrDefault(mission.Id)).DefaultIfEmpty(DateTime.MaxValue).Min();
                if (due != DateTime.MaxValue)
                {
                    if (due < _nextDetailReviewBatch) due = _nextDetailReviewBatch;
                    var seconds = (int)Math.Clamp(Math.Ceiling((due - UtcNow).TotalSeconds), 1, 600);
                    if (seconds < schedule.Seconds) return new(seconds, "Aktive Einsätze auf neue Nachforderungen prüfen");
                }
            }
            return schedule;
        }
    }

    private PollSchedule CalculatePollSchedule(DateTime? referenceTime = null, bool drawInterval = true)
    {
            var activeIds = _missions.Where(m => m.IsActive).Select(m => m.Id).ToHashSet();
            var schedule = PollingPolicy.Next(_config.Config.BotSettings, _missions,
                _running.Values.Where(m => activeIds.Contains(m.MissionId)).Select(m => m.Completion),
                referenceTime ?? UtcNow, _hasSnapshot, _hasTransportRequests || _missions.Any(m => m.HasPrisonerTransportRequest),
                random: drawInterval ? _random : (minimum, _) => minimum);
            return UtcNow < _completionDiscoveryUntil
                ? new(Math.Min(schedule.Seconds, Math.Clamp(_config.Config.BotSettings.ScanIntervalSeconds, 5, 300)), "Nach Einsatzabschluss auf Nachfolgeeinsätze prüfen")
                : schedule;
    }

    private void SchedulePoll(bool freshSnapshot = false)
    {
        if (_snapshotFailures > 0) return;
        var settings = _config.Config.BotSettings;
        var inputs = $"{settings.ScanIntervalSeconds}:{settings.MaxScanIntervalSeconds}:{settings.AdaptivePolling}:{settings.ActivePollingSeconds}:{settings.IdlePollingSeconds}:{_hasTransportRequests}:" +
            (settings.AdaptivePolling ? string.Join(";", _missions.Select(mission => $"{mission.Id}:{mission.NeedsDispatch}:{mission.EstimatedCompletion?.Ticks}")) : "") +
            "|" + string.Join(";", _missions.Where(mission => mission.sw && settings.AutoPlannedMissions).Select(mission => $"{mission.Id}:{mission.NextScheduledReviewAt?.Ticks}")) +
            "|" + (UtcNow < _completionDiscoveryUntil);
        if (!freshSnapshot && _nextAutomaticPoll != DateTime.MinValue && inputs == _pollInputs) return;
        _pollInputs = inputs;
        var anchor = freshSnapshot || _lastStatusSnapshotAt == DateTime.MinValue ? UtcNow : _lastStatusSnapshotAt;
        var schedule = CalculatePollSchedule(anchor, drawInterval: freshSnapshot || _nextAutomaticPoll == DateTime.MinValue);
        var due = anchor.AddSeconds(schedule.Seconds);
        // Actions and detail reviews may bring a deadline forward, never postpone an unread board snapshot.
        if (!freshSnapshot && _nextAutomaticPoll != DateTime.MinValue && due >= _nextAutomaticPoll) return;
        _pollSchedule = schedule; _nextAutomaticPoll = due;
        StatusPollScheduled?.Invoke(_pollSchedule, _nextAutomaticPoll);
        _log?.Write(LogLevel.Debug, "Bot", "poll.scheduled", _pollSchedule.Reason);
    }

    private bool CanAct()
    {
        if (PersonnelBusy) return false;
        if (ActionsAllowed?.Invoke() == false) return false;
        lock (_stateSync)
        {
            var allowed = _workRest.CanRun(_config.Config.BotSettings, UtcNow, string.IsNullOrWhiteSpace(_config.Config.TimeZone) ? _timeProvider.LocalTimeZone : LaunchOptions.ResolveTimeZone(_config.Config));
            if (allowed && _resting && _announcedRestReason == "Nachtruhe" && _config.Config.BotSettings.NightRestEnabled &&
                _config.Config.BotSettings.AutoPersonnelAfterNightRest && _api is IPersonnelApi)
                _personnelAfterNightPending = true;
            if (_resting == allowed || !allowed && (_announcedRestUntil != _workRest.RestUntil || _announcedRestReason != _workRest.RestReason))
            {
                _resting = !allowed;
                (_api as IMissionGenerationApi)?.ResetMissionGenerationWatchdog();
                _announcedRestUntil = _workRest.RestUntil;
                _announcedRestReason = _workRest.RestReason;
                if (allowed) _nextAutomaticPoll = DateTime.MinValue;
                RestChanged?.Invoke(_resting, _workRest.RestUntil);
                _log?.Write(LogLevel.Info, "Bot", _resting ? "rest.started" : "rest.finished",
                    _resting ? $"{_workRest.RestReason} begonnen / aktualisiert." : "Ruhepause beendet; Automatik wird fortgesetzt.");
            }
            return allowed && !_personnelAfterNightPending;
        }
    }

    private void ApplySnapshot(List<Mission> missions)
    {
        lock (_stateSync) ApplySnapshotLocked(missions);
    }

    private void ApplySnapshotLocked(List<Mission> missions)
    {
        _hasSnapshot = true;
        _lastStatusSnapshotAt = UtcNow;
        if (_snapshotFailures > 0) _log?.Write(LogLevel.Info, "Bot", "snapshot.recovered", "Einsatzliste nach Abruffehler wieder verfügbar.");
        _snapshotFailures = 0;
        // Only a successfully parsed server snapshot may remove tracked missions.
        var ids = missions.Select(m => m.Id).ToHashSet();
        foreach (var completed in _statisticsTracked.Where(id => !ids.Contains(id)).ToArray())
        { _statisticsTracked.Remove(completed); MissionFinished?.Invoke(completed); }
        foreach (var completed in _running.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _completionDiscoveryUntil = UtcNow.AddSeconds(30);
            _running.Remove(completed);
            _log?.Write(LogLevel.Info, "Bot", "mission.completed", $"Einsatz {completed} abgeschlossen / nicht mehr in eigener Einsatzliste.");
            LastActionChanged?.Invoke($"Einsatz {completed} abgeschlossen / nicht mehr in der eigenen Einsatzliste.");
        }
        foreach (var removed in _coverage.Keys.Concat(_shortfalls.Keys).Concat(_attempts.Keys).Where(id => !ids.Contains(id)).Distinct().ToList())
        { _coverage.Remove(removed); _shortfalls.Remove(removed); _attempts.Remove(removed); }
        foreach (var removed in _detailReviewDue.Keys.Where(id => !ids.Contains(id)).ToArray()) _detailReviewDue.Remove(removed);
        foreach (var removed in _unchangedPlans.Keys.Where(id => !ids.Contains(id)).ToArray()) _unchangedPlans.Remove(removed);
        var previousMissions = _missions.ToDictionary(mission => mission.Id);
        _missions = missions;
        foreach (var mission in missions)
        {
            bool previousRecognition = false;
            if (previousMissions.TryGetValue(mission.Id, out var previous))
            {
                mission.HasLiveRequirements = previous.HasLiveRequirements;
                mission.LiveRequirementsSignature = previous.LiveRequirementsSignature;
                mission.LiveRequirementText = previous.LiveRequirementText;
                var definitionChanged = previous.TypeKey != mission.TypeKey ||
                    !string.Equals((previous.caption ?? "").Trim(), (mission.caption ?? "").Trim(), StringComparison.Ordinal);
                var newPeople = mission.patients_count > previous.patients_count || mission.prisoners_count > previous.prisoners_count;
                if (!definitionChanged && !newPeople && previous.CoverageSignature == mission.CoverageSignature)
                    previousRecognition = previous.HasUnrecognizedRequirements;
                mission.RequiresDetailsReview = previous.RequiresDetailsReview || definitionChanged || newPeople;
                if (definitionChanged || newPeople)
                { _coverage.Remove(mission.Id); _shortfalls.Remove(mission.Id); _attempts.Remove(mission.Id); }
                if (previous.vehicle_state != 2 && mission.vehicle_state == 2) _detailReviewDue.Remove(mission.Id);
            }
            mission.SnapshotReceivedAt ??= UtcNow;
            mission.IsDispatchCovered = false;
            mission.PlanningProblem = "";
            mission.HasUnrecognizedRequirements = previousRecognition;
            mission.PlanningWaitReason = "";
            mission.WaitingForPersonnel = "";
            if (mission.FleetBlockReason.Length > 0)
            {
                _statisticsTracked.Remove(mission.Id);
                _running.Remove(mission.Id); _coverage.Remove(mission.Id); _attempts.Remove(mission.Id);
                continue;
            }
            if (_coverage.TryGetValue(mission.Id, out var covered))
            {
                if (covered.Signature == mission.CoverageSignature &&
                    (mission.vehicle_state > 0 || UtcNow - covered.Confirmed < TimeSpan.FromMinutes(1)))
                    mission.IsDispatchCovered = true;
                else { _coverage.Remove(mission.Id); _attempts.Remove(mission.Id); }
            }
            if (_shortfalls.TryGetValue(mission.Id, out var shortfall))
            {
                if (mission.vehicle_state == 2 && !mission.HasMissingVehicles && !mission.HasLiveRequirements)
                { _shortfalls.Remove(mission.Id); mission.HasUnrecognizedRequirements = false; }
                else if (shortfall.Signature == mission.CoverageSignature)
                { mission.PlanningProblem = shortfall.Problem; mission.WaitingForPersonnel = shortfall.StaffingWait; mission.HasUnrecognizedRequirements = shortfall.HasUnrecognizedRequirements; }
                else { _shortfalls.Remove(mission.Id); _attempts.Remove(mission.Id); }
            }
            if (mission.vehicle_state > 0 || mission.IsDispatchCovered) Track(mission, mission.StatusText);
            else if (_running.TryGetValue(mission.Id, out var pending) &&
                     UtcNow - pending.StartTime >= TimeSpan.FromMinutes(2))
                _running.Remove(mission.Id);
        }
        var signature = string.Join(";", missions.OrderBy(m => m.Id).Select(m => $"{m.Id}:{m.vehicle_state}:{m.CoverageSignature}"));
        if (signature != _boardSignature && missions.Any(m => m.NeedsDispatch))
        {
            _pendingWork = true;
            _log?.Write(LogLevel.Debug, "Bot", "work.ready", "Neue offene Einsätze / Nachforderungen werden ohne zusätzlichen Scan-Countdown ausgewertet.");
        }
        _boardSignature = signature;
        SchedulePoll(freshSnapshot: true);
        PublishBoard();
    }

    public async Task ScanOnceAsync(CancellationToken token = default, bool forceRefresh = true)
    {
        await _cycleGate.WaitAsync(token);
        try
        {
            if (!CanAct()) return;
            await RefreshMissionsAsync(token, force: forceRefresh);
            lock (_stateSync) if (_snapshotFailures > 0) return;
            if (!CanAct()) return;
            await ReviewActiveMissionsAsync(token);
            if (!CanAct()) return;
            var checkedMissions = new HashSet<long>();
            if (_api is IPlannedMissionApi plannedApi)
            {
                try
                {
                    var planned = await plannedApi.PlanScheduledMissionsAsync(_missions.ToArray(), _running.Count, token);
                    lock (_stateSync) { SchedulePoll(); PublishBoard(); }
                    if (planned != null)
                    {
                        var scheduledMission = _missions.FirstOrDefault(mission => mission.Id == planned.MissionId);
                        if (scheduledMission != null) ApplyDispatchResult(scheduledMission, planned.Dispatch);
                        LastActionChanged?.Invoke(planned.Dispatch.Message);
                        if (planned.Dispatch.Sent)
                        {
                            lock (_stateSync) _pendingWork = true;
                            await ProcessTransportsAsync(token);
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or UnauthorizedAccessException))
                { _log?.Write(LogLevel.Error, "Scheduled", "failed", "Planung angekündigter Einsätze fehlgeschlagen; normale Einsätze werden weiter geprüft."); }
            }
            if (!CanAct()) return;
            if (_config.Config.BotSettings.AutoAlarm && await DispatchBatchAsync(token, checkedMissions, urgentOnly: true, forcePlanReview: forceRefresh))
            {
                await ProcessTransportsAsync(token);
                return;
            }
            if (!CanAct()) return;
            if (_config.Config.BotSettings.AutoClaimDailyBonus && _api is IDailyBonusApi bonusApi &&
                (_timeProvider.GetLocalNow().Date != _lastBonusDate || _bonusAttemptsToday < 2 && UtcNow >= _nextBonusCheck))
            {
                if (_timeProvider.GetLocalNow().Date != _lastBonusDate) _bonusAttemptsToday = 0;
                _lastBonusDate = _timeProvider.GetLocalNow().Date;
                _bonusAttemptsToday++;
                var tomorrow = TimeZoneInfo.ConvertTimeToUtc(_lastBonusDate.AddDays(1), _timeProvider.LocalTimeZone);
                try
                {
                    var bonus = await bonusApi.CollectDailyBonusAsync(token);
                    _nextBonusCheck = (bonus.State is DailyBonusState.Unknown or DailyBonusState.Pending) && _bonusAttemptsToday < 2
                        ? UtcNow.AddMinutes(5) : tomorrow;
                    if (bonus.State == DailyBonusState.Paused) { _bonusAttemptsToday--; _nextBonusCheck = UtcNow.AddMinutes(5); }
                    DailyBonusStatusChanged?.Invoke(bonus.Message);
                    if (bonus.Claimed) DailyBonusClaimed?.Invoke();
                    LastActionChanged?.Invoke(bonus.Message);
                }
                catch (Exception ex) when (ex is not UnauthorizedAccessException && (ex is not OperationCanceledException || !token.IsCancellationRequested))
                {
                    _nextBonusCheck = _bonusAttemptsToday < 2 ? UtcNow.AddMinutes(5) : tomorrow;
                    _log?.Write(LogLevel.Error, "Bonus", "failed", _bonusAttemptsToday < 2
                        ? "Bonusprüfung fehlgeschlagen; einmaliger weiterer Versuch in fünf Minuten."
                        : "Bonusprüfung erneut fehlgeschlagen; nächste automatische Prüfung am nächsten Tag.");
                    DailyBonusStatusChanged?.Invoke("Bonusprüfung fehlgeschlagen: " + ex.Message);
                }
            }
            if (!CanAct()) return;
            if (_config.Config.BotSettings.AutoClaimTaskRewards && _api is ITaskRewardsApi taskRewardsApi && UtcNow >= _nextTaskRewardsCheck)
            {
                _nextTaskRewardsCheck = UtcNow.AddMinutes(15);
                try
                {
                    var rewards = await taskRewardsApi.CollectTaskRewardsAsync(token);
                    if (rewards.State == TaskRewardsState.Paused) _nextTaskRewardsCheck = UtcNow;
                    else if (rewards.State is TaskRewardsState.Unknown or TaskRewardsState.Pending) _nextTaskRewardsCheck = UtcNow.AddMinutes(5);
                    TaskRewardsStatusChanged?.Invoke(rewards.Message);
                    if (rewards.ClaimedCount > 0) TaskRewardsClaimed?.Invoke();
                }
                catch (Exception ex) when (ex is not UnauthorizedAccessException && (ex is not OperationCanceledException || !token.IsCancellationRequested))
                {
                    _nextTaskRewardsCheck = UtcNow.AddMinutes(5);
                    _log?.Write(LogLevel.Error, "Tasks", "failed", "Aufgabenprüfung fehlgeschlagen; weiterer Versuch frühestens in fünf Minuten.");
                    TaskRewardsStatusChanged?.Invoke("Aufgabenprüfung fehlgeschlagen: " + ex.Message);
                }
            }
            if (!CanAct()) return;
            if (_config.Config.BotSettings.AutoPromotion && _api is IPromotionApi promotionApi && UtcNow >= _nextPromotionCheck)
            {
                _nextPromotionCheck = UtcNow.AddMinutes(15);
                try
                {
                    var promotion = await promotionApi.CollectPromotionAsync(token);
                    if (promotion.State == PromotionState.Paused) _nextPromotionCheck = UtcNow;
                    else if (promotion.State is PromotionState.Unknown or PromotionState.Pending) _nextPromotionCheck = UtcNow.AddMinutes(5);
                    PromotionStatusChanged?.Invoke(promotion.Message);
                    if (promotion.Promoted) PromotionClaimed?.Invoke();
                }
                catch (Exception ex) when (ex is not UnauthorizedAccessException && (ex is not OperationCanceledException || !token.IsCancellationRequested))
                {
                    _nextPromotionCheck = UtcNow.AddMinutes(5);
                    _log?.Write(LogLevel.Error, "Promotion", "failed", "Beförderungsprüfung fehlgeschlagen; weiterer Versuch frühestens in fünf Minuten.");
                    PromotionStatusChanged?.Invoke("Beförderungsprüfung fehlgeschlagen: " + ex.Message);
                }
            }
            if (!CanAct()) return;
            await ProcessTransportsAsync(token);
            if (!CanAct()) return;
            if (_config.Config.BotSettings.AutoCollectEventItems && _api is IEventItemApi eventItems && UtcNow >= _nextEventItemCheck)
            {
                _nextEventItemCheck = UtcNow.AddMinutes(1);
                try
                {
                    var item = await eventItems.CollectEventItemAsync(token);
                    if (item != null)
                    {
                        LastActionChanged?.Invoke(item.Message);
                        _log?.Write(item.Claimed ? LogLevel.Info : LogLevel.Warning, "EventItem", "collection", item.Message);
                    }
                }
                catch (Exception ex) when (ex is not UnauthorizedAccessException && (ex is not OperationCanceledException || !token.IsCancellationRequested))
                { _log?.Write(LogLevel.Warning, "EventItem", "failed", "Kürbisprüfung unterbrochen; wird später erneut geprüft."); }
            }
            if (!CanAct()) return;
            if (!_config.Config.BotSettings.AutoAlarm)
            {
                lock (_stateSync) { _pendingWork = false; StatusChanged?.Invoke($"{_missions.Count} Einsätze geladen. Automatische Alarmierung ist ausgeschaltet."); }
                return;
            }

            if (_config.Config.BotSettings.ReleaseUnsolvableMissions && _api is IReassignmentApi releasing)
            {
                try
                {
                    var released = await releasing.ReleaseUnsolvableAsync(_missions.ToArray(), token);
                    lock (_stateSync)
                    {
                        foreach (var blocked in _missions.Where(mission => mission.FleetBlockReason.Length > 0))
                        { _running.Remove(blocked.Id); _statisticsTracked.Remove(blocked.Id); _coverage.Remove(blocked.Id); _attempts.Remove(blocked.Id); blocked.IsDispatchCovered = false; }
                        PublishBoard();
                    }
                    if (released != null)
                    {
                        LastActionChanged?.Invoke(released.Message);
                        if (released.ActionSent) { lock (_stateSync) _pendingWork = true; return; }
                    }
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or UnauthorizedAccessException))
                { _log?.Write(LogLevel.Error, "Fleet", "release.failed", "Freigabe unlösbarer Einsätze fehlgeschlagen; nächste Aktion prüft die Zuordnung erneut."); }
            }
            if (!CanAct()) return;
            if (_config.Config.BotSettings.ReassignBlockedMissions && _api is IReassignmentApi restoration)
            {
                try
                {
                    var restored = await restoration.RestoreLoanAsync(_missions.ToArray(), token);
                    if (restored != null)
                    {
                        LastActionChanged?.Invoke(restored.Message);
                        if (restored.TargetDispatch?.Plan is { MissionId: > 0 } targetPlan)
                        {
                            var restoredTarget = _missions.FirstOrDefault(mission => mission.Id == targetPlan.MissionId);
                            if (restoredTarget != null) ApplyDispatchResult(restoredTarget, restored.TargetDispatch);
                        }
                        lock (_stateSync) { _coverage.Remove(restored.SourceMissionId); _attempts.Remove(restored.SourceMissionId); }
                        if (restored.ActionSent) { lock (_stateSync) _pendingWork = true; return; }
                    }
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or UnauthorizedAccessException))
                { _log?.Write(LogLevel.Error, "Reassignment", "restore.failed", "Rückversorgung fehlgeschlagen; gespeicherte Ausleihe bleibt zur Prüfung erhalten."); }
            }

            await DispatchBatchAsync(token, checkedMissions, forcePlanReview: forceRefresh);
        }
        finally { _cycleGate.Release(); }
    }

    private bool NeedsActiveReview(Mission mission) => mission.FleetBlockReason.Length == 0 && !mission.NeedsDispatch &&
        (mission.vehicle_state > 0 || mission.IsDispatchCovered) && (!mission.sw || mission.PlannedStartAt is not { } start || start <= UtcNow);

    private async Task<IReadOnlyList<TransportResult>> ProcessTransportsAsync(CancellationToken token, long? missionId = null)
    {
        if (!CanAct() || !_config.Config.BotSettings.AutoTransport || _api is not ITransportApi transportApi) return [];
        try
        {
            var results = await transportApi.HandleTransportRequestsAsync(token, missionId);
            foreach (var transport in results) LastActionChanged?.Invoke(transport.Message);
            return results;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or UnauthorizedAccessException))
        {
            _log?.Write(LogLevel.Error, "Transport", "failed", "Transportprüfung fehlgeschlagen.");
            var result = new TransportResult(false, "Transportprüfung fehlgeschlagen: " + ex.Message);
            LastActionChanged?.Invoke(result.Message);
            return [result];
        }
    }

    private async Task ReviewActiveMissionsAsync(CancellationToken token)
    {
        if (!_config.Config.BotSettings.AutoAlarm || _api is not IActiveMissionApi inspector) return;
        Mission[] batch;
        lock (_stateSync)
        {
            if (UtcNow < _nextDetailReviewBatch) return;
            batch = _missions.Where(NeedsActiveReview).Where(mission => _detailReviewDue.GetValueOrDefault(mission.Id) <= UtcNow)
                .OrderBy(mission => _detailReviewDue.GetValueOrDefault(mission.Id)).ThenByDescending(mission => mission.patients_count > 0).ThenBy(mission => mission.Id).Take(2).ToArray();
            if (batch.Length > 0) _nextDetailReviewBatch = UtcNow.AddSeconds(5);
        }
        if (batch.Length > 0) NextActionChanged?.Invoke($"Prüfe Nachforderungen · {batch.Length} {(batch.Length == 1 ? "Einsatz" : "Einsätze")}");
        foreach (var mission in batch)
        {
            token.ThrowIfCancellationRequested();
            if (!CanAct() || !_config.Config.BotSettings.AutoAlarm) return;
            var observedSignature = mission.CoverageSignature;
            lock (_stateSync) _detailReviewDue[mission.Id] = UtcNow.AddSeconds(DetailReviewInterval);
            try
            {
                var review = await inspector.InspectActiveMissionAsync(mission, token);
                lock (_stateSync)
                {
                    _detailReviewDue[mission.Id] = UtcNow.AddSeconds(DrawDetailReviewInterval());
                    var current = _missions.FirstOrDefault(current => current.Id == mission.Id);
                    if (current == null || current.CoverageSignature != observedSignature) continue;
                    var changed = current.LiveRequirementsSignature != review.Signature;
                    var wasLive = current.HasLiveRequirements;
                    current.HasLiveRequirements = review.HasRequirements;
                    current.LiveRequirementsSignature = review.Signature;
                    current.LiveRequirementText = review.RequirementText;
                    if (review.HasRequirements && changed)
                    {
                        current.IsDispatchCovered = false; current.RequiresDetailsReview = true;
                        current.PlanningProblem = ""; current.PlanningWaitReason = "";
                        current.HasUnrecognizedRequirements = review.HasUnrecognizedRequirements;
                        _coverage.Remove(current.Id); _shortfalls.Remove(current.Id); _attempts.Remove(current.Id);
                        _pendingWork = true;
                        _log?.Write(LogLevel.Info, "Bot", "detail.shortage", current.DisplayTitle + ": " + review.RequirementText);
                    }
                    else if (!review.HasRequirements && wasLive && !current.HasMissingVehicles)
                    {
                        current.RequiresDetailsReview = false; current.PlanningProblem = ""; current.WaitingForPersonnel = "";
                        current.HasUnrecognizedRequirements = false;
                        _shortfalls.Remove(current.Id); _coverage.Remove(current.Id);
                    }
                    PublishBoard();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception)
            {
                lock (_stateSync) _detailReviewDue[mission.Id] = UtcNow.AddSeconds(Math.Max(30, DrawDetailReviewInterval()));
                _log?.Write(LogLevel.Warning, "Bot", "detail.failed", "Detailprüfung fehlgeschlagen; Übersicht und bisheriger Status bleiben erhalten.");
            }
        }
        if (batch.Length > 0) lock (_stateSync) _nextDetailReviewBatch = UtcNow.AddSeconds(_random(5, 9));
    }

    private int DetailReviewInterval => Math.Clamp(_config.Config.BotSettings.DetailReviewIntervalSeconds, 10, 120);
    private int DrawDetailReviewInterval() => _random(DetailReviewInterval,
        Math.Clamp(_config.Config.BotSettings.MaxDetailReviewIntervalSeconds, DetailReviewInterval, 120) + 1);
    private static string PlanningVehicleKey(Vehicle vehicle) =>
        $"{vehicle.Id}:{vehicle.TypeId}:{vehicle.State}:{vehicle.TargetType}:{vehicle.TargetId}:{vehicle.QueuedMissionId}:{vehicle.Personnel}:{vehicle.MaximumPersonnel}:{vehicle.BuildingId}";
    private static string PlanningFleetSignature(IEnumerable<Vehicle> fleet) => string.Join(";", fleet.OrderBy(vehicle => vehicle.Id).Select(PlanningVehicleKey));
    private HashSet<string> AvailablePlanningVehicles(IEnumerable<Vehicle> fleet) => fleet
        .Where(vehicle => vehicle.IsAvailable || _config.Config.BotSettings.ReassignBlockedMissions && vehicle.State == 4)
        .Select(PlanningVehicleKey).ToHashSet();

    private async Task<bool> DispatchBatchAsync(CancellationToken token, ISet<long> checkedMissions, bool urgentOnly = false, bool forcePlanReview = false)
    {
        var max = Math.Clamp(_config.Config.BotSettings.MaxConcurrentMissions, 1, 100);
        var attempted = 0;
        var alarmsSent = 0;
        var alarmLimit = Math.Clamp(_config.Config.BotSettings.MaxAlarmsPerCycle, 1, 100);
        var fleetApi = _api as IFleetApi;
        var fleetSnapshot = fleetApi == null ? null : await fleetApi.GetVehiclesAsync(token: token);
        var approach = ApproachPolicy.NormalizeMinutes(_config.Config.BotSettings.MaxApproachMinutes);
        var planningFleet = (fleetSnapshot == null ? "?" : PlanningFleetSignature(fleetSnapshot)) + "|approach:" + approach;
        var availableVehicles = AvailablePlanningVehicles(fleetSnapshot ?? []);
        var hasFreeVehicles = fleetSnapshot == null || fleetSnapshot.Any(v => v.IsAvailable);
        if (fleetSnapshot != null) lock (_stateSync) _hasTransportRequests = fleetSnapshot.Any(v => v.State == 5);
        Mission[] batch;
        lock (_stateSync)
        {
            _pendingWork = false;
            batch = MissionPriority.Order(_missions, _config.Config.BotSettings).Where(mission => !checkedMissions.Contains(mission.Id) &&
                (!urgentOnly || mission.HasMissingVehicles || mission.HasLiveRequirements ||
                    mission.RequiresDetailsReview && (mission.vehicle_state > 0 || _running.ContainsKey(mission.Id))))
                .OrderByDescending(mission => (_api as IReassignmentApi)?.FocusMissionId == mission.Id)
                .ThenBy(mission => mission.vehicle_state == 0 && !_running.ContainsKey(mission.Id) &&
                    _unchangedPlans.TryGetValue(mission.Id, out var waiting) && waiting.Signature == mission.CoverageSignature &&
                    waiting.ApproachMinutes == approach && UtcNow < waiting.PriorityReviewAt &&
                    availableVehicles.IsSubsetOf(waiting.AvailableVehicles)).ToArray();
        }
        foreach (var mission in batch)
        {
            token.ThrowIfCancellationRequested();
            if (!_config.Config.BotSettings.AutoAlarm) break;
            if (!CanAct()) break;
            if (mission.FleetBlockReason.Length > 0) { TraceSkip(mission, "Mit aktuellem Fuhrpark nicht lösbar; keine erneute Alarmierung."); continue; }
            if (mission.IsDispatchCovered || mission.vehicle_state == 2 && !mission.HasMissingVehicles && !mission.HasLiveRequirements && !mission.RequiresDetailsReview ||
                mission.sw)
            { TraceSkip(mission, "Bereits abgedeckt / in Bearbeitung oder Sicherheitswache noch nicht gestartet."); continue; }
            lock (_stateSync)
            {
                if (!forcePlanReview && _unchangedPlans.TryGetValue(mission.Id, out var waiting) && waiting.Signature == mission.CoverageSignature &&
                    waiting.Fleet == planningFleet && UtcNow < waiting.Until)
                { TraceSkip(mission, "Unveränderter Bedarf und Fahrzeugbestand; nächste gezielte Prüfung abwarten."); continue; }
                if (!_missions.Any(m => m.Id == mission.Id)) { TraceSkip(mission, "Einsatz inzwischen aus der Liste verschwunden."); continue; }
                if ((_api as IReassignmentApi)?.FocusMissionId is { } focus && focus != mission.Id &&
                    mission.vehicle_state == 0 && !_running.ContainsKey(mission.Id))
                { mission.PlanningWaitReason = $"Wartet: Kräfte werden für begonnenen Einsatz {focus} zusammengeführt."; continue; }
                if (!_running.ContainsKey(mission.Id) && _running.Count + (_api is IPlannedMissionApi reservations ? reservations.ReservedPlannedSlots : 0) >= max)
                {
                    mission.PlanningWaitReason = $"Wartet: Grenze von {max} parallelen Einsätzen erreicht; geplante Reservierungen zählen mit.";
                    TraceSkip(mission, mission.PlanningWaitReason); continue;
                }
                if (!hasFreeVehicles && mission.vehicle_state == 0 && !_running.ContainsKey(mission.Id) &&
                    !(mission.HasMissingVehicles && _config.Config.BotSettings.ReassignBlockedMissions && _api is IReassignmentApi))
                { mission.PlanningWaitReason = "Wartet auf freie Fahrzeuge."; TraceSkip(mission, mission.PlanningWaitReason); continue; }
                if (_attempts.TryGetValue(mission.Id, out var last) && UtcNow - last < TimeSpan.FromSeconds(60))
                {
                    mission.PlanningWaitReason = $"Letzte Alarmierung wird geprüft · erneute Prüfung in {(int)Math.Ceiling(60 - (UtcNow - last).TotalSeconds)} s.";
                    TraceSkip(mission, mission.PlanningWaitReason); continue;
                }
            }
            checkedMissions.Add(mission.Id);
            mission.PlanningWaitReason = "";
            NextActionChanged?.Invoke("Prüfe: " + mission.DisplayTitle);
            var result = await _api.DispatchAsync(mission, token: token, automatic: true);
            LastActionChanged?.Invoke(result.Message);
            attempted++;
            ApplyDispatchResult(mission, result);
            lock (_stateSync)
            {
                if (!result.Success && !result.Sent) _unchangedPlans[mission.Id] = new(mission.CoverageSignature, planningFleet,
                    UtcNow.AddSeconds(DrawDetailReviewInterval()), UtcNow.AddMinutes(2), approach, availableVehicles);
                else _unchangedPlans.Remove(mission.Id);
            }
            if (!result.Success && !result.Sent && result.Plan is { IsComplete: false } &&
                _config.Config.BotSettings.ReassignBlockedMissions && _api is IReassignmentApi reassignment)
            {
                try
                {
                    var moved = await reassignment.TryReassignAsync(mission, _missions.ToArray(), token);
                    if (moved != null)
                    {
                        LastActionChanged?.Invoke(moved.Message);
                        lock (_stateSync) { _coverage.Remove(moved.SourceMissionId); _attempts.Remove(moved.SourceMissionId); }
                        if (moved.TargetDispatch != null) ApplyDispatchResult(mission, moved.TargetDispatch);
                        if (moved.ActionSent) { lock (_stateSync) _pendingWork = true; return true; }
                    }
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or UnauthorizedAccessException))
                { _log?.Write(LogLevel.Error, "Reassignment", "failed", "Umverteilung fehlgeschlagen; Fahrzeugzuordnung wird vor weiteren Aktionen erneut geprüft."); }
            }
            if (result.Sent) alarmsSent++;
            if (result.Sent && fleetApi != null)
            {
                var updatedFleet = await fleetApi.GetVehiclesAsync(token: token);
                hasFreeVehicles = updatedFleet.Any(v => v.IsAvailable);
                approach = ApproachPolicy.NormalizeMinutes(_config.Config.BotSettings.MaxApproachMinutes);
                planningFleet = PlanningFleetSignature(updatedFleet) + "|approach:" + approach;
                availableVehicles = AvailablePlanningVehicles(updatedFleet);
            }
            if (!_api.IsLoggedIn) break;
            if (alarmsSent >= alarmLimit)
            {
                lock (_stateSync)
                    _pendingWork |= hasFreeVehicles && _missions.Any(m => !m.sw && m.NeedsDispatch &&
                        (_running.ContainsKey(m.Id) || _running.Count + (_api is IPlannedMissionApi reservations ? reservations.ReservedPlannedSlots : 0) < max) &&
                        (!_attempts.TryGetValue(m.Id, out var last) || UtcNow - last >= TimeSpan.FromSeconds(60)));
                break;
            }
        }
        lock (_stateSync)
        {
            PublishBoard();
            StatusChanged?.Invoke(!hasFreeVehicles
                ? "Alle Fahrzeuge derzeit gebunden. Rückfahrzeuge mit FMS 1 werden beim nächsten Scan berücksichtigt."
                : $"{_missions.Count} Einsätze, {_missions.Count(m => m.IsActive)} aktiv, {attempted} geprüft.");
        }
        return alarmsSent > 0;
    }

    private void TraceSkip(Mission mission, string reason) => _log?.Write(LogLevel.Debug, "Bot", "mission.skipped", reason);

    public async Task<DispatchResult> ProcessMissionAsync(Mission mission, bool preview, CancellationToken token = default)
    {
        await _cycleGate.WaitAsync(token);
        try
        {
            var result = await _api.DispatchAsync(mission, preview, token);
            LastActionChanged?.Invoke(result.Message);
            if (!preview) ApplyDispatchResult(mission, result);
            else if (result.Plan != null)
            {
                lock (_stateSync)
                {
                    var displayed = _missions.FirstOrDefault(item => item.Id == mission.Id);
                    if (displayed != null && displayed.CoverageSignature == mission.CoverageSignature)
                    {
                        displayed.PlanningProblem = string.Join("; ", result.Plan.Problems);
                        displayed.HasUnrecognizedRequirements = result.Plan.HasUnrecognizedRequirements;
                        displayed.WaitingForPersonnel = result.Plan.WaitingForPersonnel;
                        if (result.Plan.IsComplete)
                        {
                            _shortfalls.Remove(mission.Id);
                            displayed.PlanningWaitReason = displayed.IsDispatchCovered ? "" : "Plan vollständig · Alarmierung ausstehend";
                        }
                        else _shortfalls[mission.Id] = (displayed.CoverageSignature, displayed.PlanningProblem, displayed.WaitingForPersonnel, displayed.HasUnrecognizedRequirements);
                        PublishBoard();
                    }
                }
            }
            if (!preview)
            {
                var transports = await ProcessTransportsAsync(token, mission.Id);
                if (transports.Count > 0) result = result with
                {
                    Success = result.Success && transports.All(transport => transport.Success || transport.Waiting),
                    Sent = result.Sent || transports.Any(transport => transport.Sent),
                    Message = result.Message + "\n" + string.Join("\n", transports.Select(transport => transport.Message))
                };
            }
            return result;
        }
        finally { _cycleGate.Release(); }
    }

    private void ApplyDispatchResult(Mission mission, DispatchResult result)
    {
        lock (_stateSync) ApplyDispatchResultLocked(mission, result);
    }

    private void ApplyDispatchResultLocked(Mission mission, DispatchResult result)
    {
        var displayed = _missions.FirstOrDefault(m => m.Id == mission.Id);
        if (displayed == null && _hasSnapshot) { TraceSkip(mission, "Verspätetes Aktionsergebnis für inzwischen abgeschlossenen Einsatz."); return; }
        if (displayed == null) { displayed = mission; _missions.Add(displayed); }
        var currentRequirements = displayed.CoverageSignature == mission.CoverageSignature;
        if (result.Success && currentRequirements)
        {
            displayed.IsDispatchCovered = true;
            displayed.RequiresDetailsReview = false;
            displayed.PlanningWaitReason = "";
            displayed.PlanningProblem = "";
            displayed.HasUnrecognizedRequirements = false;
            displayed.WaitingForPersonnel = "";
            _coverage[mission.Id] = (mission.CoverageSignature, UtcNow);
            _shortfalls.Remove(mission.Id);
        }
        else if (!currentRequirements)
        {
            displayed.IsDispatchCovered = false;
            displayed.RequiresDetailsReview = true;
            _coverage.Remove(mission.Id);
        }
        else if (result.Plan is { IsComplete: false })
        {
            displayed.IsDispatchCovered = false;
            displayed.PlanningProblem = string.Join("; ", result.Plan.Problems);
            displayed.HasUnrecognizedRequirements = result.Plan.HasUnrecognizedRequirements;
            displayed.WaitingForPersonnel = result.Plan.WaitingForPersonnel;
            _coverage.Remove(mission.Id);
            _shortfalls[mission.Id] = (displayed.CoverageSignature, displayed.PlanningProblem, displayed.WaitingForPersonnel, displayed.HasUnrecognizedRequirements);
        }
        else if (result.Plan is { IsComplete: true })
        {
            // A recognized plan may still wait for a dispatch window or confirmation.
            // That does not preserve the previous recognition error.
            displayed.PlanningProblem = "";
            displayed.HasUnrecognizedRequirements = false;
            displayed.WaitingForPersonnel = result.Plan.WaitingForPersonnel;
            displayed.PlanningWaitReason = result.Message.Split('\n')[0];
            _shortfalls.Remove(mission.Id);
        }
        if (result.Sent || result.Success)
        {
            _attempts[mission.Id] = UtcNow;
            if (!result.KnownStaffingFailure || result.AnyVehicleAccepted || result.Success)
                Track(displayed, !currentRequirements ? displayed.StatusText : result.Success ? "Anforderungen abgedeckt · Fahrzeuge alarmiert" : result.KnownStaffingFailure ? displayed.StatusText : "Alarmierung wird geprüft");
            if (result.KnownStaffingFailure && !result.HasUnconfirmedVehicles) _attempts.Remove(mission.Id);
            if (!result.Success && result.AnyVehicleAccepted && !result.HasUnconfirmedVehicles) _attempts.Remove(mission.Id);
            if (displayed.CoverageSignature != mission.CoverageSignature) _attempts.Remove(mission.Id);
        }
        else _attempts.Remove(mission.Id); // No send: check availability again at the next scan.
        SchedulePoll();
        _log?.Write(LogLevel.Debug, "Bot", "dispatch.applied", "Alarmierungsergebnis in Einsatzliste übernommen.");
        PublishBoard();
    }

    private void Track(Mission mission, string state)
    {
        _statisticsTracked.Add(mission.Id);
        _running[mission.Id] = new(mission.Id, mission.DisplayTitle,
            _running.GetValueOrDefault(mission.Id)?.StartTime ?? UtcNow, mission.EstimatedCompletion, state);
    }

    private void PublishBoard()
    {
        MissionsChanged?.Invoke(_missions.ToArray());
        var activeIds = _missions.Where(m => m.IsActive).Select(m => m.Id).ToHashSet();
        RunningMissionsChanged?.Invoke(_running.Values.Where(m => activeIds.Contains(m.MissionId)).OrderBy(m => m.StartTime).ToArray());
    }

    public async Task ResetAsync()
    {
        await StopAsync();
        await _cycleGate.WaitAsync();
        try
        {
            lock (_stateSync)
            {
            _running.Clear(); _attempts.Clear(); _coverage.Clear(); _shortfalls.Clear(); _detailReviewDue.Clear(); _unchangedPlans.Clear(); _missions.Clear();
            _statisticsTracked.Clear();
            _hasSnapshot = false;
            _snapshotFailures = 0;
            _nextAutomaticPoll = DateTime.MinValue;
            _lastStatusSnapshotAt = _nextDetailReviewBatch = DateTime.MinValue;
            _pollInputs = "";
            _hasTransportRequests = false;
            _pendingWork = false; _boardSignature = _fleetSignature = "";
            _completionDiscoveryUntil = DateTime.MinValue;
            _nextTaskRewardsCheck = DateTime.MinValue;
            _nextPromotionCheck = DateTime.MinValue;
            _nextBonusCheck = DateTime.MinValue; _lastBonusDate = DateTime.MinValue; _bonusAttemptsToday = 0; PublishBoard();
            }
        }
        finally { _cycleGate.Release(); }
    }
}

