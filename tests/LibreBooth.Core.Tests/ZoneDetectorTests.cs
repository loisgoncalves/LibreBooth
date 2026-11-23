using LibreBooth.Core.Layouts;
using SkiaSharp;

namespace LibreBooth.Core.Tests;

public sealed class ZoneDetectorTests
{
    [Fact]
    public void Finds_the_three_holes_of_a_strip_from_top_to_bottom()
    {
        using var frame = Images.CreateFrame(600, 1800, SKColors.Black,
            SKRect.Create(30, 790, 540, 360),
            SKRect.Create(30, 30, 540, 360),
            SKRect.Create(30, 410, 540, 360));

        var zones = ZoneDetector.Detect(frame);

        Assert.Equal(3, zones.Count);
        Assert.True(zones[0].Y < zones[1].Y && zones[1].Y < zones[2].Y);
        Assert.InRange(zones[0].X, 18, 42);
        Assert.InRange(zones[0].Y, 18, 42);
        Assert.InRange(zones[0].Width, 530, 570);
        Assert.InRange(zones[0].Height, 350, 390);
    }

    [Fact]
    public void Sorts_a_grid_left_to_right_then_top_to_bottom()
    {
        using var frame = Images.CreateFrame(1800, 1200, SKColors.White,
            SKRect.Create(912, 546, 840, 474),
            SKRect.Create(48, 546, 840, 474),
            SKRect.Create(912, 48, 840, 474),
            SKRect.Create(48, 48, 840, 474));

        var zones = ZoneDetector.Detect(frame);

        Assert.Equal(4, zones.Count);
        Assert.True(zones[0].X < zones[1].X && Math.Abs(zones[0].Y - zones[1].Y) < 20, "1re ligne : gauche puis droite");
        Assert.True(zones[2].X < zones[3].X && zones[2].Y > zones[0].Y, "2e ligne : gauche puis droite");
    }

    [Fact]
    public void Ignores_tiny_transparent_specks()
    {
        using var frame = Images.CreateFrame(1800, 1200, SKColors.White,
            SKRect.Create(100, 100, 900, 600),
            SKRect.Create(1500, 1000, 6, 6));

        var zones = ZoneDetector.Detect(frame);

        Assert.Single(zones);
    }

    [Fact]
    public void Opaque_frame_has_no_zone()
    {
        using var frame = Images.CreateFrame(600, 400, SKColors.White);

        Assert.Empty(ZoneDetector.Detect(frame));
    }
}
