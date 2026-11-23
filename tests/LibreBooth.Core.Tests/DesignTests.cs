using LibreBooth.Core.Design;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Util;
using SkiaSharp;

namespace LibreBooth.Core.Tests;

public sealed class DesignTests : IDisposable
{
    private static readonly DateTime EventDate = new(2026, 11, 22);
    private readonly TempFolder _temp = new();
    private readonly LayoutCompositor _compositor = new();

    public void Dispose() => _temp.Dispose();

    private Template Postcard() => new(_temp.Sub("carte"), TemplateSeeder.CreatePostcard());

    private Template Bookmark() => new(_temp.Sub("marque-page"), TemplateSeeder.CreateBookmark());

    private string BluePhoto() => Images.WriteSolidJpeg(Path.Combine(_temp.Root, "bleu.jpg"), 1500, 1000, SKColors.Blue);

    private SKBitmap Compose(Template template, EventDesign design, string? assets = null)
    {
        var photos = Enumerable.Repeat(BluePhoto(), template.ShotCount).ToList();
        var context = new CompositionContext("Marie & Paul", EventDate, Design: new DesignContext(design, assets));
        using var image = _compositor.Compose(template, photos, context);
        return SKBitmap.FromImage(image);
    }

    public static TheoryData<string> ThemeIds()
    {
        var data = new TheoryData<string>();
        foreach (var theme in ThemeCatalog.All) data.Add(theme.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void Every_theme_renders_both_templates_with_the_photos_in_place(string themeId)
    {
        var design = ThemeCatalog.CreateDesign(themeId);
        design.Phrase = "Merci d'avoir partagé cette belle journée avec nous";

        using var card = Compose(Postcard(), design);
        using var strip = Compose(Bookmark(), design);

        Assert.Equal((1800, 1200), (card.Width, card.Height));
        Assert.Equal((1200, 1800), (strip.Width, strip.Height));
        Images.AssertColorNear(SKColors.Blue, card.GetPixel(900, 500));
        // Marque-page imprimé deux fois : la même photo dans chaque moitié.
        Images.AssertColorNear(SKColors.Blue, strip.GetPixel(300, 900));
        Images.AssertColorNear(SKColors.Blue, strip.GetPixel(900, 900));
    }

    [Fact]
    public void Applying_a_theme_keeps_texts_background_and_logo()
    {
        var design = ThemeCatalog.CreateDefaultDesign();
        design.Title = "Les 40 ans de Julie";
        design.Phrase = "#Julie40";
        design.Backgrounds[DesignStyles.Postcard] = "fond.png";
        design.LogoFile = "logo.png";

        ThemeCatalog.Find("fete-neon")!.ApplyTo(design);

        Assert.Equal("fete-neon", design.ThemeId);
        Assert.Equal(DecorStyle.Neon, design.Decor);
        Assert.Equal("Les 40 ans de Julie", design.Title);
        Assert.Equal("#Julie40", design.Phrase);
        Assert.Equal("fond.png", design.Backgrounds[DesignStyles.Postcard]);
        Assert.Equal("logo.png", design.LogoFile);
    }

    [Fact]
    public void Custom_background_replaces_the_theme_under_the_photos()
    {
        var eventFolder = _temp.Sub("evenement");
        var customization = CustomizationStore.Load(eventFolder);
        var red = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "fond-rouge.jpg"), 1800, 1200, SKColors.Red);
        CustomizationStore.ImportBackground(eventFolder, customization, DesignStyles.Postcard, red, 1800, 1200);
        customization.Design.TextsOverBackground = false;

        using var card = Compose(Postcard(), customization.Design, CustomizationStore.FolderFor(eventFolder));

        Images.AssertColorNear(SKColors.Red, card.GetPixel(20, 600));
        Images.AssertColorNear(SKColors.Red, card.GetPixel(900, 1100));
        Images.AssertColorNear(SKColors.Blue, card.GetPixel(900, 500));
    }

