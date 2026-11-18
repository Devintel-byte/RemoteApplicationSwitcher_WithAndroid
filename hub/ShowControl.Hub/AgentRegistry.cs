using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ShowControl.Hub;

/// <summary>Something on the far side of a relayed session: a phone's WebSocket or a schedule.</summary>
public interface IRelayClient
{
    Task DeliverAsync(string data);
    /// <summary>A live-stream frame from the PC. Clients that don't stream ignore it.</summary>
    Task DeliverBinaryAsync(ArraySegment<byte> frame) => Task.CompletedTask;
    Task AgentGoneAsync();
}

public sealed class AgentConn
{
    public required string AgentId { get; init; }
    public required WebSocket Socket { get; init; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public ConcurrentDictionary<string, IRelayClient> Sessions { get; } = new();
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public string? Version { get; set; }
    public string? Hostname { get; set; }
    public int Apps { get; set; }
    public int Running { get; set; }
    public int Crashed { get; set; }
}

/// <summary>
/// PCs connected to the hub over their outbound WebSocket (see the agent's HubLink.cs for the frame format).
/// Phones and schedules talk to a PC through sessions multiplexed over that one connection.
/// </summary>
public sealed class AgentRegistry(HubStore store)
{
    private readonly ConcurrentDictionary<string, AgentConn> _online = new();

    public bool IsOnline(string agentId) => _online.ContainsKey(agentId);

    /// <summary>Raised when a PC has signed in (e.g. to tell it which kiosk to show).</summary>
    public event Func<string, Task>? AgentConnected;
    public event Func<string, Task>? AgentDisconnected;

    /// <summary>Sends a control frame to every connected PC (e.g. the other hubs they should connect to).</summary>
    public async Task BroadcastAsync(Func<string, JsonObject> frameFor)
    {
        foreach (var c in Online)
        {
            try { await SendFrame(c.Socket, c.SendLock, frameFor(c.AgentId)); } catch { }
        }
    }

    public async Task SendToAsync(string agentId, JsonObject frame)
    {
        if (Get(agentId) is not { } c) return;
        try { await SendFrame(c.Socket, c.SendLock, frame); } catch { }
    }
    public AgentConn? Get(string agentId) => _online.TryGetValue(agentId, out var c) ? c : null;
    public IReadOnlyCollection<AgentConn> Online => _online.Values.ToList();

    // ---------- the agent side ----------

