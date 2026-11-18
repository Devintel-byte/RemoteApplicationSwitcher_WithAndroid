using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ShowControl.Agent;

/// <summary>Answers "SHOWCTL_DISCOVER_V1" UDP broadcasts so phones can find this PC on the LAN.</summary>
public sealed class Discovery(Func<AgentConfig> config) : IDisposable
{
    public const string Probe = "SHOWCTL_DISCOVER_V1";
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public void Start(int port)
    {
        _cts = new CancellationTokenSource();
        _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _ = Loop(_udp, _cts.Token);
        Log.Info($"Discovery listening on UDP {port}");
    }

    private async Task Loop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);
                string text = Encoding.ASCII.GetString(result.Buffer).Trim();
                if (text != Probe) continue;

                var cfg = config();
                var reply = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "agent_announce",
                    agentId = cfg.AgentId,
                    name = cfg.Name,
                    hostname = Environment.MachineName,
                    port = cfg.Port,
                    version = AgentHost.Version,
                });
                await udp.SendAsync(reply, result.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Warn($"Discovery error: {ex.Message}");
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Dispose();
    }
}
