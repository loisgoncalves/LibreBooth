using LibreBooth.Core.Layouts;
using LibreBooth.Core.Util;
using SkiaSharp;

namespace LibreBooth.Core.Tests;

public sealed class LayoutCompositorTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly LayoutCompositor _compositor = new();
    private readonly CompositionContext _context = new("Test", new DateTime(2026, 6, 20));

    public void Dispose() => _temp.Dispose();

    private Template CreateTemplate(LayoutDefinition layout, params SKRect[] frameHoles)
    {
        var folder = _temp.Sub("modele-" + Guid.NewGuid().ToString("N")[..6]);
        if (frameHoles.Length > 0)
        {
            Images.WriteFramePng(Path.Combine(folder, "cadre.png"), layout.Width, layout.Height, SKColors.White, frameHoles);
            layout.FrameFile = "cadre.png";
        }
        else
        {
            layout.FrameFile = null;
        }
        JsonDefaults.Write(Path.Combine(folder, TemplateLibrary.LayoutFileName), layout);
        return new Template(folder, layout);
    }

    [Fact]
    public void Output_has_layout_size_and_each_photo_fills_its_zone()
    {
        var template = CreateTemplate(new LayoutDefinition
        {
            Width = 600,
            Height = 400,
            BackgroundColor = "#00FF00",
            Zones = { new PhotoZone(50, 50, 200, 150), new PhotoZone(350, 50, 200, 150) },
        });
        var red = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "rouge.jpg"), 640, 480, SKColors.Red);
        var blue = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "bleu.jpg"), 640, 480, SKColors.Blue);

        using var image = _compositor.Compose(template, new[] { red, blue }, _context);
        using var bitmap = SKBitmap.FromImage(image);

        Assert.Equal(600, image.Width);
        Assert.Equal(400, image.Height);
        Images.AssertColorNear(SKColors.Red, bitmap.GetPixel(150, 125));
        Images.AssertColorNear(SKColors.Blue, bitmap.GetPixel(450, 125));
        Images.AssertColorNear(new SKColor(0, 255, 0), bitmap.GetPixel(10, 10));
    }

    [Fact]
    public void Strip_is_placed_twice_side_by_side()
    {
        var template = CreateTemplate(new LayoutDefinition
        {
            Width = 200,
            Height = 600,
            DuplicateStrip = true,
            BackgroundColor = "#FFFFFF",
            Zones = { new PhotoZone(20, 20, 160, 120) },
        });
        var red = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "rouge.jpg"), 300, 200, SKColors.Red);

        using var image = _compositor.Compose(template, new[] { red }, _context);
        using var bitmap = SKBitmap.FromImage(image);

        Assert.Equal(400, image.Width);
        Assert.Equal(600, image.Height);
        Images.AssertColorNear(SKColors.Red, bitmap.GetPixel(100, 80));
        Images.AssertColorNear(SKColors.Red, bitmap.GetPixel(300, 80));
        Images.AssertColorNear(SKColors.White, bitmap.GetPixel(100, 400));
    }

    [Fact]
    public void Frame_is_drawn_over_the_photos()
    {
        var template = CreateTemplate(
            new LayoutDefinition { Width = 600, Height = 400, Zones = { new PhotoZone(0, 0, 600, 400) } },
            SKRect.Create(50, 50, 200, 150));
        var red = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "rouge.jpg"), 600, 400, SKColors.Red);

        using var image = _compositor.Compose(template, new[] { red }, _context);
        using var bitmap = SKBitmap.FromImage(image);

        Images.AssertColorNear(SKColors.White, bitmap.GetPixel(10, 10));
        Images.AssertColorNear(SKColors.Red, bitmap.GetPixel(150, 125));
    }

    [Fact]
    public void Black_and_white_turns_photos_gray()
    {
        var template = CreateTemplate(new LayoutDefinition
        {
            Width = 300,
            Height = 200,
            BlackAndWhite = true,
            Zones = { new PhotoZone(0, 0, 300, 200) },
        });
        var red = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "rouge.jpg"), 300, 200, SKColors.Red);

        using var image = _compositor.Compose(template, new[] { red }, _context);
        using var bitmap = SKBitmap.FromImage(image);
        var pixel = bitmap.GetPixel(150, 100);

        Assert.InRange(Math.Abs(pixel.Red - pixel.Green), 0, 3);
        Assert.InRange(Math.Abs(pixel.Green - pixel.Blue), 0, 3);
    }

    [Fact]
    public void Mirror_option_flips_photos_horizontally()
    {
        var template = CreateTemplate(new LayoutDefinition { Width = 400, Height = 200, Zones = { new PhotoZone(0, 0, 400, 200) } });
        var split = Images.WriteSplitJpeg(Path.Combine(_temp.Root, "split.jpg"), 400, 200, SKColors.Red, SKColors.Blue);

        using var normal = SKBitmap.FromImage(_compositor.Compose(template, new[] { split }, _context));
        using var mirrored = SKBitmap.FromImage(_compositor.Compose(template, new[] { split }, _context with { MirrorPhotos = true }));

        Images.AssertColorNear(SKColors.Red, normal.GetPixel(50, 100));
        Images.AssertColorNear(SKColors.Blue, mirrored.GetPixel(50, 100));
    }

    [Fact]
    public void Forced_rotation_turns_the_photo_a_quarter_turn_clockwise()
    {
        var split = Images.WriteSplitJpeg(Path.Combine(_temp.Root, "split.jpg"), 400, 200, SKColors.Red, SKColors.Blue);

        using var rotated = LayoutCompositor.LoadOriented(split, 200, 400, forcedRotation: 90);

        Assert.NotNull(rotated);
        Assert.Equal(200, rotated!.Width);
        Assert.Equal(400, rotated.Height);
        // La moitié gauche (rouge) se retrouve en haut après un quart de tour horaire.
        Images.AssertColorNear(SKColors.Red, rotated.GetPixel(100, 50));
        Images.AssertColorNear(SKColors.Blue, rotated.GetPixel(100, 350));
    }

    [Fact]
    public void Large_photos_are_decoded_at_a_reduced_size()
    {
        var big = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "grande.jpg"), 3456, 2304, SKColors.Red);

        using var decoded = LayoutCompositor.LoadOriented(big, 400, 300, 0);

        Assert.NotNull(decoded);
        Assert.True(decoded!.Width < 3456, $"Largeur décodée : {decoded.Width}");
        Assert.True(decoded.Width >= 400 && decoded.Height >= 300);
    }

    [Fact]
    public void Missing_photo_leaves_the_background()
    {
        var template = CreateTemplate(new LayoutDefinition
        {
            Width = 300,
            Height = 200,
            BackgroundColor = "#0000FF",
            Zones = { new PhotoZone(0, 0, 300, 200) },
        });

        using var image = _compositor.Compose(template, new[] { Path.Combine(_temp.Root, "absente.jpg") }, _context);
        using var bitmap = SKBitmap.FromImage(image);

        Images.AssertColorNear(SKColors.Blue, bitmap.GetPixel(150, 100));
    }

    [Fact]
    public void ComposeToFile_writes_a_readable_jpeg()
    {
        var template = CreateTemplate(new LayoutDefinition
        {
            Width = 600,
            Height = 400,
            Zones = { new PhotoZone(0, 0, 600, 400) },
            Texts = { new TextField { Content = "{evenement} – {date}", X = 300, Y = 380, Size = 30 } },
        });
        var red = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "rouge.jpg"), 600, 400, SKColors.Red);
        var output = Path.Combine(_temp.Root, "montage.jpg");

        _compositor.ComposeToFile(template, new[] { red }, _context, output);

        using var decoded = SKBitmap.Decode(output);
        Assert.Equal(600, decoded.Width);
        Assert.Equal(400, decoded.Height);
    }
}
