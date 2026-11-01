using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LeitstellenPilot.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private string _nightRestStartText = "00:00";
    [ObservableProperty] private string _nightRestEndText = "06:00";
    private bool _editingNightTime;

    public double RestMinutes
    {
        get => Math.Round(RestHours * 60, 6);
        set => RestHours = Math.Clamp(value, 15, 1440) / 60;
    }

    partial void OnNightRestStartTextChanged(string value) => SetNightTimeFromText(value, start: true);
    partial void OnNightRestEndTextChanged(string value) => SetNightTimeFromText(value, start: false);

    private void SetNightTimeFromText(string value, bool start)
    {
        _editingNightTime = true;
        try
        {
            var time = ReadNightTime(value);
            if (start) NightRestStart = time;
            else NightRestEnd = time;
        }
        finally { _editingNightTime = false; }
    }

    private void UpdateNightTimeText(TimeSpan? value, bool start)
    {
        if (_editingNightTime) return;
        if (start) NightRestStartText = FormatNightTime(value);
        else NightRestEndText = FormatNightTime(value);
    }
}
