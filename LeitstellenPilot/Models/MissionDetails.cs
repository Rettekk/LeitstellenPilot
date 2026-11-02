using System.Collections.Generic;
using Newtonsoft.Json;

namespace LeitstellenPilot.Models;

public sealed class MissionDetails
{
    public string? TypeKey { get; set; }
    public string AlarmPath { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public List<KeyValuePair<string, string>> HiddenFields { get; } = new();
    public Dictionary<long, List<KeyValuePair<string, string>>> EquipmentFieldsByVehicleId { get; } = new();
    public HashSet<long> DisabledVehicleIds { get; } = new();
    public Dictionary<long, int> VehicleOrder { get; } = new();
    public Dictionary<long, int> VehicleTravelSeconds { get; } = new();
    public Dictionary<long, VehicleTravelOrigin> VehicleTravelOrigins { get; } = new();
    public List<RequirementDemand> MissingRequirements { get; } = new();
    public List<string> UnparsedRequirements { get; } = new();
    public HashSet<long> AssignedVehicleIds { get; } = new();
    public Dictionary<long, int> AssignedVehicleTypeIds { get; } = new();
    public HashSet<long> EnRouteVehicleIds { get; } = new();
    public Dictionary<long, int> CrewByVehicleId { get; } = new();
    public Dictionary<long, Dictionary<string, int>> TrainedCrewByVehicleId { get; } = new();
    public Dictionary<long, Dictionary<string, int>> UnconfirmedTrainedCrewByVehicleId { get; } = new();
    public Dictionary<long, string> VehicleStaffingProblems { get; } = new();
    public bool HasMissingRequirements { get; set; }
    public bool HasMissingRequirementsFromPage { get; set; }
    public bool HasPatients { get; set; }
    public string PatientRequirementSignature { get; set; } = "";
    public bool HasPrisonerTransportRequest { get; set; }
    public bool IsEnRouteTo(Vehicle vehicle,long missionId) => EnRouteVehicleIds.Contains(vehicle.Id) ||
        !AssignedVehicleIds.Contains(vehicle.Id) && vehicle.State==3 && vehicle.TargetType=="mission" && vehicle.TargetId==missionId;
}

public sealed record RequirementDemand(string Key, int Count);

public sealed class Vehicle
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("caption")] public string Name { get; set; } = "";
    [JsonProperty("vehicle_type")] public int TypeId { get; set; }
    [JsonProperty("building_id")] public long BuildingId { get; set; }
    [JsonProperty("fms_real")] public int State { get; set; }
    [JsonProperty("target_type")] public string? TargetType { get; set; }
    [JsonProperty("target_id")] public long? TargetId { get; set; }
    [JsonProperty("tractive_vehicle_id")] public long? TowingVehicleId { get; set; }
    [JsonProperty("assigned_personnel_count")] public int Personnel { get; set; }
    [JsonProperty("max_personnel_override")] public int? MaximumPersonnel { get; set; }
    [JsonProperty("queued_mission_id")] public long? QueuedMissionId { get; set; }
    [JsonIgnore] public bool IsAvailable =>
        QueuedMissionId.GetValueOrDefault() <= 0 &&
        (State == 1 && TargetType != "mission" || State == 2 && string.IsNullOrEmpty(TargetType));
    [JsonIgnore] public int Order { get; set; } = int.MaxValue;
}

public sealed class DispatchPlan
{
    public List<PlanningShortfall> Shortfalls { get; } = new();
    public bool CanSendPartialTopup { get; set; }
    public string WaitingForPersonnel { get; set; } = "";
    public bool ApproachLimited { get; set; }
    public long MissionId { get; init; }
    public List<RequirementDemand> Requirements { get; } = new();
    public List<Vehicle> SelectedVehicles { get; } = new();
    public Dictionary<long, int> AutomaticCrewAllocations { get; } = new();
    public List<string> Problems { get; } = new();
    public List<string> RecognitionProblems { get; } = new();
    public bool HasUnrecognizedRequirements => RecognitionProblems.Count > 0;
    public void AddRecognitionProblem(string problem)
    {
        Problems.Add(problem);
        RecognitionProblems.Add(problem);
    }
    public List<string> Notes { get; } = new();
    public bool IsComplete => Problems.Count == 0 && Requirements.Count > 0;
    public string Summary => string.Join("\n", Notes) +
        (SelectedVehicles.Count > 0 ? "\nAusgewählt: " + string.Join(", ", SelectedVehicles.ConvertAll(v => v.Name)) : "") +
        (Problems.Count > 0 ? "\nFehlt / unklar: " + string.Join("; ", Problems) : "");
}

public sealed record PlanningShortfall(string Key, string Label, int Missing, int[] VehicleTypes, bool Personnel = false);

public sealed record DispatchResult(bool Success, bool Sent, string Message, DispatchPlan? Plan = null)
{
    public bool KnownStaffingFailure { get; init; }
    public bool HasUnconfirmedVehicles { get; init; } = true;
    public bool AnyVehicleAccepted { get; init; }
}
