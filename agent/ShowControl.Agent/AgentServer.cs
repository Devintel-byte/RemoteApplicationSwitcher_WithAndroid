using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ShowControl.Agent;

/// <summary>Kestrel on 0.0.0.0:port serving /ws (controllers) and / (built-in web test panel).</summary>
public sealed class AgentServer : IAsyncDisposable
{
    private WebApplication? _app;

    private static readonly Lazy<string> PanelHtml = new(() => ShowControl.Shared.WebAssets.Page(
        Assembly.GetExecutingAssembly(), "dashboard.html", AgentHost.Version, "<h1>Show Control Agent</h1><p>Dashboard missing from build.</p>"));

    public async Task StartAsync(int port, AgentHost host)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port));

        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        _app.Map("/ws", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("WebSocket endpoint");
                return;
            }
            // Browsers send Origin; a page from another site must not drive this PC.
            if (!AdminApi.OriginAllowed(ctx))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await ClientSession.RunWebSocketAsync(socket, host, ctx.Connection.RemoteIpAddress?.ToString() ?? "?", ctx.RequestAborted);
        });

        _app.MapGet("/", (HttpContext ctx) =>
        {
            ShowControl.Shared.WebAssets.PageHeaders(ctx, allowWebSockets: true);
            return Results.Content(PanelHtml.Value, "text/html; charset=utf-8");
        });
        // Design system, font, icon and the dashboard's own script and styles (see shared/web).
        ShowControl.Shared.WebAssets.Map(_app, Assembly.GetExecutingAssembly());
        AdminApi.Map(_app, host);

        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _app.StopAsync(cts.Token); } catch { /* shutting down */ }
            await _app.DisposeAsync();
        }
    }
}
