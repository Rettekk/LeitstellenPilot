using System;
using CommunityToolkit.Mvvm.ComponentModel;
using LeitstellenPilot.Services;

namespace LeitstellenPilot.ViewModels;

public sealed class RunningMissionViewModel : ObservableObject
{
    public long MissionId { get; }
    public string MissionName { get; }
    public DateTime StartTime { get; }
    public DateTime? EstimatedCompletion { get; }
    public string StatusText { get; }
    public string RemainingTimeText => EstimatedCompletion.HasValue
        ? TimeSpan.FromSeconds(Math.Max(0, (EstimatedCompletion.Value - DateTime.UtcNow).TotalSeconds)).ToString(@"hh\:mm\:ss")
        : "Restzeit unbekannt";

    public RunningMissionViewModel(BotService.StartedMission mission)
    {
        MissionId = mission.MissionId; MissionName = mission.MissionName;
        StartTime = mission.StartTime; EstimatedCompletion = mission.Completion; StatusText = mission.State;
    }
    public void UpdateProgress() => OnPropertyChanged(nameof(RemainingTimeText));
}