    public async Task HandleAgentAsync(WebSocket ws, string remote, CancellationToken ct)
    {
        JsonObject? first;
        using (var hello = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            hello.CancelAfter(TimeSpan.FromSeconds(15));
            try { first = await ReceiveFrame(ws, hello.Token); }
            catch { return; }
        }
        if (first is null) return;

        string t = (string?)first["t"] ?? "";
        string agentId = ((string?)first["agentId"] ?? "").Trim();
        string name = ((string?)first["name"] ?? "PC").Trim();
        if (agentId.Length is < 8 or > 64) { await Refuse(ws, "Bad agent id"); return; }

        if (t == "enroll")
        {
            string code = ((string?)first["code"] ?? "").Trim().ToUpperInvariant();
            string? key = store.Write(d =>
            {
                d.EnrollCodes.RemoveAll(c => c.ExpiresAt < DateTime.UtcNow);
                var match = d.EnrollCodes.FirstOrDefault(c => HubStore.FixedEquals(c.Code, code));
                if (match is null) return null;
                d.EnrollCodes.Remove(match);
                string k = HubStore.NewToken();
                d.Agents.RemoveAll(a => a.AgentId == agentId);
                d.Agents.Add(new EnrolledAgent
                {
                    AgentId = agentId, Name = name, Hostname = (string?)first["hostname"],
                    KeyHash = HubStore.Sha256(k), EnrolledAt = DateTime.UtcNow,
                });
                return k;
            });
            if (key is null) { await Refuse(ws, "That enrollment code is wrong or has expired. Make a new one on the hub's PCs page."); return; }
            store.Audit("system", "pc_enrolled", agentId, name);
            await SendFrame(ws, new SemaphoreSlim(1, 1), new JsonObject { ["t"] = "enrolled", ["key"] = key, ["workspace"] = store.Read(d => d.Workspace.Name) });
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
            return;
        }

        if (t != "auth") { await Refuse(ws, "Expected auth"); return; }
        string keyHash = HubStore.Sha256((string?)first["key"] ?? "");
        var enrolled = store.Read(d => d.Agents.FirstOrDefault(a => a.AgentId == agentId));
        if (enrolled is null || !HubStore.FixedEquals(enrolled.KeyHash, keyHash))
        {
            await Refuse(ws, "This PC isn't enrolled with the hub (or was removed). Enroll it again from its dashboard.", "revoked");
            return;
        }

        var conn = new AgentConn { AgentId = agentId, Socket = ws, Version = (string?)first["version"], Hostname = (string?)first["hostname"] };
        if (_online.TryRemove(agentId, out var old)) await DropAsync(old, "Replaced by a new connection");
        _online[agentId] = conn;
        store.Write(d =>
        {
            var a = d.Agents.FirstOrDefault(x => x.AgentId == agentId);
            if (a is null) return;
            a.LastSeen = DateTime.UtcNow;
            a.Hostname = conn.Hostname ?? a.Hostname;
        });
        await SendFrame(ws, conn.SendLock, new JsonObject { ["t"] = "authed", ["workspace"] = store.Read(d => d.Workspace.Name) });
        Console.WriteLine($"[hub] PC online: {enrolled.Name} ({agentId}) from {remote}");
        if (AgentConnected is { } connected) { try { await connected(agentId); } catch (Exception ex) { Console.WriteLine($"[hub] {ex.Message}"); } }

        var pinger = PingLoop(conn, ct);
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var raw = await ReceiveMessage(ws, ct, 8 * 1024 * 1024);
                if (raw is null) break;
                var (kind, bytes) = raw.Value;
                if (kind == WebSocketMessageType.Binary)
                {
                    // [0x01][sid length][sid][frame]: hand the frame to that session's phone untouched.
                    if (bytes.Count < 3 || bytes[0] != 1) continue;
                    int sidLen = bytes[1];
                    if (bytes.Count < 2 + sidLen) continue;
                    string bsid = Encoding.ASCII.GetString(bytes.Array!, bytes.Offset + 2, sidLen);
                    if (conn.Sessions.TryGetValue(bsid, out var viewer))
                        _ = viewer.DeliverBinaryAsync(bytes.Slice(2 + sidLen)); // don't let one slow phone hold up this PC's other sessions
                    continue;
                }
                JsonObject? frame;
                try { frame = JsonNode.Parse(bytes.AsSpan()) as JsonObject; } catch { frame = null; }
                if (frame is null) continue;
                string? ft = (string?)frame["t"];
                string? sid = (string?)frame["sid"];
                switch (ft)
                {
                    case "msg" when sid is not null && conn.Sessions.TryGetValue(sid, out var client):
                        if ((string?)frame["data"] is { } data) _ = client.DeliverAsync(data);
                        break;
                    case "close" when sid is not null:
                        if (conn.Sessions.TryRemove(sid, out var gone)) _ = gone.AgentGoneAsync();
                        break;
                    case "info":
                        conn.Version = (string?)frame["version"] ?? conn.Version;
                        conn.Hostname = (string?)frame["hostname"] ?? conn.Hostname;
                        conn.Apps = (int?)frame["apps"] ?? 0;
                        conn.Running = (int?)frame["running"] ?? 0;
                        conn.Crashed = (int?)frame["crashed"] ?? 0;
                        var macs = frame["macs"]?.AsArray().Select(n => (string?)n).OfType<string>().ToList();
                        string? infoName = (string?)frame["name"];
                        store.Write(d =>
                        {
                            var a = d.Agents.FirstOrDefault(x => x.AgentId == agentId);
                            if (a is null) return;
                            a.LastSeen = DateTime.UtcNow;
                            if (macs is { Count: > 0 }) a.Macs = macs;
                            if (!string.IsNullOrWhiteSpace(infoName) && !a.NameLocked) a.Name = infoName!;
                        });
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (_online.TryGetValue(agentId, out var current) && ReferenceEquals(current, conn)) _online.TryRemove(agentId, out _);
            await DropAsync(conn, null);
            store.Write(d => { var a = d.Agents.FirstOrDefault(x => x.AgentId == agentId); if (a is not null) a.LastSeen = DateTime.UtcNow; });
            Console.WriteLine($"[hub] PC offline: {enrolled.Name} ({agentId})");
            if (AgentDisconnected is { } gone) { try { await gone(agentId); } catch { } }
        }
    }

