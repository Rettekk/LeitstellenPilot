using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed class ConfigService
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _sessionSync = new();
    private long _sessionVersion;
    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _sessionPath;
    private readonly ISecretProtection _secrets;
    public string ConfigurationPath => _configPath;
    public AppConfig Config { get; }
    public UserSession Session { get; private set; }
    public string MissionCatalogCachePath => Path.Combine(_directory, "cache", "mission-types.json");
    public string ReferenceDataCachePath => Path.Combine(_directory, "cache", "reference-data.json");

    public ConfigService(string? directory = null, ISecretProtection? secrets = null)
    {
        _directory = directory ?? AppPaths.BaseDir;
        _configPath = directory == null ? AppPaths.ConfigFile : Path.Combine(_directory, "config.json");
        _sessionPath = Path.Combine(_directory, "session.json");
        _secrets = secrets ?? new SecretProtection(_directory);
        Config = LoadConfig();
        Config.BotSettings.PacingDefaultsVersion = 2;
        Config.BotSettings.PollingDefaultsVersion = 2;
        // A first save also persists the current default range, even without opening settings.
        Config.BotSettings.ScanIntervalConfigured = true;
        Session = LoadSession();
    }

    private AppConfig LoadConfig()
    {
        if (!File.Exists(_configPath)) return new();
        try
        {
            var data = JObject.Parse(File.ReadAllText(_configPath));
            // Retired operator identities, leases, remote update policies and shared
            // profiles must not survive migration into the standalone configuration.
            foreach (var legacy in new[] { "Registration", "Updates", "UsageStatistics", "MissionTimings", "Proxy", "Expansion" })
                data.Property(legacy, StringComparison.OrdinalIgnoreCase)?.Remove();
            var stationUpgradeData = data.Property("StationUpgrades", StringComparison.OrdinalIgnoreCase);
            stationUpgradeData?.Remove();
            var eventItemClaims = data.Property("EventItemClaims", StringComparison.OrdinalIgnoreCase); eventItemClaims?.Remove();
            var personnelLayouts = data.Property("PersonnelLayouts", StringComparison.OrdinalIgnoreCase); personnelLayouts?.Remove();
            var personnelBackups = data.Property("PersonnelBackups", StringComparison.OrdinalIgnoreCase); personnelBackups?.Remove();
            var config = data.ToObject<AppConfig>() ?? new();
            try { config.StationUpgrades = stationUpgradeData?.Value.ToObject<StationUpgradeSettings>() ?? new(); }
            catch (JsonException) { config.StationUpgrades = new(); }
            StationUpgradePlanner.Normalize(config.StationUpgrades);
            try { config.EventItemClaims = eventItemClaims?.Value.ToObject<Dictionary<string, DateTime>>() ?? new(); }
            catch (JsonException) { config.EventItemClaims = new(); }
            try { config.PersonnelLayouts = personnelLayouts?.Value.ToObject<Dictionary<string, string>>() ?? new(); }
            catch (JsonException) { config.PersonnelLayouts = new(); }
            try { config.PersonnelBackups = personnelBackups?.Value.ToObject<Dictionary<string, PersonnelBackup>>() ?? new(); }
            catch (JsonException) { config.PersonnelBackups = new(); }
            config.BotSettings ??= new();
            var savedSettings = data.GetValue("BotSettings", StringComparison.OrdinalIgnoreCase) as JObject;
            // Before explicit upper bounds existed, customized intervals used percentage variation.
            if (config.BotSettings.ScanIntervalConfigured && savedSettings?.GetValue("MaxScanIntervalSeconds", StringComparison.OrdinalIgnoreCase) == null)
                config.BotSettings.MaxScanIntervalSeconds = 0;
            config.BotSettings.PlannedMaxDurationMinutes = PlannedMissionPolicy.NormalizeDurationLimit(config.BotSettings.PlannedMaxDurationMinutes);
            config.BotSettings.HospitalWaitMinutes = HospitalPlanner.NormalizeWait(config.BotSettings.HospitalWaitMinutes);
            config.BotSettings.MaxApproachMinutes = ApproachPolicy.NormalizeMinutes(config.BotSettings.MaxApproachMinutes);
            if (config.BotSettings.MaxScanIntervalSeconds > 0) config.BotSettings.MaxScanIntervalSeconds = Math.Clamp(config.BotSettings.MaxScanIntervalSeconds, Math.Clamp(config.BotSettings.ScanIntervalSeconds, 5, 300), 300);
            config.BotSettings.DetailReviewIntervalSeconds = Math.Clamp(config.BotSettings.DetailReviewIntervalSeconds, 10, 120);
            config.BotSettings.MaxDetailReviewIntervalSeconds = Math.Clamp(config.BotSettings.MaxDetailReviewIntervalSeconds, config.BotSettings.DetailReviewIntervalSeconds, 120);
            config.DailyBonusClaimDays ??= new();
            config.VehicleLoans ??= new();
            config.FleetBlockedMissions ??= new();
            // Older versions had a fixed scan pause rather than a user-selected interval.
            if (!config.BotSettings.ScanIntervalConfigured)
            { config.BotSettings.ScanIntervalSeconds = 10; config.BotSettings.MaxScanIntervalSeconds = 20; }
            if (config.BotSettings.PacingDefaultsVersion < 1)
            {
                config.BotSettings.MinActionDelaySeconds = 15;
                config.BotSettings.MaxActionDelaySeconds = 30;
                config.BotSettings.ScanIntervalSeconds = 10;
                config.BotSettings.AdaptivePolling = true;
                config.BotSettings.ActivePollingSeconds = 60;
                config.BotSettings.IdlePollingSeconds = 60;
                config.BotSettings.PollingVariationPercent = 10;
                config.BotSettings.AccountRefreshSeconds = 60;
                config.BotSettings.MaxAlarmsPerCycle = 1;
                config.BotSettings.PacingDefaultsVersion = 1;
            }
            if (config.BotSettings.PollingDefaultsVersion < 1)
            {
                // Only migrate the previous hidden defaults; preserve customized values.
                if (config.BotSettings.ActivePollingSeconds == 60) config.BotSettings.ActivePollingSeconds = 20;
                if (config.BotSettings.IdlePollingSeconds == 60) config.BotSettings.IdlePollingSeconds = 20;
                config.BotSettings.PollingDefaultsVersion = 1;
            }
            if (config.BotSettings.PacingDefaultsVersion < 2)
            {
                var oldPause = (config.BotSettings.MinActionDelaySeconds, config.BotSettings.MaxActionDelaySeconds);
                if (oldPause is (15, 30) or (4, 12) or (4, 11))
                { config.BotSettings.MinActionDelaySeconds = 3; config.BotSettings.MaxActionDelaySeconds = 11; }
                config.BotSettings.PacingDefaultsVersion = 2;
            }
            if (config.BotSettings.PollingDefaultsVersion < 2)
            {
                if (!config.BotSettings.ScanIntervalConfigured) config.BotSettings.ScanIntervalSeconds = 10;
                if (config.BotSettings.ActivePollingSeconds is 20 or 60) config.BotSettings.ActivePollingSeconds = 10;
                config.BotSettings.PollingDefaultsVersion = 2;
            }
            // Existing installations keep their previous personnel pacing once;
            // from the first save onwards the two ranges are independent.
            if (savedSettings?.GetValue("MinPersonnelActionDelaySeconds", StringComparison.OrdinalIgnoreCase) == null)
                config.BotSettings.MinPersonnelActionDelaySeconds = config.BotSettings.MinActionDelaySeconds;
            if (savedSettings?.GetValue("MaxPersonnelActionDelaySeconds", StringComparison.OrdinalIgnoreCase) == null)
                config.BotSettings.MaxPersonnelActionDelaySeconds = config.BotSettings.MaxActionDelaySeconds;
            config.BotSettings.MinPersonnelActionDelaySeconds = Math.Clamp(config.BotSettings.MinPersonnelActionDelaySeconds, 1, 300);
            config.BotSettings.MaxPersonnelActionDelaySeconds = Math.Clamp(config.BotSettings.MaxPersonnelActionDelaySeconds,
                config.BotSettings.MinPersonnelActionDelaySeconds, 300);
            if (!string.IsNullOrEmpty(config.Password))
            {
                try
                {
                if (config.Password.StartsWith("dpapi:") || config.Password.StartsWith("aesgcm-v1:"))
                    config.Password = _secrets.Unprotect(config.Password);
                else
                {
                    // Import the old password format once. Future saves use Windows user protection.
                    var encoded = Encoding.UTF8.GetString(Convert.FromBase64String(config.Password));
                    const string oldKey = "LeitstellenspielBot2025_SecretKey";
                    var decoded = new StringBuilder();
                    for (var i = 0; i < encoded.Length; i++) decoded.Append((char)(encoded[i] ^ oldKey[i % oldKey.Length]));
                    config.Password = decoded.ToString();
                }
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
                { config.Password = ""; }
            }
            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or CryptographicException or FormatException or PlatformNotSupportedException)
        {
            System.Diagnostics.Trace.WriteLine("Konfiguration konnte nicht geladen werden: " + ex.GetType().Name);
            return new();
        }
    }

    private UserSession LoadSession()
    {
        if (!File.Exists(_sessionPath)) return new();
        try
        {
            var data = JObject.Parse(File.ReadAllText(_sessionPath));
            var session = data["ProtectedSession"] is { } protectedSession
                ? JsonConvert.DeserializeObject<UserSession>(_secrets.Unprotect(protectedSession.ToString().Contains(':') ? protectedSession.ToString() : "dpapi:" + protectedSession))
                : data.ToObject<UserSession>();
            return session ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or CryptographicException or FormatException or PlatformNotSupportedException)
        {
            return new();
        }
    }

    public async Task SaveConfigAsync(AppConfig config)
    {
        await SaveAsync(_configPath, () =>
        {
            var password = config.Password;
            var snapshot = JObject.FromObject(config);
            snapshot["Password"] = string.IsNullOrEmpty(password) ? "" : _secrets.Protect(password);
            return snapshot.ToString(Formatting.Indented);
        });
    }

    public async Task SaveLoginDataAsync(string identifier, string password)
    {
        Config.LoginIdentifier = identifier;
        Config.Password = password;
        Config.LastConfigUpdate = DateTime.UtcNow;
        await SaveConfigAsync(Config);
    }

    public async Task SaveSessionAsync(UserSession session)
    {
        long version;
        lock (_sessionSync) { Session = session; version = ++_sessionVersion; }
        await SaveAsync(_sessionPath, () =>
            {
                lock (_sessionSync)
                {
                    if (version != _sessionVersion) return null;
                    var protectedValue = _secrets.Protect(JsonConvert.SerializeObject(session));
                    // Keep the established Windows envelope readable by earlier Windows releases and rollback builds.
                    return new JObject { ["ProtectedSession"] = protectedValue.StartsWith("dpapi:", StringComparison.Ordinal) ? protectedValue[6..] : protectedValue }.ToString();
                }
            }, temporary =>
            {
                lock (_sessionSync) if (version == _sessionVersion) File.Move(temporary, _sessionPath, true);
            });
    }

    private async Task SaveAsync(string path, Func<string?> contents, Action<string>? commit = null)
    {
        await _saveGate.WaitAsync();
        string? temporary = null;
        try
        {
            var data = contents();
            if (data == null) return;
            Directory.CreateDirectory(_directory);
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporary, data);
            SecretProtection.PrivateFile(temporary);
            for (var attempt = 0; ; attempt++)
            {
                try { if (commit != null) commit(temporary); else File.Move(temporary, path, true); break; }
                catch (Exception ex) when (attempt < 9 && ex is IOException or UnauthorizedAccessException)
                { await Task.Delay(25); } // Short-lived Windows locks during another instance's atomic replace.
            }
        }
        finally
        {
            if (temporary != null)
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _saveGate.Release();
        }
    }

    public void ClearSession()
    {
        lock (_sessionSync)
        {
            _sessionVersion++; Session = new();
            if (File.Exists(_sessionPath)) File.Delete(_sessionPath);
        }
    }

    public bool IsSessionValid() => Session.IsValid;
    public bool CanAutoLogin() => Config.BotSettings.AutoLogin &&
        !string.IsNullOrWhiteSpace(Config.LoginIdentifier) && (Session.HasStoredSession || !string.IsNullOrEmpty(Config.Password));

}

