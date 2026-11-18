using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShowControl.Hub;

public static class Roles
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Viewer = "viewer";
    public static bool Valid(string? r) => r is Admin or Operator or Viewer;
}

public sealed class HubData
{
    public Workspace Workspace { get; set; } = new();
    public List<HubUser> Users { get; set; } = new();
    public List<EnrolledAgent> Agents { get; set; } = new();
    public List<EnrollCode> EnrollCodes { get; set; } = new();
    public List<Schedule> Schedules { get; set; } = new();
    public List<SharedRoom> SharedRooms { get; set; } = new();
    public List<Kiosk> Kiosks { get; set; } = new();
    /// <summary>Links to other hubs (site hub ↔ cloud hub) and what has changed when. Never synced itself.</summary>
    public SyncState Sync { get; set; } = new();
}

/// <summary>
/// A room's tiles published from a phone so other phones in the workspace can import it. The pack is
/// stored as the phone sent it (see the app's RoomShare.kt); it never contains PC tokens or passwords.
/// </summary>
public sealed class SharedRoom
{
    /// <summary>The room's id on the phone that first shared it, so publishing again updates it.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Tiles { get; set; }
    public int Pcs { get; set; }
    public string UpdatedBy { get; set; } = "";
    public string UpdatedById { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public JsonElement Pack { get; set; }
}

public sealed class Workspace
{
    public string Name { get; set; } = "Show Control";
    /// <summary>indigo (default, matches the app icon) | teal | amber | violet | blue | coral</summary>
    public string Accent { get; set; } = "indigo";
    /// <summary>IANA time zone for schedules, e.g. Africa/Lagos.</summary>
    public string TimeZone { get; set; } = "Africa/Lagos";
    public string? LogoFile { get; set; }
    public string? LogoContentType { get; set; }
    public int LogoVersion { get; set; }
}

public sealed class HubUser
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = Roles.Operator;
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public List<UserToken> Tokens { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public sealed class UserToken
{
    /// <summary>SHA-256 of the token; the token itself is only ever given to the client.</summary>
    public string Hash { get; set; } = "";
    public string Device { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsed { get; set; }
}

public sealed class EnrolledAgent
{
    public string AgentId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Hostname { get; set; }
    public string KeyHash { get; set; } = "";
    public DateTime EnrolledAt { get; set; }
    public DateTime? LastSeen { get; set; }
    /// <summary>Reported by the PC; used to wake it from a schedule or the dashboard.</summary>
    public List<string> Macs { get; set; } = new();
    /// <summary>True once renamed on the hub, so the PC's own name doesn't overwrite it.</summary>
    public bool NameLocked { get; set; }
}

public sealed class EnrollCode
{
    public string Code { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public string CreatedBy { get; set; } = "";
}

public sealed class Schedule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string AgentId { get; set; } = "";
    /// <summary>run_scene | start_app | stop_app | stop_all | wake</summary>
    public string Action { get; set; } = "run_scene";
    /// <summary>sceneId or appId (or MAC address for wake).</summary>
    public string? TargetId { get; set; }
    public string? TargetName { get; set; }
    /// <summary>"HH:mm" in the workspace time zone.</summary>
    public string Time { get; set; } = "09:00";
    /// <summary>0 = Sunday … 6 = Saturday. Empty = every day.</summary>
    public List<int> Days { get; set; } = new();
    public DateTime? LastRun { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>Everything the hub remembers, in data/hub.json (written atomically). Audit lines go to data/audit.jsonl.</summary>
public sealed class HubStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _file;
    private readonly string _auditFile;
    public string DataDir { get; }
    public HubData Data { get; private set; } = new();

    public HubStore(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(DataDir);
        _file = Path.Combine(DataDir, "hub.json");
        _auditFile = Path.Combine(DataDir, "audit.jsonl");
        if (File.Exists(_file)) Data = JsonSerializer.Deserialize<HubData>(File.ReadAllText(_file), Json) ?? new HubData();
        if (string.IsNullOrEmpty(Data.Sync.HubId)) Data.Sync.HubId = NewToken(12);
        SyncRecords.Refresh(Data, null);
        _auditSeen = new HashSet<string>(AuditTail(2000).Select(e => e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : ""));
    }

    /// <summary>Raised (outside the lock) after a write that changed something other hubs share.</summary>
    public event Action? Changed;
    private Dictionary<string, DateTime>? _remoteStamps;

    public T Read<T>(Func<HubData, T> read)
    {
        lock (_gate) return read(Data);
    }

    public T Write<T>(Func<HubData, T> change)
    {
        lock (_gate)
        {
            var result = change(Data);
            bool shared = SyncRecords.Refresh(Data, _remoteStamps);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
            File.Move(tmp, _file, overwrite: true);
            if (shared && Changed is { } changed) ThreadPool.QueueUserWorkItem(_ => { try { changed(); } catch { } });
            return result;
        }
    }

    /// <summary>Applies records from another hub, keeping their change times (so the two hubs agree on what's newest).</summary>
    public void WriteRemote(Action<HubData> change, Dictionary<string, DateTime> stamps)
    {
        lock (_gate)
        {
            _remoteStamps = stamps;
            try { Write(change); } finally { _remoteStamps = null; }
        }
    }

    public void Write(Action<HubData> change) => Write<bool>(d => { change(d); return true; });

    // ---------- audit ----------

    private readonly object _auditGate = new();

    private readonly HashSet<string> _auditSeen;

    public void Audit(string user, string action, string? agentId = null, string? detail = null)
    {
        string id = NewToken(9);
        var line = JsonSerializer.Serialize(new { id, at = DateTime.UtcNow, user, action, agentId, detail, hub = Data.Sync.HubId }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        lock (_auditGate) { File.AppendAllText(_auditFile, line + "\n"); _auditSeen.Add(id); }
    }

    /// <summary>Adds activity lines from another hub that this one hasn't got yet. Returns how many were new.</summary>
    public int MergeAudit(IEnumerable<JsonElement> entries)
    {
        int added = 0;
        lock (_auditGate)
        {
            foreach (var e in entries)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) continue;
                if (!_auditSeen.Add(id)) continue;
                File.AppendAllText(_auditFile, e.GetRawText().ReplaceLineEndings("") + "\n");
                added++;
            }
        }
        return added;
    }

    public List<JsonElement> AuditTail(int max)
    {
        lock (_auditGate)
        {
            if (!File.Exists(_auditFile)) return new();
            // Lines from linked hubs arrive late, so read a little more than asked and sort by time.
            int n = Math.Clamp(max, 1, 2000);
            var lines = File.ReadLines(_auditFile).TakeLast(n + 200);
            var list = new List<JsonElement>();
            foreach (var l in lines)
            {
                try { list.Add(JsonDocument.Parse(l).RootElement.Clone()); } catch { }
            }
            return list
                .OrderByDescending(e => e.TryGetProperty("at", out var at) && at.TryGetDateTime(out var t) ? t : DateTime.MinValue)
                .Take(n).ToList();
        }
    }

    // ---------- crypto helpers ----------

    public static string NewToken(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public static (string Hash, string Salt) HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 120_000, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool VerifyPassword(string password, string hash, string salt)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt)) return false;
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt), 120_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(hash), actual);
    }

    public static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>Readable one-time code without look-alike characters, e.g. K7Q4-MX2P.</summary>
    public static string NewEnrollCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = Enumerable.Range(0, 8).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray();
        return new string(chars, 0, 4) + "-" + new string(chars, 4, 4);
    }
}
