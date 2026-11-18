using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShowControl.Agent;

/// <summary>Loads, validates, saves and live-reloads agent.json.</summary>
public sealed partial class ConfigStore : IDisposable
{
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShowControl", "agent.json");

    public static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private string _lastSavedJson = "";

    public string Path { get; }
    public AgentConfig Current { get; private set; } = new();

    /// <summary>Raised (on a worker thread) after the file was edited externally and reloaded.</summary>
    public event Action<AgentConfig>? Reloaded;

    public ConfigStore(string path) => Path = path;

    public void Load()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (!File.Exists(Path))
            {
                Current = CreateDefault();
                CreatedFresh = true;
                SaveLocked();
                Log.Info($"Created default config at {Path}");
            }
            else
            {
                Current = ReadFile();
                bool changed = FillMissing(Current);
                if (changed) SaveLocked();
            }
        }
        StartWatching();
    }

    public void Save()
    {
        lock (_gate) SaveLocked();
    }

    public void Mutate(Action<AgentConfig> change)
    {
        lock (_gate)
        {
            change(Current);
            SaveLocked();
        }
    }

    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private AgentConfig ReadFile()
    {
        string json = File.ReadAllText(Path);
        var cfg = JsonSerializer.Deserialize<AgentConfig>(json, FileOptions) ?? new AgentConfig();
        Validate(cfg);
        return cfg;
    }

    private void SaveLocked()
    {
        string json = JsonSerializer.Serialize(Current, FileOptions);
        _lastSavedJson = json;
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, Path, overwrite: true);
    }

    private static bool FillMissing(AgentConfig cfg)
    {
        bool changed = false;
        if (string.IsNullOrWhiteSpace(cfg.AgentId)) { cfg.AgentId = Guid.NewGuid().ToString("N"); changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.PairingPin)) { cfg.PairingPin = NewPin(); changed = true; }
        if (string.IsNullOrWhiteSpace(cfg.Name)) { cfg.Name = Environment.MachineName; changed = true; }
        foreach (var c in cfg.Clients.Where(c => string.IsNullOrWhiteSpace(c.Id))) { c.Id = Guid.NewGuid().ToString("N")[..10]; changed = true; }
        return changed;
    }

    /// <summary>Drops broken entries (with a log line) rather than refusing the whole file.</summary>
    internal static void Validate(AgentConfig cfg)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cfg.Apps = cfg.Apps.Where(a =>
        {
            if (string.IsNullOrWhiteSpace(a.Id) || !IdRegex().IsMatch(a.Id)) { Log.Warn($"Skipping app with invalid id '{a.Id}'"); return false; }
            if (string.IsNullOrWhiteSpace(a.Path)) { Log.Warn($"Skipping app '{a.Id}': no path"); return false; }
            if (!seen.Add(a.Id)) { Log.Warn($"Skipping duplicate app id '{a.Id}'"); return false; }
            if (string.IsNullOrWhiteSpace(a.Name)) a.Name = a.Id;
            return true;
        }).ToList();

        var sceneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cfg.Scenes = cfg.Scenes.Where(s =>
        {
            if (string.IsNullOrWhiteSpace(s.Id) || !IdRegex().IsMatch(s.Id) || !sceneIds.Add(s.Id)) { Log.Warn($"Skipping scene with invalid or duplicate id '{s.Id}'"); return false; }
            if (string.IsNullOrWhiteSpace(s.Name)) s.Name = s.Id;
            return true;
        }).ToList();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    internal static partial Regex IdRegex();

    /// <summary>"Resolume Arena 7" → "resolume-arena-7", made unique against existing ids.</summary>
    public static string MakeId(string name, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        string slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "item";
        if (slug.Length > 48) slug = slug[..48].Trim('-');
        string id = slug;
        for (int i = 2; taken.Contains(id); i++) id = $"{slug}-{i}";
        return id;
    }

    private void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(Path)!, System.IO.Path.GetFileName(Path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => Debounce();
        _watcher.Created += (_, _) => Debounce();
        _watcher.Renamed += (_, _) => Debounce();
    }

    private void Debounce()
    {
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ => ReloadFromDisk(), null, 400, Timeout.Infinite);
    }

    private void ReloadFromDisk()
    {
        try
        {
            AgentConfig cfg;
            lock (_gate)
            {
                string json = File.ReadAllText(Path);
                if (json == _lastSavedJson) return; // our own write
                cfg = ReadFile();
                FillMissing(cfg);
                Current = cfg;
            }
            Log.Info("Config reloaded from disk");
            Reloaded?.Invoke(cfg);
        }
        catch (Exception ex)
        {
            Log.Error("Config reload failed (keeping previous config). Check agent.json for a JSON error", ex);
        }
    }

    private static AgentConfig CreateDefault() => new()
    {
        AgentId = Guid.NewGuid().ToString("N"),
        Name = Environment.MachineName,
        PairingPin = NewPin(),
        Apps =
        {
            new AppDefinition { Id = "notepad", Name = "Notepad (test)", Path = "notepad.exe", Color = "gray" },
        },
        Scenes =
        {
            new SceneDefinition
            {
                Id = "test", Name = "Test scene", Color = "gray",
                Steps =
                {
                    new SceneStep { Action = "start", AppId = "notepad", Label = "Open Notepad" },
                    new SceneStep { Action = "wait", Seconds = 3, Label = "Hold for 3 s" },
                    new SceneStep { Action = "stop", AppId = "notepad", Label = "Close Notepad" },
                },
            },
        },
    };

    /// <summary>True the first time the agent runs on this PC (config was just created).</summary>
    public bool CreatedFresh { get; private set; }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
