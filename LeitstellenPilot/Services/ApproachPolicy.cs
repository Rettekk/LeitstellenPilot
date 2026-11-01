using System;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public static class ApproachPolicy
{
    public const int DefaultMinutes = 20;
    public static int NormalizeMinutes(int minutes) => minutes < 0 ? DefaultMinutes : Math.Min(minutes, 240);
    public static bool Allows(long vehicleId, MissionDetails details, int minutes) => NormalizeMinutes(minutes) == 0 ||
        details.VehicleTravelSeconds.TryGetValue(vehicleId, out var seconds) && seconds >= 0 && seconds <= NormalizeMinutes(minutes) * 60;
    public static string? Exclusion(long vehicleId, MissionDetails details, int minutes)
    {
        if (Allows(vehicleId, details, minutes)) return null;
        return details.VehicleTravelSeconds.TryGetValue(vehicleId, out var seconds) && seconds >= 0
            ? $"Anfahrt {seconds / 60}:{seconds % 60:00} Min. überschreitet die Grenze von {NormalizeMinutes(minutes)} Min."
            : $"Anfahrtszeit noch nicht bekannt; Grenze von {NormalizeMinutes(minutes)} Min. kann nicht bestätigt werden";
    }
}
