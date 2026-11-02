using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Models;

public sealed class EinsatzInfo
{
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("requirements", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, JToken> Requirements { get; set; } = new();
    [JsonProperty("chances", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, int> Chances { get; set; } = new();
    [JsonProperty("additional", NullValueHandling = NullValueHandling.Ignore)] public JObject Additional { get; set; } = new();
    [JsonProperty("average_credits", NullValueHandling = NullValueHandling.Ignore)] public int AverageCredits { get; set; }
}
