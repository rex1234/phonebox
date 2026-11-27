using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>A Govee light as its LAN API reports it: on or off, brightness 1–100 and the colour temperature when set.</summary>
public sealed record GoveeLightState(bool On, int Brightness, int? Kelvin);

/// <summary>
/// The Govee light widget: one Govee light on this network, switched and dimmed over Govee's LAN control protocol (UDP,
/// no cloud account). The light must have "LAN Control" switched on in the Govee Home app. Commands go to the light's
/// port 4003; it answers status requests on port 4002 of this computer, so this service keeps a listener on that port.
/// The address is the one typed into the widget's own options: the panel only names the widget ("pageId/widgetId") and
/// sends an operation, never an address. Addresses that are not an ordinary unicast IPv4 host are refused.
/// </summary>
public sealed class GoveeLan : IDisposable
{
    public const string WidgetType = "govee-light";
    internal const int StatusPort = 4002, DevicePort = 4003;
    internal static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(1200);
    /// <summary>How long a command may take to show in the light's status, and how often it is asked meanwhile.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(2000), AskAgain = TimeSpan.FromMilliseconds(250);
    internal const int MinKelvin = 2000, MaxKelvin = 9000;

    private readonly ConfigStore _config;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTime At, GoveeLightState State)> _seen = new(StringComparer.Ordinal);
    private UdpClient? _socket;

    public GoveeLan(ConfigStore config) => _config = config;

    /// <summary>
    /// The light's state for the Govee widget the panel names, as JSON with a <c>state</c>: "ok" (with <c>on</c>,
    /// <c>brightness</c> and <c>kelvin</c>), "unknown" (no such widget on an enabled page), "pick" (no address typed),
    /// "badip" (not a usable address), "offline" (no answer from the light) or "port" (the status port is in use).
    /// </summary>
    public Task<object> Read(string? deviceId, string? widgetKey, CancellationToken ct) =>
        Ask(deviceId, widgetKey, null, null, ct);

    /// <summary>
    /// Switches the light: <paramref name="op"/> is "on", "off", "brightness" (with <paramref name="value"/> 1–100) or
    /// "kelvin" (colour temperature, 2000–9000). Answers with the light's state once it shows the change (or its latest
    /// answer after <see cref="Settle"/>), in the same shape as <see cref="Read"/>, or "badop".
    /// </summary>
    public Task<object> Act(string? deviceId, string? widgetKey, string? op, int? value, CancellationToken ct)
    {
        var command = Command(op, value);
        return command is null ? Task.FromResult<object>(new { state = "badop" })
            : Ask(deviceId, widgetKey, command, s => Reflects(op!, value, s), ct);
    }

    private async Task<object> Ask(string? deviceId, string? widgetKey, string? command, Func<GoveeLightState, bool>? done,
        CancellationToken ct)
    {
        if (PanelWidgets.Find(_config.Get(), deviceId, widgetKey, WidgetType) is not { } widget) return new { state = "unknown" };
        var text = widget.Str("ip")?.Trim();
        if (string.IsNullOrEmpty(text)) return new { state = "pick" };
        if (DeviceAddress(text) is not { } ip) return new { state = "badip" };

        UdpClient socket;
        try { socket = Listener(); }
        catch (SocketException) { return new { state = "port" }; }

        var sent = DateTime.UtcNow;
        var target = new IPEndPoint(ip, DevicePort);
        // Right after a command the light still answers with its old state (for about half a second), so it is asked
        // again until the answer shows the change.
        var deadline = sent + (done is null ? Wait : Settle);
        var askAt = sent;
        GoveeLightState? latest = null;
        try
        {
            if (command is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(command);
                await socket.SendAsync(bytes, bytes.Length, target);
            }
            while (DateTime.UtcNow < deadline)
            {
                if (DateTime.UtcNow >= askAt)
                {
                    await socket.SendAsync(StatusRequest, StatusRequest.Length, target);
                    askAt = done is null ? deadline : DateTime.UtcNow + AskAgain;
                }
                await Task.Delay(50, ct);
                lock (_gate)
                    if (_seen.TryGetValue(ip.ToString(), out var seen) && seen.At >= sent) latest = seen.State;
                if (latest is not null && (done is null || done(latest))) break;
            }
        }
        catch (SocketException) { return new { state = "offline" }; }

        if (latest is null) return new { state = "offline" };
        return new { state = "ok", on = latest.On, brightness = latest.Brightness, kelvin = latest.Kelvin };
    }

    private static readonly byte[] StatusRequest = Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"devStatus\",\"data\":{}}}");

    /// <summary>The listener on the status port, started on first use. It keeps the latest status per light address.</summary>
    private UdpClient Listener()
    {
        lock (_gate)
        {
            if (_socket is not null) return _socket;
            var socket = new UdpClient(new IPEndPoint(IPAddress.Any, StatusPort));
            _socket = socket;
            _ = Listen(socket);
            return socket;
        }
    }

    private async Task Listen(UdpClient socket)
    {
        while (true)
        {
            UdpReceiveResult packet;
            try { packet = await socket.ReceiveAsync(); }
            catch (Exception) { break; } // disposed, or the socket failed: the next request opens a new one
            if (ParseStatus(Encoding.UTF8.GetString(packet.Buffer)) is not { } state) continue;
            lock (_gate) _seen[packet.RemoteEndPoint.Address.ToString()] = (DateTime.UtcNow, state);
        }
        lock (_gate)
            if (ReferenceEquals(_socket, socket)) _socket = null;
        socket.Dispose();
    }

    /// <summary>The light's answer to a status request, or null when the packet is anything else.</summary>
    internal static GoveeLightState? ParseStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("msg", out var msg) || !msg.TryGetProperty("cmd", out var cmd)
                || cmd.ValueKind != JsonValueKind.String || cmd.GetString() != "devStatus") return null;
            if (!msg.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            if (!data.TryGetProperty("onOff", out var onOff) || onOff.ValueKind != JsonValueKind.Number) return null;
            var brightness = data.TryGetProperty("brightness", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var bv)
                ? Math.Clamp(bv, 0, 100) : 0;
            int? kelvin = data.TryGetProperty("colorTemInKelvin", out var k) && k.ValueKind == JsonValueKind.Number && k.TryGetInt32(out var kv) && kv > 0
                ? kv : null;
            return new GoveeLightState(onOff.GetInt32() == 1, brightness, kelvin);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The LAN command for an operation, or null when the operation is unknown.</summary>
    internal static string? Command(string? op, int? value) => op switch
    {
        "on" => "{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":1}}}",
        "off" => "{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":0}}}",
        "brightness" when value is { } v => $"{{\"msg\":{{\"cmd\":\"brightness\",\"data\":{{\"value\":{Math.Clamp(v, 1, 100)}}}}}}}",
        "kelvin" when value is { } k =>
            "{\"msg\":{\"cmd\":\"colorwc\",\"data\":{\"color\":{\"r\":0,\"g\":0,\"b\":0},\"colorTemInKelvin\":"
            + Math.Clamp(k, MinKelvin, MaxKelvin) + "}}}",
        _ => null,
    };

    /// <summary>Whether a status answer shows the result of the operation.</summary>
    internal static bool Reflects(string op, int? value, GoveeLightState s) => op switch
    {
        "on" => s.On,
        "off" => !s.On,
        "brightness" => s.Brightness == Math.Clamp(value ?? 0, 1, 100),
        "kelvin" => s.Kelvin == Math.Clamp(value ?? 0, MinKelvin, MaxKelvin),
        _ => true,
    };

    /// <summary>
    /// The light's address when it is an ordinary unicast IPv4 host on the local network: not this computer, not
    /// loopback, multicast, broadcast or unspecified. Null otherwise.
    /// </summary>
    internal static IPAddress? DeviceAddress(string? text)
    {
        if (!IPAddress.TryParse(text?.Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        var b = ip.GetAddressBytes();
        if (b[0] is 0 or 127 || b[0] >= 224 || b[3] is 0 or 255) return null;
        return ip;
    }

    public void Dispose()
    {
        UdpClient? socket;
        lock (_gate) { socket = _socket; _socket = null; }
        socket?.Dispose();
    }
}
