namespace ShowControl.Agent;

public sealed record DisplayInfo(int Index, string Name, int Width, int Height, bool Primary);

public sealed record Capture(byte[] Jpeg, int Width, int Height);

/// <summary>Grabs what a display is showing. The Windows implementation lives in WinScreenCapture.cs.</summary>
public interface IScreenCapture
{
    IReadOnlyList<DisplayInfo> Displays();

    /// <summary>JPEG of one display, scaled to at most maxWidth. Throws InvalidOperationException with a readable message on failure.</summary>
    Capture Grab(int display, int maxWidth, int quality);

    /// <summary>
    /// A grabber for continuous frames of one display. Implementations reuse their buffers between frames,
    /// which is much faster than calling <see cref="Grab"/> repeatedly.
    /// </summary>
    IFrameGrabber OpenStream(int display, int maxWidth, int quality) => new RepeatedGrabber(this, display, maxWidth, quality);
}

/// <summary>Produces frames for one live stream. Not thread-safe: one stream loop owns it.</summary>
public interface IFrameGrabber : IDisposable
{
    /// <summary>The next frame. Throws InvalidOperationException with a readable message when the screen can't be read.</summary>
    Capture Next();
}

internal sealed class RepeatedGrabber(IScreenCapture capture, int display, int maxWidth, int quality) : IFrameGrabber
{
    public Capture Next() => capture.Grab(display, maxWidth, quality);
    public void Dispose() { }
}
