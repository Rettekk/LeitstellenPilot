using System;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public interface IPersonnelApi
{
    Task<PersonnelRunReport> OptimizePersonnelAsync(bool onlyChanged = false, IProgress<PersonnelProgress>? progress = null,
        CancellationToken token = default);
}
