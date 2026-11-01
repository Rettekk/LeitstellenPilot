using System;
using System.Collections.Generic;

namespace LeitstellenPilot.Models;

public sealed class StationUpgradeSettings
{
    public bool Enabled { get; set; }
    public int Order { get; set; }
    public int TargetLevel { get; set; } = 3;
    public long ReserveCredits { get; set; } = 100000;
    public long DailyBudgetCredits { get; set; } = 100000;
    public int IntervalMinutes { get; set; } = 15;
    public bool FireStations { get; set; } = true;
    public bool PoliceStations { get; set; } = true;
    public bool RescueStations { get; set; } = true;
    public Dictionary<string, StationUpgradeLedger> Ledgers { get; set; } = new();
}

public sealed record PendingStationUpgrade(long BuildingId, int FromLevel, int TargetLevel, long Credits, DateTime RequestedUtc);
public sealed record StationUpgradeLedger(string Day, long SpentCredits, PendingStationUpgrade? Pending = null);
public sealed record StationUpgradeSite(long Id, int TypeId, int Level, string Name);
public sealed record StationUpgradeOffer(StationUpgradeSite Site, int TargetLevel, long Credits, string ActionPath);
public enum StationUpgradeState { Disabled, Waiting, Skipped, Changed, Confirmed, Pending }
public sealed record StationUpgradeResult(StationUpgradeState State, string Message);
