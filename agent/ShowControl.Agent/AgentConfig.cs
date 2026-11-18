namespace ShowControl.Agent;

/// <summary>Everything in agent.json. Edit the file (or use the tray menu) and it reloads live.</summary>
public sealed class AgentConfig
{
    public string AgentId { get; set; } = "";
    public string Name { get; set; } = Environment.MachineName;
    public int Port { get; set; } = 47800;
    public int DiscoveryPort { get; set; } = 47801;
    public string PairingPin { get; set; } = "";
    /// <summary>Allow controllers to restart / shut down this PC.</summary>
    public bool AllowPower { get; set; }
    public List<PairedClient> Clients { get; set; } = new();
    public List<AppDefinition> Apps { get; set; } = new();
    public List<SceneDefinition> Scenes { get; set; } = new();
    /// <summary>OBS WebSocket (OBS → Tools → WebSocket Server Settings) for "obs" scene steps.</summary>
    public ObsSettings Obs { get; set; } = new();
    /// <summary>What happens when the agent starts (e.g. at Windows sign-in).</summary>
    public StartupSettings Startup { get; set; } = new();
    /// <summary>Dashboard access from other computers (this PC itself never needs a password).</summary>
    public RemoteAdminSettings RemoteAdmin { get; set; } = new();
    /// <summary>Connection to a Show Control hub for remote control over the internet.</summary>
    public HubSettings Hub { get; set; } = new();
    /// <summary>Set by the hub when this PC shows a room kiosk.</summary>
    public KioskSettings Kiosk { get; set; } = new();
}

public sealed class StartupSettings
{
    /// <summary>Seconds to wait after the agent starts before launching startup apps (lets Windows settle).</summary>
    public int DelaySec { get; set; } = 10;
    /// <summary>Scene to run after the startup apps, e.g. "main-show". Empty = none.</summary>
    public string? SceneId { get; set; }
}

public sealed class RemoteAdminSettings
{
    public bool Enabled { get; set; }
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
}

public sealed class HubSettings
{
    public bool Enabled { get; set; }
    /// <summary>e.g. https://hub.movingsurface.com.ng or http://192.168.1.10:47900</summary>
    public string? Url { get; set; }
    /// <summary>Issued by the hub at enrollment. Treat like a password.</summary>
    public string? AgentKey { get; set; }
    /// <summary>
    /// Other hubs this PC also connects to with the same key: a linked site hub on the venue's network and
    /// the cloud hub. The hubs announce each other (Auto); you can also add one on the dashboard's Hub page.
    /// </summary>
    public List<ExtraHub> Also { get; set; } = new();
}

public sealed class ExtraHub
{
    public string Url { get; set; } = "";
    /// <summary>cloud | site | manual</summary>
    public string Kind { get; set; } = "manual";
    /// <summary>Added because a hub announced it (removed again when no hub does).</summary>
    public bool Auto { get; set; }
}

public sealed class ObsSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4455;
    public string Password { get; set; } = "";
}

public sealed class PairedClient
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTime PairedAt { get; set; }
    /// <summary>operator (default) or viewer: a viewer phone can watch this PC but not control it.</summary>
    public string Role { get; set; } = "operator";
}

public sealed class AppDefinition
{
    /// <summary>Stable id the controller uses, e.g. "resolume". Letters, digits, dashes.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Full path to the .exe (or a name on PATH, e.g. notepad.exe).</summary>
    public string Path { get; set; } = "";
    /// <summary>Command-line arguments, e.g. the project file: "\"D:\\Shows\\mapping.toe\"".</summary>
    public string? Arguments { get; set; }
    /// <summary>Defaults to the exe's folder. OBS needs this to be its bin folder.</summary>
    public string? WorkingDirectory { get; set; }
    /// <summary>Process name to watch (no .exe). Defaults to the exe file name.</summary>
    public string? ProcessName { get; set; }
    /// <summary>normal | maximized | minimized</summary>
    public string WindowStyle { get; set; } = "normal";
    /// <summary>Label colour for controllers: teal | amber | violet | blue | coral | red | gray.</summary>
    public string? Color { get; set; }
    public string? Icon { get; set; }
    /// <summary>Restart automatically if it exits without being asked to.</summary>
    public bool Watchdog { get; set; }
    /// <summary>Launch this app when the agent starts (after the startup delay).</summary>
    public bool StartWithAgent { get; set; }
    /// <summary>Seconds to wait after a polite close before force-killing.</summary>
    public int GracefulTimeoutSec { get; set; } = 10;
    /// <summary>Seconds after launch to count as "running" if no window has appeared yet.</summary>
    public int ReadyDelaySec { get; set; } = 3;
    /// <summary>Seconds to wait for the process to appear before reporting a failed start.</summary>
    public int StartTimeoutSec { get; set; } = 60;

    public string EffectiveProcessName =>
        !string.IsNullOrWhiteSpace(ProcessName)
            ? ProcessName!.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)
            : System.IO.Path.GetFileNameWithoutExtension(Path);
}

public sealed class SceneDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? Icon { get; set; }
    /// <summary>Optional section on the phone, e.g. "Cues" or "Lighting".</summary>
    public string? Group { get; set; }
    /// <summary>Ask the operator to confirm before running.</summary>
    public bool Confirm { get; set; }
    public List<SceneStep> Steps { get; set; } = new();
}

public sealed class SceneStep
{
    /// <summary>start | stop | restart | stopAll | wait | waitForPort | osc | udp | http | obs | wake</summary>
    public string Action { get; set; } = "";
    public string? AppId { get; set; }
    /// <summary>For "wait": seconds to pause.</summary>
    public double Seconds { get; set; }
    /// <summary>"waitForPort": local TCP port to wait for. "osc"/"udp": destination port.</summary>
    public int Port { get; set; }
    /// <summary>"osc"/"udp": destination host (default 127.0.0.1).</summary>
    public string? Host { get; set; }
    /// <summary>"osc": address, e.g. /composition/layers/1/clips/1/connect</summary>
    public string? Address { get; set; }
    /// <summary>"osc": arguments. Whole numbers → int, decimals → float, text → string, true/false → T/F.</summary>
    public List<System.Text.Json.JsonElement>? Args { get; set; }
    /// <summary>Dashboard input for Args as typed ("1.0, 2, \"intro\""), so 1.0 stays a float. Converted on save; never stored.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ArgsText { get; set; }
    /// <summary>"udp": text payload.</summary>
    public string? Text { get; set; }
    /// <summary>"http": URL, method (GET/POST/PUT) and optional body.</summary>
    public string? Url { get; set; }
    public string? Method { get; set; }
    public string? Body { get; set; }
    /// <summary>"obs": request type, e.g. SetCurrentProgramScene, StartRecord.</summary>
    public string? Request { get; set; }
    /// <summary>"obs": requestData object, e.g. { "sceneName": "Intro" }.</summary>
    public System.Text.Json.Nodes.JsonObject? Data { get; set; }
    /// <summary>"wake": MAC address of a PC to wake (AA:BB:CC:DD:EE:FF).</summary>
    public string? Mac { get; set; }
    /// <summary>For "start": wait until the app reports running before the next step.</summary>
    public bool WaitForRunning { get; set; } = true;
    public int TimeoutSec { get; set; } = 60;
    public string? Label { get; set; }
}
