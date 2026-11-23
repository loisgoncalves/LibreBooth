using LibreBooth.Core.Config;
using LibreBooth.Core.Design;
using LibreBooth.Core.Events;
using LibreBooth.Core.Session;
using SkiaSharp;

namespace LibreBooth.Core.Tests;

/// <summary>Bouton « Recommencer » : machine d'états, rangement de la série refusée, réglage.</summary>
public sealed class RetakeTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Retake_goes_back_to_the_first_photo_from_the_review_only()
    {
        var sm = new SessionStateMachine();
        Assert.False(sm.Retake());
        Assert.True(sm.BeginShooting(3));
        Assert.False(sm.Retake());
        sm.ShotCompleted();
        sm.ShotCompleted();
        sm.ShotCompleted();
        Assert.False(sm.Retake());
        Assert.True(sm.CompositionReady());

        Assert.True(sm.Retake());
        Assert.Equal(KioskState.Shooting, sm.State);
        Assert.Equal(0, sm.ShotIndex);
        Assert.Equal(3, sm.ShotCount);

        // La nouvelle série se déroule comme la première.
        Assert.False(sm.ShotCompleted());
        Assert.False(sm.ShotCompleted());
        Assert.True(sm.ShotCompleted());
        Assert.True(sm.CompositionReady());
        Assert.True(sm.OpenEmail());
        Assert.False(sm.Retake());
    }

    [Fact]
    public void Refused_series_is_kept_aside_and_leaves_the_gallery()
    {
        var store = new EventStore(Path.Combine(_temp.Root, "Evenements"));
        var ev = store.Create("Mariage", new DateTime(2026, 11, 22));
        const string id = "20261122-150000-000";
        var photos = new[] { 1, 2, 3 }.Select(i => Path.Combine(ev.OriginalsFolder, $"{id}_{i}.jpg")).ToList();
        foreach (var photo in photos) File.WriteAllText(photo, "photo");
        var composite = Path.Combine(ev.CompositesFolder, id + ".jpg");
        File.WriteAllText(composite, "montage");
        store.SaveSession(ev, new SessionRecord { Id = id, Composite = Path.GetFileName(composite) });
        store.UpdateStats(ev, s =>
        {
            s.Photos = 3;
            s.Sessions = 1;
        });

        store.SetAsideRetakenSession(ev, id, photos, composite);

        Assert.All(photos, p => Assert.False(File.Exists(p)));
        Assert.False(File.Exists(composite));
        Assert.True(File.Exists(Path.Combine(ev.RefusedFolder, $"{id}_1.jpg")));
        Assert.True(File.Exists(Path.Combine(ev.RefusedFolder, $"{id}_3.jpg")));
        Assert.True(File.Exists(Path.Combine(ev.RefusedFolder, id + ".jpg")));
        Assert.Empty(store.ListSessions(ev));

        var stats = new EventStore(Path.Combine(_temp.Root, "Evenements")).Open(ev.Folder)!.Info.Stats;
        Assert.Equal(0, stats.Sessions);
        Assert.Equal(3, stats.Photos);
        Assert.Equal(1, stats.Retakes);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(40, ScreenSettings.MaxRetakes)]
    public void Retake_limit_stays_reasonable(int typed, int expected)
    {
        var settings = new AppSettings();
        Assert.Equal(1, settings.Screens.RetakeLimit);
        settings.Screens.RetakeLimit = typed;
        Assert.Equal(expected, settings.Normalize().Screens.RetakeLimit);
    }
}

