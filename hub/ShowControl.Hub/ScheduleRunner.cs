using System.Text.Json.Nodes;

namespace ShowControl.Hub;

/// <summary>Checks every 15 seconds for schedules due this minute (in the workspace time zone) and runs them.</summary>
public sealed class ScheduleRunner(HubStore store, AgentRegistry agents)
{
    /// <summary>Set when this hub is linked to others, so each schedule runs on one hub only.</summary>
    public HubSync? Sync { get; set; }

    public static readonly string[] Actions = { "run_scene", "start_app", "stop_app", "stop_all", "wake", "restart", "shutdown" };

    public async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await TickAsync(); }
            catch (Exception ex) { Console.WriteLine($"[hub] schedule tick failed: {ex.Message}"); }
        }
    }

    public static TimeZoneInfo Zone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        return TimeZoneInfo.Utc;
    }

    private async Task TickAsync()
    {
        var tz = Zone(store.Read(d => d.Workspace.TimeZone));
        var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        string hhmm = now.ToString("HH:mm");
        var due = store.Read(d => d.Schedules.Where(s =>
            s.Enabled && s.Time == hhmm &&
            (s.Days.Count == 0 || s.Days.Contains((int)now.DayOfWeek)) &&
            (s.LastRun is null || TimeZoneInfo.ConvertTime(s.LastRun.Value, tz).ToString("yyyyMMddHHmm") != now.ToString("yyyyMMddHHmm"))
        ).Select(s => s.Id).ToList());

        foreach (var id in due)
        {
            if (OtherHubRuns(id) is { } reason)
            {
                store.Write(d => { var x = d.Schedules.FirstOrDefault(y => y.Id == id); if (x is not null) { x.LastRun = DateTime.UtcNow; x.LastResult = reason; } });
                continue;
            }
            await RunAsync(id, "schedule", now.ToString("yyyyMMddHHmm"));
        }
    }

    /// <summary>
    /// With a site hub and the cloud hub linked, the hub the PC is connected to locally runs its schedules;
    /// the cloud only runs them for PCs no linked site can reach. Returns why this hub skips it, or null.
    /// </summary>
    private string? OtherHubRuns(string scheduleId)
    {
        if (Sync is not { } sync) return null;
        var s = store.Read(d => d.Schedules.FirstOrDefault(x => x.Id == scheduleId));
        if (s is null) return null;
        if (sync.HasSitePeers && (s.Action == "wake" ? true : sync.OnlineAtSite(s.AgentId))) return "Done by the site hub";
        if (sync.UpstreamConnected && s.Action != "wake" && !agents.IsOnline(s.AgentId)) return "Left to the cloud hub (PC not connected here)";
        return null;
    }

    /// <summary>Runs a schedule now. Returns null on success or the reason it failed.</summary>
    public async Task<string?> RunAsync(string scheduleId, string by, string? minute = null)
    {
        var s = store.Read(d => d.Schedules.FirstOrDefault(x => x.Id == scheduleId));
        if (s is null) return "Schedule not found";
        // Mark first so a slow PC can't make the same minute fire twice.
        store.Write(d => { var x = d.Schedules.FirstOrDefault(y => y.Id == scheduleId); if (x is not null) x.LastRun = DateTime.UtcNow; });

        string? error;
        if (s.Action == "wake")
        {
            error = await WakeAsync(s.AgentId, $"Schedule: {s.Name}");
        }
        else
        {
            var cmd = s.Action switch
            {
                "run_scene" => new JsonObject { ["type"] = "run_scene", ["sceneId"] = s.TargetId },
                "start_app" => new JsonObject { ["type"] = "start_app", ["appId"] = s.TargetId },
                "stop_app" => new JsonObject { ["type"] = "stop_app", ["appId"] = s.TargetId },
                "stop_all" => new JsonObject { ["type"] = "stop_all" },
                // Needs "Allow restart and shut down" on the PC's dashboard; otherwise the PC refuses and the schedule shows why.
                "restart" or "shutdown" => new JsonObject { ["type"] = "power", ["action"] = s.Action },
                _ => null,
            };
            // If two linked hubs ever fire the same minute, the PC only acts on the first.
            if (cmd is not null && minute is not null) cmd["dedupe"] = $"sch:{s.Id}:{minute}";
            error = cmd is null ? $"Unknown action {s.Action}" : await SystemCommand.RunAsync(agents, s.AgentId, $"Schedule: {s.Name}", cmd);
        }

        string result = error is null ? "Done" : $"Failed: {error}";
        store.Write(d => { var x = d.Schedules.FirstOrDefault(y => y.Id == scheduleId); if (x is not null) x.LastResult = result; });
        store.Audit(by == "schedule" ? $"schedule:{s.Name}" : by, "schedule_run", s.AgentId, $"{s.Action} {s.TargetName ?? s.TargetId} → {result}");
        Console.WriteLine($"[hub] schedule '{s.Name}': {result}");
        return error;
    }

    /// <summary>Wakes a PC by asking any other online PC to broadcast its magic packet.</summary>
    public async Task<string?> WakeAsync(string agentId, string label)
    {
        if (agents.IsOnline(agentId)) return null; // already awake
        var target = store.Read(d => d.Agents.FirstOrDefault(a => a.AgentId == agentId));
        if (target is null) return "PC not found";
        string? mac = target.Macs.FirstOrDefault();
        if (mac is null) return $"{target.Name} hasn't reported its MAC address yet (it must connect to the hub once while on)";
        var relay = agents.Online.FirstOrDefault(a => a.AgentId != agentId);
        if (relay is null) return "No other PC is online to send the wake signal from";
        return await SystemCommand.RunAsync(agents, relay.AgentId, label, new JsonObject { ["type"] = "wake", ["mac"] = mac });
    }
}
