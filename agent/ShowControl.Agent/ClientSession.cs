using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ShowControl.Agent;

public static class Wire
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object message) => JsonSerializer.Serialize(message, message.GetType(), Options);
}

public static class Roles
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Viewer = "viewer";

    /// <summary>Message types a viewer may send: look, don't touch.</summary>
    public static readonly HashSet<string> ViewerAllowed = new() { "ping", "hello", "get_state", "screenshot", "stream_start", "stream_stop", "stream_ack" };
}

/// <summary>Authenticated sessions that receive live events.</summary>
public sealed class SessionHub
{
    private readonly ConcurrentDictionary<ClientSession, byte> _sessions = new();

    public int Count => _sessions.Count;
    public IReadOnlyCollection<ClientSession> All => _sessions.Keys.ToList();
    public void Add(ClientSession s) => _sessions[s] = 0;
    public void Remove(ClientSession s) => _sessions.TryRemove(s, out _);

    public void Broadcast(object message)
    {
        if (_sessions.IsEmpty) return;
        string json = Wire.Serialize(message);
        foreach (var s in _sessions.Keys) _ = s.SendRawAsync(json);
    }
}

/// <summary>Where a session's messages go: a direct WebSocket, or a channel relayed through the hub.</summary>
public interface ISessionTransport
{
    bool IsOpen { get; }
    Task SendAsync(string json);
    /// <summary>Sends one binary message (live stream frames).</summary>
    Task SendBinaryAsync(byte[] data);
    Task CloseAsync(string reason);
}

public sealed class WebSocketTransport(WebSocket socket) : ISessionTransport
{
    private const int MaxMessageBytes = 64 * 1024;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsOpen => socket.State == WebSocketState.Open;

    public async Task SendAsync(string json)
    {
        if (!IsOpen) return;
        await _sendLock.WaitAsync();
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        if (!IsOpen) return;
        await _sendLock.WaitAsync();
        try { await socket.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    public async Task CloseAsync(string reason)
    {
        try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None); } catch { }
    }

    /// <summary>Reads frames until the socket closes, handing each text message to the session.</summary>
    public async Task PumpAsync(ClientSession session, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) { await CloseAsync("Too big"); break; }
            if (!result.EndOfMessage) continue;
            string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            if (result.MessageType == WebSocketMessageType.Text) await session.HandleAsync(text);
        }
    }
}

/// <summary>
/// One controller connection: a phone on the LAN, the dashboard, or a phone relayed by the hub.
/// Relayed sessions arrive pre-authenticated (the hub checked the user) with the user's role.
/// </summary>
public sealed class ClientSession
{
    private const int MaxBadPins = 5;

    private readonly ISessionTransport _transport;
    private readonly AgentHost _host;
    private readonly string _remote;
    private readonly string? _preAuthName;
    private bool _authenticated;
    private DateTime _lastShot = DateTime.MinValue;
    private int _badPins;
    private ScreenStream? _stream;
    private readonly object _streamLock = new();

    public string ClientName { get; private set; } = "?";
    /// <summary>The paired phone this session signed in as (LAN only).</summary>
    public string? PairedClientId { get; private set; }
    public string Role { get; private set; }
    public string Remote => _remote;
    public bool ViaHub => _preAuthName is not null;

    public ClientSession(ISessionTransport transport, AgentHost host, string remote, string? preAuthName = null, string role = Roles.Operator)
    {
        _transport = transport;
        _host = host;
        _remote = remote;
        _preAuthName = preAuthName;
        Role = role;
    }

    public static async Task RunWebSocketAsync(WebSocket socket, AgentHost host, string remote, CancellationToken ct)
    {
        var transport = new WebSocketTransport(socket);
        var session = new ClientSession(transport, host, remote);
        Log.Info($"Connection from {remote}");
        try { await transport.PumpAsync(session, ct); }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { Log.Error($"Session {remote} error", ex); }
        finally { session.Closed(); }
    }

    public void Closed()
    {
        _ = StopStreamAsync();
        _host.Hub.Remove(this);
        Log.Info($"Disconnected {_remote} ({ClientName})");
    }

    public async Task HandleAsync(string text)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { await SendAsync(Error(null, "bad_json", "Message is not valid JSON")); return; }
        if (msg is null) return;

        string type = Str(msg, "type") ?? "";
        string? id = Str(msg, "id");

