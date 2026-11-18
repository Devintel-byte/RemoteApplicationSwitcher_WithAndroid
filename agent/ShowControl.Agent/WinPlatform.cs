using System.Diagnostics;

namespace ShowControl.Agent;

/// <summary>Windows-only features for the dashboard: native file/folder pickers, shortcuts, startup, restart.</summary>
public sealed class WinPlatform : IPlatform, WinPlatformShortcuts
{
    public Task<string?> PickFileAsync(string? initialPath, string filter) => RunSta(() =>
    {
        using var owner = TopMostOwner();
        using var dlg = new OpenFileDialog
        {
            Title = "Choose the program to launch",
            Filter = filter,
            CheckFileExists = true,
            DereferenceLinks = false, // keep .lnk so we can show what it points to
            RestoreDirectory = true,
        };
        SetInitial(dlg, initialPath);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
    });

    public Task<string?> PickFolderAsync(string? initialPath) => RunSta(() =>
    {
        using var owner = TopMostOwner();
        using var dlg = new FolderBrowserDialog { Description = "Choose a folder", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)) dlg.InitialDirectory = initialPath;
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null;
    });

    public string? ResolveShortcut(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(lnkPath);
            string target = link.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read shortcut {lnkPath}: {ex.Message}");
            return null;
        }
    }

    public bool GetStartWithWindows() => StartupRegistration.IsEnabled;

    public void SetStartWithWindows(bool enabled) => StartupRegistration.Set(enabled);

    public void RestartAgent()
    {
        Log.Info("Restarting agent");
        Application.Restart();
        Environment.Exit(0);
    }

    private static void SetInitial(FileDialog dlg, string? initialPath)
    {
        if (string.IsNullOrWhiteSpace(initialPath)) return;
        string? dir = Directory.Exists(initialPath) ? initialPath : Path.GetDirectoryName(initialPath);
        if (dir is not null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (File.Exists(initialPath)) dlg.FileName = Path.GetFileName(initialPath);
    }

    /// <summary>An invisible top-most window so the dialog appears in front of the dashboard window.</summary>
    private static Form TopMostOwner()
    {
        var f = new Form
        {
            TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0,
        };
        f.Show();
        f.Activate();
        return f;
    }

    /// <summary>Dialogs need a single-threaded apartment thread of their own.</summary>
    private static Task<string?> RunSta(Func<string?> work)
    {
        var tcs = new TaskCompletionSource<string?>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { Log.Error("Picker failed", ex); tcs.SetResult(null); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    // ---------- room kiosk ----------

    private readonly object _kioskGate = new();
    private Process? _kiosk;
    private static string KioskProfile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShowControl", "kiosk-browser");
    private static string KioskPidFile => Path.Combine(KioskProfile, "showcontrol-kiosk.pid");

    public bool KioskRunning
    {
        get
        {
            lock (_kioskGate)
            {
                try { return _kiosk is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    /// <summary>
    /// Edge with its own profile (so it never mixes with anyone's normal browsing). Locked = Edge kiosk mode:
    /// full screen, no address bar, tabs or menus. Otherwise a normal maximised app window.
    /// </summary>
    public void OpenKiosk(string url, bool locked)
    {
        lock (_kioskGate)
        {
            CloseKioskLocked();
            Directory.CreateDirectory(KioskProfile);
            var args = new List<string>
            {
                $"--user-data-dir=\"{KioskProfile}\"",
                "--no-first-run", "--no-default-browser-check", "--disable-session-crashed-bubble", "--hide-crash-restore-bubble",
                "--overscroll-history-navigation=0", "--disable-pinch", "--autoplay-policy=no-user-gesture-required",
                "--disable-features=Translate,msEdgeSidebarV2,msUndersideButton,EdgeCollections",
            };
            if (locked) { args.Add("--kiosk"); args.Add($"\"{url}\""); args.Add("--edge-kiosk-type=fullscreen"); }
            else { args.Add($"--app=\"{url}\""); args.Add("--start-maximized"); }

            string? edge = FindEdge();
            var psi = edge is null
                ? new ProcessStartInfo("msedge", string.Join(' ', args)) { UseShellExecute = true }
                : new ProcessStartInfo(edge, string.Join(' ', args)) { UseShellExecute = false };
            _kiosk = Process.Start(psi);
            if (_kiosk is not null)
            {
                try { File.WriteAllText(KioskPidFile, $"{_kiosk.Id} {_kiosk.StartTime.ToUniversalTime().Ticks}"); } catch { }
            }
        }
    }

    public void CloseKiosk()
    {
        lock (_kioskGate) CloseKioskLocked();
    }

    private void CloseKioskLocked()
    {
        try { if (_kiosk is { HasExited: false }) _kiosk.Kill(entireProcessTree: true); } catch { }
        _kiosk = null;
        // A kiosk left over from before the agent restarted (same profile): close it too.
        try
        {
            if (File.Exists(KioskPidFile))
            {
                var parts = File.ReadAllText(KioskPidFile).Split(' ');
                if (int.TryParse(parts[0], out int pid) && long.TryParse(parts.ElementAtOrDefault(1), out long ticks))
                {
                    using var old = Process.GetProcessById(pid);
                    if (old.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) && old.StartTime.ToUniversalTime().Ticks == ticks)
                        old.Kill(entireProcessTree: true);
                }
                File.Delete(KioskPidFile);
            }
        }
        catch { /* already gone */ }
    }

    private static string? FindEdge()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string p = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Opens the dashboard as a chromeless app window (Edge), falling back to the default browser.</summary>
    public static void OpenDashboard(int port)
    {
        string url = $"http://localhost:{port}/";
        try
        {
            Process.Start(new ProcessStartInfo("msedge", $"--app={url} --window-size=1320,880 --new-window") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Could not open the dashboard", ex); }
        }
    }
}