/// <summary>Couleurs des écrans tirées de la personnalisation de l'événement.</summary>
public sealed class KioskPaletteTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> ThemeIds()
    {
        var data = new TheoryData<string>();
        foreach (var theme in ThemeCatalog.All) data.Add(theme.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void Every_theme_gives_readable_screens(string themeId)
    {
        var design = ThemeCatalog.CreateDesign(themeId);
        var palette = KioskPalette.Create(design);

        Assert.Equal(design.BackgroundColor.ToUpperInvariant(), palette.Background);
        AssertReadable(palette);
        Assert.Contains("thème", palette.Source);
    }

    [Fact]
    public void Unreadable_theme_colors_are_corrected()
    {
        var design = ThemeCatalog.CreateDefaultDesign();
        design.BackgroundColor = "#FFFFFF";
        design.TitleColor = "#F4F4F4";
        design.TextColor = "#FAFAFA";
        design.AccentColor = "#FEFEFE";
        design.Accent2Color = "#FDFDFD";

        var palette = KioskPalette.Create(design);

        AssertReadable(palette);
        Assert.False(palette.IsDark);
    }

    [Fact]
    public void Colors_come_from_the_png_background_when_there_is_one()
    {
        // Fond nuit (75 %), bande rose vif (15 %), un peu de blanc (10 %).
        var path = Path.Combine(_temp.Root, "fond.png");
        using (var bitmap = new SKBitmap(600, 400))
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x10, 0x16, 0x3A));
            using var pink = new SKPaint { Color = new SKColor(0xFF, 0x2E, 0x88) };
            canvas.DrawRect(SKRect.Create(0, 300, 600, 60), pink);
            using var white = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(SKRect.Create(0, 360, 600, 40), white);
            using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }

        var palette = KioskPalette.Create(ThemeCatalog.CreateDefaultDesign(), path, "votre fond du 10x15");

        Assert.True(palette.IsDark);
        Assert.Equal("votre fond du 10x15", palette.Source);
        var background = SKColor.Parse(palette.Background);
        Assert.True(background.Blue > background.Red && background.Red < 0x30);
        var accent = SKColor.Parse(palette.Accent);
        Assert.True(accent.Red > 0xD0 && accent.Green < 0x70, $"accent {palette.Accent}");
        AssertReadable(palette);
    }

    [Fact]
    public void A_nearly_transparent_frame_falls_back_to_the_theme()
    {
        var path = Images.WriteFramePng(Path.Combine(_temp.Root, "cadre.png"), 600, 400, SKColors.Red, SKRect.Create(0, 0, 600, 395));
        var design = ThemeCatalog.CreateDefaultDesign();

        var palette = KioskPalette.Create(design, path, "le cadre");

        Assert.Equal(design.BackgroundColor.ToUpperInvariant(), palette.Background);
    }

    private static void AssertReadable(KioskPalette palette)
    {
        Assert.True(KioskPalette.Contrast(palette.Foreground, palette.Background) >= KioskPalette.TextContrast, $"texte {palette.Foreground} sur {palette.Background}");
        Assert.True(KioskPalette.Contrast(palette.Muted, palette.Background) >= KioskPalette.MutedContrast, $"texte secondaire {palette.Muted} sur {palette.Background}");
        Assert.True(KioskPalette.Contrast(palette.AccentText, palette.Background) >= KioskPalette.TextContrast, $"accent {palette.AccentText} sur {palette.Background}");
        Assert.True(KioskPalette.Contrast(palette.AccentForeground, palette.Accent) >= 3.0, $"texte {palette.AccentForeground} sur l'accent {palette.Accent}");
        Assert.True(KioskPalette.Contrast(palette.Accent, palette.Background) >= KioskPalette.FillContrast
                    || palette.Accent == palette.Foreground, $"bouton {palette.Accent} sur {palette.Background}");
    }
}

/// <summary>Image de l'écran d'accueil, propre à chaque événement.</summary>
public sealed class AttractBackgroundTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Attract_image_is_fitted_to_the_screen_and_replaced_cleanly()
    {
        var eventFolder = _temp.Sub("evenement");
        var customization = CustomizationStore.Load(eventFolder);
        var photo = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "photo-4-3.jpg"), 1600, 1200, SKColors.Teal);

        var first = CustomizationStore.ImportAttractBackground(eventFolder, customization, photo);

        Assert.True(first.Cropped);
        var path = CustomizationStore.AttractBackgroundPath(eventFolder, customization);
        Assert.NotNull(path);
        using (var codec = SKCodec.Create(path!))
        {
            Assert.Equal(CustomizationStore.ScreenWidth, codec.Info.Width);
            Assert.Equal(CustomizationStore.ScreenHeight, codec.Info.Height);
        }
        Assert.Equal(customization.AttractBackground, CustomizationStore.Load(eventFolder).AttractBackground);

        // Reprise par un autre événement, puis retrait : aucun fichier ne traîne.
        var other = _temp.Sub("autre-evenement");
        var copy = CustomizationStore.CopyFrom(eventFolder, other);
        Assert.NotNull(CustomizationStore.AttractBackgroundPath(other, copy));

        CustomizationStore.RemoveAttractBackground(eventFolder, customization);
        Assert.Null(CustomizationStore.AttractBackgroundPath(eventFolder, customization));
        Assert.Empty(Directory.GetFiles(CustomizationStore.FolderFor(eventFolder), "accueil-*"));
        Assert.Null(CustomizationStore.Load(eventFolder).AttractBackground);
    }

    [Fact]
    public void Screen_sized_image_is_not_reported_as_cropped()
    {
        var eventFolder = _temp.Sub("evenement");
        var customization = CustomizationStore.Load(eventFolder);
        var photo = Images.WriteSolidJpeg(Path.Combine(_temp.Root, "photo-16-9.jpg"), 3840, 2160, SKColors.Orange);

        var result = CustomizationStore.ImportAttractBackground(eventFolder, customization, photo);

        Assert.False(result.Cropped);
        Assert.Equal(3840, result.SourceWidth);
    }
}
