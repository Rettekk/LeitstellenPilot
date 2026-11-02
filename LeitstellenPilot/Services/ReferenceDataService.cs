using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed record ReferenceDataSnapshot(Dictionary<string, JObject> Vehicles, Dictionary<string, string> Labels,
    JArray MissionFields, RequirementAliasIndex Aliases)
{
    public DispatchRuleCatalog Rules { get; init; } = DispatchRuleCatalog.Bundled;
    public Dictionary<string, string> SourceLabels { get; init; } = Labels;
}

// Public catalog data has its own client: game cookies and credentials never reach LSSM.
public sealed class ReferenceDataService : IDisposable
{
    private readonly ConfigService _config;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private DateTime _nextRefresh;
    private DateTime _nextUnknownRefresh;
    private bool _disposed;
    private ReferenceDataSnapshot _snapshot;
    private static readonly Dictionary<string, string[]> ShortageLabels = Asset<JObject>("missing-requirement-labels.json")["aliases"]!.ToObject<Dictionary<string, string[]>>()!;
    public ReferenceDataSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event Action<string>? StatusChanged;
    public ReferenceDataService(ConfigService config, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _config = config; _time = time ?? TimeProvider.System;
        _http = new(handler ?? new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LeitstellenPilot-Catalog/1.0");
        var vehicles = Asset<Dictionary<string, JObject>>("vehicle-types.json");
        var labels = Asset<JObject>("requirement-labels.json")["requirements"]!.ToObject<Dictionary<string, string>>()!;
        var fields = (JArray)Asset<JObject>("mission-field-labels.json")["fields"]!;
        _snapshot = MakeSnapshot(vehicles, labels, fields);
        LoadCache();
    }

    private static T Asset<T>(string name)
    {
        using var input = typeof(ReferenceDataService).Assembly.GetManifestResourceStream("LeitstellenPilot.Assets." + name)!;
        using var reader = new StreamReader(input); return JsonConvert.DeserializeObject<T>(reader.ReadToEnd())!;
    }
    private ReferenceDataSnapshot MakeSnapshot(Dictionary<string, JObject> vehicles, Dictionary<string, string> labels, JArray fields, JObject? definition = null)
    {
        var rules = DispatchRuleCatalog.Create(definition ?? DispatchRuleCatalog.BundledDefinition(), vehicles);
        var display = new Dictionary<string, string>(labels);
        foreach (var pair in rules.Labels.Where(p => !p.Key.StartsWith("_vehicle_", StringComparison.Ordinal))) display[pair.Key] = pair.Value;
        return new(vehicles, display, fields, RequirementCatalog.BuildAliases(vehicles, labels, fields, ShortageLabels, rules)) { Rules = rules, SourceLabels = new(labels) };
    }

    public Task RefreshAsync(CancellationToken token = default) => RefreshCoreAsync(false, token);
    public Task RefreshForUnknownAsync(CancellationToken token = default) => RefreshCoreAsync(true, token);
    private async Task RefreshCoreAsync(bool unknown, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                var now = _time.GetUtcNow().UtcDateTime;
                if (_disposed || now < (unknown ? _nextUnknownRefresh : _nextRefresh)) return;
                _nextRefresh = now.AddHours(1);
                _nextUnknownRefresh = now.AddMinutes(5);
                var responses = await Task.WhenAll(
                    FetchAsync("https://api.lss-manager.de/de_DE/vehicles", cancellation.Token),
                    FetchAsync("https://api.lss-manager.de/de_DE/einsaetze", cancellation.Token),
                    FetchAsync("https://www.leitstellenspiel.de/api/mission_type_data", cancellation.Token)).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                var old = Snapshot;
                var vehicles = new Dictionary<string, JObject>(old.Vehicles);
                var labels = new Dictionary<string, string>(old.SourceLabels);
                var fields = old.MissionFields;
                var updated = false;
                if (ValidVehicles(responses[0]))
                { foreach (var pair in responses[0]!.Properties()) vehicles[pair.Name] = (JObject)pair.Value; updated = true; }
                var candidateRules = DispatchRuleCatalog.Create(DispatchRuleCatalog.BundledDefinition(), vehicles);
                if (responses[1]?["requirements"] is JObject translations && translations.Properties().Count() > 10 &&
                    translations.Properties().All(pair => pair.Value.Type == JTokenType.String && pair.Value.ToString().Length is > 0 and <= 512))
                { foreach (var pair in translations.Properties()) if (candidateRules.Types.ContainsKey(pair.Name) || candidateRules.Labels.ContainsKey(pair.Name)) labels[pair.Name] = pair.Value.ToString(); updated = true; }
                if (responses[2]?["available_mission_values"] is JArray available && available.Count <= 2000 &&
                    available.All(field => field is JObject value && value["key"]?.Type == JTokenType.String) &&
                    available.OfType<JObject>().Any(field => field.Value<string>("key") == "need_lf"))
                {
                    fields = new JArray(available.OfType<JObject>().Where(field => RequirementCatalog.FieldRequirementKey(field.Value<string>("key") ?? "", candidateRules) != null && field["caption"]?.Type == JTokenType.String)
                        .Select(field => new JObject { ["key"] = field["key"], ["caption"] = field["caption"] })); updated = true;
                }
                if (!updated) { StatusChanged?.Invoke("Referenzdaten nicht aktualisiert; letzter gültiger Katalog bleibt verfügbar."); return; }
                var next = MakeSnapshot(vehicles, labels, fields);
                Volatile.Write(ref _snapshot, next);
                try
                {
                    var path = _config.ReferenceDataCachePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var cache = new JObject { ["fetchedAtUtc"] = now, ["vehicles"] = JObject.FromObject(vehicles), ["requirements"] = JObject.FromObject(labels), ["fields"] = fields };
                    await File.WriteAllTextAsync(path + ".tmp", cache.ToString(Formatting.None), cancellation.Token).ConfigureAwait(false);
                    File.Move(path + ".tmp", path, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusChanged?.Invoke("Referenzdaten aktualisiert; lokaler Cache konnte nicht gespeichert werden."); }
                StatusChanged?.Invoke($"Referenzdaten geprüft: {vehicles.Count} Fahrzeugtypen, {Snapshot.Aliases.Count} Bezeichnungen. Nächste Prüfung in einer Stunde.");
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or ObjectDisposedException or FormatException or CryptographicException) { }
    }

    private async Task<JObject?> FetchAsync(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        token = timeout.Token;
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 2 * 1024 * 1024) return null;
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { if (output.Length + read > 2 * 1024 * 1024) return null; output.Write(buffer, 0, read); }
            return JObject.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException) { return null; }
    }

    private static bool TryInt(JToken? value, out int result)
    {
        result = 0; return value?.Type == JTokenType.Integer && int.TryParse(value.ToString(), out result);
    }
    private static bool OptionalInteger(JObject type, string field, int maximum) => type[field] == null || TryInt(type[field], out var value) && value >= 0 && value <= maximum;
    private static bool ValidSchooling(JToken? schooling) => schooling == null || schooling.Type == JTokenType.Null || schooling is JObject schools &&
        schools.Properties().All(school => school.Value is JObject trainings && trainings.Properties().All(training => training.Value is JObject rule &&
            (rule["all"] == null || rule["all"]?.Type == JTokenType.Boolean) && OptionalInteger(rule, "min", 100)));
    private static bool ValidStaff(JToken? value) => value == null || value.Type == JTokenType.Null || value is JObject staff &&
        ValidSchooling(staff["training"]) && OptionalInteger(staff, "trainingAtScene", 100) && OptionalInteger(staff, "min", 100) && OptionalInteger(staff, "max", 100);
    private static bool ValidVehicles(JObject? data) => data != null && data.Count is >= 100 and <= 5000 && data["0"] is JObject &&
        data.Properties().All(pair => int.TryParse(pair.Name, out var id) && id >= 0 && pair.Value is JObject type &&
            type["caption"]?.Type == JTokenType.String && type.Value<string>("caption")!.Length is > 0 and <= 128 &&
            TryInt(type["minPersonnel"], out var min) && TryInt(type["maxPersonnel"], out var max) && min >= 0 && max >= min && max <= 100 &&
            (type["waterTank"] == null || TryInt(type["waterTank"], out var water) && water is >= 0 and <= 2000000) &&
            type["possibleBuildings"] is JArray buildings && buildings.All(building => TryInt(building, out var buildingId) && buildingId >= 0) &&
            (type["tractiveVehicles"] == null || type["tractiveVehicles"] is JArray towing && towing.All(vehicle => TryInt(vehicle, out var vehicleId) && vehicleId >= 0)) &&
            (type["foamTank"] == null || TryInt(type["foamTank"], out var foam) && foam >= 0) &&
            (type["pumpCapacity"] == null || TryInt(type["pumpCapacity"], out var pump) && pump >= 0) &&
            (type["isTrailer"] == null || type["isTrailer"]?.Type == JTokenType.Boolean) &&
            OptionalInteger(type, "equipmentCapacity", 10000) && OptionalInteger(type, "waterBonus", 100) && OptionalInteger(type, "foamBonus", 100) &&
            ValidSchooling(type["schooling"]) && ValidStaff(type["staff"]));

    private void LoadCache()
    {
        try
        {
            var path = _config.ReferenceDataCachePath;
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return;
            var cache = JObject.Parse(File.ReadAllText(path));
            if (cache["fetchedAtUtc"]?.Type != JTokenType.Date) return;
            var date = cache.Value<DateTime?>("fetchedAtUtc");
            if (!date.HasValue || date > _time.GetUtcNow().UtcDateTime.AddMinutes(5) || !ValidVehicles(cache["vehicles"] as JObject) ||
                cache["requirements"] is not JObject labels || cache["fields"] is not JArray fields || fields.Count > 500 ||
                fields.Any(field => field is not JObject item || item["key"]?.Type != JTokenType.String || item["caption"]?.Type != JTokenType.String ||
                    item.Value<string>("key")!.Length > 128 || item.Value<string>("caption")!.Length > 512)) return;
            var mergedVehicles = new Dictionary<string, JObject>(Snapshot.Vehicles);
            foreach (var pair in ((JObject)cache["vehicles"]!).Properties()) mergedVehicles[pair.Name] = (JObject)pair.Value;
            var mergedLabels = new Dictionary<string, string>(Snapshot.SourceLabels);
            foreach (var pair in labels.Properties()) if (pair.Value.Type == JTokenType.String && pair.Value.ToString().Length is > 0 and <= 512 &&
                (Snapshot.Rules.Types.ContainsKey(pair.Name) || Snapshot.Rules.Labels.ContainsKey(pair.Name))) mergedLabels[pair.Name] = pair.Value.ToString();
            _snapshot = MakeSnapshot(mergedVehicles, mergedLabels, fields); _nextRefresh = date.Value.AddHours(1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException) { }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _http.Dispose();  }
}
