using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;
using Newtonsoft.Json;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private int ApproachLimit => ApproachPolicy.NormalizeMinutes(_config.Config.BotSettings.MaxApproachMinutes);
    private static bool ValidLocation(double? lat, double? lon) => lat.HasValue && lon.HasValue &&
        double.IsFinite(lat.Value) && double.IsFinite(lon.Value) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    private bool ApproachAllowsSend(DispatchPlan plan, MissionDetails details) =>
        plan.SelectedVehicles.All(vehicle => ApproachPolicy.Allows(vehicle.Id, details, ApproachLimit));

    private async Task PrepareApproachesAsync(Mission mission, EinsatzInfo? spec, MissionDetails details, IReadOnlyList<Vehicle> fleet,
        CancellationToken token, IReadOnlyDictionary<long, VehicleTravelOrigin>? sceneOrigins = null, ISet<long>? fromHome = null)
    {
        if (ApproachLimit == 0) return;
        foreach (var id in fromHome ?? Enumerable.Empty<long>())
        { details.VehicleTravelOrigins.Remove(id); details.VehicleTravelSeconds.Remove(id); }
        foreach (var (id, origin) in sceneOrigins ?? new Dictionary<long, VehicleTravelOrigin>())
        { details.VehicleTravelOrigins[id] = origin; details.VehicleTravelSeconds.Remove(id); }
        // This unbounded, read-only draft is used solely to obtain canonical
        // demand types. Only the bounded planner can produce a sendable plan.
        var data = _references.Snapshot;
        var draft = new VehiclePlanner(data.Vehicles, data.Labels, data.Rules).Build(mission, spec, details,
            PlanningFleet(fleet), ReservedForMission(mission.Id), StationCrewBudgets(fleet, mission, details));
        var types = draft.Requirements.Where(demand => _rules.Types.ContainsKey(demand.Key)).SelectMany(demand => _rules.Types[demand.Key]).ToHashSet();
        var allTypes = draft.Requirements.Any(demand => !_rules.Types.ContainsKey(demand.Key));
        foreach (var type in types.ToArray())
        {
            if (!_vehicleTypes.TryGetValue(type.ToString(), out var metadata)) continue;
            types.UnionWith(metadata["tractiveVehicles"]?.ToObject<int[]>() ?? []);
            if ((metadata.Value<string>("caption") ?? "").StartsWith("AB-", StringComparison.Ordinal)) types.Add(46);
        }
        var candidates = fleet.Where(vehicle => vehicle.IsAvailable && !details.DisabledVehicleIds.Contains(vehicle.Id) &&
            (allTypes || types.Contains(vehicle.TypeId)) && !details.VehicleTravelSeconds.ContainsKey(vehicle.Id)).ToArray();
        if (!ValidLocation(details.Latitude, details.Longitude) || candidates.Length == 0) return;
        if (candidates.Any(vehicle => vehicle.State == 2 && !details.VehicleTravelOrigins.ContainsKey(vehicle.Id)))
        {
            try { await GetBuildingsAsync(token); }
            catch (Exception error) when (error is HttpRequestException or FormatException or JsonException)
            { Logger?.Write(LogLevel.Debug, "Dispatch", "approach.station.unknown", "Wachstandorte für fehlende Anfahrtszeiten noch nicht verfügbar."); }
        }
        foreach (var vehicle in candidates.Where(vehicle => vehicle.State == 2 && !details.VehicleTravelOrigins.ContainsKey(vehicle.Id)))
        {
            var station = _buildings?.FirstOrDefault(building => building.Value<long?>("id") == vehicle.BuildingId);
            var lat = station?.Value<double?>("latitude"); var lon = station?.Value<double?>("longitude");
            if (ValidLocation(lat, lon)) details.VehicleTravelOrigins[vehicle.Id] = new(lat!.Value, lon!.Value, false, 0);
        }
        var groups = candidates.Where(vehicle => details.VehicleTravelOrigins.TryGetValue(vehicle.Id, out var origin) && ValidLocation(origin.Latitude, origin.Longitude))
            .GroupBy(vehicle => { var origin = details.VehicleTravelOrigins[vehicle.Id]; return (origin.Latitude, origin.Longitude, origin.Direct); })
            .OrderBy(group => DistanceKm(group.Key.Latitude, group.Key.Longitude, details.Latitude!.Value, details.Longitude!.Value)).ToArray();
        await Task.WhenAll(groups.Select(async group =>
        {
            var (lat, lon, direct) = group.Key;
            var seconds = direct ? DistanceKm(lat, lon, details.Latitude!.Value, details.Longitude!.Value) / 190 * 3600 :
                await _routing.SecondsAsync(lat, lon, details.Latitude!.Value, details.Longitude!.Value, token);
            // An unavailable road route stays unknown. Straight-line estimates
            // must never turn a potentially long road journey into permission.
            if (seconds is not { } duration || !double.IsFinite(duration) || duration < 0) return;
            var values = group.Select(vehicle => (vehicle.Id, Seconds: (int)Math.Ceiling(duration * .8) + details.VehicleTravelOrigins[vehicle.Id].StartDelaySeconds)).ToArray();
            lock (details.VehicleTravelSeconds)
                foreach (var (id, travel) in values) details.VehicleTravelSeconds[id] = travel;
        }));
    }

    private static double DistanceKm(double sourceLat, double sourceLon, double targetLat, double targetLon)
    {
        var radians = Math.PI / 180;
        var a = Math.Pow(Math.Sin((targetLat - sourceLat) * radians / 2), 2) +
            Math.Cos(sourceLat * radians) * Math.Cos(targetLat * radians) * Math.Pow(Math.Sin((targetLon - sourceLon) * radians / 2), 2);
        return 6371 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }

    private static Dictionary<long, VehicleTravelOrigin> SceneOrigins(IEnumerable<Donor> donors) => donors
        .Where(donor => ValidLocation(donor.Details.Latitude, donor.Details.Longitude))
        .ToDictionary(donor => donor.Vehicle.Id, donor => new VehicleTravelOrigin(donor.Details.Latitude!.Value, donor.Details.Longitude!.Value, false, 0));

    private async Task PreparePackageApproachesAsync(Mission target, EinsatzInfo? spec, MissionDetails details, IReadOnlyList<Vehicle> actual,
        IReadOnlyList<Vehicle> proposed, IReadOnlyList<VehicleLoan> package, IReadOnlyList<Mission> board,
        Dictionary<long, MissionDetails> sources, CancellationToken token)
    {
        if (ApproachLimit == 0) return;
        var origins = new Dictionary<long, VehicleTravelOrigin>(); var home = new HashSet<long>();
        foreach (var loan in package)
        {
            var vehicle = actual.FirstOrDefault(vehicle => vehicle.Id == loan.VehicleId);
            if (vehicle == null) continue;
            if (loan.RecallForStaffing) { home.Add(vehicle.Id); continue; }
            if (vehicle.TargetType != "mission" || vehicle.TargetId != loan.SourceMissionId) continue;
            if (!sources.TryGetValue(loan.SourceMissionId, out var sourceDetails))
            {
                var source = board.FirstOrDefault(mission => mission.Id == loan.SourceMissionId);
                if (source == null) continue;
                sourceDetails = await ParseDetailsWithRefreshAsync(await GetTextAsync($"/missions/{source.Id}", token), source, token);
                sources[source.Id] = sourceDetails;
            }
            if (ValidLocation(sourceDetails.Latitude, sourceDetails.Longitude))
                origins[vehicle.Id] = new(sourceDetails.Latitude!.Value, sourceDetails.Longitude!.Value, false, 0);
        }
        await PrepareApproachesAsync(target, spec, details, proposed, token, origins, home);
    }
}
