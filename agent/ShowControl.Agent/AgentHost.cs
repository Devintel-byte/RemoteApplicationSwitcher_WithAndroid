using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ShowControl.Agent;

/// <summary>Wires config, process manager, scenes, the WebSocket server and discovery together.</summary>
public sealed class AgentHost : IAsyncDisposable
{
    public const string Version = "1.6.0";

    private System.Threading.Timer? _statsTimer;
    private object _latestStats = new { cpu = 0, ramUsedMb = 0, ramTotalMb = 0, uptimeSec = 0 };

    public ConfigStore Config { get; }
    public ProcessManager Processes { get; } = new();
    public SceneRunner Scenes { get; }
    public SessionHub Hub { get; } = new();
    public AdminAuth Admin { get; } = new();
    /// <summary>The hub this PC enrolled with.</summary>
    public HubLink HubLink { get; }
    /// <summary>Linked hubs it also connects to with the same key (see <see cref="HubSettings.Also"/>).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HubLink> _extraLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<(string Url, string Kind)>> _announced = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<HubLink> HubLinks => new[] { HubLink }.Concat(_extraLinks.Values).ToList();
    public KioskManager Kiosk { get; }
    /// <summary>Set by the Windows tray app; null in tests.</summary>
    public IPlatform? Platform { get; set; }
    private readonly SystemStats _stats = new();
    private readonly AgentServer _server = new();
    private readonly Discovery _discovery;

    /// <summary>Raised when a watched app exits without being asked to.</summary>
    public event Action<AppSnapshot>? AppCrashed;

    public AgentHost(string configPath)
    {
        Config = new ConfigStore(configPath);
        Scenes = new SceneRunner(Processes, () => Config.Current);
        _discovery = new Discovery(() => Config.Current);
        HubLink = new HubLink(this);
        Kiosk = new KioskManager(this);
    }

    public AgentConfig Current => Config.Current;

    /// <summary>Set by the Windows tray app; null when capture isn't available (e.g. tests).</summary>
    public IScreenCapture? ScreenCapture { get; set; }

    public async Task StartAsync()
    {
        Config.Load();
        Processes.SetDefinitions(Current.Apps);

        Processes.StatusChanged += s => Hub.Broadcast(new
        {
            type = "app_status", appId = s.Id, state = s.State.ToString().ToLowerInvariant(),
            pid = s.Pid, since = s.Since, message = s.Message,
        });
        Processes.Crashed += s => AppCrashed?.Invoke(s);
        Scenes.Progress += (sceneId, runId, step, total, label, state, message) => Hub.Broadcast(new
        {
            type = "scene_progress", sceneId, runId, step, total, label, state, message,
        });
        Config.Reloaded += cfg =>
        {
            Processes.SetDefinitions(cfg.Apps);
            Hub.Broadcast(new { type = "config_changed" });
        };

        _latestStats = _stats.Sample();
        _statsTimer = new System.Threading.Timer(_ =>
        {
            _latestStats = _stats.Sample();
            var s = _latestStats;
            Hub.Broadcast(MergeType("stats", s));
        }, null, 1000, 1000);

        await _server.StartAsync(Current.Port, this);
        try { _discovery.Start(Current.DiscoveryPort); }
        catch (Exception ex) { Log.Error($"Discovery could not bind UDP {Current.DiscoveryPort} (manual IP entry still works)", ex); }

        Log.Info($"Agent '{Current.Name}' v{Version} listening on port {Current.Port}; IPs: {string.Join(", ", LocalIps())}");

        HubLink.Start();
        SyncExtraHubs();
        Kiosk.Start();
        _ = RunStartupAsync();
    }

    // ---------------- linked hubs ----------------

