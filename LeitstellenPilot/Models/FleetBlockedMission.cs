namespace LeitstellenPilot.Models;

public sealed class FleetBlockedMission
{
    public string Account { get; set; } = "";
    public long MissionId { get; set; }
    public string RequirementKey { get; set; } = "";
    public string Reason { get; set; } = "";
}
