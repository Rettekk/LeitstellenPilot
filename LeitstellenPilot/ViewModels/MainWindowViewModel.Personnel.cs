using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BotStatus), nameof(FooterStatus), nameof(ShowFooter))] private bool _personnelBusy;
    [ObservableProperty] private bool _autoPersonnelAfterNightRest;
    [ObservableProperty] private int _minPersonnelActionDelaySeconds = 3;
    [ObservableProperty] private int _maxPersonnelActionDelaySeconds = 11;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FooterStatus), nameof(ShowFooter))] private string _personnelStatus = "Für eine vollständige Prüfung sollten alle Fahrzeuge auf ihrer Wache sein.";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPersonnelRunNotice))] private string _personnelRunNotice = "";
    public bool HasPersonnelRunNotice => PersonnelRunNotice.Length > 0;
    public ObservableCollection<PersonnelStationReport> PersonnelReports { get; } = new();
    public ObservableCollection<PersonnelStationViewModel> PersonnelProblems { get; } = new();
    public ObservableCollection<PersonnelStationViewModel> PersonnelResults { get; } = new();
    public bool HasPersonnelResults => PersonnelResults.Count > 0;
    public string PersonnelResultsTitle => $"Ergebnisse anzeigen ({PersonnelResults.Count})";
    public IAsyncRelayCommand OptimizePersonnelCommand { get; private set; } = null!;
    public IRelayCommand CancelPersonnelCommand { get; private set; } = null!;
    private void InitializePersonnel()
    {
        AutoPersonnelAfterNightRest = _config.Config.BotSettings.AutoPersonnelAfterNightRest;
        var settings = _config.Config.BotSettings;
        var minimum = Math.Clamp(settings.MinPersonnelActionDelaySeconds, 1, 300);
        var maximum = Math.Clamp(settings.MaxPersonnelActionDelaySeconds, minimum, 300);
        MinPersonnelActionDelaySeconds = minimum;
        MaxPersonnelActionDelaySeconds = maximum;
        OptimizePersonnelCommand = new AsyncRelayCommand(() => RunOperationAsync(OptimizePersonnelAsync),
            () => !_disposed && IsLoggedIn && !IsBusy && !PersonnelBusy);
        CancelPersonnelCommand = new RelayCommand(() => _bot.CancelPersonnel(), () => PersonnelBusy);
        PersonnelReports.CollectionChanged += (_, change) =>
        {
            void Add(PersonnelStationReport report)
            {
                var item = new PersonnelStationViewModel(report);
                if (report.State == "Fehler") PersonnelProblems.Insert(0, item);
                else (report.NeedsAttention ? PersonnelProblems : PersonnelResults).Add(item);
            }
            if (change.Action == NotifyCollectionChangedAction.Add)
            {
                foreach (PersonnelStationReport report in change.NewItems!) Add(report);
            }
            else
            {
                PersonnelProblems.Clear(); PersonnelResults.Clear();
                foreach (var report in PersonnelReports) Add(report);
            }
            OnPropertyChanged(nameof(HasPersonnelResults)); OnPropertyChanged(nameof(PersonnelResultsTitle));
        };
        _bot.PersonnelBusyChanged += busy => Post(() =>
        {
            if (busy) { PersonnelReports.Clear(); PersonnelRunNotice = ""; }
            PersonnelBusy = busy; UpdateCommands(); CancelPersonnelCommand.NotifyCanExecuteChanged();
        });
        _bot.PersonnelProgressChanged += update => Post(() =>
        {
            if (update.Station != null) PersonnelReports.Add(update.Station);
            else PersonnelStatus = update.Message;
        });
        _bot.PersonnelRunFinished += report => Post(() => ApplyPersonnelReport(report));
    }
    private async Task OptimizePersonnelAsync()
    {
        SelectedWorkspaceTab = 4;
        var report = await _bot.OptimizePersonnelAsync(_lifetime.Token);
        ApplyPersonnelReport(report);
    }
    private void ApplyPersonnelReport(PersonnelRunReport report)
    {
        PersonnelStatus = report.Summary;
        PersonnelRunNotice = !report.Interrupted ? "" : report.Stations.Exists(s => s.State == "Fehler")
            ? "Die Prüfung wurde gestoppt. Bitte die betroffenen Wachen prüfen und den Bot danach manuell starten."
            : report.StopReason;
    }
    partial void OnAutoPersonnelAfterNightRestChanged(bool value)
    { if (!_ready) return; _config.Config.BotSettings.AutoPersonnelAfterNightRest = value; SaveSettings(); }
    partial void OnMinPersonnelActionDelaySecondsChanged(int value)
    {
        var minimum = Math.Clamp(value, 1, 300);
        if (value != minimum) { MinPersonnelActionDelaySeconds = minimum; return; }
        _config.Config.BotSettings.MinPersonnelActionDelaySeconds = minimum;
        if (MaxPersonnelActionDelaySeconds < minimum) MaxPersonnelActionDelaySeconds = minimum;
        SaveSettings();
    }
    partial void OnMaxPersonnelActionDelaySecondsChanged(int value)
    {
        var maximum = Math.Clamp(value, Math.Clamp(MinPersonnelActionDelaySeconds, 1, 300), 300);
        if (value != maximum) { MaxPersonnelActionDelaySeconds = maximum; return; }
        _config.Config.BotSettings.MaxPersonnelActionDelaySeconds = maximum;
        SaveSettings();
    }
}
