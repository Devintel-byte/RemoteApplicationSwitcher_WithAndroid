using System.ComponentModel;
using System.Diagnostics;

namespace ShowControl.Agent;

public enum AppState { Stopped, Starting, Running, Stopping, Crashed }

public sealed record AppSnapshot(
    string Id, string Name, string? Color, string? Icon, AppState State,
    int? Pid, DateTime Since, string? Message, bool Watchdog)
{
    public object ToWire() => new
    {
        id = Id, name = Name, color = Color, icon = Icon,
        state = State.ToString().ToLowerInvariant(),
        pid = Pid, since = Since, message = Message, watchdog = Watchdog,
    };
}

/// <summary>
/// Launches, closes and watches registered apps. A 1-second poll keeps state honest even if
/// someone starts or closes an app by hand on the PC.
/// </summary>
public sealed class ProcessManager : IDisposable
{
    private sealed class Runtime(AppDefinition def)
    {
        public AppDefinition Def = def;
        public AppState State = AppState.Stopped;
        public int? Pid;
        public DateTime Since = DateTime.UtcNow;
        public string? Message;
        public bool StopRequested;
        public DateTime LaunchedAt;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Queue<DateTime> WatchdogRestarts = new();
    }

    private const int WatchdogMaxRestarts = 5;
    private static readonly TimeSpan WatchdogWindow = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly Dictionary<string, Runtime> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private readonly System.Threading.Timer _timer;
    private int _polling;

    public event Action<AppSnapshot>? StatusChanged;
    public event Action<AppSnapshot>? Crashed;