    /// <summary>Starts or stops connections to the linked hubs listed in the config.</summary>
    public void SyncExtraHubs()
    {
        var hub = Current.Hub;
        string? primary = HubLink.Normalize(hub.Url);
        var want = hub.Enabled && !string.IsNullOrEmpty(hub.AgentKey)
            ? hub.Also.Select(a => HubLink.Normalize(a.Url) ?? "").Where(u => u.Length > 0 && !string.Equals(u, primary, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();
        foreach (var url in _extraLinks.Keys.Where(u => !want.Contains(u)).ToList())
            if (_extraLinks.TryRemove(url, out var gone)) { _ = gone.StopAsync(); Log.Info($"Linked hub removed: {url}"); }
        foreach (var url in want.Where(u => !_extraLinks.ContainsKey(u)))
        {
            var link = new HubLink(this, url);
            _extraLinks[url] = link;
            Log.Info($"Also connecting to linked hub {url}");
            link.Start();
        }
        ChooseKiosk();
    }

    /// <summary>A hub said which other hubs it's linked to: connect to those as well.</summary>
    internal void HubsAnnounced(HubLink from, List<(string Url, string Kind)> hubs)
    {
        if (from.Url is null) return;
        _announced[from.Url] = hubs;
        string? primary = HubLink.Normalize(Current.Hub.Url);
        var autos = _announced.Values.SelectMany(x => x)
            .Where(h => !string.Equals(h.Url, primary, StringComparison.OrdinalIgnoreCase))
            .GroupBy(h => h.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        // Until the hub this PC enrolled with has spoken, keep the ones remembered from last time.
        if (primary is not null && !_announced.ContainsKey(primary))
            autos.AddRange(Current.Hub.Also.Where(a => a.Auto && autos.All(x => !string.Equals(x.Url, a.Url, StringComparison.OrdinalIgnoreCase))).Select(a => (a.Url, a.Kind)));
        var current = Current.Hub.Also;
        var next = current.Where(a => !a.Auto).ToList();
        next.AddRange(autos.Where(h => next.All(m => !string.Equals(m.Url, h.Url, StringComparison.OrdinalIgnoreCase)))
            .Select(h => new ExtraHub { Url = h.Url, Kind = h.Kind, Auto = true }));
        bool same = next.Count == current.Count && next.All(n => current.Any(c => string.Equals(c.Url, n.Url, StringComparison.OrdinalIgnoreCase) && c.Auto == n.Auto));
        if (!same)
        {
            Config.Mutate(c => c.Hub.Also = next);
            Log.Info($"Linked hubs now: {(next.Count == 0 ? "none" : string.Join(", ", next.Select(n => n.Url)))}");
        }
        SyncExtraHubs();
    }

    internal void HubDisconnected(HubLink link) => ChooseKiosk();

    /// <summary>
    /// The room kiosk comes from whichever connected hub is on this network (so it keeps working without
    /// internet), else the hub this PC enrolled with, else any other connected hub.
    /// </summary>
    public void ChooseKiosk(bool fromHub = false)
    {
        var pick = HubLinks.Where(l => l.State == "connected" && l.KioskOrder is not null)
            .OrderByDescending(l => l.IsLan).ThenByDescending(l => l.IsPrimary).FirstOrDefault();
        if (pick?.KioskOrder is not { } order) return;
        var d = Current.Kiosk;
        // Only a hub's own instruction reopens a kiosk closed from the tray; a change of hub only switches the link.
        if (!fromHub && d.Url == order.Url && d.Locked == order.Locked && d.Name == order.Name) return;
        Kiosk.Apply(order.Url, order.Locked, order.Name);
    }

    public async Task StopExtraHubsAsync()
    {
        foreach (var url in _extraLinks.Keys.ToList())
            if (_extraLinks.TryRemove(url, out var l)) await l.StopAsync();
        _announced.Clear();
    }

    /// <summary>Launches "start with agent" apps, then the startup scene, after the startup delay.</summary>
    private async Task RunStartupAsync()
    {
        var cfg = Current;
        var apps = cfg.Apps.Where(a => a.StartWithAgent).Select(a => a.Id).ToList();
        var scene = string.IsNullOrWhiteSpace(cfg.Startup.SceneId) ? null
            : cfg.Scenes.FirstOrDefault(s => s.Id.Equals(cfg.Startup.SceneId, StringComparison.OrdinalIgnoreCase));
        if (apps.Count == 0 && scene is null) return;

        int delay = Math.Clamp(cfg.Startup.DelaySec, 0, 600);
        Log.Info($"Startup: {apps.Count} app(s){(scene is null ? "" : $" then scene '{scene.Name}'")} in {delay}s");
        await Task.Delay(TimeSpan.FromSeconds(delay));
        foreach (var id in apps)
        {
            await Processes.StartAsync(id);
            await Task.Delay(1500);
        }
        if (scene is not null) Scenes.Run(scene);
    }

    /// <summary>Applies a change made from the dashboard: validate, save, reload apps, tell controllers.</summary>
    public void Update(Action<AgentConfig> change)
    {
        Config.Mutate(c =>
        {
            change(c);
            ConfigStore.Validate(c);
        });
        Processes.SetDefinitions(Current.Apps);
        Hub.Broadcast(new { type = "config_changed" });
    }

    public object LatestStats => _latestStats;

    // ---------- auth ----------

    public string? Pair(string pin, string clientName)
    {
        string expected = Current.PairingPin;
        if (string.IsNullOrEmpty(expected) || !FixedEquals(pin.Trim(), expected)) return null;

        string token = ConfigStore.NewToken();
        Config.Mutate(c => c.Clients.Add(new PairedClient
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Name = string.IsNullOrWhiteSpace(clientName) ? "Controller" : clientName.Trim()[..Math.Min(60, clientName.Trim().Length)],
            Token = token,
            PairedAt = DateTime.UtcNow,
        }));
        Log.Info($"Paired new controller '{clientName}'");
        return token;
    }

    public PairedClient? Authenticate(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        return Current.Clients.FirstOrDefault(c => FixedEquals(c.Token, token));
    }

    public string RegeneratePin()
    {
        string pin = ConfigStore.NewPin();
        Config.Mutate(c => c.PairingPin = pin);
        return pin;
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // ---------- state ----------

    public object BuildState(string type, string? id, string? role = null) => new
    {
        type,
        id,
        role,
        agent = new
        {
            agentId = Current.AgentId,
            name = Current.Name,
            hostname = Environment.MachineName,
            version = Version,
            ips = LocalIps(),
            macs = WakeOnLan.LocalMacs(),
            allowPower = Current.AllowPower,
            canScreenshot = ScreenCapture is not null,
            canStream = ScreenCapture is not null,
            displays = SafeDisplays(),
        },
        apps = Processes.Snapshot().Select(a => a.ToWire()).ToList(),
        scenes = Current.Scenes.Select(s => new
        {
            id = s.Id, name = s.Name, color = s.Color, icon = s.Icon, group = s.Group,
            confirm = s.Confirm, steps = s.Steps.Count,
        }).ToList(),
        stats = _latestStats,
    };

    public List<object> SafeDisplays()
    {
        try
        {
            return ScreenCapture?.Displays().Select(d => (object)new { index = d.Index, name = d.Name, width = d.Width, height = d.Height, primary = d.Primary }).ToList()
                ?? new List<object>();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list displays: {ex.Message}");
            return new List<object>();
        }
    }

    private static Dictionary<string, object?> MergeType(string type, object payload)
    {
        var dict = new Dictionary<string, object?> { ["type"] = type };
        foreach (var p in payload.GetType().GetProperties()) dict[p.Name] = p.GetValue(payload);
        return dict;
    }

    public static List<string> LocalIps()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address.ToString());
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Could not list IPs: {ex.Message}"); }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        _statsTimer?.Dispose();
        Kiosk.Dispose();
        await StopExtraHubsAsync();
        await HubLink.DisposeAsync();
        _discovery.Dispose();
        await _server.DisposeAsync();
        Processes.Dispose();
        Config.Dispose();
    }
}
