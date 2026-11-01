using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace LeitstellenPilot.Services;

public interface IWindowsStartupService
{
    bool IsAvailable { get; }
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public sealed class WindowsStartupService : IWindowsStartupService
{
    public const string ValueName = "LeitstellenPilot";
    public const string StartupArgument = "--windows-autostart";
    private readonly string? _executable;
    private readonly string? _entryAssemblyPath;
    private readonly bool _applicationProcess;
    private readonly string _registryPath;
    public WindowsStartupService(string? executable = null, string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run", string? entryAssemblyPath = null)
    {
        _executable = executable ?? Environment.ProcessPath; _registryPath = registryPath;
        _entryAssemblyPath = entryAssemblyPath ?? Environment.GetCommandLineArgs()[0];
        _applicationProcess = executable != null || Assembly.GetEntryAssembly()?.GetName().Name == "LeitstellenPilot";
    }
    private bool UsesDotnetHost => string.Equals(Path.GetFileName(_executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase);
    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows() || !_applicationProcess || string.IsNullOrWhiteSpace(_executable) ||
                !File.Exists(_executable) || !string.Equals(Path.GetExtension(_executable), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
            if (!UsesDotnetHost) return true;
            try { return _entryAssemblyPath != null && AssemblyName.GetAssemblyName(_entryAssemblyPath).Name == "LeitstellenPilot"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException) { return false; }
        }
    }
    private string Command => "\"" + Path.GetFullPath(_executable!) + "\" " +
        (UsesDotnetHost ? "\"" + Path.GetFullPath(_entryAssemblyPath!) + "\" " : "") + StartupArgument;
    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Registry.CurrentUser.OpenSubKey(_registryPath);
            return key?.GetValue(ValueName) is string command && command.TrimEnd().EndsWith(" " + StartupArgument, StringComparison.Ordinal);
        }
    }
    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows-Autostart ist nur unter Windows verfügbar.");
        if (enabled && !IsAvailable) throw new InvalidOperationException("Für Windows-Autostart wurde kein gültiger Programmstart gefunden.");
        using var key = Registry.CurrentUser.CreateSubKey(_registryPath, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
