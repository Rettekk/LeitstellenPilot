using System;
using System.Runtime.InteropServices;

namespace LeitstellenPilot.Services;

public sealed record ReleasePlatform(string RuntimeIdentifier, string ExecutableName, bool Linux, ushort ElfMachine = 62)
{
    public static ReleasePlatform WindowsX64 { get; } = new("win-x64", "LeitstellenPilot.exe", false);
    public static ReleasePlatform LinuxX64 { get; } = new("linux-x64", "LeitstellenPilot", true);
    public static ReleasePlatform LinuxArm64 { get; } = new("linux-arm64", "LeitstellenPilot", true, 183);
    public static ReleasePlatform Current => OperatingSystem.IsLinux() ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? LinuxArm64 : LinuxX64 : WindowsX64;
}
