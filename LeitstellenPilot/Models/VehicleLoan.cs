using System;

namespace LeitstellenPilot.Models;

public sealed class VehicleLoan
{
    public string Account { get; set; } = "";
    public long VehicleId { get; set; }
    public long SourceMissionId { get; set; }
    public long TargetMissionId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool ReturningToSource { get; set; }
    public bool SentToTarget { get; set; }
    public DateTime NextActionAfterUtc { get; set; }
    public string PackageId { get; set; } = "";
    public bool WithdrawalSent { get; set; }
    public bool RecallForStaffing { get; set; }
    public bool TargetConfirmed { get; set; }
    public int KnownCrew { get; set; }
    public long StaffingBuildingId { get; set; }
    public long[] ReservedVehicleIds { get; set; } = [];
}
