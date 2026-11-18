using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ShowControl.Shared;

/// <summary>
/// The web UI's static files (design system, font, icon, page scripts and styles), embedded in the program
/// with the logical name "assets/&lt;file&gt;" and served at /assets/&lt;file&gt;. Shared by the agent and the hub
/// (linked from shared/dotnet). Pages reference them as /assets/x?v=__V__; <see cref="Page"/> fills in the
/// version so a new build is picked up at once while browsers cache the files in between.
/// </summary>
public static class WebAssets
{
    private sealed record Asset(byte[] Bytes, string ContentType, string ETag);

    private static readonly ConcurrentDictionary<Assembly, Dictionary<string, Asset>> Cache = new();

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".woff2"] = "font/woff2",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".txt"] = "text/plain; charset=utf-8",
    };

    private static Dictionary<string, Asset> Load(Assembly asm) => Cache.GetOrAdd(asm, a =>
    {
        var map = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in a.GetManifestResourceNames().Where(n => n.StartsWith("assets/", StringComparison.Ordinal)))
        {
            using var s = a.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            string file = name["assets/".Length..];
            string type = Types.TryGetValue(Path.GetExtension(file), out var t) ? t : "application/octet-stream";
            string etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"";
            map[file] = new Asset(bytes, type, etag);
        }
        return map;
    });

    /// <summary>Serves /assets/{file} from <paramref name="asm"/>'s embedded resources.</summary>
    public static void Map(WebApplication app, Assembly asm)
    {
        var assets = Load(asm);
        app.MapGet("/assets/{file}", (HttpContext ctx, string file) =>
        {
            if (!assets.TryGetValue(file, out var a)) return Results.NotFound();
            ctx.Response.Headers.ETag = a.ETag;
            // Versioned links (?v=) can be cached for a day; anything else is revalidated with the ETag.
            ctx.Response.Headers.CacheControl = ctx.Request.Query.ContainsKey("v") ? "public, max-age=86400, immutable" : "no-cache";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (ctx.Request.Headers.IfNoneMatch == a.ETag) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(a.Bytes, a.ContentType);
        });
    }

    /// <summary>An embedded HTML page with "__V__" replaced by the version (for cache-busting asset links).</summary>
    public static string Page(Assembly asm, string resource, string version, string fallback)
    {
        using var s = asm.GetManifestResourceStream(resource);
        if (s is null) return fallback;
        using var r = new StreamReader(s);
        return r.ReadToEnd().Replace("__V__", Uri.EscapeDataString(version));
    }

    /// <summary>Security headers for the app pages (scripts and styles only from this server).</summary>
    public static void PageHeaders(HttpContext ctx, bool allowWebSockets)
    {
        var h = ctx.Response.Headers;
        h["X-Frame-Options"] = "DENY";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data: blob:; font-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
            $"connect-src 'self'{(allowWebSockets ? " ws: wss:" : "")}; frame-ancestors 'none'; base-uri 'none'";
        h.CacheControl = "no-store";
    }
}