        switch (type)
        {
            case "ping":
                await SendAsync(new { type = "pong", id });
                return;

            case "pair":
            {
                if (ViaHub) { await SendAsync(Error(id, "bad_request", "Pairing isn't needed through the hub")); return; }
                string? token = _host.Pair(Str(msg, "pin") ?? "", Str(msg, "clientName") ?? "Controller");
                if (token is null)
                {
                    _badPins++;
                    Log.Warn($"Wrong pairing PIN from {_remote} ({_badPins}/{MaxBadPins})");
                    await SendAsync(Error(id, "unauthorized", "Wrong pairing PIN"));
                    if (_badPins >= MaxBadPins) await _transport.CloseAsync("Too many attempts");
                    return;
                }
                await SendAsync(new { type = "paired", id, token });
                return;
            }

            case "hello":
            {
                if (_preAuthName is not null)
                {
                    ClientName = Str(msg, "clientName") is { Length: > 0 } cn ? $"{_preAuthName} ({cn})" : _preAuthName;
                }
                else
                {
                    string token = Str(msg, "token") ?? "";
                    if (_host.Admin.IsSessionToken(token))
                    {
                        Role = Roles.Admin;
                        ClientName = "Dashboard";
                    }
                    else if (_host.Authenticate(token) is { } client)
                    {
                        ClientName = Str(msg, "clientName") ?? client.Name;
                        PairedClientId = client.Id;
                        Role = client.Role == Roles.Viewer ? Roles.Viewer : Roles.Operator;
                    }
                    else
                    {
                        await SendAsync(Error(id, "unauthorized", "Unknown token. Pair again with the PIN."));
                        return;
                    }
                }
                _authenticated = true;
                _host.Hub.Add(this);
                Log.Info($"{_remote} authenticated as '{ClientName}' ({Role})");
                await SendAsync(_host.BuildState("welcome", id, Role));
                return;
            }
        }

        if (!_authenticated)
        {
            await SendAsync(Error(id, "unauthorized", "Send hello with a token first"));
            return;
        }
        if (Role == Roles.Viewer && !Roles.ViewerAllowed.Contains(type))
        {
            await SendAsync(Error(id, "forbidden", "Viewers can watch but not control"));
            return;
        }

        // Linked hubs mark scheduled commands; if two hubs send the same one, act on the first only.
        if (Str(msg, "dedupe") is { Length: > 0 } dedupe && !Dedupe.FirstTime(dedupe))
        {
            Log.Info($"{ClientName}: {type} skipped (already done: {dedupe})");
            await SendAsync(new { type = "ack", id, duplicate = true });
            return;
        }

