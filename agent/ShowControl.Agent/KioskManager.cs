namespace ShowControl.Agent;

/// <summary>What the hub asked this PC to show as its room kiosk (saved so it reopens after a reboot).</summary>
public sealed class KioskSettings
{
    /// <summary>Full link to the kiosk screen on the hub, or null when this PC shows no kiosk.</summary>
    public string? Url { get; set; }
    /// <summary>Locked full screen (Edge kiosk mode) instead of a normal window.</summary>
    public bool Locked { get; set; } = true;
    public string? Name { get; set; }
}

/// <summary>
/// Opens the room's kiosk screen in Edge when the hub says so, keeps it open in kiosk mode (reopens it
/// within a few seconds if it's closed or crashes), and closes it when the hub turns it off or staff use
/// the exit PIN. The browser itself is driven through <see cref="IPlatform"/> (Windows only).
/// </summary>
public sealed class KioskManager : IDisposable
{
    private readonly AgentHost _host;
    private readonly object _gate = new();
    private System.Threading.Timer? _watchdog;
    private bool _closedHere;
    private DateTime _lastOpen = DateTime.MinValue;

    public KioskManager(AgentHost host) => _host = host;

    public KioskSettings Desired => _host.Current.Kiosk;
    public bool Running => _host.Platform?.KioskRunning ?? false;
    /// <summary>Closed from the tray menu on this PC; stays closed until reopened there, by the hub, or a restart.</summary>
    public bool ClosedHere => _closedHere;

    public void Start()
    {
        // Give Windows a moment after sign-in before going full screen.
        _watchdog = new System.Threading.Timer(_ => Check(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>From the hub: show this kiosk (url != null) or nothing.</summary>
    public void Apply(string? url, bool locked, string? name)
    {
        bool changed;
        lock (_gate)
        {
            var cur = Desired;
            changed = cur.Url != url || cur.Locked != locked || cur.Name != name;
            if (changed) _host.Config.Mutate(c => c.Kiosk = new KioskSettings { Url = url, Locked = locked, Name = name });
            _closedHere = false;
        }
        if (url is null)
        {
            if (Running || changed) Log.Info("Kiosk: closed (turned off on the hub)");
            _host.Platform?.CloseKiosk();
            return;
        }
        if (changed || !Running)
        {
            Log.Info($"Kiosk: showing '{name}' {(locked ? "in kiosk mode" : "in a window")}");
            Open();
        }
    }

    public void CloseHere()
    {
        lock (_gate) _closedHere = true;
        _host.Platform?.CloseKiosk();
        Log.Info("Kiosk: closed on this PC (tray menu)");
    }

    public void OpenHere()
    {
        lock (_gate) _closedHere = false;
        if (Desired.Url is not null) Open();
    }

    private void Open()
    {
        var d = Desired;
        if (d.Url is null || _host.Platform is null) return;
        _lastOpen = DateTime.UtcNow;
        try { _host.Platform.OpenKiosk(d.Url, d.Locked); }
        catch (Exception ex) { Log.Error("Kiosk: couldn't open the browser", ex); }
    }

    /// <summary>Every few seconds: in kiosk mode, reopen the screen if it was closed or crashed.</summary>
    private void Check()
    {
        var d = Desired;
        if (d.Url is null || _host.Platform is null || _closedHere) return;
        if (Running) return;
        // In a normal window, closing it is the operator's choice; only kiosk mode insists. At startup, open either way.
        bool firstOpen = _lastOpen == DateTime.MinValue;
        if (!d.Locked && !firstOpen) return;
        if (DateTime.UtcNow - _lastOpen < TimeSpan.FromSeconds(4)) return;
        if (!firstOpen) Log.Warn("Kiosk: the screen was closed; reopening");
        Open();
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        // Leave the kiosk to close with the agent so a restart of the agent doesn't flash the desktop.
    }
}
