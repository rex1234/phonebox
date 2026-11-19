using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>A room's temperature in °C and, when the page shows it, the relative humidity in %.</summary>
public sealed record RoomReading(double Temp, double? Humidity);

/// <summary>
/// The Room temperature widget: the "Environment" tile of a home-monitor page (vaponitor), e.g.
/// <c>&lt;span&gt;23.3&lt;/span&gt;°C, &lt;span&gt;41.2&lt;/span&gt;% RH</c>. The page is on the local network and the panel cannot read it itself
/// (another origin, plain http), so the agent fetches and parses it. The address is the one typed into the widget's own
/// options: the panel only names the widget ("pageId/widgetId"), never an address, and the request goes through
/// <see cref="FeedHttp"/>, which refuses this computer, link-local and non-routable addresses, follows no redirect to a
/// non-public host and caps the size. Certificates are verified as usual; use the page's plain http address when its
/// certificate is a private one. Answers are cached for <see cref="CacheMs"/> so several widgets or screens cost one request.
/// </summary>
public sealed partial class RoomSensor : IDisposable
{
    public const string WidgetType = "roomtemp";
    internal const long CacheMs = 20_000;
    private const int MaxCached = 16;

    private readonly ConfigStore _config;
    private readonly Func<bool, HttpClient> _client;
    private readonly Func<long> _now;
    private readonly HttpClient? _local, _public;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Cached(long At, RoomReading? Reading, string State);

    public RoomSensor(ConfigStore config) : this(config, null, null) { }

    internal RoomSensor(ConfigStore config, Func<bool, HttpClient>? client, Func<long>? now)
    {
        _config = config;
        _now = now ?? (() => Environment.TickCount64);
        if (client is null)
        {
            _local = new HttpClient(FeedHttp.CreateHandler(true)) { Timeout = TimeSpan.FromSeconds(12) };
            _public = new HttpClient(FeedHttp.CreateHandler(false)) { Timeout = TimeSpan.FromSeconds(12) };
            client = allowPrivate => allowPrivate ? _local : _public;
        }
        _client = client;
    }

    /// <summary>
    /// The answer for the Room temperature widget the panel names ("pageId/widgetId"), always as JSON with a
    /// <c>state</c>: "ok" (with <c>temp</c>, <c>humidity</c> and <c>age</c> in ms), "unknown" (no such widget on an enabled
    /// page), "pick" (no address typed), "badurl" (not an http(s) address), "unreachable" or "nodata" (the page has no
    /// Environment tile). No address or error text reaches the panel.
    /// </summary>
    public async Task<object> Read(string? deviceId, string? widgetKey, CancellationToken ct)
    {
        var cfg = _config.Get();
        if (PanelWidgets.Find(cfg, deviceId, widgetKey, WidgetType) is not { } widget) return new { state = "unknown" };
        var url = widget.Str("url")?.Trim();
        if (string.IsNullOrEmpty(url)) return new { state = "pick" };
        if (FeedHttp.ParseTarget(url) is null) return new { state = "badurl" };

        var now = _now();
        lock (_gate)
            if (_cache.TryGetValue(url, out var hit) && now - hit.At < CacheMs) return Body(hit, now);

        Cached fresh;
        try
        {
            var res = await FeedHttp.GetAsync(_client, url, null, null, ct);
            fresh = Parse(Encoding.UTF8.GetString(res.Body ?? [])) is { } reading
                ? new Cached(now, reading, "ok")
                : new Cached(now, null, "nodata");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { fresh = new Cached(now, null, "unreachable"); }

        lock (_gate)
        {
            if (_cache.Count >= MaxCached && !_cache.ContainsKey(url))
                foreach (var old in _cache.OrderBy(kv => kv.Value.At).Take(_cache.Count - MaxCached + 1).Select(kv => kv.Key).ToList()) _cache.Remove(old);
            _cache[url] = fresh;
        }
        return Body(fresh, now);
    }

    private static object Body(Cached c, long now) => c.Reading is { } r
        ? new { state = c.State, temp = r.Temp, humidity = r.Humidity, age = now - c.At }
        : new { state = c.State };

    /// <summary>
    /// The reading in the page's "Environment" tile: the temperature (°C) and the humidity (% RH) that follow the heading.
    /// Null when there is no such tile or no plausible temperature; a missing or implausible humidity is just left out.
    /// </summary>
    internal static RoomReading? Parse(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var heading = EnvironmentHeading().Match(html);
        if (!heading.Success) return null;
        var tile = html.Substring(heading.Index, Math.Min(900, html.Length - heading.Index));
        var t = TemperatureText().Match(tile);
        if (!t.Success || Number(t.Groups[1].Value) is not { } temp || temp is < -60 or > 90) return null;
        double? humidity = HumidityText().Match(tile) is { Success: true } h && Number(h.Groups[1].Value) is { } v && v is >= 0 and <= 100 ? v : null;
        return new RoomReading(temp, humidity);
    }

    // "23,5" and "23.5" both occur: the page is rendered in the server's locale
    private static double? Number(string s) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    [GeneratedRegex(@">\s*Environment\s*<", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentHeading();

    [GeneratedRegex(@"(-?\d+(?:[.,]\d+)?)\s*(?:</span>)?\s*(?:°|&deg;|&#176;)\s*C", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TemperatureText();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(?:</span>)?\s*%\s*RH", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HumidityText();

    public void Dispose()
    {
        _local?.Dispose();
        _public?.Dispose();
    }
}
