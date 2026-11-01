using System;
using System.Collections.Generic;
using System.Linq;

namespace LeitstellenPilot.Models;

public sealed record StationPerson(long Id, string[] Training, bool Available, long? VehicleId);
public sealed record PersonnelVehicleResult(long VehicleId, string Name, int Before, int After, int Maximum, string Detail);
public sealed record PersonnelStationReport(long BuildingId, string Name, string State, string Message,
    IReadOnlyList<PersonnelVehicleResult> Vehicles, string WarningMessage = "")
{
    public bool NeedsAttention => State is "Fehler" or "Übersprungen" || WarningMessage.Length > 0;
    public string VehicleSummary => string.Join("\n", Vehicles.Select(v => $"{v.Name}: {v.Before} → {v.After} Personen · Maximum {v.Maximum}" +
        (v.Detail.Length > 0 ? " · " + v.Detail : "")));
}
public sealed record PersonnelProgress(string Message, PersonnelStationReport? Station = null);
public sealed class PersonnelRunReport
{
    public List<PersonnelStationReport> Stations { get; } = new();
    public bool Interrupted { get; set; }
    public string StopReason { get; set; } = "";
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            var changed = Stations.Count(s => s.State == "Gespeichert");
            var unchanged = Stations.Count(s => s.State == "Unverändert");
            var attention = Stations.Count(s => s.NeedsAttention);
            if (changed > 0) parts.Add($"{changed} {(changed == 1 ? "Wache" : "Wachen")} angepasst");
            if (unchanged > 0) parts.Add($"{unchanged} bereits passend");
            if (attention > 0) parts.Add($"{attention} {(attention == 1 ? "Hinweis" : "Hinweise")}");
            return $"Personalprüfung {(Interrupted ? "unterbrochen" : "abgeschlossen")}" +
                (parts.Count > 0 ? " · " + string.Join(" · ", parts) : "") + ".";
        }
    }
    public string Message => $"Personalprüfung {(Interrupted ? "unterbrochen" : "beendet")}: " +
        $"{Stations.Count(s => s.State == "Gespeichert")} {(Stations.Count(s => s.State == "Gespeichert") == 1 ? "Wache" : "Wachen")} angepasst, " +
        $"{Stations.Count(s => s.State == "Unverändert")} unverändert, " +
        $"{Stations.Count(s => s.State == "Übersprungen")} übersprungen, " +
        $"{Stations.Count(s => s.State == "Fehler")} mit Fehlern." + (StopReason.Length > 0 ? " " + StopReason : "");
}

public sealed record CrewTraining(string Key, int Minimum, bool Everyone);
public sealed record PersonnelEquipment(long Id, long VehicleId, string Name);
public sealed record PersonnelVehicleRule(Vehicle Vehicle, int Minimum, int Maximum, IReadOnlyList<CrewTraining> Training)
{
    public bool CanRide(StationPerson person) => person.Available && Training.Where(t => t.Everyone).All(t => person.Training.Contains(t.Key));
}
public sealed class PersonnelStationPlan
{
    public Dictionary<long, long?> Assignments { get; } = new();
    public Dictionary<long, int> Maximums { get; } = new();
    public List<string> Problems { get; } = new();
    public bool IsValid => Problems.Count == 0;
}
