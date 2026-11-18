using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShowControl.Agent;

/// <summary>OSC 1.0 messages over UDP: TouchDesigner (OSC In), Resolume, MadMapper, Unity, lighting desks…</summary>
public static partial class Osc
{
    public static byte[] BuildMessage(string address, IEnumerable<JsonElement>? args)
    {
        var tags = new StringBuilder(",");
        using var payload = new MemoryStream();
        foreach (var a in args ?? Enumerable.Empty<JsonElement>())
        {
            switch (a.ValueKind)
            {
                case JsonValueKind.Number when IsWhole(a, out int i):
                    tags.Append('i');
                    WriteInt(payload, i);
                    break;
                case JsonValueKind.Number:
                    tags.Append('f');
                    var fb = new byte[4];
                    BinaryPrimitives.WriteSingleBigEndian(fb, (float)a.GetDouble());
                    payload.Write(fb);
                    break;
                case JsonValueKind.True: tags.Append('T'); break;
                case JsonValueKind.False: tags.Append('F'); break;
                case JsonValueKind.Null: tags.Append('N'); break;
                default:
                    tags.Append('s');
                    WritePadded(payload, a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : a.GetRawText());
                    break;
            }
        }

        using var msg = new MemoryStream();
        WritePadded(msg, address);
        WritePadded(msg, tags.ToString());
        payload.WriteTo(msg);
        return msg.ToArray();
    }

    /// <summary>
    /// Parses typed arguments: comma-separated; "quoted" → string, true/false, numbers kept exactly as typed
    /// (so 1.0 is sent as a float and 1 as an int), anything else → string.
    /// </summary>
    public static List<JsonElement> ParseArgsText(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char ch in text)
        {
            if (ch == '"') quoted = !quoted;
            if (ch == ',' && !quoted) { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(ch);
        }
        parts.Add(current.ToString());

        var json = new StringBuilder("[");
        foreach (var raw in parts)
        {
            string v = raw.Trim();
            if (v.Length == 0) continue;
            if (json.Length > 1) json.Append(',');
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') json.Append(JsonSerializer.Serialize(v[1..^1]));
            else if (v is "true" or "false") json.Append(v);
            else if (NumberRegex().IsMatch(v)) json.Append(v.StartsWith('.') ? "0" + v : v.StartsWith("-.") ? "-0" + v[1..] : v);
            else json.Append(JsonSerializer.Serialize(v));
        }
        json.Append(']');
        using var doc = JsonDocument.Parse(json.ToString());
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public static string FormatArgs(IEnumerable<JsonElement>? args) =>
        string.Join(", ", (args ?? Enumerable.Empty<JsonElement>()).Select(e =>
            e.ValueKind == JsonValueKind.String ? "\"" + e.GetString() + "\"" : e.GetRawText()));

    [System.Text.RegularExpressions.GeneratedRegex(@"^-?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$")]
    private static partial System.Text.RegularExpressions.Regex NumberRegex();

    public static async Task SendAsync(string host, int port, string address, IEnumerable<JsonElement>? args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address) || !address.StartsWith('/')) throw new ArgumentException("OSC address must start with /");
        byte[] data = BuildMessage(address, args);
        using var udp = new UdpClient();
        await udp.SendAsync(data, host, port, ct);
    }

    private static bool IsWhole(JsonElement e, out int value)
    {
        string raw = e.GetRawText();
        value = 0;
        return !raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E') && e.TryGetInt32(out value);
    }

    private static void WriteInt(Stream s, int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        s.Write(b);
    }

    private static void WritePadded(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        s.Write(bytes);
        int pad = 4 - (bytes.Length % 4); // always at least one null terminator
        s.Write(new byte[pad]);
    }
}

public static class UdpText
{
    public static async Task SendAsync(string host, int port, string text, CancellationToken ct)
    {
        using var udp = new UdpClient();
        await udp.SendAsync(Encoding.UTF8.GetBytes(text), host, port, ct);
    }
}

public static class HttpCue
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Returns null on success or an error message.</summary>
    public static async Task<string?> SendAsync(string url, string? method, string? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod(string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant()), url);
        if (!string.IsNullOrEmpty(body))
        {
            string trimmed = body.TrimStart();
            string type = trimmed.StartsWith('{') || trimmed.StartsWith('[') ? "application/json" : "text/plain";
            req.Content = new StringContent(body, Encoding.UTF8, type);
        }
        using var res = await Client.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode} from {url}";
    }
}

