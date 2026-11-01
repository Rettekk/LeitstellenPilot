using System;
using Avalonia;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Services;

namespace LeitstellenPilot
{
    internal sealed class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try { return Run(args); }
            catch (OperationCanceledException) { return 0; }
            catch (System.Net.Http.HttpRequestException) { Console.Error.WriteLine("Verbindung vorübergehend nicht verfügbar. Programm später erneut starten."); return 1; }
            catch (Newtonsoft.Json.JsonException) { Console.Error.WriteLine("Eine Antwort oder lokale Datei konnte nicht gelesen werden."); return 2; }
            catch (DllNotFoundException) { Console.Error.WriteLine("Grafikbibliotheken fehlen. Unter Ubuntu/Debian: libx11-6 libice6 libsm6 libfontconfig1 installieren; für Server --headless verwenden."); return 2; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException or TimeZoneNotFoundException or InvalidTimeZoneException)
            { Console.Error.WriteLine("LeitstellenPilot konnte nicht starten: " + ex.Message); return 2; }
        }
        private static int Run(string[] args)
        {
            var options = LaunchOptions.Parse(args); LaunchOptions.Configure(options);
            if (options.Help) { Console.WriteLine(LaunchOptions.Usage); return 0; }
            StartupArguments.ReadArguments(args);
            var config = new ConfigService();
            if (options.InitializeConfig)
            {
                if (File.Exists(config.ConfigurationPath)) throw new IOException("Konfiguration existiert bereits; sie wurde nicht ersetzt.");
                config.Config.Headless = options.Headless ?? true; config.Config.TimeZone = "Europe/Berlin";
                config.Config.BotSettings.AutoStart = true;
                config.SaveConfigAsync(config.Config).GetAwaiter().GetResult(); Console.WriteLine("Konfiguration angelegt: " + config.ConfigurationPath); return 0;
            }
            using var profile = AcquireProfile(config);
            if (options.VerifyEnvironment)
            {
                var protector = new SecretProtection(AppPaths.BaseDir); const string probe = "LeitstellenPilot-Speichertest";
                if (protector.Unprotect(protector.Protect(probe)) != probe) throw new IOException("Speichertest fehlgeschlagen.");
                Console.WriteLine("Plattform: " + ReleasePlatform.Current.RuntimeIdentifier);
                if (OperatingSystem.IsLinux()) Console.WriteLine("Paketversion: " + LinuxExecutable.ReadVersion(Environment.ProcessPath!));
                Console.WriteLine("Geschützte Speicherung: OK");
                Console.WriteLine("Zeitzone: " + LaunchOptions.ResolveTimeZone(config.Config).Id); return 0;
            }
            if (options.UseHeadless(config.Config) || options.LoginOnly)
                return RunHeadlessAsync(config, options).GetAwaiter().GetResult();
            if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                throw new InvalidOperationException("Kein grafischer Desktop verfügbar. Mit --headless oder Headless: true starten.");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        private static FileStream AcquireProfile(ConfigService config)
        {
            var path = Path.Combine(AppPaths.BaseDir, "runtime.lock"); Directory.CreateDirectory(AppPaths.BaseDir);
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Ungültige Instanzsperre.");
            try { var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); SecretProtection.PrivateFile(path); return file; }
            catch (IOException ex) { throw new IOException("Dieses Profil wird bereits von einer anderen Instanz verwendet. Den laufenden Dienst vor einer Installation stoppen.", ex); }
        }
        private static async Task<int> RunHeadlessAsync(ConfigService config, LaunchOptions options)
        {
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            using var term = !OperatingSystem.IsWindows() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); }) : null;
            try { await using var runtime = new HeadlessRuntime(config, options); return await runtime.RunAsync(stop.Token); }
            finally { Console.CancelKeyPress -= cancel; }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont();
    }
}
