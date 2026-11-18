namespace ShowControl.Agent;

/// <summary>Things only the Windows tray app can do. Null in tests; endpoints answer "not available".</summary>
public interface IPlatform
{
    /// <summary>Shows a native Open dialog on this PC's screen. Null if cancelled.</summary>
    Task<string?> PickFileAsync(string? initialPath, string filter);

    /// <summary>Shows a native folder picker on this PC's screen. Null if cancelled.</summary>
    Task<string?> PickFolderAsync(string? initialPath);

    bool GetStartWithWindows();
    void SetStartWithWindows(bool enabled);

    /// <summary>Restarts the agent process (used after changing the port).</summary>
    void RestartAgent();

    /// <summary>Opens the kiosk screen: locked full screen (Edge kiosk mode) or a normal app window. Replaces one already open.</summary>
    void OpenKiosk(string url, bool locked);

    void CloseKiosk();

    bool KioskRunning { get; }
}
