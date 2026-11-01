using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Models;

public sealed class Mission
{
    public string id { get; set; } = "";
    public string caption { get; set; } = "";
    public string address { get; set; } = "";
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int average_credits { get; set; }
    public string icon { get; set; } = "";
    public int vehicle_state { get; set; }
    public int? mtid { get; set; }
    public int? overlay_index { get; set; }
    public string? additive_overlays { get; set; }
    public JToken? missing_text { get; set; }
    public int patients_count { get; set; }
    public int prisoners_count { get; set; }
    public bool sw { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public double sw_start_in { get; set; }
    public long date_end { get; set; }
    public long date_now { get; set; }
    [JsonIgnore] public DateTime? SnapshotReceivedAt { get; set; }

    [JsonIgnore] public long Id => long.TryParse(id, out var value) ? value : 0;
    [JsonIgnore] public string DisplayTitle => string.IsNullOrEmpty(caption) ? $"Einsatz {id}" : caption;
    [JsonIgnore] public string DisplayAddress => address;
    [JsonIgnore] public bool HasAddress => !string.IsNullOrWhiteSpace(address);
    [JsonIgnore] public bool HasStatusText => NeedsAttention || StatusText != "Offen";
    [JsonIgnore] public string CreditsFormatted => average_credits.ToString("N0");
    [JsonIgnore] public bool HasCreditEstimate => average_credits > 0;
    [JsonIgnore] public bool NeedsAttention => HasMissingVehicles || HasUnrecognizedRequirements || RequiresDetailsReview ||
        FleetBlockReason.Length > 0 || WaitingForPersonnel.Length > 0 || PlanningProblem.Length > 0;
    [JsonIgnore] public string? TypeKey => mtid.HasValue
        ? mtid + (overlay_index.HasValue ? $"-{overlay_index}" : "") +
          (string.IsNullOrEmpty(additive_overlays) ? "" : $"/{additive_overlays}") : null;
    [JsonIgnore] public string MissingVehiclesText => MissionShortageText.Read(missing_text);
    [JsonIgnore] public bool HasPrisonerTransportRequest => TransportStatus.HasPrisoners(MissingVehiclesText);
    [JsonIgnore] public bool HasMissingVehicles => !string.IsNullOrWhiteSpace(TransportStatus.WithoutPrisoners(MissingVehiclesText)) ||
        missing_text?.Type == JTokenType.Boolean && missing_text.Value<bool>();
    [JsonIgnore] public bool IsDispatchCovered { get; set; }
    [JsonIgnore] public string PlanningProblem { get; set; } = "";
    [JsonIgnore] public bool HasUnrecognizedRequirements { get; set; }
    [JsonIgnore] public bool RequiresDetailsReview { get; set; }
    [JsonIgnore] public string PlanningWaitReason { get; set; } = "";
    [JsonIgnore] public bool HasLiveRequirements { get; set; }
    [JsonIgnore] public string LiveRequirementsSignature { get; set; } = "";
    [JsonIgnore] public string LiveRequirementText { get; set; } = "";
    [JsonIgnore] public string WaitingForPersonnel { get; set; } = "";
    [JsonIgnore] public string FleetBlockReason { get; set; } = "";
    [JsonIgnore] public string ScheduledStatus { get; set; } = "";
    [JsonIgnore] public DateTime? NextScheduledReviewAt { get; set; }
    [JsonIgnore] public DateTime? PlannedStartAt => sw && SnapshotReceivedAt.HasValue && double.IsFinite(sw_start_in) && sw_start_in is > 0 and < 604800
        ? SnapshotReceivedAt.Value.AddSeconds(sw_start_in) : null;
    [JsonIgnore] public bool NeedsDispatch => !IsDispatchCovered &&
        (vehicle_state == 0 || HasMissingVehicles || HasLiveRequirements || RequiresDetailsReview || !string.IsNullOrEmpty(PlanningProblem) || FleetBlockReason.Length > 0);
    [JsonIgnore] public bool IsActive => !NeedsDispatch && (vehicle_state > 0 || IsDispatchCovered);
    [JsonIgnore] public string CoverageSignature => $"{TypeKey}|{JsonConvert.SerializeObject((caption ?? "").Trim())}|{patients_count}|{prisoners_count}|{missing_text?.ToString(Formatting.None)}|{LiveRequirementsSignature}";
    [JsonIgnore] public string StatusText =>
        FleetBlockReason.Length > 0 ? FleetBlockReason :
        ScheduledStatus.Length > 0 ? ScheduledStatus :
        WaitingForPersonnel.Length > 0 ? WaitingForPersonnel :
        PlanningWaitReason.Length > 0 ? PlanningWaitReason :
        !string.IsNullOrEmpty(PlanningProblem) ? $"Nachforderung: {PlanningProblem}" :
        HasLiveRequirements && !IsDispatchCovered ? $"Nachforderung: {LiveRequirementText}" :
        RequiresDetailsReview ? "Einsatz geändert · aktuellen Bedarf prüfen" :
        HasPrisonerTransportRequest && !HasMissingVehicles ? "Gefangenentransport offen" :
        IsDispatchCovered ? (vehicle_state == 2 ? "In Bearbeitung" : "Anforderungen abgedeckt · Fahrzeuge alarmiert") :
        HasMissingVehicles ? $"Nachforderung: {MissingVehiclesText}" :
        vehicle_state switch { 2 => "In Bearbeitung", 1 => "Fahrzeuge unterwegs", _ => "Offen" };
    [JsonIgnore] public DateTime? EstimatedCompletion => date_end > 0 && date_now > 0
        ? (SnapshotReceivedAt ?? DateTime.UtcNow).AddSeconds(Math.Max(0, date_end - date_now)) : null;
}
