using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace ShowControl.Hub;

/// <summary>Bearer tokens for people (phones and the dashboard). Tokens are stored hashed.</summary>
public sealed class HubAuth(HubStore store)
{
    private readonly ConcurrentDictionary<string, (int Count, DateTime Until)> _failures = new();

    public (string Token, HubUser User)? Login(string username, string password, string device, string remote)
    {
        if (IsLockedOut(remote)) return null;
        var user = store.Read(d => d.Users.FirstOrDefault(u => u.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (user is null || !HubStore.VerifyPassword(password, user.PasswordHash, user.PasswordSalt))
        {
            _failures.AddOrUpdate(remote, _ => (1, DateTime.UtcNow.AddMinutes(5)),
                (_, f) => (f.Until < DateTime.UtcNow ? 1 : f.Count + 1, DateTime.UtcNow.AddMinutes(5)));
            return null;
        }
        _failures.TryRemove(remote, out _);
        string token = "hub_" + HubStore.NewToken();
        store.Write(d =>
        {
            var u = d.Users.First(x => x.Id == user.Id);
            u.Tokens.RemoveAll(t => t.LastUsed < DateTime.UtcNow.AddDays(-90));
            u.Tokens.Add(new UserToken
            {
                Hash = HubStore.Sha256(token),
                Device = string.IsNullOrWhiteSpace(device) ? "Unknown device" : device.Trim()[..Math.Min(60, device.Trim().Length)],
                CreatedAt = DateTime.UtcNow, LastUsed = DateTime.UtcNow,
            });
        });
        return (token, user);
    }

    public bool IsLockedOut(string remote) => _failures.TryGetValue(remote, out var f) && f.Count >= 8 && f.Until > DateTime.UtcNow;

    public HubUser? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith("hub_")) return null;
        string hash = HubStore.Sha256(token);
        var found = store.Read(d =>
        {
            foreach (var u in d.Users)
                foreach (var t in u.Tokens)
                    if (HubStore.FixedEquals(t.Hash, hash)) return (u, t);
            return ((HubUser, UserToken)?)null;
        });
        if (found is null) return null;
        var (user, tok) = found.Value;
        if (tok.LastUsed < DateTime.UtcNow.AddHours(-1))
            store.Write(_ => tok.LastUsed = DateTime.UtcNow);
        return user;
    }

    public void Logout(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        string hash = HubStore.Sha256(token);
        store.Write(d => { foreach (var u in d.Users) u.Tokens.RemoveAll(t => t.Hash == hash); });
    }

    public HubUser? FromRequest(HttpContext ctx)
    {
        string header = ctx.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ") ? Authenticate(header[7..].Trim()) : null;
    }

    public static string? BearerToken(HttpContext ctx)
    {
        string header = ctx.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ") ? header[7..].Trim() : null;
    }
}
