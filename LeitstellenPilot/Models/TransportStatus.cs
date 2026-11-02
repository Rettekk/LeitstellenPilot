using System.Text.RegularExpressions;

namespace LeitstellenPilot.Models;

public static class TransportStatus
{
    private static readonly Regex Prisoners = new(@"\bGefangene\s+sollen\s+abtransportiert\s+werden\.?", RegexOptions.IgnoreCase);
    public static bool HasPrisoners(string text) => Prisoners.IsMatch(text);
    public static string WithoutPrisoners(string text) => Prisoners.Replace(text, "").Trim(' ', ';', ',', '\r', '\n');
}
