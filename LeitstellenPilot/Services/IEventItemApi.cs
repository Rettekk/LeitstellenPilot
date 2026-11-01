using System.Threading;
using System.Threading.Tasks;

namespace LeitstellenPilot.Services;

public sealed record EventItemResult(bool Claimed, string Message);
public interface IEventItemApi
{
    Task<EventItemResult?> CollectEventItemAsync(CancellationToken token = default);
}
