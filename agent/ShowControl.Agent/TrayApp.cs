using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ShowControl.Agent;

/// <summary>The agent lives in the system tray. The menu shows address and pairing PIN.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly AgentHost _host;
    private readonly NotifyIcon _icon;
    private readonly SynchronizationContext _ui;
    private readonly ToolStripMenuItem _title = new() { Enabled = false };
    private readonly ToolStripMenuItem _address = new() { Enabled = false };
    private readonly ToolStripMenuItem _pin = new() { Enabled = false };
    private readonly ToolStripMenuItem _clients = new() { Enabled = false };
    private readonly ToolStripMenuItem _startup = new("Start with Windows") { CheckOnClick = true };
    private readonly ToolStripMenuItem _kiosk = new("Kiosk screen") { Visible = false };
    private bool _started;

    public TrayApp(string configPath)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _host = new AgentHost(configPath) { ScreenCapture = new WinScreenCapture(), Platform = new WinPlatform() };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_title);
        menu.Items.Add(_address);
        menu.Items.Add(_pin);
        menu.Items.Add(_clients);
        menu.Items.Add(new ToolStripSeparator());
        var dashboard = new ToolStripMenuItem("Open dashboard", null, (_, _) => WinPlatform.OpenDashboard(_host.Current.Port));
        dashboard.Font = new Font(dashboard.Font, FontStyle.Bold);
        menu.Items.Add(dashboard);
        menu.Items.Add("New pairing PIN", null, (_, _) => { _host.RegeneratePin(); Refresh(); });
        menu.Items.Add("Open log folder", null, (_, _) => { Directory.CreateDirectory(Log.Folder); Open(Log.Folder); });
        // Staff on site: close the room kiosk to use this PC, then open it again (or it reopens at the next restart).
        _kiosk.Click += (_, _) => { if (_host.Kiosk.Running) _host.Kiosk.CloseHere(); else _host.Kiosk.OpenHere(); };
        menu.Items.Add(_kiosk);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _host.Platform?.CloseKiosk(); ExitThread(); });
        menu.Opening += (_, _) => Refresh();

        _startup.CheckedChanged += (_, _) =>
        {
            try { StartupRegistration.Set(_startup.Checked); }
            catch (Exception ex) { Log.Error("Could not change startup setting", ex); }
        };

        _icon = new NotifyIcon
        {
            Icon = DrawIcon(),
            Text = "Show Control Agent",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => WinPlatform.OpenDashboard(_host.Current.Port);
        _icon.BalloonTipClicked += (_, _) => WinPlatform.OpenDashboard(_host.Current.Port);

        _host.AppCrashed += snap => _ui.Post(_ =>
            _icon.ShowBalloonTip(4000, "App stopped unexpectedly",
                $"{snap.Name} exited.{(snap.Watchdog ? " Watchdog is restarting it." : "")}", ToolTipIcon.Warning), null);

        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await _host.StartAsync();
            _started = true;
            _ui.Post(_ =>
            {
                Refresh();
                _icon.ShowBalloonTip(3000, "Show Control Agent is running",
                    $"{_host.Current.Name} · PIN {_host.Current.PairingPin}. Double-click the tray icon to open the dashboard.", ToolTipIcon.Info);
                // First run on this PC: open the dashboard so apps can be added straight away.
                if (_host.Config.CreatedFresh) WinPlatform.OpenDashboard(_host.Current.Port);
            }, null);
        }
        catch (Exception ex)
        {
            Log.Error("Agent failed to start", ex);
            _ui.Post(_ => _icon.ShowBalloonTip(8000, "Show Control Agent could not start",
                $"Port {_host.Current.Port} may be in use. See the log folder.", ToolTipIcon.Error), null);
        }
    }

    private void Refresh()
    {
        var cfg = _host.Current;
        var ips = AgentHost.LocalIps();
        _title.Text = $"{cfg.Name}  ·  v{AgentHost.Version}{(_started ? "" : "  (not running)")}";
        _address.Text = ips.Count > 0 ? $"Address: {string.Join(", ", ips)} : {cfg.Port}" : "Address: no network";
        _pin.Text = $"Pairing PIN: {cfg.PairingPin}";
        _clients.Text = $"{cfg.Clients.Count} paired · {_host.Hub.Count} connected";
        try { _startup.Checked = StartupRegistration.IsEnabled; } catch { }
        _kiosk.Visible = cfg.Kiosk.Url is not null;
        _kiosk.Text = _host.Kiosk.Running ? $"Close kiosk screen ({cfg.Kiosk.Name})" : $"Open kiosk screen ({cfg.Kiosk.Name})";
        string tip = $"Show Control · {cfg.Name}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open {target}", ex); }
    }

    /// <summary>The app icon (branding/showcontrol.ico, embedded), at the size Windows uses for the tray.</summary>
    private static Icon DrawIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("showcontrol.ico");
        if (stream is null) return SystemIcons.Application;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        try { _host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        base.ExitThreadCore();
    }
}