/// <summary>Minimal obs-websocket v5 client: connect, authenticate, send one request, disconnect.</summary>
public static class ObsClient
{
    /// <summary>Returns null on success or an error message.</summary>
    public static async Task<string?> RequestAsync(ObsSettings obs, string requestType, JsonObject? data, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;

        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri($"ws://{obs.Host}:{obs.Port}"), token);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            return $"OBS not reachable on {obs.Host}:{obs.Port}. Is OBS running with WebSocket server enabled?";
        }

        var hello = await ReceiveAsync(ws, token);
        if (hello?["op"]?.GetValue<int>() != 0) return "Unexpected greeting from OBS";

        var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
        var auth = hello["d"]?["authentication"];
        if (auth is not null)
        {
            if (string.IsNullOrEmpty(obs.Password)) return "OBS asks for a password: set obs.password in agent.json";
            identify["authentication"] = AuthString(obs.Password, auth["salt"]!.GetValue<string>(), auth["challenge"]!.GetValue<string>());
        }
        await SendAsync(ws, new JsonObject { ["op"] = 1, ["d"] = identify }, token);

        var identified = await ReceiveAsync(ws, token);
        if (identified?["op"]?.GetValue<int>() != 2)
            return ws.CloseStatus == (WebSocketCloseStatus)4009 ? "OBS rejected the password" : "OBS did not accept the connection";

        string requestId = Guid.NewGuid().ToString("N");
        await SendAsync(ws, new JsonObject
        {
            ["op"] = 6,
            ["d"] = new JsonObject
            {
                ["requestType"] = requestType,
                ["requestId"] = requestId,
                ["requestData"] = data?.DeepClone() ?? new JsonObject(),
            },
        }, token);

        while (true)
        {
            var msg = await ReceiveAsync(ws, token);
            if (msg is null) return "OBS closed the connection";
            if (msg["op"]?.GetValue<int>() != 7 || msg["d"]?["requestId"]?.GetValue<string>() != requestId) continue;
            var status = msg["d"]!["requestStatus"];
            bool ok = status?["result"]?.GetValue<bool>() ?? false;
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
            return ok ? null : $"OBS: {status?["comment"]?.GetValue<string>() ?? $"{requestType} failed (code {status?["code"]})"}";
        }
    }

    public static string AuthString(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private static async Task SendAsync(ClientWebSocket ws, JsonObject msg, CancellationToken ct) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonObject?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buffer, ct); }
            catch (WebSocketException) { return null; }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray()) as JsonObject;
    }
}

/// <summary>Wake-on-LAN magic packets, sent to every IPv4 broadcast address this PC can see.</summary>
public static class WakeOnLan
{
    public static byte[] ParseMac(string mac)
    {
        string hex = new(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) throw new ArgumentException($"'{mac}' is not a MAC address");
        return Convert.FromHexString(hex);
    }

    public static byte[] MagicPacket(byte[] mac)
    {
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) Buffer.BlockCopy(mac, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    public static async Task SendAsync(string mac, CancellationToken ct, int port = 9, IEnumerable<IPAddress>? targets = null)
    {
        var packet = MagicPacket(ParseMac(mac));
        using var udp = new UdpClient { EnableBroadcast = true };
        foreach (var addr in targets ?? BroadcastAddresses())
        {
            try { await udp.SendAsync(packet, new IPEndPoint(addr, port), ct); }
            catch (SocketException ex) { Log.Warn($"WoL to {addr} failed: {ex.Message}"); }
        }
    }

    public static List<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add(new IPAddress(bc));
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Could not list broadcast addresses: {ex.Message}"); }
        return list.Distinct().ToList();
    }

    /// <summary>MACs of this PC's active wired/wireless adapters, for the phone to store.</summary>
    public static List<string> LocalMacs()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) continue;
                if (!ni.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
                var bytes = ni.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length != 6) continue;
                // Wired first: Wake-on-LAN usually only works over Ethernet.
                string mac = string.Join(":", bytes.Select(b => b.ToString("X2")));
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) list.Add(mac); else list.Insert(0, mac);
            }
        }
        catch (Exception ex) { Log.Warn($"Could not list MACs: {ex.Message}"); }
        return list;
    }
}
