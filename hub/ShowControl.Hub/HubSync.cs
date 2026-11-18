using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ShowControl.Hub;

// =====================================================================================================
// Hub-to-hub sync.
//
// A "site hub" runs on a PC in the venue (local access, works without internet). The "cloud hub" runs on
// the server (remote access). The site hub links to the cloud with a one-time code, then keeps an outbound
// WebSocket open to it (/hubsync), and both sides send each other everything they share whenever it
// changes: people (with password hashes, never sign-in tokens), PCs and their keys, schedules, kiosks with
// their covers, shared rooms, branding and the activity log. Each shared record has a change time; the
// newest version wins, and deletions travel as tombstones.
// =====================================================================================================

public sealed class SyncState
{
    public string HubId { get; set; } = "";
    /// <summary>Set on a site hub: the cloud hub it keeps in sync with.</summary>
    public UpstreamLink? Upstream { get; set; }
    /// <summary>Set on a cloud hub: the site hubs linked to it.</summary>
    public List<SiteLink> Sites { get; set; } = new();
    public List<SyncLinkCode> LinkCodes { get; set; } = new();
    /// <summary>Change time and fingerprint of every shared record (u:, a:, s:, r:, k: and w).</summary>
    public Dictionary<string, LedgerEntry> Ledger { get; set; } = new();
}

public sealed class UpstreamLink
{
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";
    public string RemoteHubId { get; set; } = "";
    public DateTime LinkedAt { get; set; }
}

public sealed class SiteLink
{
    public string HubId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public DateTime LinkedAt { get; set; }
    public DateTime? LastSeen { get; set; }
    public List<string> LanUrls { get; set; } = new();
}

public sealed class SyncLinkCode
{
    public string Code { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public string CreatedBy { get; set; } = "";
}

public sealed class LedgerEntry
{
    /// <summary>SHA-256 of the record as shared.</summary>
    public string H { get; set; } = "";
    public DateTime T { get; set; }
    /// <summary>Deleted (a tombstone, so the deletion reaches the other hub).</summary>
    public bool D { get; set; }
}

/// <summary>Turns hub data into shareable records and back.</summary>
public static class SyncRecords
{
    private static JsonObject Node(object o) => (JsonObject)JsonSerializer.SerializeToNode(o, HubStore.Json)!;

    public static Dictionary<string, JsonObject> Collect(HubData d)
    {
        var r = new Dictionary<string, JsonObject>();
        foreach (var u in d.Users)
            r["u:" + u.Id] = new JsonObject
            {
                ["id"] = u.Id, ["username"] = u.Username, ["displayName"] = u.DisplayName, ["role"] = u.Role,
                ["passwordHash"] = u.PasswordHash, ["passwordSalt"] = u.PasswordSalt, ["createdAt"] = u.CreatedAt,
            };
        foreach (var a in d.Agents)
            r["a:" + a.AgentId] = new JsonObject
            {
                ["agentId"] = a.AgentId, ["name"] = a.Name, ["hostname"] = a.Hostname, ["keyHash"] = a.KeyHash,
                ["enrolledAt"] = a.EnrolledAt, ["macs"] = new JsonArray(a.Macs.Select(m => (JsonNode?)m).ToArray()), ["nameLocked"] = a.NameLocked,
            };
        foreach (var s in d.Schedules)
        {
            var n = Node(s); n.Remove("lastRun"); n.Remove("lastResult");
            r["s:" + s.Id] = n;
        }
        foreach (var room in d.SharedRooms) r["r:" + room.Id] = Node(room);
        foreach (var k in d.Kiosks) r["k:" + k.Id] = Node(k);
        r["w"] = Node(d.Workspace);
        return r;
    }

