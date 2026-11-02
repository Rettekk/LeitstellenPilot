namespace LeitstellenPilot.Models;

public sealed record VehicleTravelOrigin(double Latitude, double Longitude, bool Direct, int StartDelaySeconds);
