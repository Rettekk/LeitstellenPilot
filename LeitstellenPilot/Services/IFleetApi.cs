using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public interface IFleetApi
{
    Task<List<Vehicle>> GetVehiclesAsync(bool refresh = false, CancellationToken token = default);
}
