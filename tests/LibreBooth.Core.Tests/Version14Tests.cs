using LibreBooth.Core.Design;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Util;
using SkiaSharp;

namespace LibreBooth.Core.Tests;

/// <summary>Personnalisation : mode « Thème LibreBooth » ou « Mes propres images ».</summary>
public sealed class DesignModeTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_new_event_starts_with_a_librebooth_theme()
    {
        var design = ThemeCatalog.CreateDefaultDesign().Normalize();

        Assert.False(design.UsesOwnImages);
        Assert.Null(design.BackgroundFileFor(DesignStyles.Postcard));
    }

    [Fact]
    public void An_older_customization_with_a_background_starts_in_own_images_mode()
    {
        var design = ThemeCatalog.CreateDefaultDesign();
        design.Backgrounds[DesignStyles.Postcard] = "fond-carte-10x15.png";
        design.OwnImages = null;

        design.Normalize();

        Assert.True(design.UsesOwnImages);
        Assert.Equal("fond-carte-10x15.png", design.BackgroundFileFor(DesignStyles.Postcard));
        Assert.Null(design.BackgroundFileFor(DesignStyles.Bookmark));
    }

    [Fact]
    public void Theme_mode_keeps_the_background_but_does_not_use_it()
    {
        var design = ThemeCatalog.CreateDefaultDesign();
        design.Backgrounds[DesignStyles.Postcard] = "fond-carte-10x15.png";
        design.OwnImages = false;

        var copy = JsonDefaults.Clone(design.Normalize());

        Assert.False(copy.UsesOwnImages);
        Assert.Null(copy.BackgroundFileFor(DesignStyles.Postcard));
        Assert.Equal("fond-carte-10x15.png", copy.StoredBackgroundFor(DesignStyles.Postcard));
        Assert.Null(new DesignContext(copy, _temp.Root).BackgroundPathFor(DesignStyles.Postcard));
    }

    [Fact]
    public void An_old_template_preview_is_replaced_once()
    {
        var library = new TemplateLibrary(_temp.Sub("Modeles"));
        var png = Images.WriteFramePng(Path.Combine(_temp.Root, "cadre.png"), 1800, 1200, SKColors.Navy, SKRect.Create(100, 100, 1600, 1000));
        var template = library.ImportFrame(png, "Cadre importé");
        File.WriteAllText(template.LegacyPreviewPath, "aperçu d'une version précédente");

        var preview = TemplatePreview.GetOrCreate(new LayoutCompositor(), template);

        Assert.False(File.Exists(template.LegacyPreviewPath));
        Assert.Equal(template.PreviewPath, preview);
        Assert.True(File.Exists(preview));
    }
}
