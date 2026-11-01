using System.Threading;
using System.Threading.Tasks;

namespace LeitstellenPilot.Services;

public interface ITaskRewardsApi
{
    Task<TaskRewardsResult> CollectTaskRewardsAsync(CancellationToken token = default);
}
