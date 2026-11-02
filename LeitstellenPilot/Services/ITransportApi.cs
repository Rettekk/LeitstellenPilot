using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LeitstellenPilot.Services;

public interface ITransportApi
{
    Task<IReadOnlyList<TransportResult>> HandleTransportRequestsAsync(CancellationToken token = default, long? missionId = null);
}
