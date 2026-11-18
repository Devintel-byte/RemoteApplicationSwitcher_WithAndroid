using System.Net.Sockets;

namespace ShowControl.Agent;

/// <summary>
/// Runs a scene's steps in order on this PC. Starting a new scene cancels the one in progress,
/// so a mis-tap can always be corrected by tapping the right scene.
/// </summary>
public sealed class SceneRunner(ProcessManager processes, Func<AgentConfig> config)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;

    /// <summary>sceneId, runId, step (1-based), total, label, state (running|done|failed), message</summary>
    public event Action<string, string, int, int, string, string, string?>? Progress;

    public void Run(SceneDefinition scene)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _current?.Cancel();
            _current = cts = new CancellationTokenSource();
        }
        _ = RunAsync(scene, cts.Token);
    }

    /// <summary>Runs one step on its own (dashboard "Test" button). Null on success, else the reason.</summary>
    public async Task<string?> TestStepAsync(SceneStep step)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(step.TimeoutSec, 5, 120) + 5));
        try { return await ExecuteAsync(step, cts.Token); }
        catch (OperationCanceledException) { return "Timed out"; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Like DefaultLabel, but with the app's display name instead of its id.</summary>
    private string FriendlyLabel(SceneStep s)
    {
        string? name = s.AppId is null ? null : config().Apps.FirstOrDefault(a => a.Id == s.AppId)?.Name;
        return s.Action.ToLowerInvariant() switch
        {
            "start" when name is not null => $"Start {name}",
            "stop" when name is not null => $"Close {name}",
            "restart" when name is not null => $"Restart {name}",
            _ => DefaultLabel(s),
        };
    }

    private async Task RunAsync(SceneDefinition scene, CancellationToken ct)
    {
        string runId = Guid.NewGuid().ToString("N")[..8];
        int total = scene.Steps.Count;
        Log.Info($"Scene '{scene.Id}' run {runId} started ({total} steps)");

        for (int i = 0; i < total; i++)
        {
            var step = scene.Steps[i];
            string label = step.Label ?? FriendlyLabel(step);
            if (ct.IsCancellationRequested)
            {
                Progress?.Invoke(scene.Id, runId, i + 1, total, label, "failed", "Cancelled by another scene");
                return;
            }

            Progress?.Invoke(scene.Id, runId, i + 1, total, label, "running", null);
            try
            {
                string? error = await ExecuteAsync(step, ct);
                if (error is not null)
                {
                    Progress?.Invoke(scene.Id, runId, i + 1, total, label, "failed", error);
                    Log.Warn($"Scene '{scene.Id}' step {i + 1} failed: {error}");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                Progress?.Invoke(scene.Id, runId, i + 1, total, label, "failed", "Cancelled by another scene");
                return;
            }
            catch (Exception ex)
            {
                Progress?.Invoke(scene.Id, runId, i + 1, total, label, "failed", ex.Message);
                Log.Error($"Scene '{scene.Id}' step {i + 1} threw", ex);
                return;
            }
        }

        Progress?.Invoke(scene.Id, runId, total, total, "Done", "done", null);
        Log.Info($"Scene '{scene.Id}' run {runId} done");
    }

    private async Task<string?> ExecuteAsync(SceneStep step, CancellationToken ct)
    {
        switch (step.Action.Trim().ToLowerInvariant())
        {
            case "start":
                if (!RequireApp(step, out var startId, out var err1)) return err1;
                await processes.StartAsync(startId);
                if (!step.WaitForRunning) return null;
                return await WaitForState(startId, step.TimeoutSec, ct);

            case "stop":
                if (!RequireApp(step, out var stopId, out var err2)) return err2;
                await processes.StopAsync(stopId);
                return null;

            case "restart":
                if (!RequireApp(step, out var rid, out var err3)) return err3;
                await processes.RestartAsync(rid);
                return step.WaitForRunning ? await WaitForState(rid, step.TimeoutSec, ct) : null;

            case "stopall":
                await processes.StopAllAsync();
                return null;

            case "wait":
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(step.Seconds, 0, 3600)), ct);
                return null;

            case "waitforport":
                return await WaitForPort(step.Port, step.TimeoutSec, ct);

            case "osc":
                if (step.Port <= 0) return "osc step needs a port";
                await Osc.SendAsync(Host(step), step.Port, step.Address ?? "", step.Args, ct);
                return null;

            case "udp":
                if (step.Port <= 0) return "udp step needs a port";
                await UdpText.SendAsync(Host(step), step.Port, step.Text ?? "", ct);
                return null;

            case "http":
                if (string.IsNullOrWhiteSpace(step.Url)) return "http step needs a url";
                return await HttpCue.SendAsync(step.Url!, step.Method, step.Body, ct);

            case "obs":
                if (string.IsNullOrWhiteSpace(step.Request)) return "obs step needs a request (e.g. SetCurrentProgramScene)";
                return await ObsClient.RequestAsync(config().Obs, step.Request!, step.Data, ct);

            case "wake":
                if (string.IsNullOrWhiteSpace(step.Mac)) return "wake step needs a mac";
                await WakeOnLan.SendAsync(step.Mac!, ct);
                return null;

            default:
                return $"Unknown step action '{step.Action}'";
        }
    }

    private static string Host(SceneStep s) => string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host!.Trim();

    private bool RequireApp(SceneStep step, out string id, out string? error)
    {
        id = step.AppId ?? "";
        error = processes.Exists(id) ? null : $"Unknown app '{step.AppId}'";
        return error is null;
    }

    private async Task<string?> WaitForState(string appId, int timeoutSec, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSec));
        while (DateTime.UtcNow < deadline)
        {
            var state = processes.GetState(appId);
            if (state == AppState.Running) return null;
            if (state == AppState.Crashed) return $"{appId} failed to start";
            await Task.Delay(250, ct);
        }
        return $"{appId} not running after {timeoutSec}s";
    }

    private static async Task<string?> WaitForPort(int port, int timeoutSec, CancellationToken ct)
    {
        if (port is <= 0 or > 65535) return "waitForPort needs a valid port";
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSec));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(1000);
                await client.ConnectAsync("127.0.0.1", port, attempt.Token);
                return null;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
            }
        }
        return $"Port {port} not open after {timeoutSec}s";
    }

    private static string DefaultLabel(SceneStep s) => s.Action.ToLowerInvariant() switch
    {
        "start" => $"Start {s.AppId}",
        "stop" => $"Stop {s.AppId}",
        "restart" => $"Restart {s.AppId}",
        "stopall" => "Stop all apps",
        "wait" => $"Wait {s.Seconds:0.#} s",
        "waitforport" => $"Wait for port {s.Port}",
        "osc" => $"OSC {s.Address}",
        "udp" => $"UDP to {Host(s)}:{s.Port}",
        "http" => $"{(s.Method ?? "GET").ToUpperInvariant()} {s.Url}",
        "obs" => $"OBS {s.Request}",
        "wake" => $"Wake {s.Mac}",
        _ => s.Action,
    };
}
