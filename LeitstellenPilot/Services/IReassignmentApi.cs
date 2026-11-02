using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed record ReassignmentResult(bool ActionSent, string Message, long SourceMissionId = 0,
    DispatchResult? TargetDispatch = null);
public interface IReassignmentApi
{
    long? FocusMissionId => null;
    Task<ReassignmentResult?> TryReassignAsync(Mission target, IReadOnlyList<Mission> board, CancellationToken token = default);
    Task<ReassignmentResult?> RestoreLoanAsync(IReadOnlyList<Mission> board, CancellationToken token = default);
    Task<ReassignmentResult?> ReleaseUnsolvableAsync(IReadOnlyList<Mission> board, CancellationToken token = default);
}
