using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;
using Newtonsoft.Json;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService : IActiveMissionApi
{
    private readonly ConcurrentDictionary<long, (DateTime ReadAt, string Html)> _inspectionPages = new();

    public async Task<ActiveMissionInspection> InspectActiveMissionAsync(Mission mission, CancellationToken token = default)
    {
        EnsureLoggedIn();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        string html;
        try { html = await GetTextAsync($"/missions/{mission.Id}", deadline.Token); }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("Detailprüfung: Zeitüberschreitung nach 5 Sekunden; später erneut prüfen.", ex); }
        var details = await ParseDetailsWithRefreshAsync(html, mission, token);
        ObserveCrew(mission, details);
        foreach (var field in details.HiddenFields.Where(field => field.Key == "authenticity_token")) Logger?.RegisterSecret(field.Value);
        foreach (var cached in _inspectionPages.Where(pair => UtcNow - pair.Value.ReadAt > TimeSpan.FromSeconds(2))) _inspectionPages.TryRemove(cached.Key, out _);
        _inspectionPages[mission.Id] = (UtcNow, html);
        var hasRequirements = details.HasMissingRequirementsFromPage;
        var signature = hasRequirements ? details.TypeKey + "|" + JsonConvert.SerializeObject(details.MissingRequirements.OrderBy(demand => demand.Key)) + "|" +
            JsonConvert.SerializeObject(details.UnparsedRequirements.Order()) + "|" + details.PatientRequirementSignature + "|" +
            string.Join(",", details.EnRouteVehicleIds.Order()) : "";
        var text = string.Join("; ", details.MissingRequirements.Select(demand => $"{demand.Count} × {_rules.Label(demand.Key, _labels)}")
            .Concat(details.UnparsedRequirements));
        var unrecognized = details.UnparsedRequirements.Count > 0 ||
            hasRequirements && details.MissingRequirements.Count == 0;
        if (unrecognized && text.Length == 0) text = "Die Nachforderung ist nicht lesbar. Bitte im Spiel prüfen.";
        return new(hasRequirements, signature, text) { HasUnrecognizedRequirements = unrecognized };
    }

    private Task<string> ReadDispatchPageAsync(Mission mission, CancellationToken token)
    {
        if (_inspectionPages.TryRemove(mission.Id, out var cached) && UtcNow - cached.ReadAt <= TimeSpan.FromSeconds(2))
            return Task.FromResult(cached.Html);
        return GetTextAsync($"/missions/{mission.Id}", token);
    }
}
