using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public interface IGameApi
{
    bool IsLoggedIn { get; }
    Task<List<Mission>> GetMissionsAsync(CancellationToken token = default);
    Task<DispatchResult> DispatchAsync(Mission mission, bool preview = false, CancellationToken token = default, bool automatic = false);
}
