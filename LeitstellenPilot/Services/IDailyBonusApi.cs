using System.Threading;
using System.Threading.Tasks;

namespace LeitstellenPilot.Services;

public interface IDailyBonusApi
{
    Task<DailyBonusResult> CollectDailyBonusAsync(CancellationToken token = default);
}
