using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LeitstellenPilot.Services;

public sealed class RoutingService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _enabled;
    private readonly SemaphoreSlim _gate = new(3, 3);
    private readonly ConcurrentDictionary<string, (double? Seconds, DateTime Until)> _cache = new();
    public RoutingService(HttpMessageHandler? handler = null, bool enabled = true)
    {
        _enabled = enabled;
        _http = new(handler ?? new HttpClientHandler { UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli });
    }
    public async Task<double?> SecondsAsync(double sourceLat, double sourceLon, double targetLat, double targetLon, CancellationToken token)
    {
        if (!_enabled || !Valid(sourceLat, sourceLon) || !Valid(targetLat, targetLon)) return null;
        var key = string.Join(',', sourceLat.ToString("R", CultureInfo.InvariantCulture), sourceLon.ToString("R", CultureInfo.InvariantCulture),
            targetLat.ToString("R", CultureInfo.InvariantCulture), targetLon.ToString("R", CultureInfo.InvariantCulture));
        if (_cache.TryGetValue(key, out var cached) && cached.Until > DateTime.UtcNow) return cached.Seconds;
        foreach (var expired in _cache.Where(pair => pair.Value.Until <= DateTime.UtcNow)) _cache.TryRemove(expired.Key, out _);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var entered = false;
        try
        {
            await _gate.WaitAsync(deadline.Token); entered = true;
            var values = key.Split(',');
            var uri = "https://osrm.missionchief.com/viaroute?loc=" + values[0] + "," + values[1] + "&loc=" + values[2] + "," + values[3];
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65536) return null;
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0)
            { if (output.Length + read > 65536) return null; output.Write(buffer, 0, read); }
            var data = JObject.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()));
            var seconds = data.Value<int?>("status") == 200 ? data["route_summary"]?.Value<double?>("total_time") :
                data.Value<string>("code") == "Ok" && data["routes"] is JArray routes && routes.First is JObject route ? route.Value<double?>("duration") : null;
            if (seconds is not { } value || !double.IsFinite(value) || value < 0 || value > 172800) return null;
            if (_cache.Count < 4096) _cache[key] = (value, DateTime.UtcNow.AddMinutes(30));
            return value;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or OperationCanceledException or IOException)
        { return null; }
        finally
        {
            if (!_cache.ContainsKey(key) && _cache.Count < 4096) _cache[key] = (null, DateTime.UtcNow.AddMinutes(1));
            if (entered) _gate.Release();
        }
    }
    private static bool Valid(double lat, double lon) => double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
