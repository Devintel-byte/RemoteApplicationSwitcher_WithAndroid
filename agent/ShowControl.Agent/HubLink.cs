using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ShowControl.Agent;

/// <summary>
/// Keeps an outbound WebSocket to the hub (no port forwarding needed at the venue). The hub multiplexes
/// remote phones over it: each "open" becomes a pre-authenticated <see cref="ClientSession"/>.
///
/// Hub ↔ agent frames (JSON, field "t"):
///   agent → hub: auth {agentId,key,name,version,hostname} | enroll {code,agentId,name,version,hostname}
///                msg {sid,data} | close {sid} | info {name,version,hostname,apps,running,crashed}
///   hub → agent: authed {workspace} | enrolled {key,workspace} | error {message}
///                open {sid,user,role} | msg {sid,data} | close {sid} | ping
///                kiosk {path,locked,name}  (path null = show no kiosk)
///                hubs {hubs:[{url,kind}]}  other linked hubs to connect to as well (same key)
///
/// Binary messages agent → hub carry live-stream frames for one session:
///   [0x01][sid length (1 byte)][sid (ASCII)][frame bytes, see ScreenStream]
/// </summary>
public sealed class HubLink : IAsyncDisposable
{
    private readonly AgentHost _host;
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ClientWebSocket? _ws;

    public string State { get; private set; } = "off"; // off | connecting | connected | error
    public string? LastError { get; private set; }
    public string? Workspace { get; private set; }
    public int RemoteSessions => _sessions.Count;

    private readonly string? _fixedUrl;
    /// <summary>The hub this PC enrolled with (the others are linked hubs using the same key).</summary>
    public bool IsPrimary => _fixedUrl is null;
    public string? Url => Normalize(_fixedUrl ?? _host.Current.Hub.Url);

