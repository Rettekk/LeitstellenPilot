using System;

using System.Collections.Generic;

namespace LeitstellenPilot.Models
{
    public class AppConfig
    {
        public bool Headless { get; set; }
        public string TimeZone { get; set; } = "";
        public string LoginIdentifier { get; set; } = ""; 
        public string Password { get; set; } = ""; 
        public string DisplayName { get; set; } = ""; 
        public BotSettings BotSettings { get; set; } = new();
        public DateTime LastConfigUpdate { get; set; } = DateTime.UtcNow;
        public Dictionary<string, string> DailyBonusClaimDays { get; set; } = new();
        public StationUpgradeSettings StationUpgrades { get; set; } = new();
        public Dictionary<string, DateTime> EventItemClaims { get; set; } = new();
        public List<VehicleLoan> VehicleLoans { get; set; } = new();
        public List<FleetBlockedMission> FleetBlockedMissions { get; set; } = new();
        public Dictionary<string, string> PersonnelLayouts { get; set; } = new();
        public Dictionary<string, PersonnelBackup> PersonnelBackups { get; set; } = new();
    }

    public class BotSettings  
    {
        public bool AutoStart { get; set; } = false;
        public bool StartWithWindows { get; set; }
        public bool AutoLogin { get; set; } = true;
        public bool AutoAlarm { get; set; } = true;
        public int ScanIntervalSeconds { get; set; } = 10;
        public int MaxScanIntervalSeconds { get; set; } = 20;
        public bool ScanIntervalConfigured { get; set; }
        public int MaxConcurrentMissions { get; set; } = 10;
        public int MaxApproachMinutes { get; set; } = Services.ApproachPolicy.DefaultMinutes;
        public int MinActionDelaySeconds { get; set; } = 3;
        public int MaxActionDelaySeconds { get; set; } = 11;
        public int MinPersonnelActionDelaySeconds { get; set; } = 3;
        public int MaxPersonnelActionDelaySeconds { get; set; } = 11;
        public bool AutoTransport { get; set; } = true;
        public bool ReleasePrisonersWithoutCell { get; set; }
        public bool ReleasePatientsWithoutBed { get; set; } = true;
        public int HospitalWaitMinutes { get; set; } = 10;
        public bool AutoClaimDailyBonus { get; set; } = true;
        public bool AutoClaimTaskRewards { get; set; } = true;
        public bool AutoPromotion { get; set; } = true;
        public bool AutoCollectEventItems { get; set; }
        public bool AdaptivePolling { get; set; } = true;
        public int ActivePollingSeconds { get; set; } = 10;
        public int DetailReviewIntervalSeconds { get; set; } = 15;
        public int MaxDetailReviewIntervalSeconds { get; set; } = 30;
        public int IdlePollingSeconds { get; set; } = 20;
        public int PollingDefaultsVersion { get; set; }
        public int PollingVariationPercent { get; set; } = 10;
        public int AccountRefreshSeconds { get; set; } = 60;
        public int MaxAlarmsPerCycle { get; set; } = 1;
        public bool PrioritizeMissingVehicles { get; set; } = true;
        public bool PreferHighCreditMissions { get; set; } = true;
        public bool ReassignBlockedMissions { get; set; } = true;
        public bool ReleaseUnsolvableMissions { get; set; } = true;
        public bool AutoPlannedMissions { get; set; } = true;
        public int PlannedMaxDurationMinutes { get; set; } = 60;
        public int PlannedBufferMinutes { get; set; } = 3;
        public int PlannedFleetReservePercent { get; set; } = 50;
        public int MaxPlannedMissions { get; set; } = 1;
        public int PacingDefaultsVersion { get; set; }
        public bool WorkRestCycleEnabled { get; set; }
        public double WorkHours { get; set; } = 4;
        public double RestHours { get; set; } = 1;
        public bool NightRestEnabled { get; set; }
        public string NightRestStart { get; set; } = "00:00";
        public string NightRestEnd { get; set; } = "06:00";
        public bool AutoPersonnelAfterNightRest { get; set; }
    }

    public sealed record PersonnelBackup(DateTime CapturedUtc, Dictionary<long, long?> Assignments, Dictionary<long, int> Maximums);
}
