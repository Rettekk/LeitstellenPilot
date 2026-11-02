using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeitstellenPilot.Models;
using LeitstellenPilot.Services;

namespace LeitstellenPilot.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ConfigService _config;
    private readonly ApiService _api;
    private readonly BotService _bot;
    private readonly ProgramLog _log;
    private readonly IWindowsStartupService _windowsStartup;
    private readonly bool _windowsAutoStart;
    private bool _revertingWindowsStartup;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Task _operation = Task.CompletedTask;
    private readonly List<Task> _settingsWrites = new();
    private DateTime _statisticsTime = DateTime.MinValue;
    private DateTime _nextBonusStatusCheck = DateTime.MinValue;
    private DateTime _lastBonusStatusDate = DateTime.MinValue;
    private long? _startingCredits;
    private bool _ready;
    private bool _disposed;
    private DateTime? _nextStatusPoll;
    private string _pollReason = "Status noch nicht geprüft";
    private DateTime? _restUntil;

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _savePassword;
    [ObservableProperty] private bool _autoLogin;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _windowsStartupBusy;
    [ObservableProperty] private string _windowsStartupStatus = "";
    public bool HasWindowsStartupStatus => !string.IsNullOrWhiteSpace(WindowsStartupStatus);
    partial void OnWindowsStartupStatusChanged(string value) => OnPropertyChanged(nameof(HasWindowsStartupStatus));
    public bool CanChangeWindowsStartup => _windowsStartup.IsAvailable && !WindowsStartupBusy;
    public bool WindowsStartupSupported => OperatingSystem.IsWindows();
    private bool WindowsBotStartRequested => _windowsAutoStart && _config.Config.BotSettings.StartWithWindows;
    private bool BotAutoStartRequested => _config.Config.BotSettings.AutoStart || WindowsBotStartRequested;
    private bool CanLoginAtStartup => _config.CanAutoLogin() || WindowsBotStartRequested &&
        !string.IsNullOrWhiteSpace(_config.Config.LoginIdentifier) && !string.IsNullOrEmpty(_config.Config.Password);
    [ObservableProperty] private bool _autoAlarm;
    [ObservableProperty] private string _missionGenerationStatus = "Beobachtung startet mit dem Bot.";
    [ObservableProperty] private bool _autoTransport;
    [ObservableProperty] private bool _releasePrisonersWithoutCell;
    [ObservableProperty] private bool _releasePatientsWithoutBed = true;
    [ObservableProperty] private int _hospitalWaitIndex = 1;
    [ObservableProperty] private bool _autoClaimDailyBonus;
    [ObservableProperty] private bool _autoClaimTaskRewards;
    [ObservableProperty] private string _taskRewardsStatus = "Aufgaben noch nicht geprüft.";
    [ObservableProperty] private bool _autoPromotion;
    [ObservableProperty] private bool _autoCollectEventItems;
    [ObservableProperty] private bool _preferHighCreditMissions;
    [ObservableProperty] private bool _reassignBlockedMissions;
    [ObservableProperty] private bool _releaseUnsolvableMissions;
    [ObservableProperty] private bool _autoPlannedMissions;
    [ObservableProperty] private int _plannedDurationLimitIndex;
    [ObservableProperty] private int _plannedBufferMinutes;
    [ObservableProperty] private int _plannedFleetReservePercent;
    public string PlannedLimitText => $"Max. {Math.Clamp(_config.Config.BotSettings.MaxPlannedMissions, 1, 20)} geplanter Einsatz · Reservierung kurz vor Abfahrt";
    [ObservableProperty] private string _promotionStatus = "Dienstgrad noch nicht geprüft.";
    [ObservableProperty] private string _dailyBonusStatus = "Bonus noch nicht geprüft.";
    [ObservableProperty] private int _minActionDelaySeconds = 3;
    [ObservableProperty] private int _maxActionDelaySeconds = 11;
    [ObservableProperty] private int _maxConcurrentMissions = 10;
    [ObservableProperty] private int _maxApproachMinutes = ApproachPolicy.DefaultMinutes;
    [ObservableProperty] private int _scanIntervalSeconds = 10;
    [ObservableProperty] private int _maxScanIntervalSeconds = 20;
    [ObservableProperty] private int _detailReviewIntervalSeconds = 15;
    [ObservableProperty] private int _maxDetailReviewIntervalSeconds = 30;
    [ObservableProperty] private bool _adaptivePolling = true;
    [ObservableProperty] private bool _workRestCycleEnabled;
    [ObservableProperty] private double _workHours = 4;
    [ObservableProperty] private double _restHours = 1;
    [ObservableProperty] private bool _nightRestEnabled;
    [ObservableProperty] private TimeSpan? _nightRestStart = TimeSpan.Zero;
    [ObservableProperty] private TimeSpan? _nightRestEnd = TimeSpan.FromHours(6);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BotStatus))] private string _restReason = "Ruhepause";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BotStatus), nameof(FooterStatus), nameof(ShowFooter))] private bool _isResting;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowFooter))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProgramTabTitle), nameof(SettingsTabTitle))] private bool _isLoggedIn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BotStatus), nameof(FooterStatus), nameof(ShowFooter))] private bool _isBotRunning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FooterStatus), nameof(ShowFooter))] private string _statusText = "Bitte anmelden.";
    [ObservableProperty] private string _lastAction = "Noch keine Alarmierung.";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BotStatus), nameof(FooterStatus), nameof(ShowFooter))] private string _nextAction = "Bot gestoppt";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPlan))] private string _planText = "";
    [ObservableProperty] private string _botLogText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FooterStatus), nameof(ShowFooter))] private int _selectedWorkspaceTab = 2;
    [ObservableProperty] private UserInfo? _userInfo;
    [ObservableProperty] private int _totalVehicles;
    [ObservableProperty] private int _availableVehicles;
    [ObservableProperty] private int _totalBuildings;

    public ObservableCollection<Mission> Missions { get; } = new();
    public ObservableCollection<RunningMissionViewModel> RunningMissions { get; } = new();
    public string CurrentUser => UserInfo?.UserName ?? "";
    public string Greeting => !string.IsNullOrWhiteSpace(CurrentUser) ? "Hallo, " + CurrentUser : "Willkommen";
    public bool HasPlan => !string.IsNullOrWhiteSpace(PlanText);
    public string ProgramTabTitle => "Programm";
    public string SettingsTabTitle => IsLoggedIn ? "Einstellungen" : "Start & Anmeldung";
    public string BotStatus => PersonnelBusy ? "Personalprüfung läuft · Alarmierung pausiert"
        : IsResting ? PollingStatus
        : !IsBotRunning ? "Bot gestoppt"
        : string.IsNullOrWhiteSpace(NextAction) || NextAction == "Bot gestoppt" ? "Automatik läuft" : NextAction;
    public string FooterStatus => !IsLoggedIn && StatusText == "Bitte anmelden." ? "" : StatusText.StartsWith("Personalprüfung", StringComparison.OrdinalIgnoreCase)
        ? SelectedWorkspaceTab == 4 || PersonnelBusy ? "" : PersonnelStatus
        : StatusText.TrimEnd('.', ' ') == BotStatus.TrimEnd('.', ' ') ? "" : StatusText;
    public bool ShowFooter => IsBusy || FooterStatus.Length > 0;
    public string CurrentCredits => UserInfo?.CurrentCredits.ToString("N0") ?? "0";
    public string CurrentCoins => UserInfo?.Coins.ToString("N0") ?? "0";
    public string UserLevel => UserInfo?.LevelTitle ?? "";
    public string SessionCredits => ((UserInfo?.TotalCredits ?? 0) - (_startingCredits ?? UserInfo?.TotalCredits ?? 0)).ToString("N0");
    public string BotButtonText => IsBotRunning ? "Bot stoppen" : "Bot starten";
    private void NotifyBotStatus()
    { OnPropertyChanged(nameof(BotStatus)); OnPropertyChanged(nameof(FooterStatus)); OnPropertyChanged(nameof(ShowFooter)); }
    public int MissionCount => Missions.Count;
    public int RunningMissionCount => RunningMissions.Count;
    public string ScanIntervalText => $"Statusaktualisierung: alle {ScanIntervalSeconds} Sekunden";
    public string PollingStatus => IsResting
        ? _restUntil.HasValue ? $"{RestReason} bis {_restUntil.Value.ToLocalTime():HH:mm}" : RestReason
        : _nextStatusPoll.HasValue && IsBotRunning
        ? $"Nächste Statusabfrage in {Math.Max(0, (int)(_nextStatusPoll.Value - DateTime.UtcNow).TotalSeconds)} s · {_pollReason}"
        : "Statusüberwachung pausiert";
    public string NightRestValidation => NightRestEnabled && (!NightRestStart.HasValue || !NightRestEnd.HasValue)
        ? "Bitte Start- und Endzeit auswählen."
        : NightRestEnabled && NightRestStart == NightRestEnd ? "Start und Ende müssen verschieden sein." : "";
    public bool HasNightRestValidation => NightRestValidation.Length > 0;
    public string CurrentBuild => typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString() ?? "unbekannt";
    public IAsyncRelayCommand LoginCommand { get; }
    public IAsyncRelayCommand LogoutCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ToggleBotCommand { get; }
    public IAsyncRelayCommand<Mission> PreviewMissionCommand { get; }
    public IAsyncRelayCommand<Mission> StartMissionCommand { get; }
    public MainWindowViewModel(bool initialize = true, ConfigService? config = null, ApiService? api = null,
        IWindowsStartupService? windowsStartup = null, bool windowsAutoStart = false)
    {
        _config = config ?? new();
        _windowsStartup = windowsStartup ?? new WindowsStartupService(); _windowsAutoStart = windowsAutoStart;
        StartWithWindows = _config.Config.BotSettings.StartWithWindows;
        WindowsStartupStatus = "";
        _log = new ProgramLog(LogSecrets);
        _api = api ?? new(_config);
        _api.Logger = _log;
        _bot = new(_api, _config, logger: _log);
        BotLogText = _log.RecentText;
        _log.Changed += UpdateLogDisplay;
        Missions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(MissionCount));
        RunningMissions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(RunningMissionCount));
        Email = _config.Config.LoginIdentifier;
        Password = _config.Config.Password;
        SavePassword = !string.IsNullOrEmpty(Password);
        AutoLogin = _config.Config.BotSettings.AutoLogin;
        AutoAlarm = _config.Config.BotSettings.AutoAlarm;
        _api.MissionGenerationStatusChanged += message => Post(() => MissionGenerationStatus = message);
        AutoTransport = _config.Config.BotSettings.AutoTransport;
        ReleasePrisonersWithoutCell = _config.Config.BotSettings.ReleasePrisonersWithoutCell;
        ReleasePatientsWithoutBed = _config.Config.BotSettings.ReleasePatientsWithoutBed;
        HospitalWaitIndex = HospitalPlanner.NormalizeWait(_config.Config.BotSettings.HospitalWaitMinutes) switch { 5 => 0, 15 => 2, _ => 1 };
        AutoClaimDailyBonus = _config.Config.BotSettings.AutoClaimDailyBonus;
        AutoClaimTaskRewards = _config.Config.BotSettings.AutoClaimTaskRewards;
        AutoPromotion = _config.Config.BotSettings.AutoPromotion;
        AutoCollectEventItems = _config.Config.BotSettings.AutoCollectEventItems;
        PreferHighCreditMissions = _config.Config.BotSettings.PreferHighCreditMissions;
        ReassignBlockedMissions = _config.Config.BotSettings.ReassignBlockedMissions;
        ReleaseUnsolvableMissions = _config.Config.BotSettings.ReleaseUnsolvableMissions;
        AutoPlannedMissions = _config.Config.BotSettings.AutoPlannedMissions;
        PlannedDurationLimitIndex = PlannedMissionPolicy.NormalizeDurationLimit(_config.Config.BotSettings.PlannedMaxDurationMinutes) switch
        { 120 => 1, 180 => 2, 0 => 3, _ => 0 };
        PlannedBufferMinutes = _config.Config.BotSettings.PlannedBufferMinutes;
        PlannedFleetReservePercent = _config.Config.BotSettings.PlannedFleetReservePercent;
        TaskRewardsStatus = AutoClaimTaskRewards ? "Aufgaben noch nicht geprüft." : "Aufgabenautomatik ausgeschaltet.";
        PromotionStatus = AutoPromotion ? "Dienstgrad noch nicht geprüft." : "Beförderungsautomatik ausgeschaltet.";
        MinActionDelaySeconds = _config.Config.BotSettings.MinActionDelaySeconds;
        MaxActionDelaySeconds = _config.Config.BotSettings.MaxActionDelaySeconds;
        MaxConcurrentMissions = _config.Config.BotSettings.MaxConcurrentMissions;
        MaxApproachMinutes = ApproachPolicy.NormalizeMinutes(_config.Config.BotSettings.MaxApproachMinutes);
        ScanIntervalSeconds = _config.Config.BotSettings.ScanIntervalSeconds;
        MaxScanIntervalSeconds = _config.Config.BotSettings.MaxScanIntervalSeconds > 0 ? _config.Config.BotSettings.MaxScanIntervalSeconds :
            Math.Clamp(ScanIntervalSeconds + ScanIntervalSeconds * _config.Config.BotSettings.PollingVariationPercent / 100, 5, 300);
        DetailReviewIntervalSeconds = Math.Clamp(_config.Config.BotSettings.DetailReviewIntervalSeconds, 10, 120);
        MaxDetailReviewIntervalSeconds = Math.Clamp(_config.Config.BotSettings.MaxDetailReviewIntervalSeconds, DetailReviewIntervalSeconds, 120);
        AdaptivePolling = _config.Config.BotSettings.AdaptivePolling;
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_config.Config.BotSettings.AccountRefreshSeconds, 15, 300));
        WorkRestCycleEnabled = _config.Config.BotSettings.WorkRestCycleEnabled;
        WorkHours = _config.Config.BotSettings.WorkHours;
        RestHours = _config.Config.BotSettings.RestHours;
        NightRestStart = ReadNightTime(_config.Config.BotSettings.NightRestStart);
        NightRestEnd = ReadNightTime(_config.Config.BotSettings.NightRestEnd);
        NightRestEnabled = _config.Config.BotSettings.NightRestEnabled;
        InitializeStationUpgrades();
        _ready = true;
        LoginCommand = new AsyncRelayCommand(() => RunOperationAsync(LoginAsync), () => !IsBusy && !IsLoggedIn);
        LogoutCommand = new AsyncRelayCommand(() => RunOperationAsync(LogoutAsync), () => !IsBusy && IsLoggedIn && !PersonnelBusy);
        RefreshCommand = new AsyncRelayCommand(() => RunOperationAsync(RefreshAsync), () => !IsBusy && IsLoggedIn && !PersonnelBusy);
        ToggleBotCommand = new AsyncRelayCommand(ToggleBotAsync, () => IsLoggedIn && (IsBotRunning || !IsBusy && !PersonnelBusy));
        PreviewMissionCommand = new AsyncRelayCommand<Mission>(m => RunOperationAsync(() => ProcessAsync(m, true)), m => m != null && IsLoggedIn && !IsBusy);
        StartMissionCommand = new AsyncRelayCommand<Mission>(m => RunOperationAsync(() => ProcessAsync(m, false)), m => m != null && IsLoggedIn && !IsBusy && !PersonnelBusy);
        InitializePersonnel();
        _bot.StatusChanged += status => Post(() => { StatusText = status; CheckSession(); });
        _bot.LastActionChanged += action => Post(() => LastAction = action);
        _bot.NextActionChanged += action => Post(() => NextAction = action);
        _bot.DailyBonusStatusChanged += status => Post(() => DailyBonusStatus = status);
        _bot.DailyBonusClaimed += () => Post(() => { _ = RunOperationAsync(RefreshAccountAsync); });
        _bot.TaskRewardsStatusChanged += status => Post(() => TaskRewardsStatus = status);
        _bot.TaskRewardsClaimed += () => Post(() => { _ = RunOperationAsync(RefreshAccountAsync); });
        _bot.PromotionStatusChanged += status => Post(() => PromotionStatus = status);
        _bot.PromotionClaimed += () => Post(() => { _ = RunOperationAsync(RefreshAccountAsync); });
        _api.ActionWaiting += seconds =>
        {
            var reason = _api.ActionWaitReason;
            Post(() => NextAction = $"{reason}: nächste Aktion in {seconds} Sekunden");
        };
        _bot.StatusPollScheduled += (schedule, due) => Post(() =>
        {
            _nextStatusPoll = due; _pollReason = schedule.Reason;
            OnPropertyChanged(nameof(PollingStatus));
        });
        _bot.RestChanged += (resting, until) =>
        {
            var reason = _bot.RestReason;
            Post(() =>
            {
                IsResting = resting; _restUntil = until; RestReason = reason;
                OnPropertyChanged(nameof(PollingStatus));
                NotifyBotStatus();
            });
        };
        _api.FleetChanged += vehicles => Post(() =>
        {
            TotalVehicles = vehicles.Count;
            AvailableVehicles = vehicles.Count(v => v.IsAvailable);
        });
        _bot.RunningChanged += running => Post(() =>
        {
            IsBotRunning = running; OnPropertyChanged(nameof(BotButtonText));
            MissionGenerationStatus = running
                ? "Stillstandprüfung aktiv · Beobachtung beginnt mit dem Einsatzabruf."
                : "Stillstandprüfung pausiert · Bot gestoppt.";
        });
        _bot.MissionsChanged += missions => Post(() => ApplyMissions(missions));
        _bot.RunningMissionsChanged += missions => Post(() =>
        {
            RunningMissions.Clear();
            foreach (var mission in missions) RunningMissions.Add(new(mission));
            OnPropertyChanged(nameof(RunningMissionCount));
        });
        _refreshTimer.Tick += async (_, _) =>
        {
            var settings = _config.Config.BotSettings;
            var nightBlocked = settings.NightRestEnabled && (!NightRestPolicy.HasValidWindow(settings) ||
                NightRestPolicy.RestUntil(settings, DateTime.UtcNow, LaunchOptions.ResolveTimeZone(_config.Config)).HasValue);
            if (IsLoggedIn && !IsBusy && !PersonnelBusy && !IsResting && !nightBlocked) await RunOperationAsync(IsBotRunning ? RefreshAccountAsync : RefreshPeriodicAsync);
        };
        _clockTimer.Tick += (_, _) =>
        {
            OnPropertyChanged(nameof(PollingStatus));
            foreach (var mission in RunningMissions) mission.UpdateProgress();
        };
        _clockTimer.Start();
        _log.Write(LogLevel.Info, "App", "started", "LeitstellenPilot gestartet.");
        if (initialize)
        {
            _refreshTimer.Start();
            _ = RunOperationAsync(InitializeAsync);
        }
    }

    private void Post(Action action) => Dispatcher.UIThread.Post(() => { if (!_disposed) action(); });

    private IEnumerable<string> LogSecrets()
    {
        yield return Password;
        yield return Email;
        yield return _config.Config.Password;
        yield return _config.Config.LoginIdentifier;
        yield return _config.Session.SessionId;
        yield return _config.Session.AuthToken;
        yield return _config.Session.UserName;
        yield return _config.Session.Cookies;
        foreach (var cookie in (_config.Session.Cookies ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = cookie.Trim().Split('=', 2);
            if (pair.Length == 2) yield return pair[1];
        }
    }

    private void UpdateLogDisplay() => Post(() => BotLogText = _log.RecentText);

    private Task RunOperationAsync(Func<Task> action)
    {
        if (IsBusy || _disposed) return Task.CompletedTask;
        _operation = ExecuteAsync(action);
        return _operation;
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        IsBusy = true;
        UpdateCommands();
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { StatusText = ex.Message; _log.Write(LogLevel.Error, "App", "operation.failed", ex.Message); CheckSession(); }
        finally { IsBusy = false; UpdateCommands(); }
    }

    private async Task InitializeAsync()
    {
        if ((_config.Config.BotSettings.AutoLogin || WindowsBotStartRequested) && _api.IsLoggedIn)
        {
            try { await RefreshAsync(); }
            catch (UnauthorizedAccessException) when (CanLoginAtStartup)
            {
                _log.Write(LogLevel.Warning, "Account", "session.rejected", "Gespeicherte Sitzung abgelehnt; einmalige Passwortanmeldung wird versucht.");
                // A restored cookie is checked first. If the server rejects it,
                // attempt the already-enabled password login once at startup.
                await LoginAsync();
                return;
            }
            if (BotAutoStartRequested && IsLoggedIn) await StartBotCheckedAsync();
        }
        else if (CanLoginAtStartup) await LoginAsync();
        else if (WindowsBotStartRequested) StatusText = "Windows-Autostart wartet auf die Spielanmeldung. Bitte einmal anmelden und die Sitzung oder das Passwort speichern.";
    }

    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
        { StatusText = "Bitte Benutzername/E-Mail und Passwort eingeben."; return; }
        StatusText = "Anmeldung läuft …";
        if (!await _api.LoginAsync(Email.Trim(), Password, _lifetime.Token))
        { StatusText = "Anmeldung fehlgeschlagen. Zugangsdaten prüfen."; _log.Write(LogLevel.Warning, "Account", "login.rejected", StatusText); return; }
        await _config.SaveLoginDataAsync(Email.Trim(), SavePassword ? Password : "");
        _startingCredits = null;
        await RefreshAsync();
        if (BotAutoStartRequested) await StartBotCheckedAsync();
    }

    private async Task LogoutAsync()
    {
        await _bot.ResetAsync();
        _api.Logout();
        UserInfo = null;
        IsLoggedIn = false;
        _startingCredits = null;
        _statisticsTime = DateTime.MinValue;
        Missions.Clear();
        RunningMissions.Clear();
        TotalVehicles = AvailableVehicles = TotalBuildings = 0;
        PlanText = "";
        DailyBonusStatus = "Bonus noch nicht geprüft.";
        TaskRewardsStatus = "Aufgaben noch nicht geprüft.";
        PromotionStatus = "Dienstgrad noch nicht geprüft.";
        _nextBonusStatusCheck = DateTime.MinValue; _lastBonusStatusDate = DateTime.MinValue;
        StatusText = "Abgemeldet.";
        _log.Write(LogLevel.Info, "Account", "logout", "Abgemeldet.");
        NotifyCounts();
    }

    private Task RefreshAsync() => RefreshCoreAsync(forceBonus: true);
    private Task RefreshPeriodicAsync() => RefreshCoreAsync(forceBonus: false);
    private async Task RefreshCoreAsync(bool forceBonus)
    {
        StatusText = "Lade Einsätze und Fahrzeugbestand …";
        UserInfo = await _api.GetUserInfoAsync(_lifetime.Token);
        IsLoggedIn = true;
        _startingCredits ??= UserInfo?.TotalCredits;
        var failures = new List<string>();
        async Task ReadSection(string label, Func<Task> read)
        {
            try { await read(); }
            catch (UnauthorizedAccessException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add(label + ": " + ex.Message); _log.Write(LogLevel.Error, "App", "section.failed", label + " konnte nicht geladen werden."); }
        }
        await ReadSection("Einsatzliste", async () =>
        {
            await _bot.RefreshMissionsAsync(_lifetime.Token);
        });
        await ReadSection("Fahrzeuge", async () =>
        {
            var vehicles = await _api.GetVehiclesAsync(true, _lifetime.Token);
            TotalVehicles = vehicles.Count;
            AvailableVehicles = vehicles.Count(v => v.IsAvailable);
        });
        if (DateTime.UtcNow - _statisticsTime > TimeSpan.FromMinutes(5))
        {
            await ReadSection("Gebäude", async () =>
            {
                var counts = await _api.GetBuildingCountsAsync(_lifetime.Token);
                TotalBuildings = counts.Total;
                _statisticsTime = DateTime.UtcNow;
            });
        }
        if (forceBonus || DateTime.Today != _lastBonusStatusDate || DateTime.UtcNow >= _nextBonusStatusCheck)
        {
            _lastBonusStatusDate = DateTime.Today;
            _nextBonusStatusCheck = DateTime.Today.AddDays(1).ToUniversalTime();
            await ReadSection("Tagesbonus", async () =>
            {
                var bonus = await _api.GetDailyBonusStatusAsync(_lifetime.Token, refresh: forceBonus);
                DailyBonusStatus = bonus.Message;
            });
        }
        StatusText = failures.Count == 0 ? $"{MissionCount} offene Einsätze geladen, {AvailableVehicles} Fahrzeuge frei."
            : "Teilweise geladen: " + string.Join("; ", failures);
        NotifyCounts();
        OnPropertyChanged(nameof(SessionCredits));
    }

    private async Task RefreshAccountAsync()
    {
        UserInfo = await _api.GetUserInfoAsync(_lifetime.Token);
        var vehicles = await _api.GetVehiclesAsync(token: _lifetime.Token);
        TotalVehicles = vehicles.Count;
        AvailableVehicles = vehicles.Count(v => v.IsAvailable);
    }

    private void ApplyMissions(IReadOnlyList<Mission> missions)
    {
        Missions.Clear();
        foreach (var mission in missions.Where(m => m.NeedsDispatch)
                     .OrderByDescending(m => m.HasMissingVehicles).ThenByDescending(m => m.average_credits))
            Missions.Add(mission);
        NotifyCounts();
    }

    private async Task ProcessAsync(Mission? mission, bool preview)
    {
        if (mission == null) return;
        StatusText = preview ? "Erstelle Fahrzeugplan …" : "Prüfe Anforderungen und alarmiere …";
        var result = await _bot.ProcessMissionAsync(mission, preview, _lifetime.Token);
        PlanText = preview ? result.Plan?.Summary ?? result.Message :
            result.Plan == null || result.Message.Contains(result.Plan.Summary, StringComparison.Ordinal)
                ? result.Message : result.Message + "\n\n" + result.Plan.Summary;
        StatusText = preview ? (result.Success ? "Plan vollständig. Fahrzeuge können alarmiert werden." : "Plan unvollständig. Details unten.")
            : result.Success ? result.Message.Split('\n')[0] : result.Message;
        if (!preview && result.Sent) await RefreshPeriodicAsync();
    }

    private async Task ToggleBotAsync()
    {
        if (_disposed) return;
        if (_bot.IsRunning) await _bot.StopAsync();
        else await RunOperationAsync(() => StartBotCheckedAsync(userInitiated: true));
    }

    private Task StartBotCheckedAsync(bool userInitiated = false)
    {
        if (!_disposed && IsLoggedIn) _bot.StartBot();
        return Task.CompletedTask;
    }

    partial void OnUserInfoChanged(UserInfo? value)
    {
        OnPropertyChanged(nameof(CurrentUser)); OnPropertyChanged(nameof(Greeting)); OnPropertyChanged(nameof(CurrentCredits));
        OnPropertyChanged(nameof(CurrentCoins));
        OnPropertyChanged(nameof(UserLevel)); OnPropertyChanged(nameof(SessionCredits));
    }
    partial void OnIsLoggedInChanged(bool value)
    {
        if (!_ready) return;
        SelectedWorkspaceTab = value ? 0 : 2;
        PersonnelReports.Clear(); PersonnelRunNotice = "";
        PersonnelStatus = "Für eine vollständige Prüfung sollten alle Fahrzeuge auf ihrer Wache sein.";
        if (!value) { Missions.Clear(); RunningMissions.Clear(); PlanText = ""; }
        UpdateCommands();
        OnPropertyChanged(nameof(FooterStatus)); OnPropertyChanged(nameof(ShowFooter));
    }
    partial void OnSelectedWorkspaceTabChanged(int value)
    {
        if (!IsLoggedIn && value is not (2 or 3)) SelectedWorkspaceTab = 2;
    }
    partial void OnIsBotRunningChanged(bool value) { if (_ready) UpdateCommands(); }
    partial void OnAutoLoginChanged(bool value) { _config.Config.BotSettings.AutoLogin = value; SaveSettings(); }
    partial void OnWindowsStartupBusyChanged(bool value) => OnPropertyChanged(nameof(CanChangeWindowsStartup));
    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_ready || _disposed || _revertingWindowsStartup) return;
        _settingsWrites.RemoveAll(task => task.IsCompleted);
        _settingsWrites.Add(SaveWindowsStartupAsync(value));
    }
    private async Task SaveWindowsStartupAsync(bool enabled)
    {
        WindowsStartupBusy = true;
        var oldSetting = _config.Config.BotSettings.StartWithWindows;
        bool? oldRegistration = null;
        try
        {
            oldRegistration = _windowsStartup.IsEnabled;
            _windowsStartup.SetEnabled(enabled);
            _config.Config.BotSettings.StartWithWindows = enabled;
            await _config.SaveConfigAsync(_config.Config);
            WindowsStartupStatus = enabled ? "Autostart aktiviert." : "Autostart deaktiviert.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Security.Cryptography.CryptographicException or InvalidOperationException or PlatformNotSupportedException)
        {
            _config.Config.BotSettings.StartWithWindows = oldSetting;
            if (oldRegistration.HasValue)
                try { _windowsStartup.SetEnabled(oldRegistration.Value); }
                catch (Exception) { _log.Write(LogLevel.Warning, "Settings", "windows-startup.rollback", "Windows-Autostarteintrag konnte nicht zurückgesetzt werden."); }
            _revertingWindowsStartup = true; StartWithWindows = oldSetting; _revertingWindowsStartup = false;
            WindowsStartupStatus = "Windows-Autostart konnte nicht gespeichert werden. Die bisherige Einstellung bleibt erhalten.";
            _log.Write(LogLevel.Error, "Settings", "windows-startup.failed", WindowsStartupStatus);
        }
        finally { WindowsStartupBusy = false; }
    }
    partial void OnAutoAlarmChanged(bool value) { _config.Config.BotSettings.AutoAlarm = value; SaveSettings(); }
    partial void OnAutoTransportChanged(bool value) { _config.Config.BotSettings.AutoTransport = value; SaveSettings(); }
    partial void OnReleasePrisonersWithoutCellChanged(bool value) { _config.Config.BotSettings.ReleasePrisonersWithoutCell = value; SaveSettings(); }
    partial void OnReleasePatientsWithoutBedChanged(bool value) { _config.Config.BotSettings.ReleasePatientsWithoutBed = value; SaveSettings(); }
    partial void OnHospitalWaitIndexChanged(int value)
    {
        _config.Config.BotSettings.HospitalWaitMinutes = value switch { 0 => 5, 2 => 15, _ => 10 };
        SaveSettings();
    }
    partial void OnAutoClaimDailyBonusChanged(bool value) { _config.Config.BotSettings.AutoClaimDailyBonus = value; SaveSettings(); }
    partial void OnAutoCollectEventItemsChanged(bool value) { _config.Config.BotSettings.AutoCollectEventItems = value; SaveSettings(); }
    partial void OnAutoClaimTaskRewardsChanged(bool value)
    {
        _config.Config.BotSettings.AutoClaimTaskRewards = value;
        if (_ready) TaskRewardsStatus = value ? "Aufgabenprüfung aktiviert; wartet auf den nächsten Prüfzeitpunkt." : "Aufgabenautomatik ausgeschaltet.";
        SaveSettings();
    }
    partial void OnAutoPromotionChanged(bool value)
    {
        _config.Config.BotSettings.AutoPromotion = value;
        if (_ready) PromotionStatus = value ? "Beförderungsprüfung aktiviert; wartet auf den nächsten Prüfzeitpunkt." : "Beförderungsautomatik ausgeschaltet.";
        SaveSettings();
    }
    partial void OnPreferHighCreditMissionsChanged(bool value)
    {
        _config.Config.BotSettings.PreferHighCreditMissions = value; SaveSettings();
    }
    partial void OnReassignBlockedMissionsChanged(bool value) { _config.Config.BotSettings.ReassignBlockedMissions = value; SaveSettings(); }
    partial void OnReleaseUnsolvableMissionsChanged(bool value) { _config.Config.BotSettings.ReleaseUnsolvableMissions = value; SaveSettings(); }
    partial void OnAutoPlannedMissionsChanged(bool value) { _config.Config.BotSettings.AutoPlannedMissions = value; SaveSettings(); }
    partial void OnPlannedDurationLimitIndexChanged(int value)
    {
        var index = Math.Clamp(value, 0, 3);
        if (value != index) { PlannedDurationLimitIndex = index; return; }
        _config.Config.BotSettings.PlannedMaxDurationMinutes = index switch { 1 => 120, 2 => 180, 3 => 0, _ => 60 };
        SaveSettings();
    }
    partial void OnPlannedBufferMinutesChanged(int value) { _config.Config.BotSettings.PlannedBufferMinutes = Math.Clamp(value, 1, 15); SaveSettings(); }
    partial void OnPlannedFleetReservePercentChanged(int value) { _config.Config.BotSettings.PlannedFleetReservePercent = Math.Clamp(value, 0, 90); SaveSettings(); }
    partial void OnMinActionDelaySecondsChanged(int value) { _config.Config.BotSettings.MinActionDelaySeconds = Math.Clamp(value, 1, 300); SaveSettings(); }
    partial void OnMaxActionDelaySecondsChanged(int value) { _config.Config.BotSettings.MaxActionDelaySeconds = Math.Clamp(value, 1, 300); SaveSettings(); }
    partial void OnMaxConcurrentMissionsChanged(int value)
    { _config.Config.BotSettings.MaxConcurrentMissions = Math.Clamp(value, 1, 100); SaveSettings(); }
    partial void OnMaxApproachMinutesChanged(int value)
    {
        var normalized = ApproachPolicy.NormalizeMinutes(value);
        if (value != normalized) { MaxApproachMinutes = normalized; return; }
        _config.Config.BotSettings.MaxApproachMinutes = normalized; SaveSettings();
    }
    partial void OnScanIntervalSecondsChanged(int value)
    {
        _config.Config.BotSettings.ScanIntervalSeconds = Math.Clamp(value, 5, 300);
        if (_ready)
        {
            _config.Config.BotSettings.ScanIntervalConfigured = true;
            if (MaxScanIntervalSeconds < _config.Config.BotSettings.ScanIntervalSeconds) MaxScanIntervalSeconds = _config.Config.BotSettings.ScanIntervalSeconds;
            _config.Config.BotSettings.MaxScanIntervalSeconds = MaxScanIntervalSeconds;
        }
        OnPropertyChanged(nameof(ScanIntervalText));
        SaveSettings();
    }
    partial void OnMaxScanIntervalSecondsChanged(int value)
    {
        if (!_ready) return;
        var maximum = Math.Clamp(value, Math.Clamp(ScanIntervalSeconds, 5, 300), 300);
        if (value != maximum) { MaxScanIntervalSeconds = maximum; return; }
        _config.Config.BotSettings.MaxScanIntervalSeconds = maximum; _config.Config.BotSettings.ScanIntervalConfigured = true;
        SaveSettings();
    }
    partial void OnAdaptivePollingChanged(bool value) { _config.Config.BotSettings.AdaptivePolling = value; SaveSettings(); }
    partial void OnDetailReviewIntervalSecondsChanged(int value)
    {
        _config.Config.BotSettings.DetailReviewIntervalSeconds = Math.Clamp(value, 10, 120);
        if (_ready && MaxDetailReviewIntervalSeconds < _config.Config.BotSettings.DetailReviewIntervalSeconds)
            MaxDetailReviewIntervalSeconds = _config.Config.BotSettings.DetailReviewIntervalSeconds;
        SaveSettings();
    }
    partial void OnMaxDetailReviewIntervalSecondsChanged(int value)
    {
        var maximum = Math.Clamp(value, Math.Clamp(DetailReviewIntervalSeconds, 10, 120), 120);
        if (value != maximum) { MaxDetailReviewIntervalSeconds = maximum; return; }
        _config.Config.BotSettings.MaxDetailReviewIntervalSeconds = maximum; SaveSettings();
    }
    partial void OnWorkRestCycleEnabledChanged(bool value) { _config.Config.BotSettings.WorkRestCycleEnabled = value; SaveSettings(); }
    partial void OnWorkHoursChanged(double value) { _config.Config.BotSettings.WorkHours = Math.Clamp(value, 0.25, 24); SaveSettings(); }
    partial void OnRestHoursChanged(double value)
    { _config.Config.BotSettings.RestHours = Math.Clamp(value, 0.25, 24); OnPropertyChanged(nameof(RestMinutes)); SaveSettings(); }
    private static TimeSpan? ReadNightTime(string? value) => NightRestPolicy.TryReadTime(value, out var time) ? time.ToTimeSpan() : null;
    private static string FormatNightTime(TimeSpan? value) => value.HasValue && value.Value >= TimeSpan.Zero && value.Value < TimeSpan.FromDays(1)
        ? TimeOnly.FromTimeSpan(value.Value).ToString("HH:mm", CultureInfo.InvariantCulture) : "";
    private void NightRestSettingsChanged()
    {
        OnPropertyChanged(nameof(NightRestValidation)); OnPropertyChanged(nameof(HasNightRestValidation));
        SaveSettings();
    }
    partial void OnNightRestEnabledChanged(bool value) { _config.Config.BotSettings.NightRestEnabled = value; NightRestSettingsChanged(); }
    partial void OnNightRestStartChanged(TimeSpan? value)
    { _config.Config.BotSettings.NightRestStart = FormatNightTime(value); UpdateNightTimeText(value, start: true); NightRestSettingsChanged(); }
    partial void OnNightRestEndChanged(TimeSpan? value)
    { _config.Config.BotSettings.NightRestEnd = FormatNightTime(value); UpdateNightTimeText(value, start: false); NightRestSettingsChanged(); }
    partial void OnSavePasswordChanged(bool value)
    {
        if (_ready && !value) { _config.Config.Password = ""; SaveSettings(); }
    }
    private void SaveSettings()
    {
        if (!_ready || _disposed) return;
        _settingsWrites.RemoveAll(task => task.IsCompleted);
        _settingsWrites.Add(SaveSettingsAsync());
    }
    private async Task SaveSettingsAsync()
    {
        try
        {
            await _config.SaveConfigAsync(_config.Config);
            _log.Write(LogLevel.Info, "Settings", "saved", "Automatik-Einstellungen gespeichert.");
        }
        catch (Exception ex) { StatusText = "Einstellungen konnten nicht gespeichert werden: " + ex.Message; _log.Write(LogLevel.Error, "Settings", "save.failed", "Einstellungen konnten nicht gespeichert werden."); }
    }
    private void CheckSession()
    {
        if (!_api.IsLoggedIn) { IsLoggedIn = false; UserInfo = null; }
    }
    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(MissionCount)); OnPropertyChanged(nameof(RunningMissionCount));
    }
    private void UpdateCommands()
    {
        LoginCommand?.NotifyCanExecuteChanged(); LogoutCommand?.NotifyCanExecuteChanged();
        RefreshCommand?.NotifyCanExecuteChanged(); ToggleBotCommand?.NotifyCanExecuteChanged();
        PreviewMissionCommand?.NotifyCanExecuteChanged(); StartMissionCommand?.NotifyCanExecuteChanged();
        OptimizePersonnelCommand?.NotifyCanExecuteChanged();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop(); _clockTimer.Stop();
        _lifetime.Cancel();
        await _bot.StopAsync();
        await _operation;
        await Task.WhenAll(_settingsWrites);
        _api.Dispose();
        _lifetime.Dispose();
        _log.Write(LogLevel.Info, "App", "closed", "LeitstellenPilot geschlossen.");
        _log.Changed -= UpdateLogDisplay;
        await _log.DisposeAsync();
    }
}
