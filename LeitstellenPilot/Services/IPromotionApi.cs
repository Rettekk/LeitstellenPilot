using System.Threading;
using System.Threading.Tasks;

namespace LeitstellenPilot.Services;

public interface IPromotionApi
{
    Task<PromotionResult> CollectPromotionAsync(CancellationToken token = default);
}
