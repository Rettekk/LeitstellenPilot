using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeitstellenPilot.Models;
using LeitstellenPilot.Services;

namespace LeitstellenPilot.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool _stationUpgradeEnabled;
    [ObservableProperty] private int _stationUpgradeOrder;
    [ObservableProperty] private int _stationUpgradeTargetLevel = 3;
    [ObservableProperty] private long _stationUpgradeReserveCredits = 100000;
    [ObservableProperty] private long _stationUpgradeDailyBudgetCredits = 100000;
    [ObservableProperty] private int _stationUpgradeIntervalMinutes = 15;
    [ObservableProperty] private bool _upgradeFireStations = true;
    [ObservableProperty] private bool _upgradePoliceStations = true;
    [ObservableProperty] private bool _upgradeRescueStations = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStationUpgradeStatus))] private string _stationUpgradeStatus = "";
    public bool HasStationUpgradeStatus => !string.IsNullOrWhiteSpace(StationUpgradeStatus);
    public int StationUpgradeTargetMode
    {
        get => StationUpgradeTargetLevel == 0 ? 0 : 1;
        set { if (value == 0) StationUpgradeTargetLevel = 0; else if (StationUpgradeTargetLevel == 0) StationUpgradeTargetLevel = 3; }
    }
    public bool HasStationUpgradeTarget => StationUpgradeTargetLevel > 0;
    public int StationUpgradeFixedTarget
    {
        get => Math.Max(1, StationUpgradeTargetLevel);
        set => StationUpgradeTargetLevel = Math.Clamp(value, 1, 30);
    }
    public IAsyncRelayCommand AcknowledgeStationUpgradeCommand { get; private set; } = null!;
    public bool HasUnconfirmedStationUpgrade
    {
        get
        {
            var id = UserInfo?.user_id ?? 0;
            if (id <= 0) int.TryParse(_config.Session.UserId, out id);
            return id > 0 && _config.Config.StationUpgrades.Ledgers.GetValueOrDefault(StationUpgradePlanner.AccountKey(id))?.Pending != null;
        }
    }
    private void RefreshStationUpgradeNotice()
    {
        OnPropertyChanged(nameof(HasUnconfirmedStationUpgrade));
        AcknowledgeStationUpgradeCommand?.NotifyCanExecuteChanged();
    }
    private void InitializeStationUpgrades()
    {
        var settings = _config.Config.StationUpgrades;
        StationUpgradeEnabled = settings.Enabled; StationUpgradeOrder = settings.Order;
        StationUpgradeTargetLevel = settings.TargetLevel; StationUpgradeReserveCredits = settings.ReserveCredits;
        StationUpgradeDailyBudgetCredits = settings.DailyBudgetCredits; StationUpgradeIntervalMinutes = settings.IntervalMinutes;
        UpgradeFireStations = settings.FireStations; UpgradePoliceStations = settings.PoliceStations; UpgradeRescueStations = settings.RescueStations;
        AcknowledgeStationUpgradeCommand = new AsyncRelayCommand(() => RunOperationAsync(AcknowledgeStationUpgradeAsync),
            () => !_disposed && IsLoggedIn && !IsBusy && !StationUpgradeEnabled && HasUnconfirmedStationUpgrade);
        _bot.StationUpgradeChanged += result => Post(() => { StationUpgradeStatus = result.Message; RefreshStationUpgradeNotice(); });
        _bot.RunningChanged += _ => Post(RefreshStationUpgradeNotice);
        PropertyChanged += (_, args) => { if (args.PropertyName is nameof(UserInfo) or nameof(IsLoggedIn) or nameof(IsBusy)) RefreshStationUpgradeNotice(); };
    }
    private async Task AcknowledgeStationUpgradeAsync()
    {
        await _api.AcknowledgePendingStationUpgradeAsync(_lifetime.Token);
        StationUpgradeStatus = "Ausbau nach manueller Prüfung freigegeben. Der reservierte Betrag bleibt im Tagesbudget berücksichtigt.";
        RefreshStationUpgradeNotice();
    }
    partial void OnStationUpgradeEnabledChanged(bool value)
    {
        _config.Config.StationUpgrades.Enabled = value;
        StationUpgradeStatus = value ? "Wird bei laufendem Bot geprüft." : "";
        RefreshStationUpgradeNotice(); SaveSettings();
    }
    partial void OnStationUpgradeOrderChanged(int value) { _config.Config.StationUpgrades.Order = Math.Clamp(value, 0, 2); SaveSettings(); }
    partial void OnStationUpgradeTargetLevelChanged(int value)
    {
        _config.Config.StationUpgrades.TargetLevel = Math.Clamp(value, 0, 30);
        OnPropertyChanged(nameof(StationUpgradeTargetMode)); OnPropertyChanged(nameof(HasStationUpgradeTarget));
        OnPropertyChanged(nameof(StationUpgradeFixedTarget)); SaveSettings();
    }
    partial void OnStationUpgradeReserveCreditsChanged(long value) { _config.Config.StationUpgrades.ReserveCredits = Math.Clamp(value, 0, 1000000000); SaveSettings(); }
    partial void OnStationUpgradeDailyBudgetCreditsChanged(long value) { _config.Config.StationUpgrades.DailyBudgetCredits = Math.Clamp(value, 0, 1000000000); SaveSettings(); }
    partial void OnStationUpgradeIntervalMinutesChanged(int value) { _config.Config.StationUpgrades.IntervalMinutes = Math.Clamp(value, 1, 1440); SaveSettings(); }
    partial void OnUpgradeFireStationsChanged(bool value) { _config.Config.StationUpgrades.FireStations = value; SaveSettings(); }
    partial void OnUpgradePoliceStationsChanged(bool value) { _config.Config.StationUpgrades.PoliceStations = value; SaveSettings(); }
    partial void OnUpgradeRescueStationsChanged(bool value) { _config.Config.StationUpgrades.RescueStations = value; SaveSettings(); }
}
