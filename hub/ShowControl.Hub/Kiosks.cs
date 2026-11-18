using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ShowControl.Hub;

/// <summary>
/// A visitor screen for one room: a grid of covers (PC scenes or apps) that people tap to switch what's
/// playing. Set up on the hub by an operator or admin; shown on a PC in the room, which the hub tells to
/// open it full screen (locked "kiosk mode") or in a normal window.
/// </summary>
public sealed class Kiosk
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Room name shown at the top of the screen.</summary>
    public string Room { get; set; } = "";
    public string Welcome { get; set; } = "";
    /// <summary>PCs in this room (for the editor's pickers).</summary>
    public List<string> AgentIds { get; set; } = new();
    /// <summary>The PC that shows the kiosk. Null = shown on whatever device opens the link.</summary>
    public string? DisplayAgentId { get; set; }
    /// <summary>Locked full screen (Edge kiosk mode) instead of a normal window.</summary>
    public bool Locked { get; set; } = true;
    public bool Enabled { get; set; } = true;
    /// <summary>Closed with the exit PIN; stays off until resumed from the hub (or the PC restarts the agent).</summary>
    public bool Paused { get; set; }
    /// <summary>When a new item is chosen, close the app the previous item started.</summary>
    public bool StopPrevious { get; set; }
    /// <summary>0 = automatic.</summary>
    public int Columns { get; set; }
    public string? ExitPinHash { get; set; }
    public string? ExitPinSalt { get; set; }
    /// <summary>The kiosk's own key (it signs in with this, not a user account). Regenerate to revoke a screen.</summary>
    public string Key { get; set; } = "";
    public List<KioskItem> Items { get; set; } = new();
    /// <summary>Viewer accounts (tour guides) who may use this kiosk from their own device.</summary>
    public List<string> ViewerIds { get; set; } = new();
    public string? ActiveItemId { get; set; }
    public DateTime? ActiveAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class KioskItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>Label colour used when there's no cover image: teal | amber | violet | blue | coral | red | gray.</summary>
    public string Color { get; set; } = "amber";
    /// <summary>run_scene | start_app</summary>
    public string Action { get; set; } = "run_scene";
    public string AgentId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string? TargetName { get; set; }
    public string? CoverFile { get; set; }
    public string? CoverType { get; set; }
    public int CoverVersion { get; set; }
}