    public static string Hash(JsonNode n) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(n.ToJsonString())))[..32];

    /// <summary>Updates the ledger after a change. Returns true if anything shared changed.</summary>
    public static bool Refresh(HubData d, Dictionary<string, DateTime>? stamps)
    {
        var now = DateTime.UtcNow;
        var ledger = d.Sync.Ledger;
        bool changed = false;
        var current = Collect(d);
        foreach (var (key, rec) in current)
        {
            string h = Hash(rec);
            if (ledger.TryGetValue(key, out var e) && e.H == h && !e.D) continue;
            ledger[key] = new LedgerEntry { H = h, T = stamps is not null && stamps.TryGetValue(key, out var t) ? t : now };
            changed = true;
        }
        foreach (var (key, e) in ledger)
        {
            if (e.D || current.ContainsKey(key)) continue;
            e.D = true; e.H = "";
            e.T = stamps is not null && stamps.TryGetValue(key, out var t) ? t : now;
            changed = true;
        }
        return changed;
    }

    /// <summary>Puts one record from another hub into this hub's data (null = delete it).</summary>
    public static void Apply(HubData d, string key, JsonObject? rec)
    {
        int colon = key.IndexOf(':');
        string kind = colon < 0 ? key : key[..colon], id = colon < 0 ? "" : key[(colon + 1)..];
        switch (kind)
        {
            case "u":
            {
                var old = d.Users.FirstOrDefault(x => x.Id == id);
                d.Users.RemoveAll(x => x.Id == id);
                if (rec is null) break;
                var u = rec.Deserialize<HubUser>(HubStore.Json)!;
                u.Tokens = old?.Tokens ?? new();
                // Same username, different account (both hubs were set up separately): the incoming one wins.
                d.Users.RemoveAll(x => string.Equals(x.Username, u.Username, StringComparison.OrdinalIgnoreCase));
                d.Users.Add(u);
                break;
            }
            case "a":
            {
                var old = d.Agents.FirstOrDefault(x => x.AgentId == id);
                d.Agents.RemoveAll(x => x.AgentId == id);
                if (rec is null) break;
                var a = rec.Deserialize<EnrolledAgent>(HubStore.Json)!;
                a.LastSeen = old?.LastSeen;
                d.Agents.Add(a);
                break;
            }
            case "s":
            {
                var old = d.Schedules.FirstOrDefault(x => x.Id == id);
                int at = old is null ? d.Schedules.Count : d.Schedules.IndexOf(old);
                d.Schedules.RemoveAll(x => x.Id == id);
                if (rec is null) break;
                var s = rec.Deserialize<Schedule>(HubStore.Json)!;
                s.LastRun = old?.LastRun; s.LastResult = old?.LastResult;
                d.Schedules.Insert(Math.Min(at, d.Schedules.Count), s);
                break;
            }
            case "r":
                d.SharedRooms.RemoveAll(x => x.Id == id);
                if (rec is not null) d.SharedRooms.Add(rec.Deserialize<SharedRoom>(HubStore.Json)!);
                break;
            case "k":
            {
                var old = d.Kiosks.FirstOrDefault(x => x.Id == id);
                int at = old is null ? d.Kiosks.Count : d.Kiosks.IndexOf(old);
                d.Kiosks.RemoveAll(x => x.Id == id);
                if (rec is not null) d.Kiosks.Insert(Math.Min(at, d.Kiosks.Count), rec.Deserialize<Kiosk>(HubStore.Json)!);
                break;
            }
            case "w":
                if (rec is not null) d.Workspace = rec.Deserialize<Workspace>(HubStore.Json)!;
                break;
        }
    }

    /// <summary>Files the shared records point at, as "folder/name" (logo in the data folder, covers in kiosk-covers).</summary>
    public static List<string> Files(HubData d)
    {
        var list = new List<string>();
        if (d.Workspace.LogoFile is { } logo) list.Add("data/" + logo);
        foreach (var k in d.Kiosks) foreach (var i in k.Items) if (i.CoverFile is { } c) list.Add("covers/" + c);
        return list;
    }
}

/// <summary>Keeps this hub in sync with linked hubs, in both directions.</summary>
public sealed class HubSync
{
    private readonly HubStore _store;
    private readonly AgentRegistry _agents;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private CancellationTokenSource? _upstreamCts;
    private readonly ConcurrentDictionary<string, (DateTime Mtime, long Size, string Sha)> _fileShas = new();

    /// <summary>Raised when the set of other hubs PCs should also connect to changes (so they can be told).</summary>
    public event Func<Task>? HubsChanged;