    public ProcessManager()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, 1000, 1000);
    }

    public void SetDefinitions(IEnumerable<AppDefinition> defs)
    {
        lock (_lock)
        {
            var incoming = defs.ToList();
            foreach (var id in _apps.Keys.Where(k => incoming.All(d => !d.Id.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
                _apps.Remove(id);
            _order.Clear();
            foreach (var def in incoming)
            {
                if (_apps.TryGetValue(def.Id, out var rt)) rt.Def = def;
                else _apps[def.Id] = new Runtime(def);
                _order.Add(def.Id);
            }
        }
        Poll();
    }

    public bool Exists(string id)
    {
        lock (_lock) return _apps.ContainsKey(id);
    }

    public AppState? GetState(string id)
    {
        lock (_lock) return _apps.TryGetValue(id, out var rt) ? rt.State : null;
    }

    public IReadOnlyList<AppSnapshot> Snapshot()
    {
        lock (_lock) return _order.Select(id => Snap(_apps[id])).ToList();
    }

    public IReadOnlyList<string> Ids()
    {
        lock (_lock) return _order.ToList();
    }

    // ---------- commands ----------

    public async Task StartAsync(string id)
    {
        Runtime? rt;
        lock (_lock) _apps.TryGetValue(id, out rt);
        if (rt is null) return;

        await rt.Gate.WaitAsync();
        try
        {
            if (rt.State is AppState.Running or AppState.Starting) return;

            var def = rt.Def;
            var existing = Find(def);
            if (existing.Length > 0)
            {
                Publish(rt, AppState.Running, null, existing[0].Id);
                DisposeAll(existing);
                return;
            }

            if (System.IO.Path.IsPathRooted(def.Path) && !File.Exists(def.Path))
            {
                Publish(rt, AppState.Crashed, $"Executable not found: {def.Path}", null);
                Log.Warn($"[{def.Id}] executable not found: {def.Path}");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = def.Path,
                Arguments = def.Arguments ?? "",
                UseShellExecute = true,
                WindowStyle = def.WindowStyle.ToLowerInvariant() switch
                {
                    "maximized" => ProcessWindowStyle.Maximized,
                    "minimized" => ProcessWindowStyle.Minimized,
                    _ => ProcessWindowStyle.Normal,
                },
            };
            string? workDir = def.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(workDir) && System.IO.Path.IsPathRooted(def.Path))
                workDir = System.IO.Path.GetDirectoryName(def.Path);
            if (!string.IsNullOrWhiteSpace(workDir)) psi.WorkingDirectory = workDir;

            lock (_lock)
            {
                rt.StopRequested = false;
                rt.LaunchedAt = DateTime.UtcNow;
            }
            Publish(rt, AppState.Starting, null, null);
            Log.Info($"[{def.Id}] starting {def.Path} {def.Arguments}");

            try
            {
                using var p = Process.Start(psi);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                Publish(rt, AppState.Crashed, $"Could not start: {ex.Message}", null);
                Log.Error($"[{def.Id}] start failed", ex);
            }
        }
        finally
        {
            rt.Gate.Release();
        }
    }

    public async Task StopAsync(string id, bool force = false)
    {
        Runtime? rt;
        lock (_lock) _apps.TryGetValue(id, out rt);
        if (rt is null) return;

        await rt.Gate.WaitAsync();
        try
        {
            var def = rt.Def;
            lock (_lock) rt.StopRequested = true;

            var procs = Find(def);
            if (procs.Length == 0)
            {
                Publish(rt, AppState.Stopped, null, null);
                return;
            }

            Publish(rt, AppState.Stopping, force ? "Force closing" : null, rt.Pid);
            Log.Info($"[{def.Id}] stopping ({(force ? "force" : "graceful")})");

            if (!force)
            {
                foreach (var p in procs)
                {
                    try { p.CloseMainWindow(); } catch { /* no window / access denied */ }
                }
                DisposeAll(procs);
                if (await WaitUntilGone(def, TimeSpan.FromSeconds(Math.Max(1, def.GracefulTimeoutSec))))
                {
                    Publish(rt, AppState.Stopped, null, null);
                    return;
                }
                Log.Warn($"[{def.Id}] did not close within {def.GracefulTimeoutSec}s, killing");
                procs = Find(def);
            }

            foreach (var p in procs)
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception ex) { Log.Warn($"[{def.Id}] kill failed for pid {SafeId(p)}: {ex.Message}"); }
            }
            DisposeAll(procs);

            bool gone = await WaitUntilGone(def, TimeSpan.FromSeconds(5));
            Publish(rt, gone ? AppState.Stopped : AppState.Running,
                gone ? null : "Could not close (try running the agent as the same user as the app)", null);
        }
        finally
        {
            rt.Gate.Release();
        }
    }

    public async Task RestartAsync(string id)
    {
        await StopAsync(id);
        await Task.Delay(500);
        await StartAsync(id);
    }

    public Task StopAllAsync(bool force = false) =>
        Task.WhenAll(Ids().Select(id => StopAsync(id, force)));

    // ---------- monitoring ----------

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            List<Runtime> list;
            lock (_lock) list = _order.Select(id => _apps[id]).ToList();

            foreach (var rt in list)
            {
                var procs = Find(rt.Def);
                bool running = procs.Length > 0;
                int? pid = running ? SafeId(procs[0]) : null;
                bool hasWindow = running && procs.Any(HasWindow);
                DisposeAll(procs);

                AppSnapshot? changed = null;
                bool crashedNow = false;
                lock (_lock)
                {
                    var elapsed = DateTime.UtcNow - rt.LaunchedAt;
                    switch (rt.State)
                    {
                        case AppState.Starting:
                            if (running && (hasWindow || elapsed.TotalSeconds >= rt.Def.ReadyDelaySec))
                                changed = Set(rt, AppState.Running, null, pid);
                            else if (!running && elapsed.TotalSeconds >= rt.Def.StartTimeoutSec)
                                changed = Set(rt, AppState.Crashed, $"Did not start within {rt.Def.StartTimeoutSec}s", null);
                            break;

                        case AppState.Running:
                            if (!running)
                            {
                                if (rt.StopRequested) changed = Set(rt, AppState.Stopped, null, null);
                                else { changed = Set(rt, AppState.Crashed, "Exited unexpectedly", null); crashedNow = true; }
                            }
                            else if (pid != rt.Pid) changed = Set(rt, AppState.Running, rt.Message, pid);
                            break;

                        case AppState.Stopped:
                        case AppState.Crashed:
                            if (running) changed = Set(rt, AppState.Running, "Started outside Show Control", pid);
                            break;
                    }
                }

                if (changed is not null) StatusChanged?.Invoke(changed);
                if (crashedNow && changed is not null)
                {
                    Log.Warn($"[{rt.Def.Id}] exited unexpectedly");
                    Crashed?.Invoke(changed);
                    if (rt.Def.Watchdog) _ = WatchdogRestart(rt);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Poll failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private async Task WatchdogRestart(Runtime rt)
    {
        lock (_lock)
        {
            while (rt.WatchdogRestarts.Count > 0 && DateTime.UtcNow - rt.WatchdogRestarts.Peek() > WatchdogWindow)
                rt.WatchdogRestarts.Dequeue();
            if (rt.WatchdogRestarts.Count >= WatchdogMaxRestarts)
            {
                var snap = Set(rt, AppState.Crashed, $"Watchdog gave up after {WatchdogMaxRestarts} restarts in 10 min", null);
                StatusChanged?.Invoke(snap);
                return;
            }
            rt.WatchdogRestarts.Enqueue(DateTime.UtcNow);
        }
        await Task.Delay(2000);
        if (GetState(rt.Def.Id) == AppState.Crashed)
        {
            Log.Info($"[{rt.Def.Id}] watchdog restart");
            await StartAsync(rt.Def.Id);
        }
    }

    // ---------- helpers ----------

    private void Publish(Runtime rt, AppState state, string? message, int? pid)
    {
        AppSnapshot snap;
        lock (_lock) snap = Set(rt, state, message, pid);
        StatusChanged?.Invoke(snap);
    }

    private static AppSnapshot Set(Runtime rt, AppState state, string? message, int? pid)
    {
        if (rt.State != state) rt.Since = DateTime.UtcNow;
        rt.State = state;
        rt.Message = message;
        rt.Pid = pid;
        return Snap(rt);
    }

    private static AppSnapshot Snap(Runtime rt) =>
        new(rt.Def.Id, rt.Def.Name, rt.Def.Color, rt.Def.Icon, rt.State, rt.Pid, rt.Since, rt.Message, rt.Def.Watchdog);

    private static Process[] Find(AppDefinition def)
    {
        try { return Process.GetProcessesByName(def.EffectiveProcessName); }
        catch { return Array.Empty<Process>(); }
    }

    private static async Task<bool> WaitUntilGone(AppDefinition def, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var procs = Find(def);
            bool any = procs.Length > 0;
            DisposeAll(procs);
            if (!any) return true;
            await Task.Delay(250);
        }
        return false;
    }

    private static bool HasWindow(Process p)
    {
        try { p.Refresh(); return p.MainWindowHandle != IntPtr.Zero; }
        catch { return false; }
    }

    private static int? SafeId(Process p)
    {
        try { return p.Id; } catch { return null; }
    }

    private static void DisposeAll(Process[] procs)
    {
        foreach (var p in procs) p.Dispose();
    }

    public void Dispose() => _timer.Dispose();
}