    /// <summary>
    /// A hub address as typed ("192.168.1.34:47900", "hub.example.com/") made into a full URL: http:// for
    /// addresses on this network, https:// otherwise. Older versions saved what was typed as is.
    /// </summary>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim().TrimEnd('/');
        if (!url.Contains("://")) url = (IsLanUrl("http://" + url) ? "http://" : "https://") + url;
        return url;
    }
    /// <summary>What this hub last said about the room kiosk (null = it hasn't said yet).</summary>
    public (string? Url, bool Locked, string? Name)? KioskOrder { get; private set; }
    public bool IsLan => IsLanUrl(Url);

    public HubLink(AgentHost host, string? url = null) { _host = host; _fixedUrl = url?.TrimEnd('/'); }

    /// <summary>On this network (localhost, a private address or a .local name): the kiosk prefers such a hub.</summary>
    public static bool IsLanUrl(string? url)
    {
        if (url is null || !Uri.TryCreate(url.Contains("://") ? url : "http://" + url, UriKind.Absolute, out var u)) return false;
        if (u.IsLoopback || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !u.Host.Contains('.')) return true;
        if (!System.Net.IPAddress.TryParse(u.Host, out var ip)) return false;
        var b = ip.MapToIPv4().GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    public void Start()
    {
        var hub = _host.Current.Hub;
        if (!hub.Enabled || string.IsNullOrWhiteSpace(Url) || string.IsNullOrWhiteSpace(hub.AgentKey))
        {
            State = "off";
            return;
        }
        _cts = new CancellationTokenSource();
        _loop = RunLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        try { _ws?.Abort(); } catch { }
        if (_loop is not null) { try { await _loop; } catch { } }
        _loop = null;
        CloseAllSessions();
        State = "off";
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        Start();
    }

    public static Uri AgentEndpoint(string hubUrl)
    {
        var u = hubUrl.Trim().TrimEnd('/');
        if (!u.Contains("://")) u = "http://" + u;
        var b = new UriBuilder(u);
        b.Scheme = b.Scheme switch { "https" or "wss" => "wss", _ => "ws" };
        if (b.Port == 80 && b.Scheme == "wss") b.Port = 443;
        b.Path = b.Path.TrimEnd('/') + "/agent";
        return b.Uri;
    }

    /// <summary>Exchanges a one-time enrollment code (from the hub's admin page) for this PC's hub key.</summary>
    public async Task<string?> EnrollAsync(string hubUrl, string code, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var ws = new ClientWebSocket();
        try { await ws.ConnectAsync(AgentEndpoint(hubUrl), timeout.Token); }
        catch (Exception ex) { return $"Can't reach the hub at {hubUrl}: {ex.Message}"; }

        var cfg = _host.Current;
        await SendFrame(ws, new JsonObject
        {
            ["t"] = "enroll", ["code"] = code.Trim(), ["agentId"] = cfg.AgentId, ["name"] = cfg.Name,
            ["version"] = AgentHost.Version, ["hostname"] = Environment.MachineName,
        }, timeout.Token);
        var reply = await ReceiveFrame(ws, timeout.Token);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }

        if (reply is null) return "The hub closed the connection";
        if ((string?)reply["t"] == "error") return (string?)reply["message"] ?? "Enrollment refused";
        if ((string?)reply["t"] != "enrolled" || reply["key"] is null) return "Unexpected answer from the hub";

        string key = (string)reply["key"]!;
        _host.Update(c => c.Hub = new HubSettings { Enabled = true, Url = Normalize(hubUrl), AgentKey = key, Also = c.Hub.Also.Where(x => !x.Auto).ToList() });
        Log.Info($"Enrolled with hub {hubUrl} (workspace '{(string?)reply["workspace"]}')");
        await RestartAsync();
        return null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        int attempt = 0;
        bool revoked = false;
        bool warned = false;
        int unknownTries = 0;
        while (!ct.IsCancellationRequested)
        {
            var hub = _host.Current.Hub;
            string url = Url ?? "";
            State = "connecting";
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                _ws = ws;
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(AgentEndpoint(url), connect.Token);
                }
                var cfg = _host.Current;
                await SendFrame(ws, new JsonObject
                {
                    ["t"] = "auth", ["agentId"] = cfg.AgentId, ["key"] = hub.AgentKey, ["name"] = cfg.Name,
                    ["version"] = AgentHost.Version, ["hostname"] = Environment.MachineName,
                }, ct);
                var reply = await ReceiveFrame(ws, ct);
                if (reply is null || (string?)reply["t"] != "authed")
                {
                    LastError = (string?)reply?["message"] ?? "Hub refused this PC";
                    State = "error";
                    Log.Warn($"Hub {url}: {LastError}");
                    if ((string?)reply?["code"] == "revoked")
                    {
                        if (IsPrimary) return; // stop retrying; needs re-enrollment
                        // A linked hub may not have this PC yet (it arrives with the next sync): try again later.
                        LastError = "This hub doesn't know this PC yet (it arrives when the hubs sync)";
                        unknownTries++;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(120, 3 << Math.Min(unknownTries, 6))), ct);
                        continue;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                Workspace = (string?)reply["workspace"];
                State = "connected";
                LastError = null;
                attempt = 0;
                warned = false;
                Log.Info($"Hub connected ({url}, workspace '{Workspace}')");
                await SendInfo(ws, ct);
                using var infoTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                var infoTask = InfoLoop(ws, infoTimer, ct);

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var frame = await ReceiveFrame(ws, ct);
                    if (frame is null) break;
                    if ((string?)frame["t"] == "error")
                    {
                        LastError = (string?)frame["message"] ?? "Hub reported an error";
                        Log.Warn($"Hub: {LastError}");
                        if ((string?)frame["code"] == "revoked") revoked = true;
                        break;
                    }
                    await HandleFrame(ws, frame);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                // An unreachable linked hub (e.g. the venue's site hub, seen from elsewhere) is normal: say so once.
                if (!warned || IsPrimary) Log.Warn($"Hub {url}: connection lost or unreachable: {ex.Message}");
                warned = true;
            }
            finally
            {
                _ws = null;
                CloseAllSessions();
                if (State == "connected") State = "error";
                _host.HubDisconnected(this);
            }

            if (ct.IsCancellationRequested) break;
            State = "error";
            if (revoked) return; // removed on the hub: wait for re-enrollment instead of retrying
            LastError ??= "Disconnected";
            attempt++;
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(IsPrimary ? 30 : 60, 2 << Math.Min(attempt, 5))), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task InfoLoop(ClientWebSocket ws, PeriodicTimer timer, CancellationToken ct)
    {
        try { while (await timer.WaitForNextTickAsync(ct) && ws.State == WebSocketState.Open) await SendInfo(ws, ct); }
        catch { /* connection ended */ }
    }

    private async Task SendInfo(ClientWebSocket ws, CancellationToken ct)
    {
        var apps = _host.Processes.Snapshot();
        await SendFrame(ws, new JsonObject
        {
            ["t"] = "info", ["name"] = _host.Current.Name, ["version"] = AgentHost.Version, ["hostname"] = Environment.MachineName,
            ["apps"] = apps.Count, ["running"] = apps.Count(a => a.State == AppState.Running),
            ["crashed"] = apps.Count(a => a.State == AppState.Crashed),
            ["macs"] = new JsonArray(WakeOnLan.LocalMacs().Select(m => (JsonNode)m!).ToArray()),
        }, ct);
    }

    private async Task HandleFrame(ClientWebSocket ws, JsonObject frame)
    {
        string? t = (string?)frame["t"];
        string? sid = (string?)frame["sid"];
        switch (t)
        {
            case "ping":
                await SendFrame(ws, new JsonObject { ["t"] = "pong" }, CancellationToken.None);
                break;

            case "open" when sid is not null:
            {
                string role = (string?)frame["role"] switch { Roles.Admin => Roles.Admin, Roles.Viewer => Roles.Viewer, _ => Roles.Operator };
                string user = (string?)frame["user"] ?? "hub user";
                var transport = new HubTransport(this, sid);
                var session = new ClientSession(transport, _host, $"hub:{user}", preAuthName: user, role: role);
                _sessions[sid] = session;
                Log.Info($"Hub: remote session for '{user}' ({role})");
                break;
            }

            // The hub tells this PC which room kiosk to show (path null = none).
            case "kiosk":
            {
                string? path = (string?)frame["path"];
                string? url = path is null ? null : (Url ?? "") + path;
                KioskOrder = (url, (bool?)frame["locked"] ?? true, (string?)frame["name"]);
                _host.ChooseKiosk(fromHub: true);
                break;
            }

            // Linked hubs: connect to these too (same key), e.g. the venue's site hub and the cloud hub.
            case "hubs":
            {
                var list = (frame["hubs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Select(h => (Url: ((string?)h["url"] ?? "").Trim().TrimEnd('/'), Kind: (string?)h["kind"] ?? "site"))
                    .Where(h => h.Url.StartsWith("http://") || h.Url.StartsWith("https://")).Take(8).ToList();
                _host.HubsAnnounced(this, list);
                break;
            }

            case "msg" when sid is not null:
                if (_sessions.TryGetValue(sid, out var s) && (string?)frame["data"] is { } data) await s.HandleAsync(data);
                break;

            case "close" when sid is not null:
                if (_sessions.TryRemove(sid, out var closed)) closed.Closed();
                break;
        }
    }

    internal async Task SendToHub(string sid, string data)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) throw new InvalidOperationException("Hub not connected");
        await SendFrame(ws, new JsonObject { ["t"] = "msg", ["sid"] = sid, ["data"] = data }, CancellationToken.None);
    }

    internal async Task SendBinaryToHub(string sid, byte[] data)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) throw new InvalidOperationException("Hub not connected");
        byte[] sidBytes = Encoding.ASCII.GetBytes(sid);
        var buffer = new byte[2 + sidBytes.Length + data.Length];
        buffer[0] = 1;
        buffer[1] = (byte)sidBytes.Length;
        sidBytes.CopyTo(buffer, 2);
        data.CopyTo(buffer, 2 + sidBytes.Length);
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(buffer, WebSocketMessageType.Binary, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    internal async Task CloseFromAgent(string sid)
    {
        if (_sessions.TryRemove(sid, out var s)) s.Closed();
        var ws = _ws;
        if (ws is not null && ws.State == WebSocketState.Open)
            await SendFrame(ws, new JsonObject { ["t"] = "close", ["sid"] = sid }, CancellationToken.None);
    }

    internal bool IsOpen => _ws?.State == WebSocketState.Open;

    private void CloseAllSessions()
    {
        foreach (var sid in _sessions.Keys.ToList())
            if (_sessions.TryRemove(sid, out var s)) s.Closed();
    }

    private async Task SendFrame(ClientWebSocket ws, JsonObject frame, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private static async Task<JsonObject?> ReceiveFrame(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 4 * 1024 * 1024) return null;
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray()) as JsonObject;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class HubTransport(HubLink link, string sid) : ISessionTransport
    {
        public bool IsOpen => link.IsOpen && link._sessions.ContainsKey(sid);
        public Task SendAsync(string json) => link.SendToHub(sid, json);
        public Task SendBinaryAsync(byte[] data) => link.SendBinaryToHub(sid, data);
        public Task CloseAsync(string reason) => link.CloseFromAgent(sid);
    }
}
