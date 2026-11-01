using LeitstellenPilot.Models;

namespace LeitstellenPilot.ViewModels;

public sealed class PersonnelStationViewModel(PersonnelStationReport report)
{
    public string Name => report.Name;
    public string State => report.State switch
    {
        "Fehler" => "Prüfung fehlgeschlagen",
        "Übersprungen" => "Noch nicht geprüft",
        _ when report.WarningMessage.Length > 0 => "Personalbedarf prüfen",
        "Gespeichert" => "Angepasst",
        _ => "Bereits passend"
    };
    public string StateColor => report.State == "Fehler" ? "#EF9292" : report.NeedsAttention ? "#E9C07B" :
        report.State == "Gespeichert" ? "#8CD0A5" : "#A5B4C8";
    public string Notice => report.State == "Fehler"
        ? "Änderungen können teilweise übernommen sein. Bitte die Personalprüfung erneut durchführen."
        : report.WarningMessage.Length > 0 ? report.WarningMessage : report.Message;
    public string Details => report.Message;
    public string Vehicles => report.VehicleSummary;
    public bool HasVehicles => report.Vehicles.Count > 0;
}
