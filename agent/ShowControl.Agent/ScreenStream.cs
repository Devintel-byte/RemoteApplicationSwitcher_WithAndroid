using System.Buffers.Binary;
using System.Diagnostics;

namespace ShowControl.Agent;

/// <summary>
/// A live view of one display, pushed to one controller as binary WebSocket messages.
///
/// Frame layout (big-endian), 12-byte header then the JPEG:
///   0  'S' (0x53)    1  kind = 1 (frame)    2  display index    3  flags (0)
///   4  sequence number (uint32)              8  width (uint16)   10  height (uint16)
///
/// Flow control: the controller answers each frame with {"type":"stream_ack","seq":n}. At most
/// <see cref="Window"/> frames are unacknowledged at once, so a slow phone or link gets fewer, fresher
/// frames instead of a growing backlog. That keeps the delay low: a frame is never older than the
/// time it takes to cross the network once.
/// </summary>
public sealed class ScreenStream : IAsyncDisposable
{
    public const int HeaderSize = 12;
    public const int Window = 2;
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RepeatAfter = TimeSpan.FromSeconds(1);

    private static int _active;
    public const int MaxActive = 6;
    public static int Active => Volatile.Read(ref _active);

    private readonly IScreenCapture _capture;
    private readonly Func<byte[], Task> _sendBinary;
    private readonly Func<object, Task> _sendJson;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _ackSignal = new(0);
    private Task? _loop;
    private uint _sent;
    private uint _acked;

    public int Display { get; }
    public int MaxWidth { get; }
    public int Fps { get; }
    public int Quality { get; }

    private ScreenStream(IScreenCapture capture, int display, int maxWidth, int fps, int quality,
        Func<byte[], Task> sendBinary, Func<object, Task> sendJson)
    {
        _capture = capture;
        Display = display;
        MaxWidth = Math.Clamp(maxWidth, 160, 1920);
        Fps = Math.Clamp(fps, 1, 30);
        Quality = Math.Clamp(quality, 30, 90);
        _sendBinary = sendBinary;
        _sendJson = sendJson;
    }

    /// <summary>Starts a stream, or returns null when too many are already running on this PC.</summary>
    public static ScreenStream? TryStart(IScreenCapture capture, int display, int maxWidth, int fps, int quality,
        Func<byte[], Task> sendBinary, Func<object, Task> sendJson)
    {
        if (Interlocked.Increment(ref _active) > MaxActive)
        {
            Interlocked.Decrement(ref _active);
            return null;
        }
        var s = new ScreenStream(capture, display, maxWidth, fps, quality, sendBinary, sendJson);
        s._loop = Task.Run(() => s.RunAsync(s._cts.Token));
        return s;
    }

    /// <summary>The controller has displayed frame <paramref name="seq"/>.</summary>
    public void Ack(uint seq)
    {
        // Acks only move forward; a late or duplicate one is ignored.
        uint prev = _acked;
        if (seq > prev && seq <= _sent)
        {
            _acked = seq;
            if (_ackSignal.CurrentCount == 0) _ackSignal.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(1.0 / Fps);
        var clock = Stopwatch.StartNew();
        IFrameGrabber? grabber = null;
        byte[]? lastJpeg = null;
        TimeSpan lastSentAt = TimeSpan.MinValue;
        string? lastError = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait for room in the window (the phone has shown enough of what we sent).
                while (_sent - _acked >= Window && !ct.IsCancellationRequested)
                {
                    if (!await _ackSignal.WaitAsync(AckTimeout, ct))
                    {
                        // No answer for a while (phone in the background, network stall): resync and carry on.
                        _acked = _sent;
                    }
                }
                if (ct.IsCancellationRequested) break;

                var started = clock.Elapsed;
                Capture frame;
                try
                {
                    grabber ??= _capture.OpenStream(Display, MaxWidth, Quality);
                    frame = grabber.Next();
                    if (lastError is not null)
                    {
                        lastError = null;
                        await _sendJson(new { type = "stream_resumed", display = Display });
                    }
                }
                catch (InvalidOperationException ex)
                {
                    grabber?.Dispose();
                    grabber = null;
                    if (ex.Message != lastError)
                    {
                        lastError = ex.Message;
                        await _sendJson(new { type = "stream_error", display = Display, message = ex.Message });
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                // A still screen produces the same JPEG; skip it but resend once a second so the phone knows we're alive.
                bool same = lastJpeg is not null && lastJpeg.AsSpan().SequenceEqual(frame.Jpeg);
                if (!same || clock.Elapsed - lastSentAt >= RepeatAfter)
                {
                    uint seq = _sent + 1;
                    var buffer = new byte[HeaderSize + frame.Jpeg.Length];
                    WriteHeader(buffer, Display, seq, frame.Width, frame.Height);
                    frame.Jpeg.CopyTo(buffer, HeaderSize);
                    _sent = seq;
                    await _sendBinary(buffer);
                    lastJpeg = frame.Jpeg;
                    lastSentAt = clock.Elapsed;
                }

                var wait = interval - (clock.Elapsed - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (StreamClosedException) { /* the viewer went away */ }
        catch (Exception ex) { Log.Error("Live stream stopped", ex); }
        finally
        {
            grabber?.Dispose();
        }
    }

    public static void WriteHeader(Span<byte> buffer, int display, uint seq, int width, int height)
    {
        buffer[0] = (byte)'S';
        buffer[1] = 1;
        buffer[2] = (byte)Math.Clamp(display, 0, 255);
        buffer[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], seq);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[8..], (ushort)Math.Clamp(width, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(buffer[10..], (ushort)Math.Clamp(height, 0, ushort.MaxValue));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        if (_loop is not null) { try { await _loop; } catch { } }
        Interlocked.Decrement(ref _active);
        _cts.Dispose();
    }
}

/// <summary>The viewer's connection closed while a frame was being sent.</summary>
public sealed class StreamClosedException(Exception? inner = null) : Exception("Viewer disconnected", inner);