    public sealed class Peer
    {
        public required string HubId { get; init; }
        public required string Name { get; set; }
        public required bool IsUpstream { get; init; }
        public required WebSocket Socket { get; init; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public HashSet<string> OnlineAgents { get; set; } = new();
        public List<string> LanUrls { get; set; } = new();
        public DateTime ConnectedAt { get; } = DateTime.UtcNow;
        public DateTime? LastSync { get; set; }
        public int Debounce;
    }

    public HubSync(HubStore store, AgentRegistry agents, int port)
    {
        _store = store; _agents = agents; _port = port;
        _store.Changed += () => _ = PushAllAsync();
        _agents.AgentConnected += _ => PingAllAsync();
        _agents.AgentDisconnected += _ => PingAllAsync();
    }

    public string HubId => _store.Read(d => d.Sync.HubId);
    public IReadOnlyCollection<Peer> Peers => _peers.Values.ToList();
    public Peer? UpstreamPeer => _peers.Values.FirstOrDefault(p => p.IsUpstream);

    /// <summary>A PC is online at a linked site hub (so that hub, not this one, runs its schedules).</summary>
    public bool OnlineAtSite(string agentId) => _peers.Values.Any(p => !p.IsUpstream && p.OnlineAgents.Contains(agentId));
    public bool HasSitePeers => _peers.Values.Any(p => !p.IsUpstream);
    public bool UpstreamConnected => UpstreamPeer is not null;
    public bool OnlineAtUpstream(string agentId) => UpstreamPeer?.OnlineAgents.Contains(agentId) == true;

    /// <summary>
    /// Other hubs this hub's PCs should also connect to: the cloud's address (for a site hub), or the
    /// linked sites' local addresses (for the cloud hub; PCs that can't reach them just keep trying quietly).
    /// </summary>
    public List<JsonObject> HubsForAgents()
    {
        var list = new List<JsonObject>();
        var up = _store.Read(d => d.Sync.Upstream);
        if (up is not null) list.Add(new JsonObject { ["url"] = up.Url, ["kind"] = "cloud" });
        foreach (var s in _store.Read(d => d.Sync.Sites.ToList()))
        {
            var live = _peers.TryGetValue(s.HubId, out var p) ? p.LanUrls : s.LanUrls;
            foreach (var u in live) list.Add(new JsonObject { ["url"] = u, ["kind"] = "site" });
        }
        return list;
    }

    // ---------------- linking ----------------

    public static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        return url;
    }

    /// <summary>Cloud side: a short code an admin types on the site hub to link it.</summary>
    public string NewLinkCode(string by) => _store.Write(d =>
    {
        d.Sync.LinkCodes.RemoveAll(c => c.ExpiresAt < DateTime.UtcNow);
        string code = HubStore.NewEnrollCode();
        d.Sync.LinkCodes.Add(new SyncLinkCode { Code = code, ExpiresAt = DateTime.UtcNow.AddMinutes(15), CreatedBy = by });
        return code;
    });

    /// <summary>Cloud side: a site hub presents a link code. Returns its secret, or null if the code is wrong.</summary>
    public (string Secret, string HubId, string Name)? Accept(string code, string siteHubId, string siteName, string prefer)
    {
        string secret = HubStore.NewToken();
        bool ok = _store.Write(d =>
        {
            d.Sync.LinkCodes.RemoveAll(c => c.ExpiresAt < DateTime.UtcNow);
            var match = d.Sync.LinkCodes.FirstOrDefault(c => HubStore.FixedEquals(c.Code, code.Trim().ToUpperInvariant()));
            if (match is null || siteHubId.Length is < 8 or > 64 || siteHubId == d.Sync.HubId) return false;
            d.Sync.LinkCodes.Remove(match);
            d.Sync.Sites.RemoveAll(s => s.HubId == siteHubId);
            d.Sync.Sites.Add(new SiteLink { HubId = siteHubId, Name = siteName.Length > 0 ? siteName[..Math.Min(60, siteName.Length)] : "Site hub", SecretHash = HubStore.Sha256(secret), LinkedAt = DateTime.UtcNow });
            if (prefer == "site") AgeLedger(d);
            return true;
        });
        if (!ok) return null;
        _store.Audit("system", "hub_linked", null, siteName);
        return (secret, HubId, _store.Read(d => d.Workspace.Name));
    }

