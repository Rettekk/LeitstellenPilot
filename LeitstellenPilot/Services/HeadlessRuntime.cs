using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

// The standalone host owns only the game login, local settings and bot lifecycle.
public sealed class HeadlessRuntime : IAsyncDisposable
{
    private readonly ConfigService _config;
    private readonly LaunchOptions _options;
    private readonly ApiService _api;
    private readonly ProgramLog _log;
    private readonly BotService _bot;
    private readonly TextWriter _output;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string?> _password;
    private readonly bool _ownsApi;
    private string _lastPrinted = "";
    private bool _disposed;
    public BotService Bot => _bot;

    public HeadlessRuntime(ConfigService config, LaunchOptions options, ApiService? api = null,
        TextWriter? output = null, TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<string?>? password = null)
    {
        _config = config; _options = options; _ownsApi = api == null; _api = api ?? new(config);
        _output = output ?? Console.Out; _time = time ?? TimeProvider.System; _delay = delay ?? Task.Delay;
        _password = password ?? (() => Environment.GetEnvironmentVariable("LEITSTELLENPILOT_PASSWORD"));
        _log = new(() => new[] { config.Config.Password, config.Session.SessionId, config.Session.Cookies });
        _log.Changed += PrintLog; _api.Logger = _log;
        _bot = new(_api, config, timeProvider: _time, logger: _log);
        _bot.StatusChanged += message => _log.Write(LogLevel.Info, "Bot", "status", message);
        _bot.RestChanged += (resting, _) => _log.Write(LogLevel.Info, "Bot", "rest", resting ? _bot.RestReason : "Ruhepause beendet.");
    }
    private void PrintLog()
    {
        var line = _log.RecentText.Split(Environment.NewLine).LastOrDefault() ?? "";
        lock (_output) { if (line.Length > 0 && line != _lastPrinted) { _output.WriteLine(line); _output.Flush(); _lastPrinted = line; } }
    }
    private void Info(string message) => _log.Write(LogLevel.Info, "Headless", "status", message);
    public async Task<int> RunAsync(CancellationToken token)
    {
        LaunchOptions.ResolveTimeZone(_config.Config);
        Info("LeitstellenPilot Standalone im Headless-Modus gestartet.");
        if (!await LoginAsync(token)) return 4;
        if (_options.LoginOnly) { Info("Anmeldung gespeichert; es wurde kein Bot gestartet."); return 0; }
        if (_config.Config.BotSettings.AutoStart || _options.Start) _bot.StartBot();
        else Info("AutoStart ist ausgeschaltet. Für einen laufenden Bot BotSettings.AutoStart aktivieren oder --start verwenden.");
        var nextAccount = _time.GetUtcNow();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = _time.GetUtcNow();
                if (_api.IsLoggedIn && !_bot.IsResting && now >= nextAccount)
                {
                    nextAccount = now.AddSeconds(Math.Clamp(_config.Config.BotSettings.AccountRefreshSeconds, 15, 300));
                    try { await _api.GetUserInfoAsync(token); await _api.GetBuildingCountsAsync(token); }
                    catch (UnauthorizedAccessException) { Info("Spielanmeldung abgelaufen. Anmeldung erneut einrichten und starten."); return 4; }
                    catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !token.IsCancellationRequested)
                    { Info("Kontoabfrage vorübergehend nicht verfügbar; vorhandene Daten bleiben erhalten."); }
                }
                await _delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await _bot.StopAsync(); }
        return 0;
    }
    private async Task<bool> LoginAsync(CancellationToken token)
    {
        if (!_config.Config.BotSettings.AutoLogin && !_options.Start && !_options.LoginOnly)
        { Info("Automatisches Anmelden ist ausgeschaltet. Für eine bewusste Anmeldung --start oder --login verwenden."); return false; }
        if (_api.IsLoggedIn)
        {
            try { await _api.GetUserInfoAsync(token); return true; }
            catch (UnauthorizedAccessException) { }
        }
        var password = _password() ?? _config.Config.Password;
        if (string.IsNullOrWhiteSpace(_config.Config.LoginIdentifier) || string.IsNullOrEmpty(password))
        { Info("Anmeldung fehlt: LoginIdentifier konfigurieren und das Passwort einmal über LEITSTELLENPILOT_PASSWORD bereitstellen."); return false; }
        _log.RegisterSecret(password); _log.RegisterSecret(_config.Config.LoginIdentifier);
        if (!await _api.LoginAsync(_config.Config.LoginIdentifier, password, token)) { Info("Spielanmeldung wurde abgelehnt."); return false; }
        if (_config.Config.BotSettings.AutoLogin) { _config.Config.Password = password; await _config.SaveConfigAsync(_config.Config); }
        await _api.GetUserInfoAsync(token); Info("Spielanmeldung bestätigt."); return true;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        await _bot.StopAsync(); _log.Changed -= PrintLog; await _log.DisposeAsync();
        if (_ownsApi) _api.Dispose();
    }
}
