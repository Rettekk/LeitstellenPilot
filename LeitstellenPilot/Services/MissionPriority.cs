using System.Collections.Generic;
using System.Linq;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public static class MissionPriority
{
    public static IOrderedEnumerable<Mission> Order(IEnumerable<Mission> missions, BotSettings settings) => missions
        .OrderBy(m => m.sw)
        .ThenByDescending(m => settings.PrioritizeMissingVehicles && m.vehicle_state > 0 && (m.HasMissingVehicles || m.HasLiveRequirements || m.RequiresDetailsReview))
        .ThenByDescending(m => settings.PrioritizeMissingVehicles && (m.HasMissingVehicles || m.HasLiveRequirements || m.PlanningProblem.Length > 0))
        .ThenByDescending(m => settings.PreferHighCreditMissions ? m.average_credits : 0).ThenBy(m => m.Id);
}
