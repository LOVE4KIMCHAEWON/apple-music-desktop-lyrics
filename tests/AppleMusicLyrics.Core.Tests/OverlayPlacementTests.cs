namespace AppleMusicLyrics.Core.Tests;

public sealed class OverlayPlacementTests
{
    public static IEnumerable<object[]> ResolutionAndScaleCases()
    {
        foreach (var (width, height) in new[] { (1920, 1080), (2560, 1440), (3840, 2160) })
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                yield return [width, height, scale];
    }

    [Theory]
    [MemberData(nameof(ResolutionAndScaleCases))]
    public void BottomCentersUsingPhysicalSizesAtEachResolutionAndScale(int screenWidth, int screenHeight, double scale)
    {
        var work = new DesktopRect(0, 0, screenWidth, screenHeight);
        var overlayWidth = (int)(800 * scale);
        var overlayHeight = (int)(120 * scale);
        var margin = (int)(60 * scale);
        var result = OverlayPlacement.BottomCenter(work, overlayWidth, overlayHeight, margin);
        Assert.Equal((screenWidth - overlayWidth) / 2, result.X);
        Assert.Equal(screenHeight - overlayHeight - margin, result.Y);
        Assert.True(result.X >= work.Left && result.X + overlayWidth <= work.Left + work.Width);
        Assert.True(result.Y >= work.Top && result.Y + overlayHeight <= work.Top + work.Height);
    }

    [Fact]
    public void SupportsMonitorWithNegativeDesktopOrigin()
    {
        var work = new DesktopRect(-2560, -200, 2560, 1400);
        Assert.Equal(new DesktopPoint(-1680, 1000), OverlayPlacement.BottomCenter(work, 800, 120, 80));
        Assert.Equal(new DesktopPoint(-2560, -200), OverlayPlacement.Clamp(work, 800, 120, new(-5000, -900)));
        Assert.Equal(new DesktopPoint(-800, 1080), OverlayPlacement.Clamp(work, 800, 120, new(1000, 2000)));
    }

    [Fact]
    public void KeepsCustomPositionWhenItStillFitsAfterDisplayChange()
    {
        var chosen = new DesktopPoint(320, 260);
        Assert.Equal(chosen, OverlayPlacement.Clamp(new(0, 0, 3840, 2160), 800, 120, chosen));
        Assert.Equal(chosen, OverlayPlacement.Clamp(new(0, 0, 1920, 1040), 800, 120, chosen));
    }

    [Fact]
    public void RecoversCustomPositionFromDisconnectedMonitor()
    {
        var primary = new DesktopRect(0, 0, 1920, 1040);
        Assert.Equal(new DesktopPoint(1120, 920), OverlayPlacement.Clamp(primary, 800, 120, new(4100, 1500)));
        Assert.Equal(new DesktopPoint(0, 0), OverlayPlacement.Clamp(primary, 800, 120, new(-2300, -500)));
    }

    [Fact]
    public void ThreeLineExpansionKeepsBottomAnchorAndMovesTopUp()
    {
        var work = new DesktopRect(1920, 100, 2560, 1400);
        var two = OverlayPlacement.BottomCenter(work, 1200, 180, 75);
        var three = OverlayPlacement.BottomCenter(work, 1200, 270, 75);
        Assert.Equal(two.X, three.X);
        Assert.Equal(two.Y + 180, three.Y + 270);
        Assert.Equal(two.Y - 90, three.Y);
    }

    [Fact]
    public void OversizedWindowAnchorsAtWorkOrigin()
    {
        var work = new DesktopRect(-300, 100, 640, 480);
        Assert.Equal(new DesktopPoint(-300, 100), OverlayPlacement.Clamp(work, 1000, 800, new(30, 50)));
        Assert.Equal(new DesktopPoint(-300, 100), OverlayPlacement.BottomCenter(work, 1000, 800, 100));
    }

    [Fact]
    public void ExcessiveBottomMarginIsClampedWithinWorkingArea()
    {
        Assert.Equal(new DesktopPoint(560, 0), OverlayPlacement.BottomCenter(new(0, 0, 1920, 1040), 800, 120, 5000));
    }

    [Fact]
    public void ZeroSizesAreValidAndArithmeticDoesNotOverflow()
    {
        Assert.Equal(new DesktopPoint(-5, -10), OverlayPlacement.Clamp(new(-5, -10, 0, 0), 0, 0, new(100, 200)));
        Assert.Equal(new DesktopPoint(int.MaxValue, int.MaxValue),
            OverlayPlacement.BottomCenter(new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue), 0, 0, 0));
        Assert.Equal(new DesktopPoint(int.MinValue, int.MinValue),
            OverlayPlacement.BottomCenter(new(int.MinValue, int.MinValue, 0, 0), int.MaxValue, int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void RejectsNegativeDimensionsAndMargin()
    {
        var work = new DesktopRect(0, 0, 1920, 1040);
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayPlacement.Clamp(work with { Width = -1 }, 800, 120, new(0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayPlacement.Clamp(work with { Height = -1 }, 800, 120, new(0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayPlacement.Clamp(work, -1, 120, new(0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayPlacement.Clamp(work, 800, -1, new(0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayPlacement.BottomCenter(work, 800, 120, -1));
    }
}