public sealed partial class KioskService(HubStore store, AgentRegistry agents)
{
    public static readonly string[] ItemActions = { "run_scene", "start_app" };
    private static readonly string[] Colors = { "teal", "amber", "violet", "blue", "coral", "red", "gray" };
    private static readonly Dictionary<string, string> CoverTypes = new()
    {
        ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["image/webp"] = ".webp",
    };
    private const long MaxCoverBytes = 3 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, DateTime> _lastRun = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTime Until)> _pinFailures = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public string CoverDir => Path.Combine(store.DataDir, "kiosk-covers");

    public static string KioskPath(Kiosk k) => "/kiosk#k=" + k.Key;

    // ---------- telling the display PC what to show ----------

    /// <summary>The frame for one PC: open its kiosk (locked or windowed), or close it.</summary>
    public JsonObject FrameFor(string agentId)
    {
        var k = store.Read(d => d.Kiosks.FirstOrDefault(x => x.DisplayAgentId == agentId));
        if (k is null || !k.Enabled || k.Paused) return new JsonObject { ["t"] = "kiosk", ["path"] = null };
        return new JsonObject { ["t"] = "kiosk", ["path"] = KioskPath(k), ["locked"] = k.Locked, ["name"] = k.Name };
    }

    public async Task PushAsync(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return;
        var conn = agents.Get(agentId);
        if (conn is null) return; // it gets the frame when it connects
        try { await AgentRegistry.SendFrame(conn.Socket, conn.SendLock, FrameFor(agentId)); }
        catch { /* connection dropping; it'll get it on reconnect */ }
    }

    // ---------- the screen itself ----------

    public Kiosk? ByKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length < 20) return null;
        return store.Read(d => d.Kiosks.FirstOrDefault(k => HubStore.FixedEquals(k.Key, key)));
    }

    /// <summary>Kiosks a person may use as a tour guide: viewers get the ones assigned to them; operators and admins get all.</summary>
    public List<Kiosk> ForUser(HubUser u) => store.Read(d => d.Kiosks
        .Where(k => u.Role != Roles.Viewer || k.ViewerIds.Contains(u.Id))
        .OrderBy(k => k.Room, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList());

    public object MyKioskDto(Kiosk k)
    {
        // The PC a tour guide picks this kiosk under: its display PC, else the room's (first) PC, else the PC its items run on.
        string? pcId = k.DisplayAgentId ?? k.AgentIds.FirstOrDefault() ?? k.Items.Select(i => i.AgentId).FirstOrDefault(a => !string.IsNullOrEmpty(a));
        string? pcName = pcId is null ? null : store.Read(d => d.Agents.FirstOrDefault(a => a.AgentId == pcId)?.Name);
        if (pcName is null) pcId = null;
        return new
        {
            id = k.Id, name = k.Name, room = k.Room, enabled = k.Enabled,
            pcId, pcName, pcOnline = pcId is not null && agents.IsOnline(pcId),
            items = k.Items.Count, available = k.Items.Count(i => agents.IsOnline(i.AgentId)),
            activeItemId = k.ActiveItemId,
            activeTitle = k.Items.FirstOrDefault(i => i.Id == k.ActiveItemId)?.Title,
            covers = k.Items.Take(4).Select(i => i.CoverFile is null ? null : MyCoverUrl(k, i)).ToList(),
            colors = k.Items.Take(4).Select(i => i.Color).ToList(),
            titles = k.Items.Take(4).Select(i => i.Title).ToList(),
        };
    }

    private static string MyCoverUrl(Kiosk k, KioskItem i) => $"/api/my/kiosks/{k.Id}/cover/{i.Id}?v={i.CoverVersion}";

    /// <summary>The kiosk grid for its screen (signed in with the kiosk key).</summary>
    public object ScreenConfig(Kiosk k) => Config(k, i => $"/api/kiosk/cover/{i.Id}?v={i.CoverVersion}&k={Uri.EscapeDataString(k.Key)}");

    /// <summary>The kiosk grid for a tour guide (signed in as a person; covers need the sign-in header).</summary>
    public object ViewerConfig(Kiosk k) => Config(k, i => MyCoverUrl(k, i));

    private object Config(Kiosk k, Func<KioskItem, string> coverUrl) => new
    {
        kiosk = new
        {
            id = k.Id, name = k.Name, room = k.Room, welcome = k.Welcome, columns = k.Columns,
            hasExitPin = k.ExitPinHash is not null, enabled = k.Enabled, paused = k.Paused, locked = k.Locked,
        },
        workspace = HubApi.WorkspaceDto(store),
        activeItemId = k.ActiveItemId,
        items = k.Items.Select(i => new
        {
            id = i.Id, title = i.Title, subtitle = i.Subtitle, color = i.Color, action = i.Action,
            available = agents.IsOnline(i.AgentId),
            cover = i.CoverFile is null ? null : coverUrl(i),
        }).ToList(),
    };

    /// <summary>A visitor (or a tour guide) tapped an item. Null on success, otherwise a message for the screen.</summary>
    public async Task<string?> RunAsync(Kiosk k, string itemId, string by = "")
    {
        var item = k.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return "That choice was removed. The screen will refresh.";
        if (!k.Enabled) return "This screen is turned off";
        // One tap at a time per screen; double taps and taps in quick succession are ignored.
        if (_lastRun.TryGetValue(k.Id, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(1.5)) return null;
        _lastRun[k.Id] = DateTime.UtcNow;
        if (!agents.IsOnline(item.AgentId)) return $"{item.Title} isn't available right now";

        await _runGate.WaitAsync();
        try
        {
            string label = by.Length > 0 ? $"Kiosk: {k.Name} ({by})" : $"Kiosk: {k.Name}";
            var previous = k.Items.FirstOrDefault(i => i.Id == k.ActiveItemId);
            if (k.StopPrevious && previous is not null && previous.Id != item.Id && previous.Action == "start_app" && agents.IsOnline(previous.AgentId))
            {
                await SystemCommand.RunAsync(agents, previous.AgentId, label, new JsonObject { ["type"] = "stop_app", ["appId"] = previous.TargetId });
            }
            var cmd = item.Action == "start_app"
                ? new JsonObject { ["type"] = "start_app", ["appId"] = item.TargetId }
                : new JsonObject { ["type"] = "run_scene", ["sceneId"] = item.TargetId };
            string? error = await SystemCommand.RunAsync(agents, item.AgentId, label, cmd);
            if (error is null)
            {
                store.Write(d => { var x = d.Kiosks.FirstOrDefault(y => y.Id == k.Id); if (x is not null) { x.ActiveItemId = item.Id; x.ActiveAt = DateTime.UtcNow; } });
            }
            store.Audit(by.Length > 0 ? by : $"kiosk:{k.Name}", "kiosk_run", item.AgentId, $"{k.Name}: {item.Title}{(error is null ? "" : " → " + error)}");
            return error is null ? null : $"{item.Title} couldn't start: {error}";
        }
        finally { _runGate.Release(); }
    }

    public bool PinLockedOut(string kioskId) => _pinFailures.TryGetValue(kioskId, out var f) && f.Count >= 5 && f.Until > DateTime.UtcNow;

    public bool CheckPin(Kiosk k, string pin)
    {
        if (k.ExitPinHash is null || k.ExitPinSalt is null) return false;
        bool ok = HubStore.VerifyPassword(pin, k.ExitPinHash, k.ExitPinSalt);
        if (ok) _pinFailures.TryRemove(k.Id, out _);
        else _pinFailures.AddOrUpdate(k.Id, _ => (1, DateTime.UtcNow.AddMinutes(5)), (_, f) => (f.Count + 1, DateTime.UtcNow.AddMinutes(5)));
        return ok;
    }

    // ---------- editing ----------

    public object Dto(Kiosk k, bool full) => new
    {
        id = k.Id, name = k.Name, room = k.Room, welcome = k.Welcome, agentIds = k.AgentIds,
        displayAgentId = k.DisplayAgentId,
        displayOnline = k.DisplayAgentId is not null && agents.IsOnline(k.DisplayAgentId),
        locked = k.Locked, enabled = k.Enabled, paused = k.Paused, stopPrevious = k.StopPrevious, columns = k.Columns,
        hasExitPin = k.ExitPinHash is not null, viewerIds = k.ViewerIds,
        activeItemId = k.ActiveItemId, activeAt = k.ActiveAt, updatedAt = k.UpdatedAt,
        path = full ? KioskPath(k) : null,
        items = k.Items.Select(i => new
        {
            id = i.Id, title = i.Title, subtitle = i.Subtitle, color = i.Color, action = i.Action,
            agentId = i.AgentId, targetId = i.TargetId, targetName = i.TargetName,
            available = agents.IsOnline(i.AgentId),
            cover = i.CoverFile is null ? null : $"/api/kiosks/{k.Id}/items/{i.Id}/cover?v={i.CoverVersion}",
        }).ToList(),
    };

    /// <summary>Applies an edit from the dashboard. Returns an error message or null.</summary>
    public string? Apply(Kiosk k, JsonObject b, bool isNew)
    {
        string name = ((string?)b["name"] ?? "").Trim();
        if (name.Length is 0 or > 60) return "Give the kiosk a name (up to 60 characters)";
        k.Name = name;
        k.Room = Trim((string?)b["room"], 60);
        k.Welcome = Trim((string?)b["welcome"], 200);
        k.Locked = (bool?)b["locked"] ?? k.Locked;
        k.Enabled = (bool?)b["enabled"] ?? k.Enabled;
        k.StopPrevious = (bool?)b["stopPrevious"] ?? k.StopPrevious;
        k.Columns = Math.Clamp((int?)b["columns"] ?? k.Columns, 0, 6);
        k.AgentIds = (b["agentIds"] as JsonArray)?.Select(n => (string?)n).OfType<string>().Distinct().Take(50).ToList() ?? k.AgentIds;
        if (b["viewerIds"] is JsonArray viewers)
        {
            var wanted = viewers.Select(n => (string?)n).OfType<string>().Distinct().ToList();
            var valid = store.Read(d => d.Users.Where(u => u.Role == Roles.Viewer && wanted.Contains(u.Id)).Select(u => u.Id).ToList());
            k.ViewerIds = wanted.Where(valid.Contains).ToList();
        }

        string? display = ((string?)b["displayAgentId"])?.Trim();
        if (string.IsNullOrEmpty(display)) display = null;
        if (display is not null)
        {
            if (!store.Read(d => d.Agents.Any(a => a.AgentId == display))) return "The display PC isn't on this hub";
            string? other = store.Read(d => d.Kiosks.FirstOrDefault(x => x.Id != k.Id && x.DisplayAgentId == display)?.Name);
            if (other is not null) return $"That PC already shows the kiosk \"{other}\". A PC can show one kiosk.";
        }
        k.DisplayAgentId = display;

        if (b.ContainsKey("exitPin"))
        {
            string pin = ((string?)b["exitPin"] ?? "").Trim();
            if (pin.Length == 0) { k.ExitPinHash = null; k.ExitPinSalt = null; }
            else
            {
                if (!PinRegex().IsMatch(pin)) return "The exit PIN must be 4 to 8 digits";
                (k.ExitPinHash, k.ExitPinSalt) = HubStore.HashPassword(pin);
            }
        }

        if (b["items"] is JsonArray arr)
        {
            if (arr.Count > 48) return "A kiosk can have up to 48 items";
            var old = k.Items.ToDictionary(i => i.Id);
            var items = new List<KioskItem>();
            int n = 0;
            foreach (var node in arr)
            {
                n++;
                if (node is not JsonObject o) continue;
                string id = ((string?)o["id"] ?? "").Trim();
                if (!IdRegex().IsMatch(id)) id = HubStore.NewToken(8);
                var it = old.TryGetValue(id, out var prev) ? prev : new KioskItem { Id = id };
                it.Title = Trim((string?)o["title"], 60);
                it.Subtitle = Trim((string?)o["subtitle"], 80);
                it.Color = Colors.Contains((string?)o["color"]) ? (string)o["color"]! : "amber";
                it.Action = ItemActions.Contains((string?)o["action"]) ? (string)o["action"]! : "run_scene";
                it.AgentId = ((string?)o["agentId"] ?? "").Trim();
                it.TargetId = ((string?)o["targetId"] ?? "").Trim();
                it.TargetName = Trim((string?)o["targetName"], 60);
                if (it.Title.Length == 0) it.Title = it.TargetName ?? "";
                if (it.Title.Length == 0) return $"Item {n} needs a title";
                if (it.AgentId.Length == 0 || !store.Read(d => d.Agents.Any(a => a.AgentId == it.AgentId))) return $"Item {n} ({it.Title}): choose a PC";
                if (it.TargetId.Length == 0) return $"Item {n} ({it.Title}): choose a {(it.Action == "start_app" ? "app" : "scene")}";
                items.Add(it);
            }
            foreach (var removed in old.Values.Where(o => items.All(i => i.Id != o.Id))) DeleteCoverFile(removed);
            k.Items = items;
            if (k.ActiveItemId is not null && items.All(i => i.Id != k.ActiveItemId)) k.ActiveItemId = null;
        }
        k.UpdatedAt = DateTime.UtcNow;
        return null;
    }

    public async Task<string?> SaveCoverAsync(Kiosk k, string itemId, HttpRequest req)
    {
        var item = k.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return "Save the kiosk first, then add the cover";
        string type = (req.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (!CoverTypes.TryGetValue(type, out var ext)) return "Use a PNG, JPG or WebP image";
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms);
        if (ms.Length == 0) return "The image is empty";
        if (ms.Length > MaxCoverBytes) return "Keep cover images under 3 MB (about 1920 × 1080 JPG is plenty)";
        Directory.CreateDirectory(CoverDir);
        DeleteCoverFile(item);
        string file = $"{k.Id}-{item.Id}{ext}";
        await File.WriteAllBytesAsync(Path.Combine(CoverDir, file), ms.ToArray());
        store.Write(d =>
        {
            var it = d.Kiosks.FirstOrDefault(x => x.Id == k.Id)?.Items.FirstOrDefault(i => i.Id == itemId);
            if (it is null) return;
            it.CoverFile = file; it.CoverType = type; it.CoverVersion++;
        });
        return null;
    }

    public void RemoveCover(string kioskId, string itemId) => store.Write(d =>
    {
        var it = d.Kiosks.FirstOrDefault(x => x.Id == kioskId)?.Items.FirstOrDefault(i => i.Id == itemId);
        if (it is null) return;
        DeleteCoverFile(it);
        it.CoverFile = null; it.CoverType = null; it.CoverVersion++;
    });

    public IResult CoverResult(KioskItem? item, HttpContext ctx)
    {
        if (item?.CoverFile is null) return Results.NotFound();
        string path = Path.Combine(CoverDir, item.CoverFile);
        if (!File.Exists(path)) return Results.NotFound();
        ctx.Response.Headers.CacheControl = "private, max-age=86400";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(path, item.CoverType ?? "application/octet-stream");
    }

    public void DeleteCoverFile(KioskItem item)
    {
        if (item.CoverFile is null) return;
        try { File.Delete(Path.Combine(CoverDir, item.CoverFile)); } catch { }
    }

    private static string Trim(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length > max ? s[..max] : s;
    }

    [GeneratedRegex("^[0-9]{4,8}$")] private static partial Regex PinRegex();
    [GeneratedRegex("^[A-Za-z0-9_-]{4,40}$")] private static partial Regex IdRegex();
}

public static class KioskApi
{
    public static void Map(WebApplication app, HubStore store, HubAuth auth, KioskService kiosks, Func<HttpContext, string, IResult?> need)
    {
        var api = app.MapGroup("/api");

        // ---------- managing kiosks (operator or admin) ----------

        api.MapGet("/kiosks", (HttpContext ctx) =>
        {
            if (need(ctx, Roles.Operator) is { } deny) return deny;
            return Results.Ok(new { kiosks = store.Read(d => d.Kiosks.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList()).Select(k => kiosks.Dto(k, false)).ToList() });
        });

        api.MapGet("/kiosks/{id}", (HttpContext ctx, string id) =>
        {
            if (need(ctx, Roles.Operator) is { } deny) return deny;
            var k = store.Read(d => d.Kiosks.FirstOrDefault(x => x.Id == id));
            return k is null ? Results.NotFound() : Results.Ok(kiosks.Dto(k, true));
        });

        api.MapPost("/kiosks", (Delegate)(Func<HttpContext, Task<IResult>>)(async ctx =>
        {
            if (need(ctx, Roles.Operator) is { } deny) return deny;
            var b = await Body(ctx);
            if (b is null) return Bad("Send the kiosk as JSON");
            var k = new Kiosk { Id = HubStore.NewToken(9), Key = HubStore.NewToken(24) };
            if (kiosks.Apply(k, b, isNew: true) is { } err) return Bad(err);
            store.Write(d => d.Kiosks.Add(k));
            store.Audit(auth.FromRequest(ctx)!.Username, "kiosk_created", k.DisplayAgentId, k.Name);
            await kiosks.PushAsync(k.DisplayAgentId);
            return Results.Ok(kiosks.Dto(k, true));
        }));

        api.MapPut("/kiosks/{id}", (Delegate)(Func<HttpContext, string, Task<IResult>>)(async (ctx, id) =>
        {
            if (need(ctx, Roles.Operator) is { } deny) return deny;
            var b = await Body(ctx);
            if (b is null) return Bad("Send the kiosk as JSON");
            string? oldDisplay = null, error = null;
            Kiosk? saved = store.Write(d =>
            {
                var k = d.Kiosks.FirstOrDefault(x => x.Id == id);
                if (k is null) return null;
                oldDisplay = k.DisplayAgentId;
                // Validate on a copy so a refused edit changes nothing.
                var copy = JsonSerializer.Deserialize<Kiosk>(JsonSerializer.Serialize(k, HubStore.Json), HubStore.Json)!;
                error = kiosks.Apply(copy, b, isNew: false);
                if (error is not null) return k;
                d.Kiosks[d.Kiosks.IndexOf(k)] = copy;
                return copy;
            });
            if (saved is null) return Results.NotFound();
            if (error is not null) return Bad(error);
            store.Audit(auth.FromRequest(ctx)!.Username, "kiosk_changed", saved.DisplayAgentId, saved.Name);
            if (oldDisplay != saved.DisplayAgentId) await kiosks.PushAsync(oldDisplay);
            await kiosks.PushAsync(saved.DisplayAgentId);
            return Results.Ok(kiosks.Dto(saved, true));
        }));

        api.MapDelete("/kiosks/{id}", async (HttpContext ctx, string id) =>
        {
            if (need(ctx, Roles.Operator) is { } deny) return deny;
            var k = store.Read(d => d.Kiosks.FirstOrDefault(x => x.Id == id));
            if (k is null) return Results.NotFound();
            foreach (var it in k.Items) kiosks.DeleteCoverFile(it);
            store.Write(d => d.Kiosks.RemoveAll(x => x.Id == id));
            store.Audit(auth.FromRequest(ctx)!.Username, "kiosk_removed", k.DisplayAgentId, k.Name);
            await kiosks.PushAsync(k.DisplayAgentId);
            return Results.Ok();
        });

        // New key: any screen using the old link stops working (e.g. a tablet that walked off).
        api.MapPost("/kiosks/{id}/key", async (HttpContext ctx, string id) =>
        {
            if (need(ctx, Roles.Operator) is { } deny) return deny;
            var k = store.Write(d => { var x = d.Kiosks.FirstOrDefault(y => y.Id == id); if (x is not null) x.Key = HubStore.NewToken(24); return x; });
            if (k is null) return Results.NotFound();
            store.Audit(auth.FromRequest(ctx)!.Username, "kiosk_new_key", k.DisplayAgentId, k.Name);
            await kiosks.PushAsync(k.DisplayAgentId);
            return Results.Ok(kiosks.Dto(k, true));
        });

        // Bring a kiosk back after someone closed it with the exit PIN.
        api.MapPost("/kiosks/{id}/resume", async (HttpContext ctx, string id) =>
        {
            if (need(ctx, Roles.Operator) is { } deny) return deny;
            var k = store.Write(d => { var x = d.Kiosks.FirstOrDefault(y => y.Id == id); if (x is not null) x.Paused = false; return x; });
            if (k is null) return Results.NotFound();
            await kiosks.PushAsync(k.DisplayAgentId);
            return Results.Ok(kiosks.Dto(k, true));
        });

        api.MapGet("/kiosks/{id}/items/{itemId}/cover", (HttpContext ctx, string id, string itemId) =>
        {
            if (need(ctx, Roles.Operator) is { } deny) return deny;
            var item = store.Read(d => d.Kiosks.FirstOrDefault(x => x.Id == id)?.Items.FirstOrDefault(i => i.Id == itemId));
            return kiosks.CoverResult(item, ctx);
        });

        api.MapPut("/kiosks/{id}/items/{itemId}/cover", (Delegate)(Func<HttpContext, string, string, Task<IResult>>)(async (ctx, id, itemId) =>
        {
            if (need(ctx, Roles.Operator) is { } deny) return deny;
            var k = store.Read(d => d.Kiosks.FirstOrDefault(x => x.Id == id));
            if (k is null) return Results.NotFound();
            if (await kiosks.SaveCoverAsync(k, itemId, ctx.Request) is { } err) return Bad(err);
            return Results.Ok(kiosks.Dto(store.Read(d => d.Kiosks.First(x => x.Id == id)), true));
        }));

        api.MapDelete("/kiosks/{id}/items/{itemId}/cover", (HttpContext ctx, string id, string itemId) =>
        {
            if (need(ctx, Roles.Operator) is { } deny) return deny;
            kiosks.RemoveCover(id, itemId);
            return Results.Ok();
        });

        // People who can be given kiosks (for the editor's "Viewers" picker; operators can't list all users).
        api.MapGet("/viewers", (HttpContext ctx) =>
        {
            if (need(ctx, Roles.Operator) is { } deny) return deny;
            return Results.Ok(new { viewers = store.Read(d => d.Users.Where(u => u.Role == Roles.Viewer).OrderBy(u => u.DisplayName).Select(u => new { id = u.Id, username = u.Username, displayName = u.DisplayName }).ToList()) });
        });

        // ---------- tour guides: a person's own kiosks, used from their own device ----------

        api.MapGet("/my/kiosks", (HttpContext ctx) =>
        {
            if (need(ctx, Roles.Viewer) is { } deny) return deny;
            var me = auth.FromRequest(ctx)!;
            return Results.Ok(new { user = new { me.DisplayName, me.Role }, kiosks = kiosks.ForUser(me).Select(kiosks.MyKioskDto).ToList() });
        });

        api.MapGet("/my/kiosks/{id}/config", (HttpContext ctx, string id) =>
        {
            if (need(ctx, Roles.Viewer) is { } deny) return deny;
            var k = kiosks.ForUser(auth.FromRequest(ctx)!).FirstOrDefault(x => x.Id == id);
            if (k is null) return Results.NotFound(new { error = "This kiosk isn't assigned to you any more" });
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(kiosks.ViewerConfig(k));
        });

        api.MapGet("/my/kiosks/{id}/cover/{itemId}", (HttpContext ctx, string id, string itemId) =>
        {
            if (need(ctx, Roles.Viewer) is { } deny) return deny;
            var k = kiosks.ForUser(auth.FromRequest(ctx)!).FirstOrDefault(x => x.Id == id);
            return kiosks.CoverResult(k?.Items.FirstOrDefault(i => i.Id == itemId), ctx);
        });

        api.MapPost("/my/kiosks/{id}/run", (Delegate)(Func<HttpContext, string, Task<IResult>>)(async (ctx, id) =>
        {
            if (need(ctx, Roles.Viewer) is { } deny) return deny;
            var me = auth.FromRequest(ctx)!;
            var k = kiosks.ForUser(me).FirstOrDefault(x => x.Id == id);
            if (k is null) return Results.NotFound(new { error = "This kiosk isn't assigned to you any more" });
            var b = await Body(ctx);
            string? error = await kiosks.RunAsync(k, (string?)b?["itemId"] ?? "", me.Username);
            return Results.Ok(new { ok = error is null, message = error });
        }));

        // ---------- the kiosk screen (signs in with its key) ----------

        api.MapGet("/kiosk/config", (HttpContext ctx) =>
        {
            var k = kiosks.ByKey(ctx.Request.Headers["X-Kiosk-Key"].ToString());
            if (k is null) return Results.Problem("This screen's link is no longer valid. Ask an operator for the new one.", statusCode: 401);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(kiosks.ScreenConfig(k));
        });

        api.MapGet("/kiosk/cover/{itemId}", (HttpContext ctx, string itemId, string? k) =>
        {
            var kiosk = kiosks.ByKey(k);
            return kiosk is null ? Results.NotFound() : kiosks.CoverResult(kiosk.Items.FirstOrDefault(i => i.Id == itemId), ctx);
        });

        api.MapPost("/kiosk/run", (Delegate)(Func<HttpContext, Task<IResult>>)(async ctx =>
        {
            var k = kiosks.ByKey(ctx.Request.Headers["X-Kiosk-Key"].ToString());
            if (k is null) return Results.Problem("This screen's link is no longer valid.", statusCode: 401);
            var b = await Body(ctx);
            string? error = await kiosks.RunAsync(k, (string?)b?["itemId"] ?? "");
            return Results.Ok(new { ok = error is null, message = error });
        }));

        // Staff close the kiosk with its PIN (e.g. to use the PC). It stays closed until resumed from the hub.
        api.MapPost("/kiosk/exit", (Delegate)(Func<HttpContext, Task<IResult>>)(async ctx =>
        {
            var k = kiosks.ByKey(ctx.Request.Headers["X-Kiosk-Key"].ToString());
            if (k is null) return Results.Problem("This screen's link is no longer valid.", statusCode: 401);
            if (k.ExitPinHash is null) return Bad("No exit PIN is set for this kiosk. Turn it off from the hub instead.");
            if (kiosks.PinLockedOut(k.Id)) return Results.Problem("Too many wrong PINs. Try again in 5 minutes.", statusCode: 429);
            var b = await Body(ctx);
            if (!kiosks.CheckPin(k, (string?)b?["pin"] ?? "")) { store.Audit($"kiosk:{k.Name}", "kiosk_exit_wrong_pin"); return Bad("Wrong PIN"); }
            store.Write(d => { var x = d.Kiosks.FirstOrDefault(y => y.Id == k.Id); if (x is not null) x.Paused = true; });
            store.Audit($"kiosk:{k.Name}", "kiosk_exit", k.DisplayAgentId);
            await kiosks.PushAsync(k.DisplayAgentId);
            return Results.Ok(new { ok = true, closesScreen = k.DisplayAgentId is not null && k.Locked });
        }));
    }

    private static async Task<JsonObject?> Body(HttpContext ctx)
    {
        try { return await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static IResult Bad(string message) => Results.BadRequest(new { error = message });
}
