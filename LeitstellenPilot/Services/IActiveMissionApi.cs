using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed record ActiveMissionInspection(bool HasRequirements, string Signature, string RequirementText)
{
    public bool HasUnrecognizedRequirements { get; init; }
}

public interface IActiveMissionApi
{
    Task<ActiveMissionInspection> InspectActiveMissionAsync(Mission mission, CancellationToken token = default);
}
