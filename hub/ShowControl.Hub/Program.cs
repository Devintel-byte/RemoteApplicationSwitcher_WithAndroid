using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShowControl.Hub;

// ---------- settings ----------
string dataDir = Environment.GetEnvironmentVariable("SC_HUB_DATA")
    ?? (args.SkipWhile(a => a != "--data").Skip(1).FirstOrDefault())
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
string settingsPath = Path.Combine(dataDir, "settings.json");
if (!File.Exists(settingsPath)) File.WriteAllText(settingsPath, JsonSerializer.Serialize(new HubSettings(), HubStore.Json));
var settings = JsonSerializer.Deserialize<HubSettings>(File.ReadAllText(settingsPath), HubStore.Json) ?? new HubSettings();
if (int.TryParse(Environment.GetEnvironmentVariable("SC_HUB_PORT"), out int envPort)) settings.Port = envPort;
// Behind a web server (Apache on cPanel, nginx, Caddy) set SC_HUB_BIND=127.0.0.1 so only it can reach the hub.
string bind = (Environment.GetEnvironmentVariable("SC_HUB_BIND") ?? settings.Bind ?? "0.0.0.0").Trim();
if (!System.Net.IPAddress.TryParse(bind, out var bindAddress)) bindAddress = System.Net.IPAddress.Any;

var store = new HubStore(dataDir);

// First run: a one-time setup code protects the "create the first admin" page.
string? setupCode = null;
string setupFile = Path.Combine(store.DataDir, "setup-code.txt");
if (store.Read(d => d.Users.Count == 0))
{
    setupCode = File.Exists(setupFile) ? File.ReadAllText(setupFile).Trim() : HubStore.NewEnrollCode();
    File.WriteAllText(setupFile, setupCode);
}

// ---------- web host ----------
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 4 * 1024 * 1024; // kiosk cover images up to 3 MB
    if (!string.IsNullOrWhiteSpace(settings.CertificatePath))
        k.Listen(bindAddress, settings.Port, l => l.UseHttps(settings.CertificatePath!, settings.CertificatePassword));
    else
        k.Listen(bindAddress, settings.Port);
});

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var auth = new HubAuth(store);
var agents = new AgentRegistry(store);
var schedules = new ScheduleRunner(store, agents);
var kiosks = new KioskService(store, agents);
agents.AgentConnected += id => kiosks.PushAsync(id);

// Linked hubs (a site hub on the venue's network ↔ the cloud hub): see HubSync.cs.
var sync = new HubSync(store, agents, settings.Port);
schedules.Sync = sync;
sync.KiosksChanged += id => kiosks.PushAsync(id);
JsonObject HubsFrame() => new() { ["t"] = "hubs", ["hubs"] = new JsonArray(sync.HubsForAgents().ToArray<JsonNode?>()) };
agents.AgentConnected += id => agents.SendToAsync(id, HubsFrame());
sync.HubsChanged += () => agents.BroadcastAsync(_ => HubsFrame());

// PCs connect here (outbound from the venue: no port forwarding needed there).
app.Map("/agent", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await agents.HandleAgentAsync(ws, ctx.Connection.RemoteIpAddress?.ToString() ?? "?", ctx.RequestAborted);
});

// Phones and the dashboard control a PC here, using the normal Show Control protocol.
app.Map("/ws/agent/{agentId}", async (HttpContext ctx, string agentId) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    string origin = ctx.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await ClientRelay.RunAsync(ws, agentId, agents, store, auth, ctx.RequestAborted);
});

HubApi.Map(app, store, auth, agents, schedules, () => setupCode is not null && store.Read(d => d.Users.Count == 0) ? setupCode : null);
KioskApi.Map(app, store, auth, kiosks, (ctx, role) => HubApi.Require(ctx, auth, role));
HubSync.Map(app, store, sync, (ctx, role) => HubApi.Require(ctx, auth, role), ctx => auth.FromRequest(ctx));

// First run on a site hub: instead of creating an admin, join the cloud hub and take its people and settings.
app.MapPost("/api/setup/join", async (HttpContext ctx) =>
{
    if (store.Read(d => d.Users.Count > 0)) return Results.Problem("This hub is already set up", statusCode: 409);
    JsonObject? b;
    try { b = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject; } catch { b = null; }
    string code = ((string?)b?["code"] ?? "").Trim().ToUpperInvariant();
    if (setupCode is null || !HubStore.FixedEquals(code, setupCode)) return Results.BadRequest(new { error = "Wrong setup code. It's printed in the hub's console and saved in data/setup-code.txt." });
    string? err = await sync.JoinAsync((string?)b?["url"] ?? "", (string?)b?["linkCode"] ?? "", "cloud", "setup");
    if (err is not null) return Results.BadRequest(new { error = err });
    if (store.Read(d => d.Users.Count == 0)) return Results.BadRequest(new { error = "Linked, but no accounts have arrived from the other hub yet. Wait a moment and reload." });
    try { File.Delete(setupFile); } catch { }
    return Results.Ok(new { ok = true });
});

// Pages. The visitor screen for a room signs in with the kiosk key in the link's #k=…; tour guides with their account.
var asm = Assembly.GetExecutingAssembly();
string kioskHtml = ShowControl.Shared.WebAssets.Page(asm, "kiosk.html", HubInfo.Version, "<h1>Kiosk page missing from build</h1>");
string adminHtml = ShowControl.Shared.WebAssets.Page(asm, "admin.html", HubInfo.Version, "<h1>Show Control Hub</h1>");
app.MapGet("/kiosk", (HttpContext ctx) => { ShowControl.Shared.WebAssets.PageHeaders(ctx, allowWebSockets: false); return Results.Content(kioskHtml, "text/html; charset=utf-8"); });
app.MapGet("/", (HttpContext ctx) => { ShowControl.Shared.WebAssets.PageHeaders(ctx, allowWebSockets: true); return Results.Content(adminHtml, "text/html; charset=utf-8"); });
ShowControl.Shared.WebAssets.Map(app, asm);

var cts = new CancellationTokenSource();
_ = schedules.LoopAsync(cts.Token);
if (store.Read(d => d.Sync.Upstream) is not null) sync.StartUpstream();

await app.StartAsync();
Console.WriteLine($"Show Control Hub {HubInfo.Version} listening on {(settings.CertificatePath is null ? "http" : "https")}://{bindAddress}:{settings.Port}  (data: {store.DataDir})");
if (setupCode is not null)
{
    Console.WriteLine();
    Console.WriteLine($"  First run: open the hub in a browser and enter setup code  {setupCode}");
    Console.WriteLine($"  (also saved in {setupFile})");
    Console.WriteLine();
}
await app.WaitForShutdownAsync(cts.Token);
cts.Cancel();

namespace ShowControl.Hub
{
    public static class HubInfo { public const string Version = "1.4.0"; }

    public sealed class HubSettings
    {
        public int Port { get; set; } = 47900;
        /// <summary>Address to listen on. 0.0.0.0 = everywhere; 127.0.0.1 = only this server (behind Apache/nginx/Caddy).</summary>
        public string? Bind { get; set; }
        /// <summary>Optional .pfx to serve HTTPS directly. Most setups put Caddy or nginx in front instead.</summary>
        public string? CertificatePath { get; set; }
        public string? CertificatePassword { get; set; }
    }
}
