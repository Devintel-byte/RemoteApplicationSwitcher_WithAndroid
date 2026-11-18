using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ShowControl.Agent;

/// <summary>GDI screen capture. Works for windowed and borderless-fullscreen apps (TouchDesigner, Resolume, OBS, most Unity builds).
/// Exclusive-fullscreen DirectX apps may come out black.
///
/// Frames are scaled straight from the screen into a small bitmap with StretchBlt (one GDI call, no
/// full-resolution copy), so a 1080p display streams at 640 px wide in a few milliseconds per frame.</summary>
public sealed class WinScreenCapture : IScreenCapture
{
    private static readonly ImageCodecInfo? JpegCodec =
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public IReadOnlyList<DisplayInfo> Displays() =>
        Screen.AllScreens
            .Select((s, i) => new DisplayInfo(i, $"Display {i + 1}", s.Bounds.Width, s.Bounds.Height, s.Primary))
            .ToList();

    public Capture Grab(int display, int maxWidth, int quality)
    {
        using var grabber = new Grabber(display, maxWidth, quality);
        return grabber.Next();
    }

    public IFrameGrabber OpenStream(int display, int maxWidth, int quality) => new Grabber(display, maxWidth, quality);

    private static Rectangle BoundsOf(int display)
    {
        var screens = Screen.AllScreens;
        if (display < 0 || display >= screens.Length) throw new InvalidOperationException($"No display {display + 1} (this PC has {screens.Length})");
        return screens[display].Bounds;
    }

    private sealed class Grabber : IFrameGrabber
    {
        private readonly int _display;
        private readonly int _maxWidth;
        private readonly EncoderParameters _encoderParams;
        private readonly MemoryStream _jpeg = new();
        private Rectangle _bounds;
        private Bitmap? _bitmap;
        private int _frames;

        public Grabber(int display, int maxWidth, int quality)
        {
            _display = display;
            _maxWidth = Math.Clamp(maxWidth, 160, 1920);
            _encoderParams = new EncoderParameters(1);
            _encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 30, 95));
            Resize(BoundsOf(display));
        }

        private void Resize(Rectangle bounds)
        {
            _bounds = bounds;
            int w = Math.Min(_maxWidth, bounds.Width);
            int h = Math.Max(1, (int)Math.Round(bounds.Height * (w / (double)bounds.Width)));
            _bitmap?.Dispose();
            _bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        }

        public Capture Next()
        {
            // Pick up resolution or monitor changes now and then rather than paying for it every frame.
            if (++_frames % 60 == 0)
            {
                var now = BoundsOf(_display);
                if (now != _bounds) Resize(now);
            }
            var bmp = _bitmap!;

            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr dest = g.GetHdc();
                IntPtr screen = GetDC(IntPtr.Zero);
                bool ok;
                try
                {
                    SetStretchBltMode(dest, HALFTONE);
                    SetBrushOrgEx(dest, 0, 0, IntPtr.Zero);
                    ok = bmp.Width == _bounds.Width
                        ? BitBlt(dest, 0, 0, bmp.Width, bmp.Height, screen, _bounds.Left, _bounds.Top, SRCCOPY)
                        : StretchBlt(dest, 0, 0, bmp.Width, bmp.Height, screen, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, SRCCOPY);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, screen);
                    g.ReleaseHdc(dest);
                }
                if (!ok) throw new InvalidOperationException("Screen unavailable (PC locked, signed out or on the login screen)");
            }

            _jpeg.SetLength(0);
            if (JpegCodec is null) bmp.Save(_jpeg, ImageFormat.Jpeg);
            else bmp.Save(_jpeg, JpegCodec, _encoderParams);
            return new Capture(_jpeg.ToArray(), bmp.Width, bmp.Height);
        }

        public void Dispose()
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _encoderParams.Dispose();
            _jpeg.Dispose();
        }
    }

    private const int HALFTONE = 4;
    private const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
}
