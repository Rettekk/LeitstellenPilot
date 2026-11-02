using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed record PlannedMissionResult(long MissionId, DispatchResult Dispatch);
public interface IPlannedMissionApi
{
    int ReservedPlannedSlots { get; }
    Task<PlannedMissionResult?> PlanScheduledMissionsAsync(IReadOnlyList<Mission> board, int occupiedSlots, CancellationToken token = default);
}
