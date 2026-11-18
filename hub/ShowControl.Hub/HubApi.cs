using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ShowControl.Hub;

public static partial class HubApi
{
    private static readonly string[] Accents = { "indigo", "teal", "amber", "violet", "blue", "coral" };
    private static readonly Dictionary<string, string> LogoTypes = new()
    {
        ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["image/webp"] = ".webp", ["image/svg+xml"] = ".svg",
    };

    public static void Map(WebApplication app, HubStore store, HubAuth auth, AgentRegistry agents, ScheduleRunner schedules, Func<string?> setupCode)
    {
        var api = app.MapGroup("/api");

        // ---------- setup & sign-in ----------

        api.MapGet("/health", () => Results.Ok(new { ok = true, version = HubInfo.Version }));

        api.MapGet("/setup", () => Results.Ok(new { needed = store.Read(d => d.Users.Count == 0) }));

        // Name and logo for the sign-in screen (shown before anyone signs in).
        api.MapGet("/public/workspace", () => Results.Ok(new
        {
            setupNeeded = store.Read(d => d.Users.Count == 0),
            workspace = WorkspaceDto(store),
        }));

        api.MapPost("/setup", H(async ctx =>
        {
            if (store.Read(d => d.Users.Count > 0)) return Results.Problem("This hub is already set up", statusCode: 409);
            var b = await Body(ctx);
            string code = ((string?)b?["code"] ?? "").Trim();
            string? expected = setupCode();
            if (expected is null || !HubStore.FixedEquals(code.ToUpperInvariant(), expected)) return Bad("Wrong setup code. It's printed in the hub's console and saved in data/setup-code.txt.");
            if (UserError(b, isNew: true) is { } err) return Bad(err);
            string workspace = ((string?)b!["workspaceName"] ?? "").Trim();
            store.Write(d =>
            {
                if (workspace.Length > 0) d.Workspace.Name = workspace[..Math.Min(60, workspace.Length)];
                d.Users.Add(NewUser(b, Roles.Admin));
            });
            File.Delete(Path.Combine(store.DataDir, "setup-code.txt"));
            store.Audit((string)b["username"]!, "hub_setup");
            var login = auth.Login((string)b["username"]!, (string)b["password"]!, "Dashboard", Remote(ctx))!.Value;
            return Results.Ok(SignedIn(store, login.Token, login.User));
        }));

        api.MapPost("/login", H(async ctx =>
        {
            var b = await Body(ctx);
            string remote = Remote(ctx);
            if (auth.IsLockedOut(remote)) return Results.Problem("Too many attempts. Try again in 5 minutes.", statusCode: 429);
            var r = auth.Login((string?)b?["username"] ?? "", (string?)b?["password"] ?? "", (string?)b?["device"] ?? "", remote);
            if (r is null) return Results.Problem("Wrong username or password", statusCode: 401);
            store.Audit(r.Value.User.Username, "signed_in", null, (string?)b?["device"]);
            return Results.Ok(SignedIn(store, r.Value.Token, r.Value.User));
        }));

        api.MapPost("/logout", (HttpContext ctx) => { auth.Logout(HubAuth.BearerToken(ctx)); return Results.Ok(); });

        api.MapGet("/me", (HttpContext ctx) =>
        {
            var u = auth.FromRequest(ctx);
            return u is null ? Unauthorized() : Results.Ok(new { user = UserDto(u), workspace = WorkspaceDto(store) });
        });

        api.MapPut("/me/password", H(async ctx =>
        {
            var u = auth.FromRequest(ctx);
            if (u is null) return Unauthorized();
            var b = await Body(ctx);
            if (!HubStore.VerifyPassword((string?)b?["current"] ?? "", u.PasswordHash, u.PasswordSalt)) return Bad("Your current password is wrong");
            string pw = (string?)b?["new"] ?? "";
            if (pw.Length < 8) return Bad("Use at least 8 characters");
            var (hash, salt) = HubStore.HashPassword(pw);
            store.Write(d => { var x = d.Users.First(y => y.Id == u.Id); x.PasswordHash = hash; x.PasswordSalt = salt; });
            return Results.Ok();
        }));

        // ---------- workspace & branding ----------

        api.MapGet("/workspace", (HttpContext ctx) => auth.FromRequest(ctx) is null ? Unauthorized() : Results.Ok(WorkspaceDto(store)));

        api.MapPut("/workspace", H(async ctx =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            var b = await Body(ctx);
            string name = ((string?)b?["name"] ?? "").Trim();
            string accent = (string?)b?["accent"] ?? "indigo";
            string tz = ((string?)b?["timeZone"] ?? "").Trim();
            if (name.Length is 0 or > 60) return Bad("Give the workspace a name");
            if (!Accents.Contains(accent)) return Bad("Unknown accent colour");
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)) return Bad($"Unknown time zone '{tz}'. Use a name like Africa/Lagos.");
            store.Write(d => { d.Workspace.Name = name; d.Workspace.Accent = accent; d.Workspace.TimeZone = tz; });
            store.Audit(auth.FromRequest(ctx)!.Username, "branding_changed");
            return Results.Ok(WorkspaceDto(store));
        }));

        api.MapGet("/workspace/logo", (HttpContext ctx) =>
        {
            var w = store.Read(d => d.Workspace);
            if (w.LogoFile is null) return Results.NotFound();
            string path = Path.Combine(store.DataDir, w.LogoFile);
            if (!File.Exists(path)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // SVGs are served as images only, with scripting blocked.
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            return Results.File(path, w.LogoContentType ?? "application/octet-stream");
        });

        api.MapPut("/workspace/logo", H(async ctx =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            string type = (ctx.Request.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            if (!LogoTypes.TryGetValue(type, out var ext)) return Bad("Use a PNG, JPG, WebP or SVG image");
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            if (ms.Length == 0) return Bad("The image is empty");
            if (ms.Length > 1024 * 1024) return Bad("Keep the logo under 1 MB");
            string file = "logo" + ext;
            foreach (var old in Directory.GetFiles(store.DataDir, "logo.*")) File.Delete(old);
            await File.WriteAllBytesAsync(Path.Combine(store.DataDir, file), ms.ToArray());
            store.Write(d => { d.Workspace.LogoFile = file; d.Workspace.LogoContentType = type; d.Workspace.LogoVersion++; });
            store.Audit(auth.FromRequest(ctx)!.Username, "logo_changed");
            return Results.Ok(WorkspaceDto(store));
        }));

        api.MapDelete("/workspace/logo", (HttpContext ctx) =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            foreach (var old in Directory.GetFiles(store.DataDir, "logo.*")) File.Delete(old);
            store.Write(d => { d.Workspace.LogoFile = null; d.Workspace.LogoContentType = null; d.Workspace.LogoVersion++; });
            return Results.Ok(WorkspaceDto(store));
        });

        // ---------- PCs ----------

        api.MapGet("/agents", (HttpContext ctx) =>
        {
            // Viewers (tour guides) only use their kiosks; they don't see the PCs.
            if (Need(ctx, auth, Roles.Operator) is { } deny) return deny;
            var list = store.Read(d => d.Agents.Select(a =>
            {
                var live = agents.Get(a.AgentId);
                return new
                {
                    agentId = a.AgentId, name = a.Name, hostname = live?.Hostname ?? a.Hostname,
                    online = live is not null, version = live?.Version, lastSeen = live is not null ? DateTime.UtcNow : a.LastSeen,
                    apps = live?.Apps ?? 0, running = live?.Running ?? 0, crashed = live?.Crashed ?? 0,
                    enrolledAt = a.EnrolledAt, canWake = a.Macs.Count > 0,
                };
            }).OrderByDescending(a => a.online).ThenBy(a => a.name, StringComparer.OrdinalIgnoreCase).ToList());
            return Results.Ok(new { agents = list });
        });

        api.MapPost("/agents/enroll-code", (HttpContext ctx) =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            var user = auth.FromRequest(ctx)!;
            var code = new EnrollCode { Code = HubStore.NewEnrollCode(), ExpiresAt = DateTime.UtcNow.AddMinutes(15), CreatedBy = user.Username };
            store.Write(d => { d.EnrollCodes.RemoveAll(c => c.ExpiresAt < DateTime.UtcNow); d.EnrollCodes.Add(code); });
            return Results.Ok(new { code = code.Code, expiresAt = code.ExpiresAt });
        });

        api.MapPut("/agents/{id}", H2(async (ctx, id) =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            string name = ((string?)(await Body(ctx))?["name"] ?? "").Trim();
            if (name.Length is 0 or > 60) return Bad("Give the PC a name");
            bool ok = store.Write(d => { var a = d.Agents.FirstOrDefault(x => x.AgentId == id); if (a is null) return false; a.Name = name; a.NameLocked = true; return true; });
            return ok ? Results.Ok() : Results.NotFound();
        }));

        api.MapDelete("/agents/{id}", async (HttpContext ctx, string id) =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            string? name = store.Write(d => { var a = d.Agents.FirstOrDefault(x => x.AgentId == id); if (a is null) return null; d.Agents.Remove(a); d.Schedules.RemoveAll(s => s.AgentId == id); return a.Name; });
            if (name is null) return Results.NotFound();
            await agents.KickAsync(id);
            store.Audit(auth.FromRequest(ctx)!.Username, "pc_removed", id, name);
            return Results.Ok();
        });

        api.MapPost("/agents/{id}/wake", async (HttpContext ctx, string id) =>
        {
            if (Need(ctx, auth, Roles.Operator) is { } deny) return deny;
            var user = auth.FromRequest(ctx)!;
            string? error = await schedules.WakeAsync(id, user.DisplayName);
            store.Audit(user.Username, "wake", id, error ?? "sent");
            return error is null ? Results.Ok() : Bad(error);
        });

        // Whole-site maintenance: wake, restart or shut down every PC (or a chosen few) at once.
        api.MapPost("/agents/power", H(async ctx =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            var user = auth.FromRequest(ctx)!;
            var b = await Body(ctx);
            string action = (string?)b?["action"] ?? "";
            if (action is not ("wake" or "restart" or "shutdown")) return Bad("action must be wake, restart or shutdown");
            var wanted = (b?["agentIds"] as JsonArray)?.Select(n => (string?)n).OfType<string>().ToHashSet();
            var targets = store.Read(d => d.Agents.Where(a => wanted is null || wanted.Contains(a.AgentId)).Select(a => (a.AgentId, a.Name)).ToList());
            var results = await Task.WhenAll(targets.Select(async t =>
            {
                string? error;
                if (action == "wake")
                    error = agents.IsOnline(t.AgentId) ? "already on" : await schedules.WakeAsync(t.AgentId, user.DisplayName);
                else
                    error = agents.IsOnline(t.AgentId)
                        ? await SystemCommand.RunAsync(agents, t.AgentId, user.DisplayName, new JsonObject { ["type"] = "power", ["action"] = action })
                        : "offline";
                return new { agentId = t.AgentId, name = t.Name, ok = error is null, message = error };
            }));
            store.Audit(user.Username, "power_all", null, $"{action}: {results.Count(r => r.ok)} of {results.Length} PCs");
            return Results.Ok(new { action, results });
        }));

        // ---------- people ----------

        api.MapGet("/users", (HttpContext ctx) =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            return Results.Ok(new { users = store.Read(d => d.Users.Select(u => UserDetailDto(u, d)).ToList()) });
        });

        api.MapPost("/users", H(async ctx =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            var b = await Body(ctx);
            if (UserError(b, isNew: true) is { } err) return Bad(err);
            string role = (string?)b!["role"] ?? Roles.Operator;
            if (!Roles.Valid(role)) return Bad("Choose a role");
            if (store.Read(d => d.Users.Any(u => u.Username.Equals(((string)b["username"]!).Trim(), StringComparison.OrdinalIgnoreCase)))) return Bad("That username is taken");
            var user = NewUser(b, role);
            store.Write(d => d.Users.Add(user));
            store.Audit(auth.FromRequest(ctx)!.Username, "user_added", null, $"{user.Username} ({role})");
            return Results.Ok(store.Read(d => UserDetailDto(user, d)));
        }));

        api.MapPut("/users/{id}", H2(async (ctx, id) =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            var me = auth.FromRequest(ctx)!;
            var b = await Body(ctx);
            string display = ((string?)b?["displayName"] ?? "").Trim();
            string role = (string?)b?["role"] ?? "";
            string? pw = (string?)b?["password"];
            if (display.Length is 0 or > 60) return Bad("Enter a name");
            if (!Roles.Valid(role)) return Bad("Choose a role");
            if (pw is not null && pw.Length > 0 && pw.Length < 8) return Bad("Use at least 8 characters for the password");
            string? error = store.Write(d =>
            {
                var u = d.Users.FirstOrDefault(x => x.Id == id);
                if (u is null) return "User not found";
                if (u.Role == Roles.Admin && role != Roles.Admin && d.Users.Count(x => x.Role == Roles.Admin) == 1) return "Keep at least one admin";
                u.DisplayName = display;
                u.Role = role;
                if (!string.IsNullOrEmpty(pw)) { var (h, s) = HubStore.HashPassword(pw); u.PasswordHash = h; u.PasswordSalt = s; u.Tokens.Clear(); }
                return null;
            });
            if (error is not null) return Bad(error);
            store.Audit(me.Username, "user_changed", null, $"{id} → {role}");
            return Results.Ok();
        }));

        api.MapDelete("/users/{id}", (HttpContext ctx, string id) =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            var me = auth.FromRequest(ctx)!;
            if (me.Id == id) return Bad("You can't remove yourself");
            string? error = store.Write(d =>
            {
                var u = d.Users.FirstOrDefault(x => x.Id == id);
                if (u is null) return "User not found";
                if (u.Role == Roles.Admin && d.Users.Count(x => x.Role == Roles.Admin) == 1) return "Keep at least one admin";
                d.Users.Remove(u);
                return null;
            });
            if (error is not null) return Bad(error);
            store.Audit(me.Username, "user_removed", null, id);
            return Results.Ok();
        });

        api.MapDelete("/users/{id}/sessions", (HttpContext ctx, string id) =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            bool ok = store.Write(d => { var u = d.Users.FirstOrDefault(x => x.Id == id); if (u is null) return false; u.Tokens.Clear(); return true; });
            return ok ? Results.Ok() : Results.NotFound();
        });

        // ---------- schedules ----------

        api.MapGet("/schedules", (HttpContext ctx) =>
        {
            if (Need(ctx, auth, Roles.Operator) is { } deny) return deny;
            var tz = store.Read(d => d.Workspace.TimeZone);
            return Results.Ok(new { timeZone = tz, schedules = store.Read(d => d.Schedules.ToList()) });
        });

        api.MapPost("/schedules", H(async ctx => await SaveSchedule(ctx, store, auth, null)));
        api.MapPut("/schedules/{id}", H2(async (ctx, id) => await SaveSchedule(ctx, store, auth, id)));

        api.MapDelete("/schedules/{id}", (HttpContext ctx, string id) =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            bool ok = store.Write(d => d.Schedules.RemoveAll(s => s.Id == id) > 0);
            return ok ? Results.Ok() : Results.NotFound();
        });

        api.MapPost("/schedules/{id}/run", async (HttpContext ctx, string id) =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            string? error = await schedules.RunAsync(id, auth.FromRequest(ctx)!.Username);
            return Results.Ok(new { ok = error is null, message = error ?? "Done" });
        });

        // ---------- shared rooms (tiles published from phones) ----------

        api.MapGet("/rooms", (HttpContext ctx) =>
        {
            if (Need(ctx, auth, Roles.Operator) is { } deny) return deny;
            return Results.Ok(new
            {
                rooms = store.Read(d => d.SharedRooms.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new { r.Id, r.Name, r.Kind, r.Tiles, r.Pcs, r.UpdatedBy, r.UpdatedAt }).ToList()),
            });
        });

        api.MapGet("/rooms/{id}", (HttpContext ctx, string id) =>
        {
            if (Need(ctx, auth, Roles.Operator) is { } deny) return deny;
            var room = store.Read(d => d.SharedRooms.FirstOrDefault(r => r.Id == id));
            return room is null ? Results.NotFound(new { error = "That shared room was removed" }) : Results.Ok(room);
        });

        api.MapPut("/rooms/{id}", H2(async (ctx, id) =>
        {
            if (Need(ctx, auth, Roles.Operator) is { } deny) return deny;
            var user = auth.FromRequest(ctx)!;
            if (!RoomIdRegex().IsMatch(id)) return Bad("Bad room id");
            if (ctx.Request.ContentLength > MaxPackBytes) return Bad("That room is too big to share");
            var b = await Body(ctx);
            if (b?["pack"] is not JsonObject pack) return Bad("Missing pack");
            if ((int?)pack["v"] != 1) return Bad("This app version shares rooms in a format the hub doesn't know. Update the hub.");
            string name = ((string?)pack["room"]?["name"] ?? "").Trim();
            if (name.Length is 0 or > 60) return Bad("The room needs a name (up to 60 characters)");
            int tiles = (pack["tiles"] as JsonArray)?.Count ?? 0, pcs = (pack["pcs"] as JsonArray)?.Count ?? 0;
            if (tiles > 200 || pcs > 100) return Bad("Too many tiles or PCs to share");
            var element = JsonSerializer.SerializeToElement(pack);
            bool existed = store.Write(d =>
            {
                var r = d.SharedRooms.FirstOrDefault(x => x.Id == id);
                bool had = r is not null;
                if (r is null) { r = new SharedRoom { Id = id }; d.SharedRooms.Add(r); }
                r.Name = name; r.Kind = (string?)pack["room"]?["kind"] ?? ""; r.Tiles = tiles; r.Pcs = pcs;
                r.UpdatedBy = user.DisplayName; r.UpdatedById = user.Id; r.UpdatedAt = DateTime.UtcNow; r.Pack = element;
                return had;
            });
            store.Audit(user.Username, existed ? "room_updated" : "room_shared", null, name);
            return Results.Ok(new { id, name, tiles, pcs, updated = existed });
        }));

        api.MapDelete("/rooms/{id}", (HttpContext ctx, string id) =>
        {
            if (Need(ctx, auth, Roles.Operator) is { } deny) return deny;
            var user = auth.FromRequest(ctx)!;
            var room = store.Read(d => d.SharedRooms.FirstOrDefault(r => r.Id == id));
            if (room is null) return Results.NotFound();
            if (user.Role != Roles.Admin && room.UpdatedById != user.Id) return Results.Problem("Only an admin or the person who shared it can remove this room", statusCode: 403);
            store.Write(d => d.SharedRooms.RemoveAll(r => r.Id == id));
            store.Audit(user.Username, "room_unshared", null, room.Name);
            return Results.Ok();
        });

        // ---------- activity ----------

        api.MapGet("/audit", (HttpContext ctx, int? limit) =>
        {
            if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
            return Results.Ok(new { entries = store.AuditTail(limit ?? 300) });
        });
    }

    // ---------- schedules ----------

    private static async Task<IResult> SaveSchedule(HttpContext ctx, HubStore store, HubAuth auth, string? id)
    {
        if (Need(ctx, auth, Roles.Admin) is { } deny) return deny;
        Schedule? s;
        try { s = (await Body(ctx))?.Deserialize<Schedule>(HubStore.Json); }
        catch (JsonException ex) { return Bad(ex.Message); }
        if (s is null) return Bad("Send the schedule as JSON");
        s.Name = s.Name.Trim();
        if (s.Name.Length is 0 or > 60) return Bad("Give the schedule a name");
        if (!ScheduleRunner.Actions.Contains(s.Action)) return Bad("Choose what to do");
        if (!TimeRegex().IsMatch(s.Time)) return Bad("Use a 24-hour time like 08:30");
        if (s.Days.Any(d => d is < 0 or > 6)) return Bad("Days must be 0 (Sunday) to 6 (Saturday)");
        if (s.Action is "run_scene" or "start_app" or "stop_app" && string.IsNullOrWhiteSpace(s.TargetId)) return Bad("Choose the scene or app");
        if (!store.Read(d => d.Agents.Any(a => a.AgentId == s.AgentId))) return Bad("Choose a PC");
        s.Days = s.Days.Distinct().OrderBy(d => d).ToList();

        var user = auth.FromRequest(ctx)!;
        if (id is null)
        {
            s.Id = HubStore.NewToken(9);
            s.LastRun = null;
            s.LastResult = null;
            store.Write(d => d.Schedules.Add(s));
        }
        else
        {
            bool ok = store.Write(d =>
            {
                var old = d.Schedules.FirstOrDefault(x => x.Id == id);
                if (old is null) return false;
                s.Id = id; s.LastRun = old.LastRun; s.LastResult = old.LastResult;
                d.Schedules[d.Schedules.IndexOf(old)] = s;
                return true;
            });
            if (!ok) return Results.NotFound();
        }
        store.Audit(user.Username, id is null ? "schedule_added" : "schedule_changed", s.AgentId, $"{s.Name} at {s.Time}");
        return Results.Ok(s);
    }

    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    private static partial Regex TimeRegex();

    // ---------- users ----------

    private static string? UserError(JsonObject? b, bool isNew)
    {
        string username = ((string?)b?["username"] ?? "").Trim();
        string display = ((string?)b?["displayName"] ?? "").Trim();
        string pw = (string?)b?["password"] ?? "";
        if (!Regex.IsMatch(username, "^[A-Za-z0-9._-]{3,32}$")) return "Usernames are 3–32 letters, digits, dots, dashes or underscores";
        if (display.Length > 60) return "That name is too long";
        if (isNew && pw.Length < 8) return "Use at least 8 characters for the password";
        return null;
    }

    private static HubUser NewUser(JsonObject b, string role)
    {
        var (hash, salt) = HubStore.HashPassword((string)b["password"]!);
        string username = ((string)b["username"]!).Trim();
        string display = ((string?)b["displayName"] ?? "").Trim();
        return new HubUser
        {
            Id = HubStore.NewToken(9), Username = username, DisplayName = display.Length > 0 ? display : username,
            Role = role, PasswordHash = hash, PasswordSalt = salt, CreatedAt = DateTime.UtcNow,
        };
    }

    private static object UserDto(HubUser u) => new { id = u.Id, username = u.Username, displayName = u.DisplayName, role = u.Role };

    private static object UserDetailDto(HubUser u, HubData d) => new
    {
        id = u.Id, username = u.Username, displayName = u.DisplayName, role = u.Role, createdAt = u.CreatedAt,
        kiosks = d.Kiosks.Where(k => k.ViewerIds.Contains(u.Id)).Select(k => k.Name).ToList(),
        devices = u.Tokens.OrderByDescending(t => t.LastUsed).Select(t => new { device = t.Device, lastUsed = t.LastUsed }).ToList(),
    };

    internal static object WorkspaceDto(HubStore store) => store.Read(d => new
    {
        name = d.Workspace.Name, accent = d.Workspace.Accent, timeZone = d.Workspace.TimeZone,
        logoUrl = d.Workspace.LogoFile is null ? null : $"/api/workspace/logo?v={d.Workspace.LogoVersion}",
    });

    private static object SignedIn(HubStore store, string token, HubUser user) => new { token, user = UserDto(user), workspace = WorkspaceDto(store) };

    // ---------- plumbing ----------

    private static readonly Dictionary<string, int> Rank = new() { [Roles.Viewer] = 0, [Roles.Operator] = 1, [Roles.Admin] = 2 };

    public static IResult? Require(HttpContext ctx, HubAuth auth, string role) => Need(ctx, auth, role);

    private static IResult? Need(HttpContext ctx, HubAuth auth, string role)
    {
        var u = auth.FromRequest(ctx);
        if (u is null) return Unauthorized();
        return Rank[u.Role] >= Rank[role] ? null : Results.Problem("You don't have permission to do that", statusCode: 403);
    }

    private static IResult Unauthorized() => Results.Problem("Sign in required", statusCode: 401);
    private static IResult Bad(string message) => Results.BadRequest(new { error = message });

    private const long MaxPackBytes = 512 * 1024;

    [GeneratedRegex("^[A-Za-z0-9_-]{4,64}$")]
    private static partial Regex RoomIdRegex();

    /// <summary>
    /// The caller's address. Behind a reverse proxy on this server (Apache, nginx, Caddy) it's the last
    /// X-Forwarded-For entry, the one the proxy added; anything earlier in that header came from the client
    /// and is ignored. Direct connections never trust the header.
    /// </summary>
    private static string Remote(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        string direct = ip?.ToString() ?? "?";
        if (ip is null || !System.Net.IPAddress.IsLoopback(ip)) return direct;
        string last = ctx.Request.Headers["X-Forwarded-For"].ToString().Split(',').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "";
        return last.Length > 0 ? last : direct;
    }

    private static async Task<JsonObject?> Body(HttpContext ctx)
    {
        try { return await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Forces the Delegate overload so the IResult is executed (a lone HttpContext lambda binds as RequestDelegate).</summary>
    private static Delegate H(Func<HttpContext, Task<IResult>> f) => f;
    private static Delegate H2(Func<HttpContext, string, Task<IResult>> f) => f;
}
