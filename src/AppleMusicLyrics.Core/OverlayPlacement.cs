namespace AppleMusicLyrics.Core;

/// <summary>A monitor working area expressed in physical desktop pixels.</summary>
public readonly record struct DesktopRect(int Left, int Top, int Width, int Height);

public readonly record struct DesktopPoint(int X, int Y);

/// <summary>
/// Pure placement arithmetic. The Windows caller supplies the selected monitor's physical
/// working area and the overlay's size after applying that monitor's DPI scale.
/// </summary>
public static class OverlayPlacement
{
    public static DesktopPoint BottomCenter(DesktopRect work, int width, int height, int bottomMargin)
    {
        Validate(work, width, height);
        ArgumentOutOfRangeException.ThrowIfNegative(bottomMargin);
        var left = (long)work.Left + Math.Max(0L, (long)work.Width - width) / 2;
        var top = (long)work.Top + work.Height - height - bottomMargin;
        return ClampCore(work, width, height, left, top);
    }

    /// <summary>
    /// Preserve a custom position while it fits. Oversized windows anchor at the work-area
    /// origin; the caller may separately reduce their size to make the full window visible.
    /// </summary>
    public static DesktopPoint Clamp(DesktopRect work, int width, int height, DesktopPoint desired)
    {
        Validate(work, width, height);
        return ClampCore(work, width, height, desired.X, desired.Y);
    }

    private static DesktopPoint ClampCore(DesktopRect work, int width, int height, long x, long y) => new(
        ClampCoordinate(work.Left, work.Width, width, x),
        ClampCoordinate(work.Top, work.Height, height, y));

    private static int ClampCoordinate(int origin, int areaSize, int windowSize, long desired)
    {
        // Wider intermediate arithmetic also handles monitors with negative desktop origins.
        var maximum = Math.Min(int.MaxValue, (long)origin + Math.Max(0L, (long)areaSize - windowSize));
        return (int)Math.Clamp(desired, (long)origin, maximum);
    }

    private static void Validate(DesktopRect work, int width, int height)
    {
        if (work.Width < 0 || work.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(work), "Working-area dimensions must be nonnegative.");
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
    }
}
