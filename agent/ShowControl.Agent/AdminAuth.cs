using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ShowControl.Agent;

/// <summary>
/// Dashboard sessions. On this PC the dashboard gets a session automatically; from another computer
/// it needs the remote admin password (if remote admin is turned on).
/// </summary>
public sealed class AdminAuth
{
    private readonly ConcurrentDictionary<string, (DateTime Expires, bool Local)> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTime Until)> _failures = new();
    private static readonly TimeSpan SessionLife = TimeSpan.FromHours(12);

    public string CreateSession(bool local = false)
    {
        string token = "adm_" + ConfigStore.NewToken();
        _sessions[token] = (DateTime.UtcNow + SessionLife, local);
        return token;
    }

    public bool IsSessionToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith("adm_")) return false;
        if (!_sessions.TryGetValue(token, out var entry)) return false;
        if (entry.Expires < DateTime.UtcNow) { _sessions.TryRemove(token, out _); return false; }
        return true;
    }

    public void Revoke(string token) => _sessions.TryRemove(token, out _);

    /// <summary>Signs out every remote dashboard (after a password change or turning remote access off).</summary>
    public void RevokeRemote()
    {
        foreach (var (token, entry) in _sessions)
            if (!entry.Local) _sessions.TryRemove(token, out _);
    }

    public static (string Hash, string Salt) HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 120_000, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool VerifyPassword(string password, string? hash, string? salt)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt)) return false;
        byte[] expected = Convert.FromBase64String(hash);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt), 120_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Slows down password guessing: 5 misses locks that address out for 5 minutes.</summary>
    public bool IsLockedOut(string remote) =>
        _failures.TryGetValue(remote, out var f) && f.Count >= 5 && f.Until > DateTime.UtcNow;

    public void RecordFailure(string remote) =>
        _failures.AddOrUpdate(remote, _ => (1, DateTime.UtcNow.AddMinutes(5)),
            (_, f) => (f.Until < DateTime.UtcNow ? 1 : f.Count + 1, DateTime.UtcNow.AddMinutes(5)));

    public void ClearFailures(string remote) => _failures.TryRemove(remote, out _);

    public static bool IsLoopback(IPAddress? ip) => ip is not null && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
}
