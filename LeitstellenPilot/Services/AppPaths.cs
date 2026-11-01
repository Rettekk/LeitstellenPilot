using System;
using System.IO;

namespace LeitstellenPilot.Services
{
    public static class AppPaths
    {
        private static string? _configurationOverride;
        public static void Configure(string? configurationPath) => _configurationOverride = configurationPath == null ? null : Path.GetFullPath(configurationPath);
        public static string BaseDir =>
            _configurationOverride != null ? Path.GetDirectoryName(_configurationOverride)! :
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LSS") :
            LinuxProfileDirectory(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg) ? xdg :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));

        public static string LinuxProfileDirectory(string configurationRoot)
        {
            var preferred = Path.Combine(configurationRoot, "LSS");
            var legacy = Path.Combine(configurationRoot, "leitstellenpilot");
            // Keep the entire existing profile, including its local encryption key.
            // Explicit --config paths bypass this default-directory selection.
            return !Directory.Exists(preferred) && Directory.Exists(legacy) ? legacy : preferred;
        }

        public static string ConfigFile => _configurationOverride ?? Path.Combine(BaseDir, "config.json");
        
        public static string SessionFile => Path.Combine(BaseDir, "session.json");


        public static void EnsureDirectoryExists()
        {
            if (!Directory.Exists(BaseDir))
            {
                Directory.CreateDirectory(BaseDir);
                Console.WriteLine($"📁 Verzeichnis erstellt: {BaseDir}");
            }
        }
    }
}
