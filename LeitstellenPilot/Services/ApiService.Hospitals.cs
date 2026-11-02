using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private sealed record PatientWait(string RequestKey, DateTime Deadline, HospitalWaitSlot? Slot);
    private readonly Dictionary<long, PatientWait> _patientWaits = new();
    private readonly Dictionary<long, (HospitalDetails Details, DateTime Until)> _hospitalDetails = new();
    private string TransportSettingsKey => $"{_config.Config.BotSettings.ReleasePrisonersWithoutCell}/{_config.Config.BotSettings.ReleasePatientsWithoutBed}/{HospitalPlanner.NormalizeWait(_config.Config.BotSettings.HospitalWaitMinutes)}";

    private async Task<(TransportAction? Action, string? WaitReason)> SelectTransportAsync(string html, Vehicle vehicle, ISet<long> own,
        long? missionId, bool prisonersOnly, CancellationToken token, bool refreshHospitals = false)
    {
        var settings = _config.Config.BotSettings;
        var patient = prisonersOnly ? null : HospitalPageParser.ReadRequest(html, vehicle.Id, _httpClient.BaseAddress!, own);
        if (patient == null)
        {
            _patientWaits.Remove(vehicle.Id);
            return (TransportPlanner.Choose(html, vehicle.Id, _httpClient.BaseAddress!, own, settings.ReleasePrisonersWithoutCell, missionId, prisonersOnly), null);
        }
        var details = new Dictionary<long, HospitalDetails>();
        foreach (var offer in patient.Hospitals.Where(offer => offer.FreeBeds == 0 || patient.Department != null && offer.DepartmentMatches == null))
        {
            if (!refreshHospitals && _hospitalDetails.TryGetValue(offer.Action.BuildingId, out var cached) && cached.Until > UtcNow)
            { details[offer.Action.BuildingId] = cached.Details; continue; }
            try
            {
                var hospitalHtml = await GetTextAsync($"/buildings/{offer.Action.BuildingId}", token);
                var hospital = HospitalPageParser.ReadHospital(hospitalHtml, offer.Action.BuildingId, UtcNow);
                details[offer.Action.BuildingId] = hospital;
                _hospitalDetails[offer.Action.BuildingId] = (hospital, UtcNow.AddSeconds(30));
                Logger?.Write(LogLevel.Debug, "Transport", "hospital.parsed", "Krankenhausbelegung gelesen.");
            }
            catch (Exception error) when (error is HttpRequestException or FormatException)
            {
                Logger?.Write(LogLevel.Warning, "Transport", "hospital.read.failed", $"Krankenhaus {offer.Action.BuildingId}: Belegung oder Fachabteilungen konnten nicht bestätigt werden.");
                var reason = error is FormatException ? "Abgerufene Seite enthält keine erkennbare Krankenhausüberschrift oder Patiententabelle" :
                    error is HttpRequestException { StatusCode: { } status } ? $"Krankenhausabruf fehlgeschlagen (HTTP {(int)status})" : "Krankenhausabruf fehlgeschlagen (Verbindung oder Zeitüberschreitung)";
                var unknown = new HospitalDetails(offer.Action.BuildingId, new HashSet<string>(), false, [], false) { ParseIssue = reason };
                details[offer.Action.BuildingId] = unknown;
                _hospitalDetails[offer.Action.BuildingId] = (unknown, UtcNow.AddSeconds(30));
            }
        }
        HashSet<long>? recheckedFull = null;
        if (patient.Hospitals.Any(offer => offer.FreeBeds == 0 && details.TryGetValue(offer.Action.BuildingId, out var hospital) && hospital.FreeBeds > 0))
        {
            // Capacity offered to this RTW is authoritative, including beds
            // already reserved for other transports. Resolve a conflicting
            // hospital counter with a fresh offer instead of waiting forever.
            var fresh = HospitalPageParser.ReadRequest(await GetTextAsync($"/vehicles/{vehicle.Id}", token), vehicle.Id, _httpClient.BaseAddress!, own);
            if (fresh == null) return (null, "Patientensprechwunsch hat sich geändert; wird erneut geprüft.");
            patient = fresh;
            recheckedFull = patient.Hospitals.Where(offer => offer.FreeBeds == 0).Select(offer => offer.Action.BuildingId).ToHashSet();
        }
        var key = $"{missionId}/{patient.Department}/{HospitalPlanner.NormalizeWait(settings.HospitalWaitMinutes)}";
        var alreadyWaiting = _patientWaits.TryGetValue(vehicle.Id, out var waiting) && waiting.RequestKey == key;
        var deadline = alreadyWaiting ? waiting!.Deadline : UtcNow.AddMinutes(HospitalPlanner.NormalizeWait(settings.HospitalWaitMinutes));
        var reserved = _patientWaits.Where(pair => pair.Key != vehicle.Id && pair.Value.Deadline >= UtcNow && pair.Value.Slot != null)
            .Select(pair => (pair.Value.Slot!.BuildingId, pair.Value.Slot.PatientId)).ToHashSet();
        var protectedBeds = patient.Hospitals.ToDictionary(offer => offer.Action.BuildingId, offer =>
            _patientWaits.Where(pair => pair.Value.Deadline >= UtcNow && pair.Value.Slot?.BuildingId == offer.Action.BuildingId)
                .OrderBy(pair => pair.Value.Slot!.At).ThenBy(pair => pair.Key).Take(offer.FreeBeds).Count(pair => pair.Key != vehicle.Id));
        var decision = HospitalPlanner.Choose(patient, details, UtcNow, deadline, reserved, settings.ReleasePatientsWithoutBed, recheckedFull, protectedBeds);
        if (decision.Action == null && (decision.Wait != null || alreadyWaiting)) _patientWaits[vehicle.Id] = new(key, deadline, decision.Wait);
        else if (decision.Action == null) _patientWaits.Remove(vehicle.Id);
        return (decision.Action, decision.Action == null ? decision.Message : null);
    }

    private static bool PatientRequestCleared(string html)
    {
        var page = new HtmlDocument(); page.LoadHtml(html);
        return page.DocumentNode.SelectSingleNode("//*[@id='vehicle_details' or @id='vehicle_caption']") != null &&
            page.DocumentNode.SelectSingleNode("//*[@data-transport-request-type='patient' or @id='own-hospitals' or @id='leave_without_transport_no_compensation']") == null;
    }
}
