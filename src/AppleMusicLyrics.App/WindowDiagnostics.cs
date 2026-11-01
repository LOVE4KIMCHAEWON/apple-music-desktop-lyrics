using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppleMusicLyrics.App;

/// <summary>Read-only checks of this application's own overlay during explicit preview QA.</summary>
internal static class WindowDiagnostics
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExLayered = 0x00080000;
    private const long WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Captures styles, rendered alpha, and a passive hit-test at an opaque active-lyric pixel.
    /// This sends no input, changes no window state, and is not proof of every mouse scenario.
    /// Call on the UI thread after layout and the short text fade have completed.
    /// </summary>
    public static object Capture(OverlayWindow window)
    {
        window.Dispatcher.VerifyAccess();
        var hwnd = new WindowInteropHelper(window).Handle;
        var exStyles = hwnd != IntPtr.Zero ? GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() : 0;
        var foreground = GetForegroundWindow();
        object? bounds = null;
        if (GetWindowRect(hwnd, out var rect))
            bounds = new { rect.Left, rect.Top, rect.Right, rect.Bottom };
        var primaryInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var primaryMonitor = MonitorFromPoint(new NativePoint(0, 0), 1);
        object? primaryBounds = null;
        object? primaryWorkArea = null;
        if (GetMonitorInfo(primaryMonitor, ref primaryInfo))
        {
            primaryBounds = new { primaryInfo.Monitor.Left, primaryInfo.Monitor.Top, primaryInfo.Monitor.Right, primaryInfo.Monitor.Bottom };
            primaryWorkArea = new { primaryInfo.Work.Left, primaryInfo.Work.Top, primaryInfo.Work.Right, primaryInfo.Work.Bottom };
        }

        object? alphaCorners = null;
        object? lyricPixel = null;
        string? hitWindow = null;
        bool? hitIsOverlay = null;
        string? samplingError = null;
        var dpi = VisualTreeHelper.GetDpi(window);
        if (window.IsLoaded && window.ActualWidth > 0 && window.ActualHeight > 0)
        {
            var width = Math.Max(1, checked((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX)));
            var height = Math.Max(1, checked((int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY)));
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            bitmap.CopyPixels(pixels, stride, 0);
            alphaCorners = new
            {
                TopLeft = pixels[3],
                TopRight = pixels[(width - 1) * 4 + 3],
                BottomLeft = pixels[(height - 1) * stride + 3],
                BottomRight = pixels[(height - 1) * stride + (width - 1) * 4 + 3]
            };

            if (window.FindName("ActiveLine") is FrameworkElement activeLine)
            {
                // Restrict the search to the lyric row, excluding edit controls and the lock button.
                var lyricBounds = activeLine.TransformToAncestor(window).TransformBounds(new Rect(activeLine.RenderSize));
                var left = Math.Clamp((int)Math.Floor(lyricBounds.Left * dpi.DpiScaleX), 0, width - 1);
                var top = Math.Clamp((int)Math.Floor(lyricBounds.Top * dpi.DpiScaleY), 0, height - 1);
                var right = Math.Clamp((int)Math.Ceiling(lyricBounds.Right * dpi.DpiScaleX), left + 1, width);
                var bottom = Math.Clamp((int)Math.Ceiling(lyricBounds.Bottom * dpi.DpiScaleY), top + 1, height);
                var found = FindOpaquePixel(pixels, stride, left, top, right, bottom);
                if (found is { } pixel)
                {
                    // Bitmap coordinates are physical pixels; PointToScreen accepts WPF DIPs.
                    var local = new Point((pixel.X + 0.5) / dpi.DpiScaleX, (pixel.Y + 0.5) / dpi.DpiScaleY);
                    var screen = window.PointToScreen(local);
                    var screenPixel = new NativePoint((int)Math.Floor(screen.X), (int)Math.Floor(screen.Y));
                    var hit = WindowFromPoint(screenPixel);
                    hitWindow = HandleText(hit);
                    hitIsOverlay = hit == hwnd;
                    var offset = pixel.Y * stride + pixel.X * 4;
                    lyricPixel = new
                    {
                        BitmapX = pixel.X,
                        BitmapY = pixel.Y,
                        LocalDipX = local.X,
                        LocalDipY = local.Y,
                        ScreenPixelX = screenPixel.X,
                        ScreenPixelY = screenPixel.Y,
                        Alpha = pixels[offset + 3],
                        PremultipliedRed = pixels[offset + 2],
                        PremultipliedGreen = pixels[offset + 1],
                        PremultipliedBlue = pixels[offset]
                    };
                }
                else samplingError = "No active-lyric pixel with alpha >= 200 was rendered; hit-testing was not sampled.";
            }
            else samplingError = "The active lyric visual was not found; hit-testing was not sampled.";
        }
        else samplingError = "The overlay has no loaded layout; alpha and hit-testing were not sampled.";

        return new
        {
            Hwnd = HandleText(hwnd),
            IsVisible = window.IsVisible,
            IsLoaded = window.IsLoaded,
            WpfLeftDip = window.Left,
            WpfTopDip = window.Top,
            NativeIsVisible = IsWindowVisible(hwnd),
            BoundsInScreenPixels = bounds,
            PrimaryMonitorBoundsInScreenPixels = primaryBounds,
            PrimaryWorkAreaInScreenPixels = primaryWorkArea,
            ExStyles = "0x" + exStyles.ToString("X"),
            IsTopmost = (exStyles & WsExTopmost) != 0,
            IsLayered = (exStyles & WsExLayered) != 0,
            IsClickThroughStyleSet = (exStyles & WsExTransparent) != 0,
            IsNoActivateStyleSet = (exStyles & WsExNoActivate) != 0,
            IsToolWindow = (exStyles & WsExToolWindow) != 0,
            ForegroundHwnd = HandleText(foreground),
            ForegroundIsOverlay = foreground == hwnd && hwnd != IntPtr.Zero,
            DpiScaleX = dpi.DpiScaleX,
            DpiScaleY = dpi.DpiScaleY,
            RenderedCornerAlpha = alphaCorners,
            OpaqueActiveLyricPixel = lyricPixel,
            WindowFromPointHwnd = hitWindow,
            WindowFromPointIsOverlay = hitIsOverlay,
            SamplingError = samplingError,
            ProbeScope = "Passive Win32 styles and WindowFromPoint sample; no mouse input was sent."
        };
    }

    private static (int X, int Y)? FindOpaquePixel(byte[] pixels, int stride, int left, int top, int right, int bottom)
    {
        // Prefer the most opaque pixel. The edit background's alpha is 170, below this threshold.
        (int X, int Y)? found = null;
        byte strongestAlpha = 199;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var alpha = pixels[y * stride + x * 4 + 3];
            if (alpha <= strongestAlpha) continue;
            found = (x, y);
            strongestAlpha = alpha;
            if (alpha == 255) return found;
        }
        return found;
    }

    private static string HandleText(IntPtr handle) => "0x" + handle.ToInt64().ToString("X");

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
