using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using LeitstellenPilot.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService : IGameApi, IFleetApi, ITransportApi, IDailyBonusApi, ITaskRewardsApi, IPromotionApi, IReassignmentApi, IPlannedMissionApi, IMissionGenerationApi, IPersonnelApi, IStationUpgradeApi, IEventItemApi, IDisposable
{
    private sealed class AutomaticActionPausedException() : InvalidOperationException("Automatik pausiert; Aktion wurde nicht gesendet.");
    private sealed class AutomationUnavailableException() : InvalidOperationException("Spielaktionen sind derzeit pausiert. Keine Spielaktion gesendet.");
    private readonly HttpClient _httpClient;
    private readonly CookieContainer _cookies = new();
    private readonly ConfigService _config;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly SemaphoreSlim _inventoryGate = new(1, 1);
    private readonly SemaphoreSlim _buildingGate = new(1, 1);
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly SemaphoreSlim _bonusPageGate = new(1, 1);
    private readonly TimeSpan _requestInterval;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _requestDelay;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;
    private DateTime _serverRetryAfter = DateTime.MinValue;
    private DateTime _lastRequest = DateTime.MinValue;
    private DateTime _inventoryTime = DateTime.MinValue;
    private long _inventoryVersion;
    private DateTime _catalogTime = DateTime.MinValue;
    private DateTime _nextCatalogRefresh = DateTime.MinValue;
    private DateTime _nextMissingTypeRefresh = DateTime.MinValue;
    private List<Vehicle> _inventory = new();
    private Dictionary<string, EinsatzInfo> _catalog = new();
    private readonly Dictionary<long, DateTime> _pendingVehicles = new();
    private readonly Dictionary<long, (DateTime Expires, long[] VehicleIds)> _pendingMissions = new();
    private readonly Dictionary<long, DateTime> _pendingTransports = new();
    private volatile HashSet<long> _prisonerTransportMissions = new();
    private readonly Dictionary<long, (DateTime Until, string SettingsKey)> _transportChecks = new();
    private JArray? _buildings;
    private DateTime _buildingTime = DateTime.MinValue;
    private DateTime _pendingBonusUntil = DateTime.MinValue;
    private DailyBonusPage? _bonusPage;
    private DateTime _bonusPageDate = DateTime.MinValue;
    private int _bonusPageReads;
    private readonly ReferenceDataService _references;
    private readonly bool _refreshReferences;
    private Task _referenceRefresh = Task.CompletedTask;
    private Dictionary<string, JObject> _vehicleTypes => _references.Snapshot.Vehicles;
    private Dictionary<string, string> _labels => _references.Snapshot.Labels;
    private RequirementAliasIndex _aliases => _references.Snapshot.Aliases;
    private DispatchRuleCatalog _rules => _references.Snapshot.Rules;
    private VehiclePlanner CreateVehiclePlanner()
    {
        var data = _references.Snapshot; return new(data.Vehicles, data.Labels, data.Rules, _config.Config.BotSettings.MaxApproachMinutes);
    }
    private readonly ActionPacer _pacer;
    private readonly RoutingService _routing;
    public event Action<int>? ActionWaiting;
    public string ActionWaitReason => _pacer.WaitingReason;
    public Func<bool>? AutomaticActionsAllowed { get; set; }
    public Func<bool>? ActionsAllowed { get; set; }
    public Func<Mission, bool>? PartialTopupsAllowed { get; set; }
    private ProgramLog? _logger;
    public ProgramLog? Logger
    {
        get => _logger;
        set
        {
            _logger = value;
            _logger?.Write(LogLevel.Info, "Catalog", "loaded", "Einsatztypen-Katalog bereit.");
        }
    }
    public event Action<IReadOnlyList<Vehicle>>? FleetChanged;
    // The server decides whether a stored cookie has expired; a local age limit must
    // not stop a healthy long-running session or prevent checking it after restart.
    public bool IsLoggedIn => _config.Session.HasStoredSession;
    public string CurrentUser => _config.Session.UserName;

    public ApiService(ConfigService config, HttpMessageHandler? handler = null, TimeSpan? requestInterval = null,
        ActionPacer? pacer = null, TimeProvider? timeProvider = null, Func<TimeSpan, CancellationToken, Task>? requestDelay = null, bool useBundledCatalog = true,
        ReferenceDataService? references = null, RoutingService? routing = null)
    {
        _config = config;
        _requestInterval = requestInterval ?? TimeSpan.FromMilliseconds(750);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _requestDelay = requestDelay ?? Task.Delay;
        _pacer = pacer ?? new ActionPacer(config);
        _pacer.Waiting += seconds => ActionWaiting?.Invoke(seconds);
        _httpClient = new(handler ?? new HttpClientHandler
        {
            CookieContainer = _cookies, UseCookies = true, UseProxy = false, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        { BaseAddress = new Uri(Constants.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LeitstellenPilot/1.0");
        _references = references ?? new ReferenceDataService(config, time: _timeProvider);
        _routing = routing ?? new RoutingService(enabled: handler == null);
        _refreshReferences = handler == null || references != null;
        _references.StatusChanged += ReferenceStatusChanged;
        LoadCatalogCache(useBundledCatalog);
        RestoreCookies();
    }

    private static T LoadAsset<T>(string name)
    {
        using var stream = typeof(ApiService).Assembly.GetManifestResourceStream("LeitstellenPilot.Assets." + name)
            ?? throw new InvalidOperationException("Fehlender Fahrzeugkatalog: " + name);
        using var reader = new StreamReader(stream);
        return JsonConvert.DeserializeObject<T>(reader.ReadToEnd()) ?? throw new FormatException("Ungültiger Fahrzeugkatalog.");
    }

    private void RestoreCookies()
    {
        if (!_config.Session.HasStoredSession) return;
        var header = _config.Session.Cookies;
        if (string.IsNullOrEmpty(header)) header = "_session_id=" + _config.Session.SessionId;
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair.Length == 2)
            {
                try { _cookies.Add(_httpClient.BaseAddress!, new Cookie(pair[0], pair[1], "/")); }
                catch (CookieException) { /* Ignore malformed legacy cookie entries. */ }
            }
        }
    }

    public async Task<bool> LoginAsync(string identifier, string password, CancellationToken token = default)
    {
        if (ActionsAllowed?.Invoke() == false) throw new AutomationUnavailableException();
        Logger?.RegisterSecret(identifier);
        Logger?.RegisterSecret(password);
        Logger?.Write(LogLevel.Info, "Account", "login.started", "Anmeldung gestartet.");
        Logout();
        var page = new HtmlDocument();
        page.LoadHtml(await GetTextAsync("/users/sign_in", token));
        var form = page.DocumentNode.SelectSingleNode("//form[contains(@action,'sign_in')]");
        var csrf = form?.SelectSingleNode(".//input[@name='authenticity_token']")?.GetAttributeValue("value", "");
        if (string.IsNullOrEmpty(csrf)) throw new InvalidOperationException("Anmeldeformular nicht erkannt.");
        Logger?.RegisterSecret(HtmlEntity.DeEntitize(csrf));
        var identifierField = form?.SelectSingleNode(".//input[@name='user[login]' or @name='user[email]']")
            ?.GetAttributeValue("name", "") ?? (identifier.Contains('@') ? "user[email]" : "user[login]");
        var fields = new List<KeyValuePair<string, string>>
        {
            new("authenticity_token", HtmlEntity.DeEntitize(csrf) ?? ""), new(identifierField, identifier),
            new("user[password]", password), new("user[remember_me]", "1"), new("utf8", "✓")
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users/sign_in") { Content = new FormUrlEncodedContent(fields) };
        using var response = await SendAsync(request, token);
        if ((int)response.StatusCode is not (200 or 302 or 303)) return false;
        // A session cookie can also exist after a failed login. Verify the account API.
        try
        {
            var user = JsonConvert.DeserializeObject<UserInfo>(await GetTextAsync("/api/userinfo", token));
            if (user == null || user.user_id <= 0 || string.IsNullOrWhiteSpace(user.UserName)) return false;
            var sessionCookie = _cookies.GetCookies(_httpClient.BaseAddress!).Cast<Cookie>()
                .FirstOrDefault(c => c.Name.Contains("session", StringComparison.OrdinalIgnoreCase));
            if (sessionCookie == null) return false;
            await _config.SaveSessionAsync(new UserSession
            {
                UserName = user.UserName, UserId = user.user_id.ToString(), DisplayName = user.UserName,
                SessionId = sessionCookie.Value, Cookies = _cookies.GetCookieHeader(_httpClient.BaseAddress!),
                LastLogin = DateTime.UtcNow, LastActivity = DateTime.UtcNow, SessionDurationHours = 24
            });
            Logger?.Write(LogLevel.Info, "Account", "login.confirmed", "Anmeldung vom Server bestätigt.");
            return true;
        }
        catch (UnauthorizedAccessException) { Logout(); return false; }
        catch (JsonException) { Logout(); return false; }
    }

    public void Logout()
    {
        _generationWatchdog.Reset(clearCooldown: true);
        Logger?.RegisterSecret(_config.Session.SessionId);
        Logger?.RegisterSecret(_config.Session.AuthToken);
        foreach (var part in (_config.Session.Cookies ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair.Length == 2) Logger?.RegisterSecret(pair[1]);
        }
        foreach (Cookie cookie in _cookies.GetAllCookies()) cookie.Expired = true;
        _config.ClearSession();
        _inventory = new();
        _inventoryTime = DateTime.MinValue;
        _buildings = null;
        _pendingMissions.Clear();
        _eventItemOwnMissions = new(); _eventItemOffers.Clear(); _eventItemSession = ""; _lastEventItemInspection = 0;
        _pendingVehicles.Clear();
        _trainedTrips.Clear();
        _pendingTransports.Clear();
        _transportChecks.Clear();
        _patientWaits.Clear();
        _hospitalDetails.Clear();
        _prisonerTransportMissions = new();
        _pendingBonusUntil = DateTime.MinValue;
        _pendingTaskRewardsUntil = DateTime.MinValue;
        _pendingPromotionUntil = DateTime.MinValue;
        _nextReassignmentTry = DateTime.MinValue;
        _nextUnsolvableReview = DateTime.MinValue; _stockSignature = ""; _pendingReleases.Clear();
        _plannedAlarms.Clear();
        _inspectionPages.Clear();
        _observedCrew.Clear();
        ClearStaffing();
        _bonusPage = null; _bonusPageDate = DateTime.MinValue; _bonusPageReads = 0;
    }

    public async Task<UserInfo?> GetUserInfoAsync(CancellationToken token = default)
    {
        var user = JsonConvert.DeserializeObject<UserInfo>(await GetTextAsync("/api/userinfo", token));
        if (user == null || user.user_id <= 0)
        { Logout(); throw new UnauthorizedAccessException("Keine gültige Anmeldung."); }
        return user;
    }

    public async Task<List<Mission>> GetMissionsAsync(CancellationToken token = default)
    {
        EnsureLoggedIn();
        List<Mission> missions;
        try { missions = GamePageParser.ParseMissions(await GetTextAsync("/map/mission_markers_own.js.erb", token)); }
        catch { ResetMissionGenerationWatchdog(); throw; }
        var receivedAt = UtcNow;
        foreach (var mission in missions) mission.SnapshotReceivedAt = receivedAt;
        // Mission markers are sufficient to load the board. Optional catalogue
        // enrichment must not hold up missions, fleet, buildings and bonus status.
        var catalog = _catalog;
        foreach (var mission in missions)
            if (mission.TypeKey != null && catalog.TryGetValue(mission.TypeKey, out var specification))
                mission.average_credits = specification.AverageCredits;
        var result = missions.Where(m => m.Id > 0).DistinctBy(m => m.Id).ToList();
        _eventItemOwnMissions = result.Select(m => m.Id).ToHashSet();
        _eventItemSession = _config.Session.SessionId;
        foreach (var id in _eventItemOffers.Keys.Where(id => !_eventItemOwnMissions.Contains(id))) _eventItemOffers.TryRemove(id, out _);
        if (_config.Config.BotSettings.ReleaseUnsolvableMissions)
            foreach (var mission in result)
                mission.FleetBlockReason = _config.Config.FleetBlockedMissions.FirstOrDefault(block => block.Account == LoanAccount && block.MissionId == mission.Id)?.Reason ?? "";
        _prisonerTransportMissions = result.Where(m => m.HasPrisonerTransportRequest).Select(m => m.Id).ToHashSet();
        Logger?.Write(LogLevel.Debug, "API", "missions.snapshot", "Einsatzliste gelesen.");
        if (GenerationAllowed)
        {
            var status = _generationWatchdog.Observe(result.Select(mission => mission.Id), UtcNow);
            if (status != null) GenerationStatus(status, "observed");
        }
        else ResetMissionGenerationWatchdog();
        return result;
    }

    public async Task<List<Vehicle>> GetVehiclesAsync(bool refresh = false, CancellationToken token = default)
    {
        EnsureLoggedIn();
        var observedVersion = Interlocked.Read(ref _inventoryVersion);
        await _inventoryGate.WaitAsync(token);
        try
        {
            // Concurrent callers requesting a fresh snapshot may share the fetch that
            // finished while they were queued. A later sequential refresh still reads anew.
            if ((!refresh || observedVersion != _inventoryVersion) &&
                DateTime.UtcNow - _inventoryTime < TimeSpan.FromSeconds(5)) return _inventory.ToList();
            var result = new List<Vehicle>();
            var seen = new HashSet<string>();
            int? declaredTotal = null;
            string? next = "/api/v2/vehicles?limit=1000";
            while (next != null)
            {
                if (!seen.Add(next) || seen.Count > 500) throw new FormatException("Ungültiges Fahrzeug-Paging.");
                var page = JObject.Parse(await GetTextAsync(next, token));
                if (page["result"] is not JArray vehicles) throw new FormatException("Fahrzeug-API liefert keine Fahrzeugliste.");
                result.AddRange(vehicles.ToObject<List<Vehicle>>() ?? new());
                if (page["paging"]?["count_total"] is { } total)
                {
                    if (total.Type != JTokenType.Integer || !int.TryParse(total.ToString(), out var count) || count < 0)
                        throw new FormatException("Fahrzeug-API liefert keine gültige Gesamtzahl.");
                    declaredTotal = count;
                }
                var nextPage = page["paging"]?["next_page"]?.Value<string>();
                if (string.IsNullOrEmpty(nextPage)) next = null;
                else
                {
                    var uri = new Uri(_httpClient.BaseAddress!, nextPage);
                    if (!SameOrigin(uri) || !uri.AbsolutePath.StartsWith("/api/v2/vehicles", StringComparison.Ordinal))
                        throw new FormatException("Ungültiger Link in der Fahrzeug-API.");
                    next = uri.PathAndQuery;
                }
            }
            var completeInventory = result.DistinctBy(v => v.Id).ToList();
            if (declaredTotal.HasValue && completeInventory.Count != declaredTotal.Value)
                throw new FormatException($"Unvollständige Fahrzeugliste ({completeInventory.Count} von {declaredTotal.Value}). Bestandsprüfung wird wiederholt.");
            _inventory = completeInventory;
            ForgetEndedTrainedTrips(completeInventory);
            _inventoryTime = DateTime.UtcNow;
            Interlocked.Increment(ref _inventoryVersion);
            FleetChanged?.Invoke(_inventory.ToArray());
            Logger?.Write(LogLevel.Debug, "API", "fleet.snapshot", "Fahrzeugbestand gelesen.");
            return _inventory.ToList();
        }
        finally { _inventoryGate.Release(); }
    }

    public async Task<(int Total, int Active)> GetBuildingCountsAsync(CancellationToken token = default)
    {
        var buildings = await GetBuildingsAsync(token);
        return (buildings.Count, buildings.Count(b => b.Value<bool?>("enabled") != false));
    }

    private async Task<JArray> GetBuildingsAsync(CancellationToken token, bool refresh = false)
    {
        EnsureLoggedIn();
        await _buildingGate.WaitAsync(token);
        try
        {
            if (!refresh && _buildings != null && DateTime.UtcNow - _buildingTime < TimeSpan.FromMinutes(5)) return _buildings;
            var buildings = JArray.Parse(await GetTextAsync("/api/buildings", token));
            _buildings = buildings;
            _buildingTime = DateTime.UtcNow;
            return buildings;
        }
        finally { _buildingGate.Release(); }
    }

    public async Task<Vehicle> GetVehicleAsync(long id, CancellationToken token = default)
    {
        EnsureLoggedIn();
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        var page = JObject.Parse(await GetTextAsync($"/api/v2/vehicles/{id}", token));
        var vehicle = page["result"] is JObject result ? result.ToObject<Vehicle>() : null;
        if (vehicle == null || vehicle.Id != id) throw new FormatException("Fahrzeug-API liefert kein passendes Fahrzeug.");
        // A transport changes availability. Discard the full-fleet snapshot rather
        // than mixing this newer individual response into a snapshot being read elsewhere.
        await _inventoryGate.WaitAsync(token);
        try { _inventoryTime = DateTime.MinValue; }
        finally { _inventoryGate.Release(); }
        return vehicle;
    }

    public async Task<IReadOnlyList<string>> InspectBonusLinksAsync(CancellationToken token = default)
    {
        var page = new HtmlDocument(); page.LoadHtml(await GetTextAsync("/daily_bonuses", token));
        return page.DocumentNode.SelectNodes("//a[contains(@href,'bonus') or contains(@href,'reward') or contains(@href,'daily')] | //form | //input[@type='submit'] | //button[@type='submit']")
            ?.Select(node =>
            {
                if (node.Name is "input" or "button") return "Button: " + node.GetAttributeValue("value", node.InnerText.Trim()) + " [" + node.GetAttributeValue("class", "") + "] disabled=" + (node.Attributes["disabled"] != null);
                var href = node.GetAttributeValue("href", node.GetAttributeValue("action", ""));
                var uri = new Uri(_httpClient.BaseAddress!, href);
                var parsed = node.Name == "form" ? DailyBonusPlanner.Read(node.OuterHtml, _httpClient.BaseAddress!) : null;
                var day = parsed?.Day ?? parsed?.DisabledDays?.SingleOrDefault();
                return uri.AbsolutePath + (day.GetValueOrDefault() > 0 ? $" (Tag {day})" : "") + " [" + node.GetAttributeValue("data-method", node.GetAttributeValue("method", "get")) + "] " +
                    string.Join(",", node.SelectNodes(".//input[@name]")?.Select(input => input.GetAttributeValue("name", "")) ?? Enumerable.Empty<string>());
            }).Distinct().ToArray() ?? Array.Empty<string>();
    }

    public async Task<IReadOnlyList<TransportResult>> HandleTransportRequestsAsync(CancellationToken token = default, long? missionId = null)
    {
        var results = await HandleTransportsCoreAsync(token, missionId);
        foreach (var result in results)
            Logger?.Write(result.Success || result.Waiting ? LogLevel.Info : LogLevel.Warning, "Transport", "result", result.Message);
        return results;
    }

    private async Task<IReadOnlyList<TransportResult>> HandleTransportsCoreAsync(CancellationToken token, long? requestedMissionId)
    {
        await _dispatchGate.WaitAsync(token);
        var results = new List<TransportResult>();
        try
        {
            EnsureLoggedIn();
            if (!_config.Config.BotSettings.AutoTransport || AutomaticActionsAllowed?.Invoke() == false || requestedMissionId <= 0) return results;
            var transportMissions = _prisonerTransportMissions;
            var fleet = await GetVehiclesAsync(token: token);
            foreach (var id in _patientWaits.Keys.Where(id => !fleet.Any(v => v.Id == id && v.State == 5)).ToArray()) _patientWaits.Remove(id);
            var requests = fleet.Where(v => (v.State == 5 ||
                v.State == 4 && v.TargetType == "mission" && transportMissions.Contains(v.TargetId.GetValueOrDefault())) &&
                (!requestedMissionId.HasValue || v.TargetType == "mission" && v.TargetId == requestedMissionId))
                .OrderBy(v => _patientWaits.TryGetValue(v.Id, out var wait) && wait.Slot != null ? wait.Slot.At : DateTime.MaxValue)
                .ThenBy(v => v.Id).ToArray();
            if (requests.Length == 0) return results;
            var ownBuildings = (await GetBuildingsAsync(token, refresh: true)).Select(b => b.Value<long>("id")).ToHashSet();
            foreach (var vehicle in requests)
            {
                token.ThrowIfCancellationRequested();
                if (AutomaticActionsAllowed?.Invoke() == false) break;
                if (_pendingTransports.GetValueOrDefault(vehicle.Id) > UtcNow) continue;
                var releaseEnabled = _config.Config.BotSettings.ReleasePrisonersWithoutCell;
                if (_transportChecks.TryGetValue(vehicle.Id, out var check) && check.Until > UtcNow && check.SettingsKey == TransportSettingsKey) continue;
                var missionId = vehicle.TargetType == "mission" ? vehicle.TargetId : null;
                var prisonersOnly = vehicle.State == 4;
                var actionSent = false;
                try
                {
                    var page = await GetTextAsync($"/vehicles/{vehicle.Id}", token);
                    var selection = await SelectTransportAsync(page, vehicle, ownBuildings, missionId, prisonersOnly, token);
                    var action = selection.Action;
                    if (action == null)
                    {
                        _transportChecks[vehicle.Id] = (UtcNow.AddSeconds(30), TransportSettingsKey);
                        results.Add(new(false, $"Fahrzeug {vehicle.Id}: " + (selection.WaitReason ?? "Kein freies eigenes Transportziel erkannt. Freie Zelle / Krankenhaus im Spiel prüfen." +
                            (prisonersOnly || page.Contains("/gefangene/entlassen", StringComparison.Ordinal)
                                ? !releaseEnabled ? " Gefangenenentlassung ist ausgeschaltet." : " Kein passender Entlassungslink angeboten."
                                : ""))) { Waiting = selection.WaitReason != null });
                        continue;
                    }
                    Logger?.RegisterSecret(action.Token);
                    Logger?.Write(LogLevel.Debug, "Transport", "destination.selected", "Transportziel ausgewählt.");
                    await _pacer.WaitAsync(token);
                    if (!_config.Config.BotSettings.AutoTransport || AutomaticActionsAllowed?.Invoke() == false) break;
                    var current = await GetVehicleAsync(vehicle.Id, token);
                    if (current.State != 5 && !(prisonersOnly && current.State == 4)) continue;
                    if (current.TargetType != vehicle.TargetType || current.TargetId != vehicle.TargetId) continue;
                    page = await GetTextAsync($"/vehicles/{vehicle.Id}", token);
                    selection = await SelectTransportAsync(page, current, ownBuildings, missionId, prisonersOnly, token, refreshHospitals: action.IsPatientRelease);
                    action = selection.Action;
                    if (action == null)
                    {
                        _transportChecks[vehicle.Id] = (UtcNow.AddSeconds(30), TransportSettingsKey);
                        results.Add(new(false, $"Fahrzeug {vehicle.Id}: " + (selection.WaitReason ?? "Transportziel inzwischen nicht mehr verfügbar.")) { Waiting = selection.WaitReason != null }); continue;
                    }
                    if (!_config.Config.BotSettings.AutoTransport || AutomaticActionsAllowed?.Invoke() == false) break;
                    _pendingTransports[vehicle.Id] = UtcNow.AddMinutes(2);
                    using var request = new HttpRequestMessage(action.Post ? HttpMethod.Post : HttpMethod.Get, action.Uri);
                    request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, $"/vehicles/{vehicle.Id}");
                    if (action.Post) request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("authenticity_token", action.Token) });
                    actionSent = true;
                    var selectedWait = HospitalPlanner.NormalizeWait(_config.Config.BotSettings.HospitalWaitMinutes);
                    using var response = await SendAsync(request, token, automaticAction: true, actionAllowed: () =>
                        _config.Config.BotSettings.AutoTransport && (!action.IsPrisonerRelease || _config.Config.BotSettings.ReleasePrisonersWithoutCell) &&
                        (!action.IsPatientRelease || _config.Config.BotSettings.ReleasePatientsWithoutBed && HospitalPlanner.NormalizeWait(_config.Config.BotSettings.HospitalWaitMinutes) == selectedWait));
                    CheckAuthentication(response);
                    if ((int)response.StatusCode is not (200 or 302 or 303))
                    { results.Add(new(false, $"Fahrzeug {vehicle.Id}: Transport abgelehnt (HTTP {(int)response.StatusCode}).") { Sent = true }); continue; }
                    var updated = await GetVehicleAsync(vehicle.Id, token);
                    var confirmed = action.IsPrisonerRelease || action.IsPatientRelease
                        ? updated.State is 1 or 2 && (updated.TargetType != "mission" || updated.TargetId != missionId)
                        : updated.TargetType == "building" && updated.TargetId == action.BuildingId;
                    if (action.IsPrisonerRelease && !confirmed)
                    {
                        var after = await GetTextAsync($"/vehicles/{vehicle.Id}", token);
                        confirmed = TransportPlanner.Choose(after, vehicle.Id, _httpClient.BaseAddress!, new HashSet<long>(),
                            allowPrisonerRelease: true, missionId: missionId, prisonersOnly: true)?.IsPrisonerRelease != true;
                    }
                    if (action.IsPatientRelease && !confirmed && updated.State == 4 && updated.TargetType == current.TargetType && updated.TargetId == current.TargetId)
                        confirmed = PatientRequestCleared(await GetTextAsync($"/vehicles/{vehicle.Id}", token));
                    if (action.Kind == "patient" || action.IsPatientRelease)
                    {
                        _patientWaits.Remove(vehicle.Id);
                        _transportChecks.Remove(vehicle.Id);
                        if (action.BuildingId > 0) _hospitalDetails.Remove(action.BuildingId);
                    }
                    results.Add(new(confirmed, $"Fahrzeug {vehicle.Id}: " + (confirmed ? action.IsPrisonerRelease
                        ? "Gefangene mangels freier eigener Zelle entlassen (keine Transportvergütung)."
                        : action.IsPatientRelease ? "Patient ohne Transport entlassen; innerhalb der eingestellten Wartefrist kein freies Krankenhausbett erwartet (keine Vergütung)."
                        : (action.Kind == "patient" ? "Patiententransport" : "Gefangenentransport") + $" zu Gebäude {action.BuildingId} bestätigt."
                        : "Aktion gesendet, Bestätigung noch ausstehend.")) { Sent = true });
                }
                catch (AutomaticActionPausedException)
                {
                    _pendingTransports.Remove(vehicle.Id);
                    results.Add(new(false, $"Fahrzeug {vehicle.Id}: Automatik pausiert; Transport wurde nicht gesendet."));
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (UnauthorizedAccessException) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or InvalidOperationException)
                { results.Add(new(false, $"Fahrzeug {vehicle.Id}: {ex.Message}") { Sent = actionSent }); }
            }
            return results;
        }
        finally { _dispatchGate.Release(); }
    }

    public async Task<DailyBonusResult> CollectDailyBonusAsync(CancellationToken token = default)
    {
        var result = await CollectDailyBonusCoreAsync(token);
        Logger?.Write(result.State is DailyBonusState.Unknown or DailyBonusState.Pending ? LogLevel.Warning : LogLevel.Info,
            "Bonus", "result", result.Message);
        return result;
    }

    public async Task<DailyBonusResult> GetDailyBonusStatusAsync(CancellationToken token = default, bool refresh = false)
    {
        EnsureLoggedIn();
        var account = string.IsNullOrEmpty(_config.Session.UserId) ? _config.Session.UserName : _config.Session.UserId;
        var result = _config.Config.DailyBonusClaimDays.GetValueOrDefault(account) == _timeProvider.GetLocalNow().ToString("yyyy-MM-dd")
            ? new(false, "Tagesbonus für dieses Konto heute bereits abgeholt.", DailyBonusState.AlreadyClaimed)
            : DescribeBonus(await ReadBonusPageAsync(token, refresh: refresh));
        Logger?.Write(result.State == DailyBonusState.Unknown ? LogLevel.Warning : LogLevel.Info, "Bonus", "status", result.Message);
        return result;
    }

    private async Task<DailyBonusPage> ReadBonusPageAsync(CancellationToken token, bool refresh = false, bool actionVerification = false)
    {
        await _bonusPageGate.WaitAsync(token);
        try
        {
            var today = _timeProvider.GetLocalNow().Date;
            if (_bonusPageDate != today) { _bonusPageDate = today; _bonusPage = null; _bonusPageReads = 0; }
            if (!refresh && !actionVerification)
            {
                if (_bonusPage is { Recognized: true }) return _bonusPage;
                if (_bonusPageReads >= 2) return _bonusPage ?? new(false, false, new());
            }
            if (!actionVerification) _bonusPageReads++;
            var page = DailyBonusPlanner.Read(await GetTextAsync("/daily_bonuses", token), _httpClient.BaseAddress!);
            _bonusPage = page;
            foreach (var field in page.Fields.Where(f => f.Key == "authenticity_token")) Logger?.RegisterSecret(field.Value);
            Logger?.Write(LogLevel.Debug, "Bonus", "availability", "Alle Tagesformulare geprüft.");
            return page;
        }
        finally { _bonusPageGate.Release(); }
    }

    private static DailyBonusResult DescribeBonus(DailyBonusPage page)
    {
        if (!page.Recognized) return new(false, "Bonusformular nicht eindeutig erkannt. Bitte im Spiel prüfen.", DailyBonusState.Unknown);
        if (page.Available) return new(false, (page.Day.HasValue ? $"Tag {page.Day}: " : "") + "Tagesbonus verfügbar.", DailyBonusState.Available, page.Day);
        return new(false, page.ClaimedThroughDay.HasValue
            ? $"Tagesbonus derzeit nicht verfügbar; zuletzt abgeholt: Tag {page.ClaimedThroughDay}."
            : "Tagesbonus derzeit nicht verfügbar / bereits abgeholt.", DailyBonusState.NotAvailable, page.ClaimedThroughDay);
    }

    private async Task<DailyBonusResult> CollectDailyBonusCoreAsync(CancellationToken token)
    {
        await _dispatchGate.WaitAsync(token);
        try
        {
            EnsureLoggedIn();
            var account = string.IsNullOrEmpty(_config.Session.UserId) ? _config.Session.UserName : _config.Session.UserId;
            if (!_config.Config.BotSettings.AutoClaimDailyBonus || AutomaticActionsAllowed?.Invoke() == false)
                return new(false, "Automatische Bonusabholung pausiert / ausgeschaltet.", DailyBonusState.Paused);
            var today = _timeProvider.GetLocalNow().ToString("yyyy-MM-dd");
            if (_config.Config.DailyBonusClaimDays.GetValueOrDefault(account) == today)
                return new(false, "Tagesbonus für dieses Konto heute bereits abgeholt.", DailyBonusState.AlreadyClaimed);
            if (_pendingBonusUntil > UtcNow) return new(false, "Letzte Bonusabholung wartet noch auf Bestätigung.", DailyBonusState.Pending);
            var bonus = await ReadBonusPageAsync(token);
            if (!bonus.Recognized || !bonus.Available) return DescribeBonus(bonus);
            var selectedDay = bonus.Day;
            var selectedPath = bonus.ActionPath;
            await _pacer.WaitAsync(token);
            if (!_config.Config.BotSettings.AutoClaimDailyBonus || AutomaticActionsAllowed?.Invoke() == false)
                return new(false, "Bonusabholung pausiert / ausgeschaltet.", DailyBonusState.Paused);
            bonus = await ReadBonusPageAsync(token, actionVerification: true);
            if (!bonus.Recognized || !bonus.Available) return DescribeBonus(bonus);
            if (bonus.Day != selectedDay || bonus.ActionPath != selectedPath)
                return new(false, "Bonustag während der Aktionspause geändert; wird erneut geprüft.", DailyBonusState.Unknown, bonus.Day);
            if (!_config.Config.BotSettings.AutoClaimDailyBonus || AutomaticActionsAllowed?.Invoke() == false)
                return new(false, "Bonusabholung pausiert / ausgeschaltet.", DailyBonusState.Paused);
            _pendingBonusUntil = UtcNow.AddHours(1);
            using var request = new HttpRequestMessage(HttpMethod.Post, bonus.ActionPath!)
            { Content = new FormUrlEncodedContent(bonus.Fields) };
            request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, "/daily_bonuses");
            using var response = await SendAsync(request, token, automaticAction: true, actionAllowed: () => _config.Config.BotSettings.AutoClaimDailyBonus);
            CheckAuthentication(response);
            if ((int)response.StatusCode is not (200 or 302 or 303))
                return new(false, $"Bonusabholung nicht bestätigt (HTTP {(int)response.StatusCode}).", DailyBonusState.Pending, bonus.Day);
            var after = await ReadBonusPageAsync(token, actionVerification: true);
            var confirmed = after.Recognized && (bonus.Day.HasValue
                ? after.DisabledDays?.Contains(bonus.Day.Value) == true && after.Day != bonus.Day
                : !after.Available);
            if (!confirmed) return new(false, "Bonusabholung gesendet, Bestätigung noch ausstehend.", DailyBonusState.Pending, bonus.Day);
            _pendingBonusUntil = DateTime.MinValue;
            _config.Config.DailyBonusClaimDays[account] = _timeProvider.GetLocalNow().ToString("yyyy-MM-dd");
            await _config.SaveConfigAsync(_config.Config);
            return new(true, (bonus.Day.HasValue ? $"Tag {bonus.Day}: " : "") + "Tagesbonus abgeholt. Credits / Coins werden mit dem Kontostand aktualisiert.", DailyBonusState.Claimed, bonus.Day);
        }
        catch (AutomaticActionPausedException)
        {
            _pendingBonusUntil = DateTime.MinValue;
            return new(false, "Bonusabholung pausiert; keine Abholung gesendet.", DailyBonusState.Paused);
        }
        finally { _dispatchGate.Release(); }
    }

    private async Task<Dictionary<string, EinsatzInfo>> GetCatalogAsync(CancellationToken token, string? requiredTypeKey = null)
    {
        await _catalogGate.WaitAsync(token);
        try
        {
            if (_refreshReferences && _referenceRefresh.IsCompleted) _referenceRefresh = _references.RefreshAsync();
            var missingType = !string.IsNullOrEmpty(requiredTypeKey) && !_catalog.ContainsKey(requiredTypeKey);
            var refreshForMissingType = missingType && UtcNow >= _nextMissingTypeRefresh;
            if (_catalog.Count > 0 && (UtcNow - _catalogTime < TimeSpan.FromHours(1) || UtcNow < _nextCatalogRefresh) && !refreshForMissingType)
                return _catalog;
            // Seasonal types can appear while the ordinary hourly cache is still fresh.
            // Share a retry limit across all unknown types, including failed downloads.
            _nextMissingTypeRefresh = UtcNow.AddMinutes(5);
            try
            {
                var entries = JsonConvert.DeserializeObject<List<EinsatzInfo>>(await GetTextAsync("/einsaetze.json", token))
                    ?? throw new FormatException("Einsatztypen konnten nicht gelesen werden.");
                _catalog = ValidateCatalog(entries);
                _catalogTime = UtcNow;
                _nextCatalogRefresh = UtcNow.AddHours(1);
               
                Logger?.Write(LogLevel.Info, "Catalog", "updated", "Einsatztypen-Katalog aktualisiert.");
                try
                {
                    var path = _config.MissionCatalogCachePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temporary = path + ".tmp";
                    await File.WriteAllTextAsync(temporary, JsonConvert.SerializeObject(new CatalogSnapshot { FetchedAtUtc = _catalogTime, Entries = entries }), token);
                    File.Move(temporary, path, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Logger?.Write(LogLevel.Warning, "Catalog", "save.failed", "Katalog ist verfügbar, konnte aber nicht lokal gespeichert werden."); }
            }
            catch (Exception ex) when (_catalog.Count > 0 && ex is (HttpRequestException or JsonException or FormatException or IOException))
            {
                _nextCatalogRefresh = UtcNow.AddHours(1);
                Logger?.Write(LogLevel.Warning, "Catalog", "fallback", "Einsatzkatalog-Abruf fehlgeschlagen; letzter gültiger Datenstand wird verwendet.");
            }
            return _catalog;
        }
        finally { _catalogGate.Release(); }
    }

    private sealed class CatalogSnapshot
    {
        [JsonProperty("fetchedAtUtc")] public DateTime FetchedAtUtc { get; set; }
        [JsonProperty("entries")] public List<EinsatzInfo> Entries { get; set; } = new();
    }

    private static Dictionary<string, EinsatzInfo> ValidateCatalog(List<EinsatzInfo>? entries)
    {
        if (entries == null || entries.Count == 0 || entries.Any(e => e == null || string.IsNullOrWhiteSpace(e.Id) || e.Requirements == null || e.Additional == null || e.Chances == null))
            throw new FormatException("Einsatzkatalog enthält keine gültigen Einsatztypen.");
        return entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Last());
    }

    private void LoadCatalogCache(bool useBundledCatalog)
    {
        if (useBundledCatalog)
        {
            var bundled = LoadAsset<CatalogSnapshot>("mission-types.json");
            _catalog = ValidateCatalog(bundled.Entries); _catalogTime = bundled.FetchedAtUtc;
        }
        try
        {
            if (!File.Exists(_config.MissionCatalogCachePath)) return;
            var saved = JsonConvert.DeserializeObject<CatalogSnapshot>(File.ReadAllText(_config.MissionCatalogCachePath));
            if (saved == null || saved.FetchedAtUtc < _catalogTime || saved.FetchedAtUtc > UtcNow.AddMinutes(5)) return;
            _catalog = ValidateCatalog(saved.Entries); _catalogTime = saved.FetchedAtUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        { System.Diagnostics.Trace.WriteLine("Katalog-Cache: " + ex.GetType().Name); }
    }

    public async Task<DispatchResult> DispatchAsync(Mission mission, bool preview = false, CancellationToken token = default, bool automatic = false)
    {
        using var operation = Logger?.BeginMission(mission.Id);
        Logger?.Write(LogLevel.Debug, "Dispatch", "started", "Fahrzeugplanung gestartet.");
        try
        {
            var result = await DispatchCoreAsync(mission, preview, token, automatic);
            Logger?.Write(result.Success ? LogLevel.Info : LogLevel.Warning, "Dispatch", "result", result.Message);
            return result;
        }
        catch (OperationCanceledException)
        {
            Logger?.Write(token.IsCancellationRequested ? LogLevel.Info : LogLevel.Error, "Dispatch", "cancelled",
                token.IsCancellationRequested ? "Alarmierung / Planprüfung abgebrochen." : "Zeitüberschreitung bei der Alarmierung / Planprüfung.");
            throw;
        }
    }

    private async Task<DispatchResult> DispatchCoreAsync(Mission mission, bool preview, CancellationToken token, bool automatic, bool requireReassignment = false, bool requirePlanned = false, bool requireComplete = false)
    {
        await _dispatchGate.WaitAsync(token);
        DispatchPlan? plan = null;
        var sent = false;
        try
        {
            EnsureLoggedIn();
            if (!preview && ActionsAllowed?.Invoke() == false) return new(false, false, "Spielaktionen sind derzeit pausiert.");
            var catalog = await GetCatalogAsync(token, mission.TypeKey);
            var html = await ReadDispatchPageAsync(mission, token);
            var details = await ParseDetailsWithRefreshAsync(html, mission, token);
            if (details.TypeKey != null && !catalog.ContainsKey(details.TypeKey)) catalog = await GetCatalogAsync(token, details.TypeKey);
            if (mission.HasLiveRequirements && !mission.HasMissingVehicles && !details.HasMissingRequirementsFromPage)
                return new(true, false, "Die aktuelle Nachforderung ist inzwischen verschwunden.");
            foreach (var field in details.HiddenFields.Where(f => f.Key == "authenticity_token")) Logger?.RegisterSecret(field.Value);
            var vehicles = await GetVehiclesAsync(!preview, token);
            ObserveStaffing(details.VehicleStaffingProblems, vehicles);
            if (_pendingMissions.TryGetValue(mission.Id, out var pending))
            {
                var accepted = pending.VehicleIds.All(id => vehicles.Any(v => v.Id == id && v.TargetType == "mission" && v.TargetId == mission.Id));
                if (accepted || pending.Expires <= DateTime.UtcNow)
                {
                    _pendingMissions.Remove(mission.Id);
                    if (accepted) foreach (var id in pending.VehicleIds) _pendingVehicles.Remove(id);
                }
                else return new(false, false, "Die letzte Alarmierung dieses Einsatzes wartet noch auf Bestätigung. Bitte den nächsten Scan abwarten.");
            }
            foreach (var vehicle in vehicles) vehicle.Order = details.VehicleOrder.GetValueOrDefault(vehicle.Id, int.MaxValue);
            foreach (var expired in _pendingVehicles.Where(p => p.Value <= DateTime.UtcNow ||
                         vehicles.Any(v => v.Id == p.Key && !v.IsAvailable)).Select(p => p.Key).ToList())
                _pendingVehicles.Remove(expired);
            var specification = details.TypeKey != null ? catalog.GetValueOrDefault(details.TypeKey) : null;
            if (!preview && automatic && PlannedAutomationWait(mission, specification) is { } durationWait)
            { SkipPlannedMission(mission, specification, durationWait); return new(false, false, durationWait); }
            await ReadTrainedPersonnelAsync(mission, specification, details, vehicles, token);
            await PrepareApproachesAsync(mission, specification, details, vehicles, token);
            plan = requirePlanned ? BuildPlannedPlan(mission, specification, details, vehicles, out _) :
                CreateVehiclePlanner().Build(mission, specification, details, PlanningFleet(vehicles), ReservedForMission(mission.Id), StationCrewBudgets(vehicles, mission, details));
            AddStaffingWait(plan, vehicles);
            var action = ValidateAlarmForm(mission, details, plan);
            LimitPartialTopup(mission, plan, automatic);
            if (preview) return new(plan.IsComplete, false, plan.Summary, plan);
            if (!plan.IsComplete && (!plan.CanSendPartialTopup || requirePlanned || requireComplete)) return new(false, false, plan.Summary, plan);
            if (plan.SelectedVehicles.Count == 0) return new(true, false, "Alle ermittelten Anforderungen sind bereits abgedeckt.", plan);
            if (requirePlanned && CheckPlannedDispatch(mission, plan, details, vehicles) is { } plannedWait)
                return new(false, false, plannedWait, plan);

            await _pacer.WaitAsync(token);
            if (automatic && (!_config.Config.BotSettings.AutoAlarm || requireReassignment && !_config.Config.BotSettings.ReassignBlockedMissions || requirePlanned && !_config.Config.BotSettings.AutoPlannedMissions || AutomaticActionsAllowed?.Invoke() == false))
                return new(false, false, "Automatische Alarmierung pausiert / ausgeschaltet.", plan);
            // Availability, expansion, missing requirements and CSRF token can change during the pause.
            details = await ParseDetailsWithRefreshAsync(await GetTextAsync($"/missions/{mission.Id}", token), mission, token);
            if (details.TypeKey != null && !catalog.ContainsKey(details.TypeKey)) catalog = await GetCatalogAsync(token, details.TypeKey);
            if (mission.HasLiveRequirements && !mission.HasMissingVehicles && !details.HasMissingRequirementsFromPage)
                return new(true, false, "Die aktuelle Nachforderung ist inzwischen abgedeckt; keine weiteren Fahrzeuge nötig.");
            foreach (var field in details.HiddenFields.Where(f => f.Key == "authenticity_token")) Logger?.RegisterSecret(field.Value);
            vehicles = await GetVehiclesAsync(true, token);
            ObserveStaffing(details.VehicleStaffingProblems, vehicles);
            foreach (var vehicle in vehicles) vehicle.Order = details.VehicleOrder.GetValueOrDefault(vehicle.Id, int.MaxValue);
            specification = details.TypeKey != null ? catalog.GetValueOrDefault(details.TypeKey) : null;
            if (automatic && PlannedAutomationWait(mission, specification) is { } changedDurationWait)
            { SkipPlannedMission(mission, specification, changedDurationWait); return new(false, false, changedDurationWait, plan); }
            await ReadTrainedPersonnelAsync(mission, specification, details, vehicles, token);
            await PrepareApproachesAsync(mission, specification, details, vehicles, token);
            plan = requirePlanned ? BuildPlannedPlan(mission, specification, details, vehicles, out _) :
                CreateVehiclePlanner().Build(mission, specification, details, PlanningFleet(vehicles), ReservedForMission(mission.Id), StationCrewBudgets(vehicles, mission, details));
            AddStaffingWait(plan, vehicles);
            action = ValidateAlarmForm(mission, details, plan);
            LimitPartialTopup(mission, plan, automatic);
            if (!plan.IsComplete && (!plan.CanSendPartialTopup || requirePlanned || requireComplete)) return new(false, false, plan.Summary, plan);
            if (plan.SelectedVehicles.Count == 0) return new(true, false, "Anforderungen inzwischen abgedeckt.", plan);
            if (requirePlanned && CheckPlannedDispatch(mission, plan, details, vehicles) is { } changedPlannedWait)
                return new(false, false, changedPlannedWait, plan);
            if (automatic && (!_config.Config.BotSettings.AutoAlarm || requireReassignment && !_config.Config.BotSettings.ReassignBlockedMissions || requirePlanned && !_config.Config.BotSettings.AutoPlannedMissions || AutomaticActionsAllowed?.Invoke() == false))
                return new(false, false, "Automatische Alarmierung pausiert / ausgeschaltet.", plan);

            var fields = details.HiddenFields.Where(p => p.Key is not ("commit" or "alarm")).ToList();
            fields.Add(new("commit", "Alarmieren"));
            fields.AddRange(plan.SelectedVehicles.Select(v => new KeyValuePair<string, string>("vehicle_ids[]", v.Id.ToString())));
            foreach (var vehicle in plan.SelectedVehicles)
                if (details.EquipmentFieldsByVehicleId.TryGetValue(vehicle.Id, out var equipment)) fields.AddRange(equipment.Distinct());
            foreach (var vehicle in plan.SelectedVehicles) _pendingVehicles[vehicle.Id] = DateTime.UtcNow.AddMinutes(2);
            _pendingMissions[mission.Id] = (DateTime.UtcNow.AddMinutes(2), plan.SelectedVehicles.Select(v => v.Id).ToArray());
            using var request = new HttpRequestMessage(HttpMethod.Post, action) { Content = new FormUrlEncodedContent(fields) };
            request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, $"/missions/{mission.Id}");
            // After sending starts, a network failure has an ambiguous outcome: never blindly resend.
            sent = true;
            using var response = await SendAsync(request, token, automaticAction: automatic, actionAllowed: () => _config.Config.BotSettings.AutoAlarm &&
                PlannedAutomationWait(mission, specification) == null &&
                ApproachAllowsSend(plan, details) &&
                (!requireReassignment || _config.Config.BotSettings.ReassignBlockedMissions) &&
                (!requirePlanned || _config.Config.BotSettings.AutoPlannedMissions && PlannedBudgetAllows(plan, details, vehicles, mission)), requireAccess: true,
                requestAllowed: () => ApproachAllowsSend(plan, details));
            CheckAuthentication(response);
            if ((int)response.StatusCode is not (200 or 302 or 303))
                return new(false, true, $"Alarmierung abgelehnt (HTTP {(int)response.StatusCode}).", plan);
            var alarmHtml = await response.Content.ReadAsStringAsync(token);
            var staffingFailures = new Dictionary<long, string>(GamePageParser.ReadStaffingProblems(alarmHtml));
            var alarmNotices = new HashSet<string>(GamePageParser.ReadAlarmNotices(alarmHtml));
            MissionDetails? alarmDetails = null;
            if ((int)response.StatusCode is 302 or 303)
            {
                // Read the redirect before an API request can discard a one-request flash notice.
                // Only the current mission page may be followed, never another host or action.
                var redirect = response.Headers.Location == null ? new Uri(_httpClient.BaseAddress!, $"/missions/{mission.Id}") :
                    new Uri(_httpClient.BaseAddress!, response.Headers.Location);
                if (SameOrigin(redirect) && redirect.AbsolutePath == $"/missions/{mission.Id}")
                {
                    alarmHtml = await GetTextAsync(redirect.PathAndQuery, token);
                    foreach (var failure in GamePageParser.ReadStaffingProblems(alarmHtml)) staffingFailures[failure.Key] = failure.Value;
                    foreach (var notice in GamePageParser.ReadAlarmNotices(alarmHtml)) alarmNotices.Add(notice);
                    alarmDetails = await ParseDetailsWithRefreshAsync(alarmHtml, mission, token);
                }
                else plan.Notes.Add("Antwortweiterleitung führt nicht zur aktuellen Einsatzseite; sie wird nicht automatisch geöffnet.");
            }
            var refreshed = await GetVehiclesAsync(true, token);
            var confirmed = plan.SelectedVehicles.All(selected => refreshed.Any(v => v.Id == selected.Id &&
                v.TargetType == "mission" && v.TargetId == mission.Id) || alarmDetails?.AssignedVehicleIds.Contains(selected.Id) == true);
            if (!confirmed)
            {
                if (alarmDetails == null)
                {
                    var afterHtml = await GetTextAsync($"/missions/{mission.Id}", token);
                    alarmDetails = await ParseDetailsWithRefreshAsync(afterHtml, mission, token);
                    foreach (var notice in GamePageParser.ReadAlarmNotices(afterHtml)) alarmNotices.Add(notice);
                }
                var after = alarmDetails;
                foreach (var failure in after.VehicleStaffingProblems) staffingFailures[failure.Key] = failure.Value;
                confirmed = plan.SelectedVehicles.All(selected => after.AssignedVehicleIds.Contains(selected.Id) ||
                    refreshed.Any(v => v.Id == selected.Id && v.TargetType == "mission" && v.TargetId == mission.Id));
                var acceptedIds = plan.SelectedVehicles.Where(selected => after.AssignedVehicleIds.Contains(selected.Id) ||
                    refreshed.Any(vehicle => vehicle.Id == selected.Id && vehicle.TargetType == "mission" && vehicle.TargetId == mission.Id)).Select(vehicle => vehicle.Id).ToHashSet();
                RememberConfirmedTraining(mission.Id,details,plan.SelectedVehicles,acceptedIds);
                var rejected = staffingFailures.Where(failure => plan.SelectedVehicles.Any(vehicle => vehicle.Id == failure.Key) && !acceptedIds.Contains(failure.Key))
                    .ToDictionary(failure => failure.Key, failure => failure.Value);
                if (rejected.Count > 0)
                {
                    ObserveStaffing(rejected, refreshed, freshResponse: true, equipmentVehicleIds: details.EquipmentFieldsByVehicleId.Keys.ToHashSet());
                    foreach (var id in acceptedIds.Concat(rejected.Keys)) _pendingVehicles.Remove(id);
                    var unknown = plan.SelectedVehicles.Where(vehicle => !acceptedIds.Contains(vehicle.Id) && !rejected.ContainsKey(vehicle.Id)).Select(vehicle => vehicle.Id).ToArray();
                    if (unknown.Length == 0) _pendingMissions.Remove(mission.Id);
                    else _pendingMissions[mission.Id] = (DateTime.UtcNow.AddMinutes(2), unknown);
                    plan.Problems.Add("Spiel hat Fahrzeuge wegen fehlendem Personal / passender Ausbildung abgelehnt.");
                    AddStaffingWait(plan, refreshed);
                    var message = plan.WaitingForPersonnel.Length > 0 ? plan.WaitingForPersonnel : plan.Problems.Last();
                    if (acceptedIds.Count > 0) message = $"{acceptedIds.Count} Fahrzeuge bestätigt. " + message;
                    if (unknown.Length > 0) message += " Weitere Fahrzeugzuordnung noch unbestätigt; keine blinde Wiederholung.";
                    return new(false, true, message, plan) { KnownStaffingFailure = true, HasUnconfirmedVehicles = unknown.Length > 0, AnyVehicleAccepted = acceptedIds.Count > 0 };
                }
            }
            if (confirmed)
            {
                RememberConfirmedTraining(mission.Id,details,plan.SelectedVehicles,plan.SelectedVehicles.Select(vehicle=>vehicle.Id).ToHashSet());
                _pendingMissions.Remove(mission.Id);
                foreach (var vehicle in plan.SelectedVehicles) _pendingVehicles.Remove(vehicle.Id);
            }
            return confirmed
                ? new(plan.IsComplete, true, $"{mission.DisplayTitle}: {plan.SelectedVehicles.Count} Fahrzeuge alarmiert." +
                    (plan.IsComplete ? "" : " Weitere Kräfte fehlen noch; wartet auf freie Fahrzeuge.") + $"\n{plan.Summary}", plan)
                    { AnyVehicleAccepted = true, HasUnconfirmedVehicles = false }
                : new(false, true, "Alarmierung gesendet, Fahrzeugzuordnung noch nicht bestätigt: " +
                    string.Join(", ", plan.SelectedVehicles.Take(10).Select(vehicle => vehicle.Name + " (#" + vehicle.Id + ")")) + ". " +
                    (alarmNotices.Count > 0 ? "Spielantwort: " + string.Join(" ", alarmNotices) + " " : "") +
                    "Der nächste Scan prüft den Einsatz erneut; keine blinde Wiederholung.", plan);
        }
        catch (AutomationUnavailableException)
        {
            _pendingMissions.Remove(mission.Id);
            if (plan != null) foreach (var vehicle in plan.SelectedVehicles) _pendingVehicles.Remove(vehicle.Id);
            return new(false, false, "Spielaktionen sind derzeit pausiert. Keine Fahrzeuge gesendet.", plan);
        }
        catch (AutomaticActionPausedException)
        {
            _pendingMissions.Remove(mission.Id);
            if (plan != null) foreach (var vehicle in plan.SelectedVehicles) _pendingVehicles.Remove(vehicle.Id);
            return new(false, false, "Automatische Alarmierung pausiert; keine Fahrzeuge gesendet.", plan);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            Logger?.Write(LogLevel.Error, "Dispatch", "failed", "Fehler bei der Alarmierung / Planprüfung.");
            return new(false, sent, ex.Message, plan);
        }
        finally { _dispatchGate.Release(); }
    }

    private void LimitPartialTopup(Mission mission, DispatchPlan plan, bool automatic)
    {
        if (!automatic || !plan.CanSendPartialTopup || PartialTopupsAllowed?.Invoke(mission) != false ||
            plan.Requirements.Any(demand => demand.Key is "ambulances" or "nef" or "helicopter" or "kdow_lna" or "kdow_orgl")) return;
        plan.CanSendPartialTopup = false;
        plan.Notes.Add("Weitere Feuerwehrfahrzeuge erst als vollständige Einsatzgruppe alarmieren.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token, bool automaticAction = false, Func<bool>? actionAllowed = null, bool requireAccess = false, Func<bool>? requestAllowed = null)
    {
        await _requestGate.WaitAsync(token);
        var endpoint = new Uri(_httpClient.BaseAddress!, request.RequestUri!).AbsolutePath;
        try
        {
            var earliest = _lastRequest + _requestInterval;
            if (_serverRetryAfter > earliest) earliest = _serverRetryAfter;
            var delay = earliest - UtcNow;
            if (delay > TimeSpan.Zero) await _requestDelay(delay, token);
            if ((requireAccess || automaticAction || request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) && ActionsAllowed?.Invoke() == false)
                throw new AutomationUnavailableException();
            if (automaticAction && (AutomaticActionsAllowed?.Invoke() == false || actionAllowed?.Invoke() == false)) throw new AutomaticActionPausedException();
            token.ThrowIfCancellationRequested();
            if (requestAllowed?.Invoke() == false) throw new AutomaticActionPausedException();
            var response = await _httpClient.SendAsync(request, token);
            Logger?.Write((int)response.StatusCode >= 400 ? LogLevel.Warning :
                endpoint == "/mission-generate" ? LogLevel.Info : LogLevel.Debug, "HTTP", "response",
                $"{request.Method} {endpoint}: HTTP {(int)response.StatusCode}");
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var retryAfter = response.Headers.RetryAfter;
                var wait = retryAfter?.Delta ?? (retryAfter?.Date - _timeProvider.GetUtcNow()) ?? TimeSpan.FromSeconds(30);
                _serverRetryAfter = UtcNow + (wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
                Logger?.Write(LogLevel.Warning, "HTTP", "cooldown", "Server fordert eine Abfragepause.");
            }
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            Logger?.Write(token.IsCancellationRequested ? LogLevel.Debug : LogLevel.Error, "HTTP", "failed",
                token.IsCancellationRequested ? "HTTP-Anfrage abgebrochen." : "HTTP-Anfrage fehlgeschlagen.");
            throw;
        }
        finally { _lastRequest = UtcNow; _requestGate.Release(); }
    }

    private Uri ValidateAlarmForm(Mission mission, MissionDetails details, DispatchPlan plan)
    {
        var previousProblems = plan.Problems.Count;
        var action = new Uri(_httpClient.BaseAddress!, string.IsNullOrEmpty(details.AlarmPath) ? "/" : details.AlarmPath);
        if (!SameOrigin(action) || action.AbsolutePath != $"/missions/{mission.Id}/alarm" && action.AbsolutePath != $"/missions/{mission.Id}")
            plan.AddRecognitionProblem("Alarmierungsformular nicht erkannt.");
        if (!details.HiddenFields.Any(p => p.Key == "authenticity_token" && !string.IsNullOrEmpty(p.Value)))
            plan.AddRecognitionProblem("Das Alarmierungsformular enthält keinen gültigen Token.");
        if (plan.Problems.Count != previousProblems) plan.CanSendPartialTopup = false;
        return action;
    }

    private async Task<string> GetTextAsync(string path, CancellationToken token, bool automaticAction = false,
        Func<bool>? actionAllowed = null, string? csrfToken = null)
    {
        try { return await ReadTextCoreAsync(path, token, automaticAction, actionAllowed, csrfToken); }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            var endpoint = new Uri(_httpClient.BaseAddress!, path).AbsolutePath;
            throw new HttpRequestException($"Abruf {endpoint}: Zeitüberschreitung nach {_httpClient.Timeout.TotalSeconds:0} Sekunden. Bitte später erneut versuchen.", ex);
        }
    }

    private async Task<string> ReadTextCoreAsync(string path, CancellationToken token, bool automaticAction = false,
        Func<bool>? actionAllowed = null, string? csrfToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Referrer = _httpClient.BaseAddress;
        if (path.StartsWith("/mission-generate?", StringComparison.Ordinal))
        {
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Headers.Accept.ParseAdd("*/*");
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        }
        else if (path.StartsWith("/map/", StringComparison.Ordinal))
        {
            // Rails rejects script responses to non-XHR requests with HTTP 422.
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Headers.Accept.ParseAdd("text/javascript");
        }
        else if (path.StartsWith("/api/", StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal))
            request.Headers.Accept.ParseAdd("application/json");
        if (csrfToken != null) request.Headers.Add("X-CSRF-Token", csrfToken);
        using var response = await SendAsync(request, token, automaticAction: automaticAction, actionAllowed: actionAllowed);
        CheckAuthentication(response);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Abruf {request.RequestUri!.AbsolutePath}: HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                null, response.StatusCode);
        if (path != "/users/sign_in" && text.Contains("/users/sign_in", StringComparison.Ordinal) && text.Contains("<form", StringComparison.OrdinalIgnoreCase))
        {
            var page = new HtmlDocument();
            page.LoadHtml(text);
            if (page.DocumentNode.SelectSingleNode("//form[contains(@action,'/users/sign_in')]") != null)
            { Logout(); throw new UnauthorizedAccessException("Die Sitzung ist abgelaufen. Bitte erneut anmelden."); }
        }
        ObserveEventItem(path, text);
        return text;
    }

    private void CheckAuthentication(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            response.Headers.Location?.ToString().Contains("/users/sign_in", StringComparison.Ordinal) == true)
        {
            Logger?.Write(LogLevel.Warning, "Account", "session.expired", "Server hat die Sitzung abgelehnt.");
            Logout();
            throw new UnauthorizedAccessException("Die Sitzung ist abgelaufen. Bitte erneut anmelden.");
        }
    }
    private void EnsureLoggedIn()
    {
        if (!IsLoggedIn) throw new UnauthorizedAccessException("Bitte zuerst anmelden.");
    }
    private bool SameOrigin(Uri uri) => uri.Scheme == _httpClient.BaseAddress!.Scheme &&
        uri.Host == _httpClient.BaseAddress.Host && uri.Port == _httpClient.BaseAddress.Port;
    private void ReferenceStatusChanged(string status) => Logger?.Write(LogLevel.Info, "Catalog", "references.checked", status);
    public void Dispose()
    {
        _references.StatusChanged -= ReferenceStatusChanged;
        _references.Dispose();
        _routing.Dispose();
        _httpClient.Dispose();
    }
}

