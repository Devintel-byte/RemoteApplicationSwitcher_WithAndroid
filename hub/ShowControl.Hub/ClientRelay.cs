using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ShowControl.Hub;

/// <summary>
/// A phone (or the hub dashboard) controlling one PC through the hub at /ws/agent/{agentId}.
/// It speaks the same protocol as on the LAN: the first message is hello with the user's hub token
/// instead of the PC's pairing token. Everything after that is relayed both ways.
/// </summary>
public sealed class ClientRelay : IRelayClient
{
    private static readonly HashSet<string> ViewerAllowed = new() { "ping", "hello", "get_state", "screenshot", "stream_start", "stream_stop", "stream_ack" };
    private static readonly HashSet<string> Audited = new() { "start_app", "stop_app", "restart_app", "run_scene", "stop_all", "power", "wake" };

    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly AgentRegistry _agents;
    private readonly HubStore _store;
    private readonly string _agentId;

    private ClientRelay(WebSocket ws, AgentRegistry agents, HubStore store, string agentId)
    {
        _ws = ws;
        _agents = agents;
        _store = store;
        _agentId = agentId;
    }

    public static async Task RunAsync(WebSocket ws, string agentId, AgentRegistry agents, HubStore store, HubAuth auth, CancellationToken ct)
    {
        var relay = new ClientRelay(ws, agents, store, agentId);
        await relay.RunAsync(auth, ct);
    }

    private async Task RunAsync(HubAuth auth, CancellationToken ct)
    {
        string? helloText;
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            t.CancelAfter(TimeSpan.FromSeconds(15));
            try { helloText = await AgentRegistry.ReceiveText(_ws, t.Token, 64 * 1024); }
            catch { return; }
        }
        if (helloText is null) return;
        var hello = TryParse(helloText);
        if ((string?)hello?["type"] != "hello")
        {
            await Fail("unauthorized", "Send hello with your hub token first", 4401);
            return;
        }

        var user = auth.Authenticate((string?)hello!["token"]);
        if (user is null)
        {
            await Fail("unauthorized", "Your hub sign-in has expired. Sign in to the hub again.", 4401);
            return;
        }
        if (user.Role == Roles.Viewer)
        {
            // Viewer accounts are tour guides: they use the kiosks assigned to them (hub address /kiosk), not the PCs.
            await Fail("forbidden", "Viewer accounts use their kiosks only. Open the hub's address in a browser to see them.", 4403);
            return;
        }
        var enrolled = _store.Read(d => d.Agents.FirstOrDefault(a => a.AgentId == _agentId));
        if (enrolled is null)
        {
            await Fail("unauthorized", "This PC was removed from the hub.", 4404);
            return;
        }

        string sid = await _agents.OpenAsync(_agentId, user.DisplayName, user.Role, this) ?? "";
        if (sid.Length == 0)
        {
            // PC offline: close so the app shows "offline" and retries with backoff.
            try { await _ws.CloseAsync((WebSocketCloseStatus)4404, "PC offline", CancellationToken.None); } catch { }
            return;
        }

        string role = user.Role;
        try
        {
            await _agents.SendAsync(_agentId, sid, helloText);
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                string? text = await AgentRegistry.ReceiveText(_ws, ct, 64 * 1024);
                if (text is null) break;
                var msg = TryParse(text);
                string type = (string?)msg?["type"] ?? "";
                if (role == Roles.Viewer && !ViewerAllowed.Contains(type))
                {
                    await DeliverAsync(new JsonObject { ["type"] = "error", ["id"] = (string?)msg?["id"], ["code"] = "forbidden", ["message"] = "Viewers can watch but not control" }.ToJsonString());
                    continue;
                }
                if (type == "pair") continue; // hub sign-in replaces pairing
                if (Audited.Contains(type))
                    _store.Audit(user.Username, type, _agentId, (string?)msg?["appId"] ?? (string?)msg?["sceneId"] ?? (string?)msg?["action"] ?? (string?)msg?["mac"]);
                if (!await _agents.SendAsync(_agentId, sid, text)) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            await _agents.CloseAsync(_agentId, sid);
        }
    }

    public async Task DeliverAsync(string data)
    {
        if (_ws.State != WebSocketState.Open) return;
        await _send.WaitAsync();
        try { await _ws.SendAsync(Encoding.UTF8.GetBytes(data), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { _send.Release(); }
    }

    /// <summary>
    /// Forwards a live frame. The PC only sends a couple of frames ahead of the phone's acks, so frames
    /// queue here only briefly; nothing is dropped (a dropped frame would never be acked).
    /// </summary>
    public async Task DeliverBinaryAsync(ArraySegment<byte> frame)
    {
        if (_ws.State != WebSocketState.Open) return;
        await _send.WaitAsync();
        try { await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None); }
        catch { }
        finally { _send.Release(); }
    }

    public async Task AgentGoneAsync()
    {
        try { await _ws.CloseAsync((WebSocketCloseStatus)4404, "PC went offline", CancellationToken.None); } catch { }
    }

    private async Task Fail(string code, string message, int closeCode)
    {
        await DeliverAsync(new JsonObject { ["type"] = "error", ["id"] = "hello", ["code"] = code, ["message"] = message }.ToJsonString());
        try { await _ws.CloseAsync((WebSocketCloseStatus)closeCode, code, CancellationToken.None); } catch { }
    }

    private static JsonObject? TryParse(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; } catch { return null; }
    }
}

/// <summary>A short-lived session the hub itself uses (schedules, "Run now"): hello, one command, wait for the answer.</summary>
public sealed class SystemCommand : IRelayClient
{
    private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _welcomed;

    public static async Task<string?> RunAsync(AgentRegistry agents, string agentId, string label, JsonObject command)
    {
        var client = new SystemCommand();
        string? sid = await agents.OpenAsync(agentId, label, Roles.Admin, client);
        if (sid is null) return "PC is offline";
        try
        {
            await agents.SendAsync(agentId, sid, new JsonObject { ["type"] = "hello", ["id"] = "hello", ["clientName"] = "hub" }.ToJsonString());
            command["id"] = "cmd";
            await agents.SendAsync(agentId, sid, command.ToJsonString());
            var finished = await Task.WhenAny(client._done.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            return finished == client._done.Task ? client._done.Task.Result : "No answer from the PC";
        }
        finally
        {
            await agents.CloseAsync(agentId, sid);
        }
    }

    public Task DeliverAsync(string data)
    {
        JsonObject? m;
        try { m = JsonNode.Parse(data) as JsonObject; } catch { return Task.CompletedTask; }
        string? type = (string?)m?["type"];
        if (type == "welcome") _welcomed = true;
        if ((string?)m?["id"] != "cmd") return Task.CompletedTask;
        if (type == "ack") _done.TrySetResult(null);
        else if (type == "error") _done.TrySetResult((string?)m?["message"] ?? "Refused");
        return Task.CompletedTask;
    }

    public Task AgentGoneAsync()
    {
        _done.TrySetResult(_welcomed ? "PC disconnected" : "PC is offline");
        return Task.CompletedTask;
    }
}