    [Fact]
    public void Imported_background_is_resized_to_the_template_and_crop_is_reported()
    {
        var eventFolder = _temp.Sub("evenement");
        var customization = CustomizationStore.Load(eventFolder);
        var square = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "carre.jpg"), 1000, 1000, SKColors.Green);

        var result = CustomizationStore.ImportBackground(eventFolder, customization, DesignStyles.Bookmark, square, 600, 1800);

        Assert.True(result.Cropped);
        using var saved = SKBitmap.Decode(Path.Combine(CustomizationStore.FolderFor(eventFolder), result.FileName));
        Assert.Equal((600, 1800), (saved.Width, saved.Height));
        Assert.Equal(result.FileName, CustomizationStore.Load(eventFolder).Design.BackgroundFileFor(DesignStyles.Bookmark));
    }

    [Fact]
    public void Large_logo_is_reduced_and_drawn()
    {
        var eventFolder = _temp.Sub("evenement");
        var customization = CustomizationStore.Load(eventFolder);
        var logo = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "logo.jpg"), 3000, 1500, SKColors.Lime);

        CustomizationStore.ImportLogo(eventFolder, customization, logo);

        using var saved = SKBitmap.Decode(Path.Combine(CustomizationStore.FolderFor(eventFolder), CustomizationStore.LogoFileName));
        Assert.Equal(1200, saved.Width);
        using var card = Compose(Postcard(), customization.Design, CustomizationStore.FolderFor(eventFolder));
        var area = DesignLayout.Compute(TemplateSeeder.CreatePostcard(), customization.Design, customization.Design.Decor, hasLogo: true).LogoArea!.Value;
        Images.AssertColorNear(SKColors.Lime, card.GetPixel((int)area.MidX, (int)area.MidY));
    }

    [Fact]
    public void Rounded_corners_leave_the_background_in_the_corner_of_the_photo()
    {
        var design = ThemeCatalog.CreateDesign("mariage-ivoire");
        design.PhotoCornerRadius = 60;
        design.Decor = DecorStyle.None;
        var zone = TemplateSeeder.CreatePostcard().Zones[0];

        using var card = Compose(Postcard(), design);

        Images.AssertColorNear(SKColor.Parse(design.BackgroundColor), card.GetPixel(zone.X + 3, zone.Y + 3));
        Images.AssertColorNear(SKColors.Blue, card.GetPixel(zone.X + 80, zone.Y + 80));
    }

    [Fact]
    public void Customization_round_trips_and_defaults_offer_both_templates()
    {
        var eventFolder = _temp.Sub("evenement");
        var fresh = CustomizationStore.Load(eventFolder);
        Assert.True(fresh.IsNew);
        Assert.Equal(TemplateSeeder.DefaultTemplateIds, fresh.Templates);
        Assert.Equal(2, fresh.MaxCopies);

        fresh.MaxCopies = 4;
        fresh.Templates = new List<string> { TemplateSeeder.BookmarkId };
        fresh.Design.Title = "Soirée Acme";
        ThemeCatalog.Find("entreprise-noir")!.ApplyTo(fresh.Design);
        CustomizationStore.Save(eventFolder, fresh);

        var loaded = CustomizationStore.Load(eventFolder);
        Assert.False(loaded.IsNew);
        Assert.Equal(4, loaded.MaxCopies);
        Assert.Equal(new[] { TemplateSeeder.BookmarkId }, loaded.Templates);
        Assert.Equal("Soirée Acme", loaded.Design.Title);
        Assert.Equal(DecorStyle.Minimal, loaded.Design.Decor);
    }

    [Fact]
    public void Out_of_range_customization_is_corrected()
    {
        var customization = new EventCustomization { MaxCopies = 50 };
        customization.Design.TitleScale = 400;
        customization.Design.AccentColor = "pas une couleur";

        customization.Normalize();

        Assert.Equal(EventCustomization.MaxCopiesLimit, customization.MaxCopies);
        Assert.Equal(EventDesign.MaxScale, customization.Design.TitleScale);
        Assert.True(SKColor.TryParse(customization.Design.AccentColor, out _));
    }

    [Fact]
    public void Copying_from_another_event_brings_files_and_settings()
    {
        var source = _temp.Sub("source");
        var target = _temp.Sub("cible");
        var customization = CustomizationStore.Load(source);
        customization.Design.Phrase = "Merci !";
        CustomizationStore.ImportLogo(source, customization, Images.WriteSolidJpeg(Path.Combine(_temp.Root, "logo.jpg"), 400, 200, SKColors.Lime));

        var copy = CustomizationStore.CopyFrom(source, target);

        Assert.Equal("Merci !", copy.Design.Phrase);
        Assert.True(File.Exists(Path.Combine(CustomizationStore.FolderFor(target), CustomizationStore.LogoFileName)));
        Assert.Equal("Merci !", CustomizationStore.Load(target).Design.Phrase);
    }

    [Fact]
    public void Event_preview_is_cached_and_renewed_when_the_design_changes()
    {
        var template = Postcard();
        JsonDefaults.Write(template.LayoutFile, template.Layout);
        var cache = _temp.Sub("apercus");
        var design = ThemeCatalog.CreateDefaultDesign();

        var first = TemplatePreview.GetOrCreate(_compositor, template, new PreviewContext("Marie & Paul", EventDate, new DesignContext(design, null), cache));
        var again = TemplatePreview.GetOrCreate(_compositor, template, new PreviewContext("Marie & Paul", EventDate, new DesignContext(design, null), cache));
        design.BackgroundColor = "#000000";
        var changed = TemplatePreview.GetOrCreate(_compositor, template, new PreviewContext("Marie & Paul", EventDate, new DesignContext(design, null), cache));

        Assert.Equal(first, again);
        Assert.NotEqual(first, changed);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(changed));
    }

    [Theory]
    [InlineData(DesignStyles.Postcard, 1800, 1200)]
    [InlineData(DesignStyles.Bookmark, 600, 1800)]
    public void Guide_has_the_size_of_one_print(string style, int width, int height)
    {
        var layout = style == DesignStyles.Postcard ? TemplateSeeder.CreatePostcard() : TemplateSeeder.CreateBookmark();
        var path = Path.Combine(_temp.Root, "gabarit.png");

        DesignGuide.WritePng(layout, path);

        using var guide = SKBitmap.Decode(path);
        Assert.Equal((width, height), (guide.Width, guide.Height));
    }

    [Fact]
    public void Text_variables_include_a_readable_date()
    {
        Assert.Equal("Marie & Paul – 22 novembre 2026", TextUtil.ApplyVariables("{evenement} – {date_longue}", "Marie & Paul", EventDate));
        Assert.Equal("1er mai 2027", TextUtil.ApplyVariables("{date_longue}", "", new DateTime(2027, 5, 1)));
        Assert.Equal("22.11.2026", TextUtil.ApplyVariables("{date}", "", EventDate));
    }
}
