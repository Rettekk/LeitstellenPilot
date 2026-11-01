using System.Linq;

namespace LeitstellenPilot.Services;

public static class StartupArguments
{
    public static bool WindowsAutoStart { get; private set; }
    public static void ReadArguments(string[] args) => WindowsAutoStart = args.Contains(WindowsStartupService.StartupArgument);
}