        switch (type)
        {
            case "get_state":
                await SendAsync(_host.BuildState("state", id, Role));
                break;

            case "start_app":
            case "stop_app":
            case "restart_app":
            {
                string appId = Str(msg, "appId") ?? "";
                if (!_host.Processes.Exists(appId)) { await SendAsync(Error(id, "unknown_app", $"No app '{appId}' on this PC")); break; }
                await SendAsync(new { type = "ack", id });
                Log.Info($"{ClientName}: {type} {appId}");
                _ = type switch
                {
                    "start_app" => _host.Processes.StartAsync(appId),
                    "stop_app" => _host.Processes.StopAsync(appId, Bool(msg, "force")),
                    _ => _host.Processes.RestartAsync(appId),
                };
                break;
            }

            case "stop_all":
                await SendAsync(new { type = "ack", id });
                Log.Info($"{ClientName}: stop_all");
                _ = _host.Processes.StopAllAsync(Bool(msg, "force"));
                break;

            case "run_scene":
            {
                string sceneId = Str(msg, "sceneId") ?? "";
                var scene = _host.Current.Scenes.FirstOrDefault(s => s.Id.Equals(sceneId, StringComparison.OrdinalIgnoreCase));
                if (scene is null) { await SendAsync(Error(id, "unknown_scene", $"No scene '{sceneId}' on this PC")); break; }
                await SendAsync(new { type = "ack", id });
                Log.Info($"{ClientName}: run_scene {sceneId}");
                _host.Scenes.Run(scene);
                break;
            }

            case "screenshot":
            {
                var cap = _host.ScreenCapture;
                if (cap is null) { await SendAsync(Error(id, "unsupported", "Screen capture is not available on this agent")); break; }
                if (DateTime.UtcNow - _lastShot < TimeSpan.FromMilliseconds(800)) { await SendAsync(Error(id, "busy", "Screenshots are limited to about one per second")); break; }
                _lastShot = DateTime.UtcNow;
                int display = Int(msg, "display") ?? 0;
                int maxWidth = Int(msg, "maxWidth") ?? 640;
                try
                {
                    var shot = await Task.Run(() => cap.Grab(display, maxWidth, 70));
                    await SendAsync(new
                    {
                        type = "screenshot", id, display,
                        displays = _host.SafeDisplays(),
                        width = shot.Width, height = shot.Height,
                        jpeg = Convert.ToBase64String(shot.Jpeg),
                    });
                }
                catch (InvalidOperationException ex) { await SendAsync(Error(id, "capture_failed", ex.Message)); }
                catch (Exception ex) { Log.Error("Screenshot failed", ex); await SendAsync(Error(id, "capture_failed", "Screenshot failed")); }
                break;
            }

            case "stream_start":
            {
                var cap = _host.ScreenCapture;
                if (cap is null) { await SendAsync(Error(id, "unsupported", "Screen capture is not available on this agent")); break; }
                int display = Int(msg, "display") ?? 0;
                await StopStreamAsync();
                var stream = ScreenStream.TryStart(cap, display, Int(msg, "maxWidth") ?? 640, Int(msg, "fps") ?? 15, Int(msg, "quality") ?? 60,
                    data => SendBinaryAsync(data), m => SendAsync(m));
                if (stream is null) { await SendAsync(Error(id, "busy", $"This PC is already streaming to {ScreenStream.MaxActive} viewers")); break; }
                lock (_streamLock) _stream = stream;
                await SendAsync(new
                {
                    type = "stream_started", id, display, displays = _host.SafeDisplays(),
                    fps = stream.Fps, maxWidth = stream.MaxWidth, window = ScreenStream.Window,
                });
                break;
            }

            case "stream_stop":
                await StopStreamAsync();
                await SendAsync(new { type = "ack", id });
                break;

            case "stream_ack":
            {
                ScreenStream? current;
                lock (_streamLock) current = _stream;
                long seq = Long(msg, "seq") ?? 0;
                if (current is not null && seq > 0 && seq <= uint.MaxValue) current.Ack((uint)seq);
                break;
            }

            case "wake":
            {
                string mac = Str(msg, "mac") ?? "";
                try { WakeOnLan.ParseMac(mac); }
                catch (ArgumentException ex) { await SendAsync(Error(id, "bad_request", ex.Message)); break; }
                await SendAsync(new { type = "ack", id });
                Log.Info($"{ClientName}: wake {mac}");
                _ = WakeOnLan.SendAsync(mac, CancellationToken.None);
                break;
            }

            case "power":
            {
                if (!_host.Current.AllowPower) { await SendAsync(Error(id, "forbidden", "Power control is turned off on this PC (Settings → Allow restart and shut down)")); break; }
                string action = Str(msg, "action") ?? "";
                string? args = action switch { "restart" => "/r /t 5", "shutdown" => "/s /t 5", _ => null };
                if (args is null) { await SendAsync(Error(id, "bad_request", "action must be restart or shutdown")); break; }
                await SendAsync(new { type = "ack", id });
                Log.Warn($"{ClientName}: power {action}");
                Process.Start(new ProcessStartInfo("shutdown", args) { UseShellExecute = false, CreateNoWindow = true });
                break;
            }

            default:
                await SendAsync(Error(id, "unknown_type", $"Unknown message type '{type}'"));
                break;
        }
    }

    private static object Error(string? id, string code, string message) => new { type = "error", id, code, message };

    internal static string? Str(JsonObject o, string key)
    {
        try { return o[key]?.GetValue<string>(); }
        catch (InvalidOperationException) { return o[key]?.ToJsonString(); }
    }

    private static int? Int(JsonObject o, string key)
    {
        try { return o[key]?.GetValue<int>(); }
        catch (Exception) { return int.TryParse(Str(o, key), out int v) ? v : null; }
    }

    private static long? Long(JsonObject o, string key)
    {
        try { return o[key]?.GetValue<long>(); }
        catch (Exception) { return long.TryParse(Str(o, key), out long v) ? v : null; }
    }

    private async Task StopStreamAsync()
    {
        ScreenStream? old;
        lock (_streamLock) { old = _stream; _stream = null; }
        if (old is not null) await old.DisposeAsync();
    }

    private static bool Bool(JsonObject o, string key)
    {
        try { return o[key]?.GetValue<bool>() ?? false; }
        catch (Exception) { return false; }
    }

    private Task SendAsync(object message) => SendRawAsync(Wire.Serialize(message));

    public async Task SendRawAsync(string json)
    {
        if (!_transport.IsOpen) { _host.Hub.Remove(this); return; }
        try { await _transport.SendAsync(json); }
        catch { _host.Hub.Remove(this); }
    }

    /// <summary>Called from the stream loop: a failure ends that loop (cleanup happens in <see cref="Closed"/>).</summary>
    private async Task SendBinaryAsync(byte[] data)
    {
        if (!_transport.IsOpen) throw new StreamClosedException();
        try { await _transport.SendBinaryAsync(data); }
        catch (Exception ex) { throw new StreamClosedException(ex); }
    }

    public Task CloseAsync(string reason) => _transport.CloseAsync(reason);
}

/// <summary>Remembers recent scheduled commands (by the key the hub gives them) for 15 minutes.</summary>
internal static class Dedupe
{
    private static readonly Dictionary<string, DateTime> Seen = new();

    public static bool FirstTime(string key)
    {
        lock (Seen)
        {
            var now = DateTime.UtcNow;
            foreach (var old in Seen.Where(x => x.Value < now).Select(x => x.Key).ToList()) Seen.Remove(old);
            if (Seen.ContainsKey(key)) return false;
            Seen[key] = now.AddMinutes(15);
            return true;
        }
    }
}
