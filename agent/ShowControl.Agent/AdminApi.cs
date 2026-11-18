using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ShowControl.Agent;

/// <summary>
/// REST endpoints behind the dashboard. Every request must:
///   • name this PC in its Host header (blocks DNS-rebinding),
///   • come from the dashboard's own origin and carry X-ShowControl: 1 (blocks other websites),
///   • be from this PC, or carry a session from the remote-admin password.
/// </summary>
public static class AdminApi
{
    private static readonly string[] StepActions = { "start", "stop", "restart", "stopall", "wait", "waitforport", "osc", "udp", "http", "obs", "wake" };
    private static readonly HashSet<string> RunnableExt = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".bat", ".cmd", ".lnk", ".ps1", ".com", ".url" };
    private const string LocalFlag = "sc.local";

    public static void Map(WebApplication app, AgentHost host)
    {
        var api = app.MapGroup("/api/admin");
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!HostAllowed(http)) return Results.Problem("Unknown host name", statusCode: 421);
            if (!OriginAllowed(http)) return Results.Problem("Cross-site request blocked", statusCode: 403);
            if (http.Request.Headers["X-ShowControl"] != "1") return Results.Problem("Missing X-ShowControl header", statusCode: 400);

            bool local = AdminAuth.IsLoopback(http.Connection.RemoteIpAddress);
            http.Items[LocalFlag] = local;
            string path = http.Request.Path.Value ?? "";
            bool open = path.EndsWith("/session") || path.EndsWith("/login");
            if (!local && !open)
            {
                if (!host.Current.RemoteAdmin.Enabled) return Results.Problem("The dashboard only opens on this PC. Turn on remote access in Settings.", statusCode: 403);
                string? bearer = http.Request.Headers.Authorization.ToString() is { } a && a.StartsWith("Bearer ") ? a[7..] : null;
                if (!host.Admin.IsSessionToken(bearer)) return Results.Problem("Sign in required", statusCode: 401);
            }
            return await next(ctx);
        });

        // ---------- session ----------

        api.MapGet("/session", (HttpContext http) =>
        {
            bool local = IsLocal(http);
            string? bearer = http.Request.Headers.Authorization.ToString() is { } a && a.StartsWith("Bearer ") ? a[7..] : null;
            if (local) return Results.Ok(new { local = true, token = host.Admin.CreateSession(local: true) });
            if (host.Admin.IsSessionToken(bearer)) return Results.Ok(new { local = false, token = bearer });
            return Results.Ok(new { local = false, token = (string?)null, remoteEnabled = host.Current.RemoteAdmin.Enabled });
        });

        api.MapPost("/login", H(async http =>
        {
            string remote = http.Connection.RemoteIpAddress?.ToString() ?? "?";
            var ra = host.Current.RemoteAdmin;
            if (!ra.Enabled || string.IsNullOrEmpty(ra.PasswordHash)) return Results.Problem("Remote access is turned off on this PC", statusCode: 403);
            if (host.Admin.IsLockedOut(remote)) return Results.Problem("Too many attempts. Try again in 5 minutes.", statusCode: 429);
            var body = await ReadJson(http);
            string password = (string?)body?["password"] ?? "";
            if (!AdminAuth.VerifyPassword(password, ra.PasswordHash, ra.PasswordSalt))
            {
                host.Admin.RecordFailure(remote);
                Log.Warn($"Dashboard: wrong password from {remote}");
                return Results.Problem("Wrong password", statusCode: 401);
            }
            host.Admin.ClearFailures(remote);
            Log.Info($"Dashboard: remote sign-in from {remote}");
            return Results.Ok(new { token = host.Admin.CreateSession() });
        }));

        // ---------- state ----------

        api.MapGet("/state", (HttpContext http) => Results.Ok(BuildState(host, IsLocal(http))));

        api.MapGet("/logs", () => Results.Ok(new { lines = Log.Tail(300) }));

        // ---------- settings ----------

        api.MapPut("/settings", H(async http =>
        {
            var b = await ReadJson(http);
            if (b is null) return Bad("Send the settings as JSON");
            string name = ((string?)b["name"] ?? "").Trim();
            if (name.Length is 0 or > 60) return Bad("Give this PC a name (up to 60 characters)");
            int port = Int(b, "port") ?? host.Current.Port;
            int dport = Int(b, "discoveryPort") ?? host.Current.DiscoveryPort;
            if (port is < 1024 or > 65535 || dport is < 1024 or > 65535 || port == dport) return Bad("Ports must be different numbers between 1024 and 65535");
            string? startupScene = ((string?)b["startupSceneId"])?.Trim();
            if (!string.IsNullOrEmpty(startupScene) && host.Current.Scenes.All(s => s.Id != startupScene)) return Bad("The startup scene doesn't exist");
            bool remoteOn = Bool(b, "remoteAdminEnabled") ?? host.Current.RemoteAdmin.Enabled;
            string? newPassword = (string?)b["remoteAdminPassword"];
            if (newPassword is not null && newPassword.Length < 8) return Bad("Use at least 8 characters for the remote password");
            if (remoteOn && newPassword is null && string.IsNullOrEmpty(host.Current.RemoteAdmin.PasswordHash)) return Bad("Set a password before turning on remote access");

            bool restart = port != host.Current.Port || dport != host.Current.DiscoveryPort;
            host.Update(c =>
            {
                c.Name = name;
                c.Port = port;
                c.DiscoveryPort = dport;
                c.AllowPower = Bool(b, "allowPower") ?? c.AllowPower;
                c.Obs.Host = ((string?)b["obsHost"])?.Trim() is { Length: > 0 } oh ? oh : c.Obs.Host;
                c.Obs.Port = Int(b, "obsPort") is int op and > 0 and < 65536 ? op : c.Obs.Port;
                if ((string?)b["obsPassword"] is { } opw) c.Obs.Password = opw;
                c.Startup.DelaySec = Math.Clamp(Int(b, "startupDelaySec") ?? c.Startup.DelaySec, 0, 600);
                c.Startup.SceneId = string.IsNullOrEmpty(startupScene) ? null : startupScene;
                c.RemoteAdmin.Enabled = remoteOn;
                if (newPassword is not null)
                {
                    var (hash, salt) = AdminAuth.HashPassword(newPassword);
                    c.RemoteAdmin.PasswordHash = hash;
                    c.RemoteAdmin.PasswordSalt = salt;
                }
            });
            if (newPassword is not null || !remoteOn) host.Admin.RevokeRemote();
            Log.Info("Dashboard: settings saved");
            return Results.Ok(new { restartNeeded = restart });
        }));

        api.MapPost("/pin", () => Results.Ok(new { pin = host.RegeneratePin() }));

        api.MapDelete("/clients/{id}", (string id) =>
        {
            var client = host.Current.Clients.FirstOrDefault(c => c.Id == id);
            if (client is null) return Results.NotFound();
            host.Update(c => c.Clients.RemoveAll(x => x.Id == id));
            foreach (var s in host.Hub.All.Where(s => s.PairedClientId == id)) _ = s.CloseAsync("Access removed");
            Log.Info($"Dashboard: removed phone '{client.Name}'");
            return Results.Ok();
        });

        // Make a paired phone a viewer (watch only) or an operator again. Its open connection is
        // closed so it reconnects straight away with the new role.
        api.MapPut("/clients/{id}", H(async http =>
        {
            string id = (string)http.Request.RouteValues["id"]!;
            var body = await ReadJson(http);
            string role = body is null ? "" : ClientSession.Str(body, "role") ?? "";
            if (role is not (Roles.Operator or Roles.Viewer)) return Results.BadRequest(new { error = "role must be operator or viewer" });
            var client = host.Current.Clients.FirstOrDefault(c => c.Id == id);
            if (client is null) return Results.NotFound();
            host.Update(c => { var x = c.Clients.First(x => x.Id == id); x.Role = role; });
            foreach (var s in host.Hub.All.Where(s => s.PairedClientId == id)) _ = s.CloseAsync("Role changed");
            Log.Info($"Dashboard: '{client.Name}' is now {role}");
            return Results.Ok(new { id, role });
        }));

        // ---------- apps ----------

        api.MapPost("/apps", H(http => SaveApp(http, host, null)));
        api.MapPut("/apps/{id}", async (HttpContext http, string id) => await SaveApp(http, host, id));
        api.MapDelete("/apps/{id}", async (string id) =>
        {
            if (host.Current.Apps.All(a => a.Id != id)) return Results.NotFound();
            var usedBy = host.Current.Scenes.Where(s => s.Steps.Any(st => st.AppId == id)).Select(s => s.Name).ToList();
            if (usedBy.Count > 0) return Bad($"Used by: {string.Join(", ", usedBy)}. Remove those steps first.");
            if (host.Processes.GetState(id) is AppState.Running) await host.Processes.StopAsync(id);
            host.Update(c => c.Apps.RemoveAll(a => a.Id == id));
            Log.Info($"Dashboard: removed app '{id}'");
            return Results.Ok();
        });

        // ---------- scenes ----------

        api.MapPost("/scenes", H(http => SaveScene(http, host, null)));
        api.MapPut("/scenes/{id}", async (HttpContext http, string id) => await SaveScene(http, host, id));
        api.MapDelete("/scenes/{id}", (string id) =>
        {
            if (host.Current.Scenes.All(s => s.Id != id)) return Results.NotFound();
            host.Update(c =>
            {
                c.Scenes.RemoveAll(s => s.Id == id);
                if (c.Startup.SceneId == id) c.Startup.SceneId = null;
            });
            Log.Info($"Dashboard: removed scene '{id}'");
            return Results.Ok();
        });

        api.MapPut("/order", H(async http =>
        {
            var b = await ReadJson(http);
            var apps = b?["apps"]?.AsArray().Select(n => (string?)n).OfType<string>().ToList();
            var scenes = b?["scenes"]?.AsArray().Select(n => (string?)n).OfType<string>().ToList();
            host.Update(c =>
            {
                if (apps is not null) c.Apps = c.Apps.OrderBy(a => Rank(apps, a.Id)).ToList();
                if (scenes is not null) c.Scenes = c.Scenes.OrderBy(s => Rank(scenes, s.Id)).ToList();
            });
            return Results.Ok();
        }));

        api.MapPost("/test-step", H(async http =>
        {
            var body = await ReadJson(http);
            SceneStep? step;
            try { step = body?.Deserialize<SceneStep>(ConfigStore.FileOptions); }
            catch (JsonException ex) { return Bad(ex.Message); }
            if (step is null) return Bad("Send a step");
            if (ValidateStep(step, host.Current) is { } err) return Bad(err);
            string? error = await host.Scenes.TestStepAsync(step);
            return Results.Ok(new { ok = error is null, message = error ?? "Worked" });
        }));

        api.MapPost("/test-obs", async () =>
        {
            string? error = await ObsClient.RequestAsync(host.Current.Obs, "GetVersion", null, CancellationToken.None);
            return Results.Ok(new { ok = error is null, message = error ?? "Connected to OBS" });
        });

        // ---------- picking files on this PC ----------

        api.MapGet("/fs", (string? path, string? mode) => Results.Ok(Browse(path, mode ?? "file")));

        api.MapPost("/pick", H(async http =>
        {
            if (host.Platform is null) return Results.Problem("Not available on this agent", statusCode: 501);
            if (!IsLocal(http)) return Results.Problem("The Windows file picker opens on this PC's screen, so it's only offered there. Use Browse instead.", statusCode: 409);
            var b = await ReadJson(http);
            string kind = (string?)b?["kind"] ?? "file";
            string? initial = (string?)b?["initial"];
            string? picked = kind == "folder"
                ? await host.Platform.PickFolderAsync(initial)
                : await host.Platform.PickFileAsync(initial, kind == "project" ? "All files|*.*" : "Programs|*.exe;*.bat;*.cmd;*.lnk|All files|*.*");
            return Results.Ok(new { path = picked });
        }));

        api.MapPost("/inspect", H(async http =>
        {
            var b = await ReadJson(http);
            string path = ((string?)b?["path"] ?? "").Trim().Trim('"');
            if (path.Length == 0) return Bad("No path");
            return Results.Ok(Inspect(path, host));
        }));

        api.MapGet("/processes", () => Results.Ok(new { processes = WindowedProcesses() }));

        // ---------- Windows integration ----------

        api.MapPut("/start-with-windows", H(async http =>
        {
            if (host.Platform is null) return Results.Problem("Not available on this agent", statusCode: 501);
            bool on = Bool(await ReadJson(http), "enabled") ?? false;
            host.Platform.SetStartWithWindows(on);
            return Results.Ok(new { enabled = host.Platform.GetStartWithWindows() });
        }));

        api.MapPost("/restart", () =>
        {
            if (host.Platform is null) return Results.Problem("Not available on this agent", statusCode: 501);
            _ = Task.Run(async () => { await Task.Delay(500); host.Platform.RestartAgent(); });
            return Results.Ok();
        });

        // ---------- hub ----------

        api.MapPost("/hub/enroll", H(async http =>
        {
            var b = await ReadJson(http);
            string url = ((string?)b?["url"] ?? "").Trim();
            string code = ((string?)b?["code"] ?? "").Trim();
            if (!Uri.TryCreate(url.Contains("://") ? url : "http://" + url, UriKind.Absolute, out _)) return Bad("Enter the hub address, e.g. https://hub.example.com");
            if (code.Length < 4) return Bad("Enter the enrollment code from the hub's PCs page");
            string? error = await host.HubLink.EnrollAsync(url, code);
            return error is null ? Results.Ok() : Bad(error);
        }));

        api.MapPost("/hub/disconnect", async () =>
        {
            await host.HubLink.StopAsync();
            await host.StopExtraHubsAsync();
            host.Update(c => c.Hub = new HubSettings());
            host.Kiosk.Apply(null, true, null); // a kiosk comes from the hub; without the hub it can't work
            Log.Info("Dashboard: disconnected from hub");
            return Results.Ok();
        });

        api.MapPost("/hub/reconnect", async () =>
        {
            foreach (var l in host.HubLinks) await l.RestartAsync();
            return Results.Ok();
        });

        // Another hub linked to this PC's hub (e.g. the venue's site hub): same key, so no code is needed.
        api.MapPost("/hub/also", H(async http =>
        {
            var b = await ReadJson(http);
            string url = ((string?)b?["url"] ?? "").Trim().TrimEnd('/');
            url = HubLink.Normalize(url) ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return Bad("Enter the hub address, e.g. http://192.168.1.10:47900");
            if (!host.Current.Hub.Enabled) return Bad("Connect this PC to its hub first (with a code)");
            if (string.Equals(url, HubLink.Normalize(host.Current.Hub.Url), StringComparison.OrdinalIgnoreCase) || host.Current.Hub.Also.Any(a => string.Equals(a.Url, url, StringComparison.OrdinalIgnoreCase)))
                return Bad("This PC already connects to that hub");
            host.Update(c => c.Hub.Also.Add(new ExtraHub { Url = url, Kind = HubLink.IsLanUrl(url) ? "site" : "cloud" }));
            host.SyncExtraHubs();
            Log.Info($"Dashboard: also connecting to {url}");
            return Results.Ok();
        }));

        api.MapPost("/hub/also/remove", H(async http =>
        {
            var b = await ReadJson(http);
            string url = ((string?)b?["url"] ?? "").Trim().TrimEnd('/');
            host.Update(c => c.Hub.Also.RemoveAll(a => string.Equals(a.Url, url, StringComparison.OrdinalIgnoreCase)));
            host.SyncExtraHubs();
            return Results.Ok();
        }));
    }

    /// <summary>Forces the Delegate overload so the IResult is executed (a lone HttpContext lambda would bind as RequestDelegate).</summary>
    private static Delegate H(Func<HttpContext, Task<IResult>> handler) => handler;

    // ---------- state ----------

    private static object BuildState(AgentHost host, bool local)
    {
        var cfg = host.Current;
        var runtime = host.Processes.Snapshot().ToDictionary(a => a.Id);
        bool? startWithWindows = null;
        try { startWithWindows = host.Platform?.GetStartWithWindows(); } catch { }
        return new
        {
            local,
            canPick = host.Platform is not null && local,
            agent = new
            {
                agentId = cfg.AgentId, name = cfg.Name, hostname = Environment.MachineName, version = AgentHost.Version,
                ips = AgentHost.LocalIps(), macs = WakeOnLan.LocalMacs(), displays = host.SafeDisplays(),
                canScreenshot = host.ScreenCapture is not null,
                canStream = host.ScreenCapture is not null,
            },
            pin = cfg.PairingPin,
            configPath = host.Config.Path,
            logFolder = Log.Folder,
            startWithWindows,
            settings = new
            {
                name = cfg.Name, port = cfg.Port, discoveryPort = cfg.DiscoveryPort, allowPower = cfg.AllowPower,
                obsHost = cfg.Obs.Host, obsPort = cfg.Obs.Port, obsHasPassword = !string.IsNullOrEmpty(cfg.Obs.Password),
                startupDelaySec = cfg.Startup.DelaySec, startupSceneId = cfg.Startup.SceneId,
                remoteAdminEnabled = cfg.RemoteAdmin.Enabled, remoteAdminHasPassword = !string.IsNullOrEmpty(cfg.RemoteAdmin.PasswordHash),
            },
            apps = cfg.Apps.Select(a =>
            {
                runtime.TryGetValue(a.Id, out var r);
                return new
                {
                    def = a,
                    state = (r?.State ?? AppState.Stopped).ToString().ToLowerInvariant(),
                    pid = r?.Pid, since = r?.Since, message = r?.Message,
                    exists = !System.IO.Path.IsPathRooted(a.Path) || File.Exists(a.Path),
                };
            }).ToList(),
            scenes = ScenesForDashboard(cfg.Scenes),
            clients = cfg.Clients.Select(c => new { id = c.Id, name = c.Name, pairedAt = c.PairedAt, role = c.Role == Roles.Viewer ? Roles.Viewer : Roles.Operator }).ToList(),
            connections = host.Hub.All.Select(s => new { name = s.ClientName, role = s.Role, remote = s.Remote, viaHub = s.ViaHub }).ToList(),
            hub = new
            {
                enabled = cfg.Hub.Enabled, url = HubLink.Normalize(cfg.Hub.Url), state = host.HubLink.State, lastError = host.HubLink.LastError,
                kiosk = cfg.Kiosk.Url is null ? null : new { name = cfg.Kiosk.Name, locked = cfg.Kiosk.Locked, running = host.Kiosk.Running, closedHere = host.Kiosk.ClosedHere },
                workspace = host.HubLinks.Select(l => l.Workspace).FirstOrDefault(w => w is not null), remoteSessions = host.HubLinks.Sum(l => l.RemoteSessions),
                // Every hub this PC connects to: the one it enrolled with first, then linked ones.
                hubs = host.HubLinks.Select(l => new
                {
                    url = l.Url, primary = l.IsPrimary, lan = l.IsLan, state = l.State, lastError = l.LastError,
                    kind = l.IsPrimary ? (l.IsLan ? "site" : "cloud") : cfg.Hub.Also.FirstOrDefault(a => string.Equals(a.Url, l.Url, StringComparison.OrdinalIgnoreCase))?.Kind ?? "manual",
                    auto = !l.IsPrimary && (cfg.Hub.Also.FirstOrDefault(a => string.Equals(a.Url, l.Url, StringComparison.OrdinalIgnoreCase))?.Auto ?? false),
                    sessions = l.RemoteSessions,
                }).Where(h => h.url is not null).ToList(),
                // Where the hub's shared pages (kiosks, people, schedules…) open: a hub on this network first.
                siteUrl = host.HubLinks.Where(l => l.Url is not null).OrderByDescending(l => l.State == "connected").ThenByDescending(l => l.IsLan).ThenByDescending(l => l.IsPrimary).Select(l => l.Url).FirstOrDefault(),
            },
            stats = host.LatestStats,
        };
    }

    /// <summary>Scenes with each step's OSC args also given as text, so 1.0 survives the round trip through the browser.</summary>
    private static JsonNode ScenesForDashboard(List<SceneDefinition> scenes)
    {
        var node = JsonSerializer.SerializeToNode(scenes, Wire.Options)!.AsArray();
        for (int i = 0; i < scenes.Count; i++)
        {
            var steps = node[i]!["steps"]!.AsArray();
            for (int j = 0; j < scenes[i].Steps.Count; j++)
                if (scenes[i].Steps[j].Args is { } a) steps[j]!["argsText"] = Osc.FormatArgs(a);
        }
        return node;
    }

    // ---------- apps & scenes ----------

    private static async Task<IResult> SaveApp(HttpContext http, AgentHost host, string? id)
    {
        AppDefinition? a;
        try { a = (await ReadJson(http))?.Deserialize<AppDefinition>(ConfigStore.FileOptions); }
        catch (JsonException ex) { return Bad(ex.Message); }
        if (a is null) return Bad("Send the app as JSON");

        a.Name = a.Name.Trim();
        a.Path = a.Path.Trim().Trim('"');
        if (a.Name.Length is 0 or > 60) return Bad("Give the app a name");
        if (a.Path.Length == 0) return Bad("Choose the program to launch");
        a.WorkingDirectory = string.IsNullOrWhiteSpace(a.WorkingDirectory) ? null : a.WorkingDirectory.Trim().Trim('"');
        a.ProcessName = string.IsNullOrWhiteSpace(a.ProcessName) ? null : a.ProcessName.Trim();
        a.Arguments = string.IsNullOrWhiteSpace(a.Arguments) ? null : a.Arguments.Trim();
        a.WindowStyle = a.WindowStyle is "maximized" or "minimized" ? a.WindowStyle : "normal";
        a.GracefulTimeoutSec = Math.Clamp(a.GracefulTimeoutSec, 1, 120);
        a.ReadyDelaySec = Math.Clamp(a.ReadyDelaySec, 0, 120);
        a.StartTimeoutSec = Math.Clamp(a.StartTimeoutSec, 5, 600);

        if (id is null)
        {
            a.Id = string.IsNullOrWhiteSpace(a.Id) ? ConfigStore.MakeId(a.Name, host.Current.Apps.Select(x => x.Id)) : a.Id.Trim();
            if (!ConfigStore.IdRegex().IsMatch(a.Id)) return Bad("The id can only use letters, digits, - and _");
            if (host.Current.Apps.Any(x => x.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase))) return Bad($"An app with id '{a.Id}' already exists");
            host.Update(c => c.Apps.Add(a));
            Log.Info($"Dashboard: added app '{a.Name}' ({a.Path})");
        }
        else
        {
            if (host.Current.Apps.All(x => x.Id != id)) return Results.NotFound();
            a.Id = id; // ids are stable: phones and scenes refer to them
            host.Update(c => c.Apps = c.Apps.Select(x => x.Id == id ? a : x).ToList());
            Log.Info($"Dashboard: updated app '{a.Name}'");
        }
        return Results.Ok(a);
    }

    private static async Task<IResult> SaveScene(HttpContext http, AgentHost host, string? id)
    {
        SceneDefinition? s;
        try { s = (await ReadJson(http))?.Deserialize<SceneDefinition>(ConfigStore.FileOptions); }
        catch (JsonException ex) { return Bad(ex.Message); }
        if (s is null) return Bad("Send the scene as JSON");

        s.Name = s.Name.Trim();
        if (s.Name.Length is 0 or > 60) return Bad("Give the scene a name");
        s.Group = string.IsNullOrWhiteSpace(s.Group) ? null : s.Group.Trim();
        if (s.Steps.Count == 0) return Bad("Add at least one step");
        for (int i = 0; i < s.Steps.Count; i++)
        {
            var step = s.Steps[i];
            step.Label = string.IsNullOrWhiteSpace(step.Label) ? null : step.Label.Trim();
            if (ValidateStep(step, host.Current) is { } err) return Bad($"Step {i + 1}: {err}");
        }

        if (id is null)
        {
            s.Id = string.IsNullOrWhiteSpace(s.Id) ? ConfigStore.MakeId(s.Name, host.Current.Scenes.Select(x => x.Id)) : s.Id.Trim();
            if (!ConfigStore.IdRegex().IsMatch(s.Id)) return Bad("The id can only use letters, digits, - and _");
            if (host.Current.Scenes.Any(x => x.Id.Equals(s.Id, StringComparison.OrdinalIgnoreCase))) return Bad($"A scene with id '{s.Id}' already exists");
            host.Update(c => c.Scenes.Add(s));
            Log.Info($"Dashboard: added scene '{s.Name}'");
        }
        else
        {
            if (host.Current.Scenes.All(x => x.Id != id)) return Results.NotFound();
            s.Id = id;
            host.Update(c => c.Scenes = c.Scenes.Select(x => x.Id == id ? s : x).ToList());
            Log.Info($"Dashboard: updated scene '{s.Name}'");
        }
        return Results.Ok(s);
    }

    internal static string? ValidateStep(SceneStep step, AgentConfig cfg)
    {
        if (step.ArgsText is not null)
        {
            try { step.Args = Osc.ParseArgsText(step.ArgsText); }
            catch (JsonException) { return "check the OSC arguments"; }
            step.ArgsText = null;
        }
        string action = step.Action.Trim().ToLowerInvariant();
        if (!StepActions.Contains(action)) return $"unknown action '{step.Action}'";
        switch (action)
        {
            case "start" or "stop" or "restart":
                if (cfg.Apps.All(a => a.Id != step.AppId)) return "choose an app";
                break;
            case "wait":
                if (step.Seconds is <= 0 or > 3600) return "wait between 0.1 and 3600 seconds";
                break;
            case "waitforport":
            case "udp":
                if (step.Port is <= 0 or > 65535) return "enter a port between 1 and 65535";
                break;
            case "osc":
                if (step.Port is <= 0 or > 65535) return "enter a port between 1 and 65535";
                if (string.IsNullOrWhiteSpace(step.Address) || !step.Address.StartsWith('/')) return "the OSC address must start with /";
                break;
            case "http":
                if (!Uri.TryCreate(step.Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return "enter a full http:// or https:// address";
                break;
            case "obs":
                if (string.IsNullOrWhiteSpace(step.Request)) return "choose an OBS request";
                break;
            case "wake":
                try { WakeOnLan.ParseMac(step.Mac ?? ""); } catch (ArgumentException) { return "enter a MAC address like AA:BB:CC:DD:EE:FF"; }
                break;
        }
        step.TimeoutSec = Math.Clamp(step.TimeoutSec, 1, 600);
        return null;
    }

    // ---------- files & processes ----------

    private static object Browse(string? path, string mode)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var places = new List<object>();
            void Add(string label, Environment.SpecialFolder f)
            {
                string p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) places.Add(new { name = label, path = p, kind = "place" });
            }
            Add("Desktop", Environment.SpecialFolder.DesktopDirectory);
            Add("Documents", Environment.SpecialFolder.MyDocuments);
            Add("Program Files", Environment.SpecialFolder.ProgramFiles);
            Add("Program Files (x86)", Environment.SpecialFolder.ProgramFilesX86);
            Add("Start menu programs", Environment.SpecialFolder.CommonPrograms);
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.CDRom) places.Add(new { name = string.IsNullOrEmpty(d.VolumeLabel) ? d.Name : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})", path = d.RootDirectory.FullName, kind = "drive" }); }
                catch { }
            }
            return new { path = (string?)null, parent = (string?)null, entries = places };
        }

        var dir = new DirectoryInfo(path);
        if (!dir.Exists) return new { path, parent = dir.Parent?.FullName, entries = Array.Empty<object>(), error = "Folder not found" };
        var entries = new List<object>();
        string? error = null;
        try
        {
            foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (d.Attributes.HasFlag(FileAttributes.Hidden) || d.Attributes.HasFlag(FileAttributes.System)) continue;
                entries.Add(new { name = d.Name, path = d.FullName, kind = "dir" });
                if (entries.Count > 1500) break;
            }
            if (mode != "folder")
            {
                foreach (var f in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (f.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                    bool runnable = RunnableExt.Contains(f.Extension);
                    if (mode == "file" && !runnable) continue;
                    entries.Add(new { name = f.Name, path = f.FullName, kind = runnable ? "program" : "file", size = f.Length });
                    if (entries.Count > 2000) break;
                }
            }
        }
        catch (UnauthorizedAccessException) { error = "Windows doesn't allow reading this folder"; }
        catch (IOException ex) { error = ex.Message; }
        return new { path = dir.FullName, parent = dir.Parent?.FullName, entries, error };
    }

    private static object Inspect(string path, AgentHost host)
    {
        string target = path;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && host.Platform is WinPlatformShortcuts sc)
            target = sc.ResolveShortcut(path) ?? path;

        bool rooted = System.IO.Path.IsPathRooted(target);
        bool exists = !rooted || File.Exists(target);
        string? description = null;
        try
        {
            if (rooted && exists && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var info = FileVersionInfo.GetVersionInfo(target);
                description = new[] { info.FileDescription, info.ProductName }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
            }
        }
        catch { }
        string processName = System.IO.Path.GetFileNameWithoutExtension(target);
        bool running = false;
        try { var ps = Process.GetProcessesByName(processName); running = ps.Length > 0; foreach (var p in ps) p.Dispose(); } catch { }

        return new
        {
            path = target,
            originalPath = path,
            exists,
            suggestedName = description ?? Prettify(processName),
            processName,
            workingDirectory = rooted ? System.IO.Path.GetDirectoryName(target) : null,
            running,
            alreadyAdded = host.Current.Apps.Where(a => a.Path.Equals(target, StringComparison.OrdinalIgnoreCase)).Select(a => a.Name).FirstOrDefault(),
        };
    }

    private static string Prettify(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].Replace('_', ' ').Replace('-', ' ');

    private static List<object> WindowedProcesses()
    {
        var list = new List<(string Name, object Item)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    string? file = null;
                    try { file = p.MainModule?.FileName; } catch { /* elevated or 32/64-bit mismatch */ }
                    string key = file ?? p.ProcessName;
                    if (!seen.Add(key)) continue;
                    list.Add((p.ProcessName, new { name = p.ProcessName, pid = p.Id, title = p.MainWindowTitle, path = file }));
                }
                catch { }
            }
        }
        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.Item).ToList();
    }

    // ---------- helpers ----------

    private static bool HostAllowed(HttpContext http)
    {
        string h = http.Request.Host.Host.Trim('[', ']');
        if (h is "localhost" or "127.0.0.1" or "::1") return true;
        if (h.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(h, out var ip) && AgentHost.LocalIps().Contains(ip.ToString())) return true;
        return false;
    }

    internal static bool OriginAllowed(HttpContext http)
    {
        string origin = http.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // non-browser clients
        return Uri.TryCreate(origin, UriKind.Absolute, out var o)
               && string.Equals(o.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocal(HttpContext http) => http.Items[LocalFlag] is true;

    private static int Rank(List<string> order, string id) { int i = order.IndexOf(id); return i < 0 ? int.MaxValue : i; }

    private static async Task<JsonObject?> ReadJson(HttpContext http)
    {
        try { return await JsonNode.ParseAsync(http.Request.Body) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static int? Int(JsonObject? o, string key)
    {
        try { return o?[key]?.GetValue<int>(); } catch { return int.TryParse((string?)o?[key], out int v) ? v : null; }
    }

    private static bool? Bool(JsonObject? o, string key)
    {
        try { return o?[key]?.GetValue<bool>(); } catch { return null; }
    }

    private static IResult Bad(string message) => Results.BadRequest(new { error = message });
}

/// <summary>Optional platform capability: resolving Windows shortcuts to their target.</summary>
public interface WinPlatformShortcuts
{
    string? ResolveShortcut(string lnkPath);
}