    /// <summary>Makes this hub's own versions older than anything the other hub has (PC enrolments excepted), so the other hub's settings win where both have something.</summary>
    private static void AgeLedger(HubData d)
    {
        foreach (var (key, e) in d.Sync.Ledger) if (!key.StartsWith("a:")) e.T = DateTime.UnixEpoch;
    }

    /// <summary>Site side: link to a cloud hub with the code made there.</summary>
    public async Task<string?> JoinAsync(string url, string code, string prefer, string by)
    {
        url = NormalizeUrl(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "That address doesn't look right";
        if (_store.Read(d => d.Sync.Sites.Count > 0)) return "Sites are linked to this hub, so it can't link to another one itself";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        HttpResponseMessage res;
        try
        {
            res = await http.PostAsync(new Uri(uri, "/api/sync/accept"), JsonContent(new JsonObject
            {
                ["code"] = code, ["hubId"] = HubId, ["name"] = SiteName(), ["prefer"] = prefer,
            }));
        }
        catch (Exception ex) { return $"Couldn't reach {uri.Host}: {ex.Message}"; }
        var body = await ReadJson(res);
        if (!res.IsSuccessStatusCode) return (string?)body?["error"] ?? $"The other hub said {(int)res.StatusCode}";
        string secret = (string?)body?["secret"] ?? "";
        string remoteId = (string?)body?["hubId"] ?? "";
        if (secret.Length < 20) return "The other hub's answer wasn't understood. Is it Show Control Hub 1.3 or later?";
        _store.Write(d =>
        {
            d.Sync.Upstream = new UpstreamLink { Url = url, Secret = secret, RemoteHubId = remoteId, LinkedAt = DateTime.UtcNow };
            if (prefer != "site") AgeLedger(d);
        });
        _store.Audit(by, "hub_linked", null, url);
        StartUpstream();
        // Wait for the first sync so people can sign in with their accounts straight away.
        for (int i = 0; i < 40 && UpstreamPeer?.LastSync is null; i++) await Task.Delay(250);
        if (HubsChanged is { } hc) await hc();
        return null;
    }

    public async Task UnlinkUpstreamAsync(string by)
    {
        _store.Write(d => d.Sync.Upstream = null);
        _upstreamCts?.Cancel();
        if (UpstreamPeer is { } p) await DropAsync(p);
        _store.Audit(by, "hub_unlinked");
        if (HubsChanged is { } hc) await hc();
    }

    public async Task RemoveSiteAsync(string hubId, string by)
    {
        _store.Write(d => d.Sync.Sites.RemoveAll(s => s.HubId == hubId));
        if (_peers.TryGetValue(hubId, out var p)) await DropAsync(p);
        _store.Audit(by, "hub_unlinked", null, hubId);
        if (HubsChanged is { } hc) await hc();
    }

    private static string SiteName() => Environment.MachineName;

    // ---------------- connections ----------------

    /// <summary>Site side: keeps the connection to the cloud hub open.</summary>
    public void StartUpstream()
    {
        _upstreamCts?.Cancel();
        var cts = _upstreamCts = new CancellationTokenSource();
        _ = Task.Run(() => UpstreamLoopAsync(cts.Token));
    }

    private async Task UpstreamLoopAsync(CancellationToken ct)
    {
        int delay = 2;
        while (!ct.IsCancellationRequested)
        {
            var up = _store.Read(d => d.Sync.Upstream);
            if (up is null) return;
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ws.Options.SetRequestHeader("Authorization", "Bearer " + up.Secret);
                ws.Options.SetRequestHeader("X-Hub-Id", HubId);
                var u = new Uri(up.Url);
                var wsUri = new UriBuilder(u) { Scheme = u.Scheme == "https" ? "wss" : "ws", Path = "/hubsync" }.Uri;
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(20));
                    await ws.ConnectAsync(wsUri, connect.Token);
                }
                delay = 2;
                Console.WriteLine($"[hub] linked hub connected: {up.Url}");
                await SessionAsync(ws, up.RemoteHubId.Length > 0 ? up.RemoteHubId : "upstream", up.Url, true, ct);
                Console.WriteLine($"[hub] linked hub disconnected: {up.Url}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (WebSocketException ex) when (ex.Message.Contains("401") || ex.Message.Contains("403"))
            {
                Console.WriteLine($"[hub] {up.Url} refused the link (it was removed there). Link again from Linked hubs.");
                delay = 60;
            }
            catch (Exception ex) { Console.WriteLine($"[hub] can't reach linked hub {up.Url}: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch { return; }
            delay = Math.Min(delay * 2, 30);
        }
    }

    /// <summary>Cloud side: a site hub connects to /hubsync.</summary>
    public async Task AcceptSiteAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        string hubId = ctx.Request.Headers["X-Hub-Id"].ToString();
        string secret = HubAuth.BearerToken(ctx) ?? "";
        var site = _store.Read(d => d.Sync.Sites.FirstOrDefault(s => s.HubId == hubId));
        if (site is null || !HubStore.FixedEquals(site.SecretHash, HubStore.Sha256(secret))) { ctx.Response.StatusCode = 403; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        Console.WriteLine($"[hub] site hub connected: {site.Name} from {ctx.Connection.RemoteIpAddress}");
        await SessionAsync(ws, hubId, site.Name, false, ctx.RequestAborted);
        Console.WriteLine($"[hub] site hub disconnected: {site.Name}");
    }

    private async Task SessionAsync(WebSocket ws, string peerId, string name, bool upstream, CancellationToken ct)
    {
        var peer = new Peer { HubId = peerId, Name = name, IsUpstream = upstream, Socket = ws };
        if (_peers.TryRemove(peerId, out var old)) await DropAsync(old);
        _peers[peerId] = peer;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pinger = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(20), cts.Token); await SendAsync(peer, PingFrame()); } catch { return; }
            }
        });
        try
        {
            await SendAsync(peer, HelloFrame());
            await SendAsync(peer, SnapshotFrame());
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var text = await AgentRegistry.ReceiveText(ws, cts.Token, 64 * 1024 * 1024);
                if (text is null) break;
                JsonObject? m;
                try { m = JsonNode.Parse(text) as JsonObject; } catch { continue; }
                if (m is null) continue;
                try { await HandleAsync(peer, m); }
                catch (Exception ex) { Console.WriteLine($"[hub] sync message failed: {ex.Message}"); }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        finally
        {
            cts.Cancel();
            _peers.TryRemove(new KeyValuePair<string, Peer>(peerId, peer));
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
        }
    }

    private async Task DropAsync(Peer p)
    {
        _peers.TryRemove(new KeyValuePair<string, Peer>(p.HubId, p));
        try { await p.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Unlinked", CancellationToken.None); } catch { }
    }

    // ---------------- messages ----------------

    private JsonObject HelloFrame() => new()
    {
        ["t"] = "hello", ["hubId"] = HubId, ["name"] = _store.Read(d => d.Workspace.Name), ["version"] = HubInfo.Version,
        ["lanUrls"] = new JsonArray(LanUrls().Select(u => (JsonNode?)u).ToArray()),
    };

    private JsonObject PingFrame() => new() { ["t"] = "online", ["agents"] = new JsonArray(_agents.Online.Select(a => (JsonNode?)a.AgentId).ToArray()) };

    private JsonObject SnapshotFrame()
    {
        var (records, ledger, files) = _store.Read(d => (SyncRecords.Collect(d), d.Sync.Ledger.ToDictionary(x => x.Key, x => x.Value), SyncRecords.Files(d)));
        var led = new JsonObject();
        foreach (var (k, e) in ledger) led[k] = new JsonObject { ["h"] = e.H, ["t"] = e.T, ["d"] = e.D };
        var recs = new JsonObject();
        foreach (var (k, r) in records) recs[k] = r;
        var fileShas = new JsonObject();
        foreach (var f in files) if (FileSha(f) is { } sha) fileShas[f] = sha;
        return new JsonObject
        {
            ["t"] = "snapshot", ["ledger"] = led, ["records"] = recs, ["files"] = fileShas,
            ["audit"] = new JsonArray(_store.AuditTail(200).Select(e => JsonNode.Parse(e.GetRawText())).ToArray()),
            ["agents"] = new JsonArray(_agents.Online.Select(a => (JsonNode?)a.AgentId).ToArray()),
        };
    }

    private async Task HandleAsync(Peer peer, JsonObject m)
    {
        switch ((string?)m["t"])
        {
            case "hello":
                peer.Name = (string?)m["name"] ?? peer.Name;
                peer.LanUrls = (m["lanUrls"] as JsonArray)?.Select(x => (string?)x).OfType<string>().Take(8).ToList() ?? new();
                if (!peer.IsUpstream)
                {
                    _store.Write(d =>
                    {
                        var s = d.Sync.Sites.FirstOrDefault(x => x.HubId == peer.HubId);
                        if (s is null) return;
                        s.LastSeen = DateTime.UtcNow; s.LanUrls = peer.LanUrls;
                    });
                    if (HubsChanged is { } hc) await hc();
                }
                break;
            case "online":
                peer.OnlineAgents = (m["agents"] as JsonArray)?.Select(x => (string?)x).OfType<string>().ToHashSet() ?? new();
                break;
            case "snapshot":
                peer.OnlineAgents = (m["agents"] as JsonArray)?.Select(x => (string?)x).OfType<string>().ToHashSet() ?? peer.OnlineAgents;
                Merge(m);
                if (m["audit"] is JsonArray audit)
                    _store.MergeAudit(audit.Select(a => JsonDocument.Parse(a!.ToJsonString()).RootElement.Clone()));
                var want = new JsonArray();
                if (m["files"] is JsonObject files)
                    foreach (var (name, sha) in files)
                        if (FileSha(name) != (string?)sha && SafeFile(name) is not null) want.Add(name);
                if (want.Count > 0) await SendAsync(peer, new JsonObject { ["t"] = "want", ["files"] = want });
                peer.LastSync = DateTime.UtcNow;
                break;
            case "want":
                foreach (var n in (m["files"] as JsonArray ?? new JsonArray()).Select(x => (string?)x).OfType<string>().Take(500))
                {
                    var path = SafeFile(n);
                    if (path is null || !File.Exists(path)) continue;
                    await SendAsync(peer, new JsonObject { ["t"] = "file", ["name"] = n, ["data"] = Convert.ToBase64String(await File.ReadAllBytesAsync(path)) });
                }
                break;
            case "file":
            {
                var path = SafeFile((string?)m["name"]);
                if (path is null) break;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".part";
                await File.WriteAllBytesAsync(tmp, Convert.FromBase64String((string?)m["data"] ?? ""));
                File.Move(tmp, path, overwrite: true);
                _fileShas.TryRemove(path, out _);
                break;
            }
        }
    }

    /// <summary>Takes whatever the other hub has that is newer than ours.</summary>
    private void Merge(JsonObject snapshot)
    {
        if (snapshot["ledger"] is not JsonObject led || snapshot["records"] is not JsonObject recs) return;
        var local = _store.Read(d => d.Sync.Ledger.ToDictionary(x => x.Key, x => new LedgerEntry { H = x.Value.H, T = x.Value.T, D = x.Value.D }));
        var take = new List<(string Key, JsonObject? Rec, DateTime T)>();
        foreach (var (key, node) in led)
        {
            if (node is not JsonObject e || !ValidKey(key)) continue;
            string h = (string?)e["h"] ?? "";
            bool del = (bool?)e["d"] ?? false;
            DateTime t = e["t"]?.GetValue<DateTime>() ?? DateTime.UnixEpoch;
            local.TryGetValue(key, out var mine);
            if (mine is null && del) continue;
            if (mine is not null && mine.H == h && mine.D == del) continue;
            bool theirsWins = mine is null || t > mine.T || (t == mine.T && string.CompareOrdinal(h, mine.H) > 0);
            if (!theirsWins) continue;
            var rec = del ? null : recs[key] as JsonObject;
            if (!del && rec is null) continue;
            take.Add((key, rec is null ? null : (JsonObject)rec.DeepClone(), t));
        }
        if (take.Count == 0) return;
        // People first (usernames), then PCs, then what refers to them.
        static int Order(string k) => k[0] switch { 'w' => 0, 'u' => 1, 'a' => 2, 's' => 3, 'r' => 4, _ => 5 };
        take.Sort((a, b) => Order(a.Key).CompareTo(Order(b.Key)));
        var stamps = take.ToDictionary(x => x.Key, x => x.T);
        var kioskAgents = new HashSet<string>();
        _store.WriteRemote(d =>
        {
            foreach (var (key, rec, _) in take)
            {
                if (key.StartsWith("k:"))
                {
                    if (d.Kiosks.FirstOrDefault(k => "k:" + k.Id == key)?.DisplayAgentId is { } before) kioskAgents.Add(before);
                    if ((string?)rec?["displayAgentId"] is { } after) kioskAgents.Add(after);
                }
                try { SyncRecords.Apply(d, key, rec); }
                catch (Exception ex) { Console.WriteLine($"[hub] couldn't apply {key}: {ex.Message}"); }
            }
        }, stamps);
        Console.WriteLine($"[hub] sync: took {take.Count} change{(take.Count == 1 ? "" : "s")} from a linked hub");
        // A PC removed on the other hub is disconnected here too.
        foreach (var (key, rec, _) in take) if (rec is null && key.StartsWith("a:")) _ = _agents.KickAsync(key[2..]);
        CleanFiles();
        if (KiosksChanged is { } kc) foreach (var a in kioskAgents) _ = kc(a);
    }

    /// <summary>Raised for each PC whose kiosk changed through sync (so it's told to open or close it).</summary>
    public event Func<string, Task>? KiosksChanged;

    private static bool ValidKey(string key) => key == "w" || (key.Length is > 2 and < 100 && key[1] == ':' && "uasrk".Contains(key[0]));

    // ---------------- files ----------------

    private string? SafeFile(string? name)
    {
        if (name is null) return null;
        int slash = name.IndexOf('/');
        if (slash < 0) return null;
        string folder = name[..slash], file = name[(slash + 1)..];
        if (file.Length is 0 or > 120 || file.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || file.StartsWith('.')) return null;
        return folder switch
        {
            "data" => Path.Combine(_store.DataDir, file),
            "covers" => Path.Combine(_store.DataDir, "kiosk-covers", file),
            _ => null,
        };
    }

    private string? FileSha(string name)
    {
        var path = SafeFile(name);
        if (path is null || !File.Exists(path)) return null;
        var fi = new FileInfo(path);
        if (_fileShas.TryGetValue(path, out var c) && c.Mtime == fi.LastWriteTimeUtc && c.Size == fi.Length) return c.Sha;
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..32];
        _fileShas[path] = (fi.LastWriteTimeUtc, fi.Length, sha);
        return sha;
    }

    /// <summary>Removes cover images no kiosk uses any more (after another hub removed or replaced them).</summary>
    private void CleanFiles()
    {
        try
        {
            var used = _store.Read(d => d.Kiosks.SelectMany(k => k.Items).Select(i => i.CoverFile).OfType<string>().ToHashSet());
            string dir = Path.Combine(_store.DataDir, "kiosk-covers");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir))
                if (!used.Contains(Path.GetFileName(f)) && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddMinutes(-2)) File.Delete(f);
        }
        catch { }
    }

    // ---------------- sending ----------------

    private async Task PushAllAsync()
    {
        foreach (var p in _peers.Values)
        {
            // Many writes in a row (a kiosk save plus its covers) go out as one snapshot.
            if (Interlocked.Exchange(ref p.Debounce, 1) == 1) continue;
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                Interlocked.Exchange(ref p.Debounce, 0);
                try { await SendAsync(p, SnapshotFrame()); } catch { }
            });
        }
    }

    private Task PingAllAsync()
    {
        foreach (var p in _peers.Values) _ = SendAsync(p, PingFrame()).ContinueWith(_ => { });
        return Task.CompletedTask;
    }

    private static async Task SendAsync(Peer p, JsonObject frame)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
        await p.SendLock.WaitAsync();
        try { await p.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { p.SendLock.Release(); }
    }

    private static StringContent JsonContent(JsonObject o) => new(o.ToJsonString(), Encoding.UTF8, "application/json");
    private static async Task<JsonObject?> ReadJson(HttpResponseMessage r)
    {
        try { return JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject; } catch { return null; }
    }

    /// <summary>This PC's addresses on the local network (private IPv4), as hub URLs.</summary>
    public List<string> LanUrls()
    {
        // Override when the hub can't see its own address (containers, several network cards): SC_HUB_LAN_URLS=http://192.168.1.10:47900
        if (Environment.GetEnvironmentVariable("SC_HUB_LAN_URLS") is { Length: > 0 } env)
            return env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4).ToList();
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a.Address)) list.Add($"http://{a.Address}:{_port}");
            }
        }
        catch { }
        return list.Distinct().Take(4).ToList();
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.MapToIPv4().GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    // ---------------- API ----------------

    public static void Map(WebApplication app, HubStore store, HubSync sync, Func<HttpContext, string, IResult?> need, Func<HttpContext, HubUser?> who)
    {
        app.Map("/hubsync", sync.AcceptSiteAsync);
        var api = app.MapGroup("/api/sync");

        api.MapGet("", (HttpContext ctx) =>
        {
            if (need(ctx, Roles.Admin) is { } deny) return deny;
            var (up, sites, id) = store.Read(d => (d.Sync.Upstream, d.Sync.Sites.ToList(), d.Sync.HubId));
            var upPeer = sync.UpstreamPeer;
            return Results.Ok(new
            {
                hubId = id,
                lanUrls = sync.LanUrls(),
                upstream = up is null ? null : new { url = up.Url, linkedAt = up.LinkedAt, online = upPeer is not null, name = upPeer?.Name, lastSync = upPeer?.LastSync },
                sites = sites.Select(s =>
                {
                    sync._peers.TryGetValue(s.HubId, out var p);
                    return new { hubId = s.HubId, name = s.Name, linkedAt = s.LinkedAt, lastSeen = p is not null ? DateTime.UtcNow : s.LastSeen, online = p is not null, lanUrls = p?.LanUrls ?? s.LanUrls, pcsOnline = p?.OnlineAgents.Count ?? 0 };
                }).ToList(),
            });
        });

        api.MapPost("/link-code", (HttpContext ctx) =>
        {
            if (need(ctx, Roles.Admin) is { } deny) return deny;
            if (store.Read(d => d.Sync.Upstream is not null)) return Results.BadRequest(new { error = "This hub is itself linked to another hub. Make the code on that one." });
            string code = sync.NewLinkCode(who(ctx)?.Username ?? "");
            return Results.Ok(new { code, expiresInMinutes = 15 });
        });

        // Called by the site hub (no user signed in: the one-time code is the proof).
        api.MapPost("/accept", async (HttpContext ctx) =>
        {
            JsonObject? b;
            try { b = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject; } catch { b = null; }
            var r = sync.Accept((string?)b?["code"] ?? "", (string?)b?["hubId"] ?? "", (string?)b?["name"] ?? "", (string?)b?["prefer"] ?? "cloud");
            if (r is null) return Results.BadRequest(new { error = "That link code is wrong or has expired. Make a new one on the other hub (Linked hubs)." });
            return Results.Ok(new { secret = r.Value.Secret, hubId = r.Value.HubId, name = r.Value.Name });
        });

        api.MapPost("/join", async (HttpContext ctx) =>
        {
            if (need(ctx, Roles.Admin) is { } deny) return deny;
            JsonObject? b;
            try { b = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject; } catch { b = null; }
            string? err = await sync.JoinAsync((string?)b?["url"] ?? "", (string?)b?["code"] ?? "", (string?)b?["prefer"] ?? "cloud", who(ctx)?.Username ?? "");
            return err is null ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = err });
        });

        api.MapDelete("/upstream", async (HttpContext ctx) =>
        {
            if (need(ctx, Roles.Admin) is { } deny) return deny;
            await sync.UnlinkUpstreamAsync(who(ctx)?.Username ?? "");
            return Results.Ok(new { ok = true });
        });

        api.MapDelete("/sites/{id}", async (HttpContext ctx, string id) =>
        {
            if (need(ctx, Roles.Admin) is { } deny) return deny;
            await sync.RemoveSiteAsync(id, who(ctx)?.Username ?? "");
            return Results.Ok(new { ok = true });
        });
    }
}
