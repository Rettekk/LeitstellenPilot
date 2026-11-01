using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Models;

public static class MissionShortageText
{
    public static string Read(JToken? token)
    {
        if (token == null || token.Type is JTokenType.Null or JTokenType.Boolean) return "";
        if (token.Type == JTokenType.String)
        {
            var text = WebUtility.HtmlDecode(token.Value<string>() ?? "").Trim();
            if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Equals("null", StringComparison.OrdinalIgnoreCase)) return "";
            if (text.StartsWith('{'))
            {
                try { return Read(JObject.Parse(text)); }
                catch (JsonException) { } // Preserve malformed/unknown content as an explicit planning problem.
            }
            return Regex.Replace(Regex.Replace(text, @"<br\s*/?>|</(?:div|p|li)>", ";", RegexOptions.IgnoreCase), "<[^>]+>", " ").Trim();
        }
        if (token is JObject groups)
            return string.Join("; ", groups.Properties().Select(property => property.Value.Type is JTokenType.String or JTokenType.Null ||
                property.Value.Type == JTokenType.Boolean && !property.Value.Value<bool>()
                    ? Read(property.Value) : $"{property.Name}: {property.Value.ToString(Formatting.None)}")
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        return token.ToString(Formatting.None);
    }
}