    private static async Task PingLoop(AgentConn conn, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(25));
            while (await timer.WaitForNextTickAsync(ct) && conn.Socket.State == WebSocketState.Open)
                await SendFrame(conn.Socket, conn.SendLock, new JsonObject { ["t"] = "ping" });
        }
        catch { }
    }

    private static async Task DropAsync(AgentConn conn, string? reason)
    {
        foreach (var sid in conn.Sessions.Keys.ToList())
            if (conn.Sessions.TryRemove(sid, out var c)) _ = c.AgentGoneAsync();
        if (reason is not null)
        {
            try { await conn.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None); } catch { }
        }
    }

    /// <summary>Disconnects a PC that was removed from the hub.</summary>
    public async Task KickAsync(string agentId)
    {
        if (!_online.TryRemove(agentId, out var conn)) return;
        try { await SendFrame(conn.Socket, conn.SendLock, new JsonObject { ["t"] = "error", ["code"] = "revoked", ["message"] = "This PC was removed from the hub. Enroll it again to reconnect." }); } catch { }
        await DropAsync(conn, "Removed from hub");
    }

    // ---------- sessions ----------

    /// <summary>Opens a relayed session. Null if the PC is offline.</summary>
    public async Task<string?> OpenAsync(string agentId, string user, string role, IRelayClient client)
    {
        var conn = Get(agentId);
        if (conn is null) return null;
        string sid = HubStore.NewToken(12);
        conn.Sessions[sid] = client;
        try
        {
            await SendFrame(conn.Socket, conn.SendLock, new JsonObject { ["t"] = "open", ["sid"] = sid, ["user"] = user, ["role"] = role });
            return sid;
        }
        catch
        {
            conn.Sessions.TryRemove(sid, out _);
            return null;
        }
    }

    public async Task<bool> SendAsync(string agentId, string sid, string data)
    {
        var conn = Get(agentId);
        if (conn is null || !conn.Sessions.ContainsKey(sid)) return false;
        try
        {
            await SendFrame(conn.Socket, conn.SendLock, new JsonObject { ["t"] = "msg", ["sid"] = sid, ["data"] = data });
            return true;
        }
        catch { return false; }
    }

    public async Task CloseAsync(string agentId, string sid)
    {
        var conn = Get(agentId);
        if (conn is null || !conn.Sessions.TryRemove(sid, out _)) return;
        try { await SendFrame(conn.Socket, conn.SendLock, new JsonObject { ["t"] = "close", ["sid"] = sid }); } catch { }
    }

    // ---------- framing ----------

    private static async Task Refuse(WebSocket ws, string message, string? code = null)
    {
        try
        {
            var f = new JsonObject { ["t"] = "error", ["message"] = message };
            if (code is not null) f["code"] = code;
            await SendFrame(ws, new SemaphoreSlim(1, 1), f);
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "refused", CancellationToken.None);
        }
        catch { }
    }

    public static async Task SendFrame(WebSocket ws, SemaphoreSlim gate, JsonObject frame)
    {
        await gate.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { gate.Release(); }
    }

    public static async Task<JsonObject?> ReceiveFrame(WebSocket ws, CancellationToken ct)
    {
        string? text = await ReceiveText(ws, ct, 8 * 1024 * 1024);
        if (text is null) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch { return new JsonObject(); }
    }

    /// <summary>One whole WebSocket message, text or binary. Null when the socket closes or the message is too big.</summary>
    public static async Task<(WebSocketMessageType Kind, ArraySegment<byte> Data)?> ReceiveMessage(WebSocket ws, CancellationToken ct, int max)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > max) return null;
            if (r.EndOfMessage) return (r.MessageType, new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length));
        }
    }

    public static async Task<string?> ReceiveText(WebSocket ws, CancellationToken ct, int max)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > max) return null;
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
