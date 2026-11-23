using LibreBooth.Core.Design;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Util;
using SkiaSharp;

namespace LibreBooth.Core.Tests;

public sealed class TemplateTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_two_default_templates_are_created_once()
    {
        var library = new TemplateLibrary(_temp.Sub("Modeles"));

        Assert.True(TemplateSeeder.SeedIfNeeded(library));
        Assert.False(TemplateSeeder.SeedIfNeeded(library));

        var templates = library.LoadAll().ToDictionary(t => t.Id);
        Assert.Equal(2, templates.Count);

        var postcard = templates[TemplateSeeder.PostcardId];
        Assert.Equal(1, postcard.ShotCount);
        Assert.Equal(DesignStyles.Postcard, postcard.Layout.DesignStyle);
        Assert.False(postcard.Layout.DuplicateStrip);
        Assert.Equal(PrintProfiles.Postcard, postcard.Layout.PrintProfile);
        Assert.True(postcard.Layout.Width > postcard.Layout.Height, "10x15 en paysage");

        var bookmark = templates[TemplateSeeder.BookmarkId];
        Assert.Equal(3, bookmark.ShotCount);
        Assert.Equal(DesignStyles.Bookmark, bookmark.Layout.DesignStyle);
        Assert.True(bookmark.Layout.DuplicateStrip);
        Assert.Equal(PrintProfiles.Strips, bookmark.Layout.PrintProfile);
    }

    [Fact]
    public void Old_test_templates_go_to_the_trash_and_imported_frames_stay()
    {
        var root = _temp.Sub("Modeles");
        foreach (var id in new[] { "10x15-1-photo", "10x15-4-photos", "bandelette-3-photos", "mon-cadre" })
        {
            var folder = Path.Combine(root, id);
            Directory.CreateDirectory(folder);
            JsonDefaults.Write(Path.Combine(folder, TemplateLibrary.LayoutFileName),
                new LayoutDefinition { Name = id, Zones = { new PhotoZone(10, 10, 100, 100) } });
        }
        File.WriteAllText(Path.Combine(root, ".modeles-initialises"), "v1");
        var library = new TemplateLibrary(root);

        Assert.True(TemplateSeeder.SeedIfNeeded(library));

        var ids = library.LoadAll().Select(t => t.Id).OrderBy(id => id).ToList();
        Assert.Equal(new[] { TemplateSeeder.PostcardId, "marque-page-3-photos", "mon-cadre" }.OrderBy(id => id), ids);
        Assert.Equal(3, Directory.GetDirectories(Path.Combine(root, "_corbeille")).Length);
    }

    [Theory]
    [InlineData(TemplateSeeder.PostcardId, 1800, 1200)]
    [InlineData(TemplateSeeder.BookmarkId, 1200, 1800)]
    public void Default_templates_produce_a_10x15_at_300_dpi(string id, int width, int height)
    {
        var library = new TemplateLibrary(_temp.Sub("Modeles"));
        TemplateSeeder.SeedIfNeeded(library);
        var template = library.Find(id)!;

        using var image = TemplatePreview.ComposeSample(new LayoutCompositor(), template);

        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
    }

    [Fact]
    public void Preview_is_created_then_reused()
    {
        var library = new TemplateLibrary(_temp.Sub("Modeles"));
        TemplateSeeder.SeedIfNeeded(library);
        var template = library.Find(TemplateSeeder.BookmarkId)!;
        var compositor = new LayoutCompositor();

        var preview = TemplatePreview.GetOrCreate(compositor, template);
        var firstWrite = File.GetLastWriteTimeUtc(preview);
        var again = TemplatePreview.GetOrCreate(compositor, template);

        Assert.True(File.Exists(preview));
        Assert.Equal(preview, again);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(again));
    }

    [Fact]
    public void Importing_a_strip_png_detects_zones_and_duplication()
    {
        var library = new TemplateLibrary(_temp.Sub("Modeles"));
        var png = Images.WriteFramePng(Path.Combine(_temp.Root, "mon-cadre.png"), 600, 1800, SKColors.Navy,
            SKRect.Create(30, 30, 540, 360),
            SKRect.Create(30, 410, 540, 360),
            SKRect.Create(30, 790, 540, 360));

        var template = library.ImportFrame(png, "Mon cadre mariage");

        Assert.Equal(3, template.ShotCount);
        Assert.True(template.Layout.DuplicateStrip);
        Assert.True(File.Exists(Path.Combine(template.Folder, "cadre.png")));
        Assert.Equal("Mon cadre mariage", library.Find(template.Id)!.Name);
    }

    [Fact]
    public void Deleted_template_goes_to_the_trash()
    {
        var library = new TemplateLibrary(_temp.Sub("Modeles"));
        TemplateSeeder.SeedIfNeeded(library);

        library.Delete(library.Find(TemplateSeeder.PostcardId)!);

        Assert.Single(library.LoadAll());
        Assert.Single(Directory.GetDirectories(Path.Combine(library.Root, "_corbeille")));
    }
}

public sealed class DnpStatusCodesTests
{
    [Theory]
    [InlineData(0x00010001, PrinterCondition.Ready)]
    [InlineData(0x00010080, PrinterCondition.Ready)]
    [InlineData(0x00010002, PrinterCondition.Busy)]
    [InlineData(0x00010008, PrinterCondition.Error)]
    [InlineData(0x00010010, PrinterCondition.Error)]
    [InlineData(0x00020001, PrinterCondition.Error)]
    [InlineData(0x00040001, PrinterCondition.Error)]
    [InlineData(0x12345678, PrinterCondition.Unknown)]
    [InlineData(0, PrinterCondition.Unknown)]
    [InlineData(-1, PrinterCondition.Unknown)]
    public void Status_codes_are_translated(int code, PrinterCondition expected)
    {
        Assert.Equal(expected, DnpStatusCodes.Describe(code).Condition);
    }

    [Fact]
    public void Paper_end_message_mentions_the_roll()
    {
        Assert.Contains("papier", DnpStatusCodes.Describe(0x00010008).Message);
    }

    [Fact]
    public void A_6x8_roll_counts_two_postcards_per_sheet()
    {
        // Valeurs relevées sur une DS620 chargée en 15x20 : 102 feuilles, 204 demi-feuilles, rouleau de 200.
        var sixByEight = new DnpProbeResult { Media = "00310", Remaining = 102, RemainingHalf = 204, InitialCount = 200 };
        var sixByFour = new DnpProbeResult { Media = "00300", Remaining = 350, RemainingHalf = 700 };

        Assert.Equal(("15x20 (6x8)", true), DnpStatusCodes.DescribeMedia("00310"));
        Assert.Equal(204, DnpStatusCodes.RemainingPostcards(sixByEight));
        Assert.Equal(350, DnpStatusCodes.RemainingPostcards(sixByFour));
        Assert.Equal(204, DnpStatusCodes.RemainingPostcards(new DnpProbeResult { Media = "00310", Remaining = 102 }));
        Assert.Null(DnpStatusCodes.RemainingPostcards(new DnpProbeResult()));
    }
}
