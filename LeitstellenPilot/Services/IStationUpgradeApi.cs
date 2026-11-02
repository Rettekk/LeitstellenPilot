using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public interface IStationUpgradeApi
{
    Task<StationUpgradeResult> UpgradeStationAsync(CancellationToken token = default);
    Task AcknowledgePendingStationUpgradeAsync(CancellationToken token = default);
}
