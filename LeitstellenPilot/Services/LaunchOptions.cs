using System;
using System.IO;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed record LaunchOptions(bool? Headless = null, string? ConfigPath = null, bool InitializeConfig = false,
    bool Start = false, bool Help = false, bool VerifyEnvironment = false, bool LoginOnly = false)
{
    public static LaunchOptions Current { get; private set; } = new();
    public static void Configure(LaunchOptions options) { Current = options; AppPaths.Configure(options.ConfigPath); }
    public bool UseHeadless(AppConfig config) => Headless ?? config.Headless;
    public static LaunchOptions Parse(string[] args)
    {
        var result = new LaunchOptions();
        string Value(ref int index)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--")) throw new ArgumentException("Wert für " + args[index - 1] + " fehlt.");
            return args[index];
        }
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--headless": if (result.Headless == false) throw new ArgumentException("--gui und --headless können nicht gemeinsam verwendet werden."); result = result with { Headless = true }; break;
                case "--gui": if (result.Headless == true) throw new ArgumentException("--gui und --headless können nicht gemeinsam verwendet werden."); result = result with { Headless = false }; break;
                case "--config": result = result with { ConfigPath = Path.GetFullPath(Value(ref i)) }; break;
                case "--init-config": result = result with { InitializeConfig = true }; break;
                case "--start": result = result with { Start = true }; break;
                case "--login": result = result with { LoginOnly = true }; break;
                case "--check-environment": result = result with { VerifyEnvironment = true }; break;
                case "--help": case "-h": result = result with { Help = true }; break;
                case "--windows-autostart": break;
                default: throw new ArgumentException("Unbekannte Option: " + args[i]);
            }
        }
        return result;
    }
    public static TimeZoneInfo ResolveTimeZone(AppConfig config) => string.IsNullOrWhiteSpace(config.TimeZone) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(config.TimeZone);
    public const string Usage = "LeitstellenPilot Standalone [--gui | --headless] [--config DATEI]\n" +
        "  --init-config         Neue Konfiguration anlegen (vorhandene Datei wird nicht ersetzt)\n" +
        "  --login               Nur am Spiel anmelden und die Sitzung geschützt speichern\n" +
        "  --start               Bot nach der Spielanmeldung starten\n" +
        "  --check-environment   Laufzeit und geschützte lokale Speicherung prüfen\n" +
        "Passwort für die erste Anmeldung: LEITSTELLENPILOT_PASSWORD oder gespeicherte Anmeldung.\n";
}
