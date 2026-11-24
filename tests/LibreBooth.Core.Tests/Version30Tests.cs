using System.Globalization;
using System.Text.RegularExpressions;
using LibreBooth.Core.Admin;
using LibreBooth.Core.Config;
using LibreBooth.Core.Design;
using LibreBooth.Core.Hardware;
using SkiaSharp;

namespace LibreBooth.Core.Tests;

/// <summary>Version 3.0.0 : couleurs de l'admin (clair et sombre), réglage d'apparence et icônes lisibles sous Windows 10.</summary>
public sealed class AdminAppearanceTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> Palettes => new() { "clair", "sombre" };

    private static AdminPalette Palette(string name) => name == "sombre" ? AdminPalette.Dark : AdminPalette.Light;

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Texts_are_readable_on_every_background(string name)
    {
        var p = Palette(name);
        var pairs = new (string What, string Text, string Background)[]
        {
            ("texte / fond", p.Text, p.Window),
            ("texte / menu", p.Text, p.Sidebar),
            ("texte / menu survolé", p.Text, p.SidebarHover),
            ("texte / page ouverte", p.Text, p.SidebarSelected),
            ("texte / carte", p.Text, p.Card),
            ("texte / champ", p.Text, p.Control),
            ("texte / champ survolé", p.Text, p.ControlHover),
            ("texte / champ appuyé", p.Text, p.ControlPressed),
            ("texte / choix sélectionné", p.Text, p.AccentSoft),
            ("texte / bandeau succès", p.Text, p.SuccessBackground),
            ("texte / bandeau attention", p.Text, p.WarningBackground),
            ("texte / bandeau erreur", p.Text, p.ErrorBackground),
            ("texte / bandeau info", p.Text, p.InfoBackground),
            ("discret / fond", p.TextMuted, p.Window),
            ("discret / menu", p.TextMuted, p.Sidebar),
            ("discret / menu survolé", p.TextMuted, p.SidebarHover),
            ("discret / page ouverte", p.TextMuted, p.SidebarSelected),
            ("discret / carte", p.TextMuted, p.Card),
            ("discret / champ", p.TextMuted, p.Control),
            ("discret / champ survolé", p.TextMuted, p.ControlHover),
            ("discret / choix sélectionné", p.TextMuted, p.AccentSoft),
            ("accent / fond", p.AccentText, p.Window),
            ("accent / menu", p.AccentText, p.Sidebar),
            ("accent / carte", p.AccentText, p.Card),
            ("accent / champ", p.AccentText, p.Control),
            ("accent / choix sélectionné", p.AccentText, p.AccentSoft),
            ("bouton principal", p.OnAccent, p.Accent),
            ("bouton principal survolé", p.OnAccent, p.AccentHover),
            ("bouton principal appuyé", p.OnAccent, p.AccentPressed),
            ("bouton Quitter", p.OnDanger, p.Danger),
            ("bouton Quitter survolé", p.OnDanger, p.DangerHover),
            ("bouton Quitter appuyé", p.OnDanger, p.DangerPressed),
            ("succès / bandeau", p.Success, p.SuccessBackground),
            ("succès / carte", p.Success, p.Card),
            ("succès / fond", p.Success, p.Window),
            ("attention / bandeau", p.Warning, p.WarningBackground),
            ("attention / carte", p.Warning, p.Card),
            ("attention / fond", p.Warning, p.Window),
            ("erreur / bandeau", p.Error, p.ErrorBackground),
            ("erreur / carte", p.Error, p.Card),
            ("erreur / fond", p.Error, p.Window),
            ("info / bandeau", p.Info, p.InfoBackground),
            ("info / carte", p.Info, p.Card),
            ("info / fond", p.Info, p.Window),
        };

        var unreadable = pairs
            .Select(pair => (pair.What, Contrast: KioskPalette.Contrast(pair.Text, pair.Background)))
            .Where(pair => pair.Contrast < KioskPalette.TextContrast)
            .Select(pair => $"{pair.What} : {pair.Contrast:0.00}")
            .ToList();
        Assert.True(unreadable.Count == 0, $"Contraste insuffisant ({name}) : " + string.Join(", ", unreadable));
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Field_borders_and_status_icons_stand_out(string name)
    {
        var p = Palette(name);
        Assert.True(KioskPalette.Contrast(p.ControlBorder, p.Card) >= 3, "bordure des champs sur une carte");
        Assert.True(KioskPalette.Contrast(p.ControlBorder, p.Window) >= 3, "bordure des champs sur le fond");
        foreach (var fill in new[] { p.SuccessFill, p.WarningFill, p.ErrorFill, p.InfoFill })
        {
            // Icône blanche sur une pastille pleine : contraste des éléments graphiques (3:1).
            Assert.True(KioskPalette.Contrast("#FFFFFF", fill) >= 3, $"icône blanche sur {fill}");
        }
    }

    [Fact]
    public void Light_and_dark_palettes_define_the_same_colors()
    {
        var light = AdminPalette.Light.Colors();
        var dark = AdminPalette.Dark.Colors();

        Assert.Equal(light.Select(c => c.Key), dark.Select(c => c.Key));
        Assert.Contains(light, c => c.Key == "Window");
        Assert.All(light.Concat(dark), c => Assert.Matches("^#[0-9A-F]{6}$", c.Value));
        Assert.False(AdminPalette.Light.IsDark);
        Assert.True(AdminPalette.Dark.IsDark);
        Assert.Same(AdminPalette.Dark, AdminPalette.For(AdminAppearance.Dark));
        Assert.Same(AdminPalette.Light, AdminPalette.For(AdminAppearance.Light));
    }

    [Fact]
    public void Appearance_is_light_by_default_and_kept_in_config()
    {
        Assert.Equal(AdminAppearance.Light, new AppSettings().AdminAppearance);
        Assert.Equal(AdminAppearance.Light, new AppSettings { AdminAppearance = (AdminAppearance)7 }.Normalize().AdminAppearance);

        var path = Path.Combine(_temp.Root, "config.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { AdminAppearance = AdminAppearance.Dark });

        Assert.Contains("\"AdminAppearance\": \"Dark\"", File.ReadAllText(path));
        Assert.Equal(AdminAppearance.Dark, store.Load().AdminAppearance);
    }

    [Fact]
    public void Config_of_version_2_opens_in_light()
    {
        var path = Path.Combine(_temp.Root, "config.json");
        File.WriteAllText(path, """{ "Version": 1, "AdminPin": "4321", "CheckForUpdates": true }""");

        var settings = new SettingsStore(path).Load();

        Assert.Equal("4321", settings.AdminPin);
        Assert.Equal(AdminAppearance.Light, settings.AdminAppearance);
    }

    /// <summary>
    /// Windows 10 n'a pas la police « Segoe Fluent Icons » : LibreBooth retombe alors sur « Segoe MDL2 Assets ».
    /// Chaque icône de l'application doit donc exister dans cette police.
    /// </summary>
    [Fact]
    public void Every_icon_exists_in_the_Windows_10_icon_font()
    {
        using var typeface = SKTypeface.FromFamilyName("Segoe MDL2 Assets");
        Assert.True(typeface?.FamilyName == "Segoe MDL2 Assets", "Police Segoe MDL2 Assets introuvable sur ce PC");

        var app = Path.Combine(RepositoryRoot(), "src", "LibreBooth.App");
        var glyphs = new SortedDictionary<int, string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            var pattern = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".xaml" => @"&#x([EFef][0-9A-Fa-f]{3});",
                ".cs" => @"\\u([EFef][0-9A-Fa-f]{3})",
                _ => null,
            };
            if (pattern == null) continue;
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, pattern))
            {
                glyphs.TryAdd(int.Parse(match.Groups[1].Value, NumberStyles.HexNumber), Path.GetFileName(file));
            }
            // Icônes écrites telles quelles (caractères de la zone privée d'Unicode), au lieu de leur code.
            foreach (var c in text.Where(c => c is >= '' and <= ''))
            {
                glyphs.TryAdd(c, Path.GetFileName(file));
            }
        }

        Assert.NotEmpty(glyphs);
        var missing = glyphs.Where(g => typeface!.GetGlyph(g.Key) == 0).Select(g => $"U+{g.Key:X4} ({g.Value})").ToList();
        Assert.True(missing.Count == 0, "Icônes absentes de Segoe MDL2 Assets : " + string.Join(", ", missing));
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "LibreBooth.sln"))) return folder.FullName;
        }
        throw new DirectoryNotFoundException("Dossier du projet (LibreBooth.sln) introuvable");
    }
}

/// <summary>Version 3.0.0 : liste « Prêt pour l'événement ? » de l'accueil de l'admin.</summary>
public sealed class ReadinessTests
{
    /// <summary>Un booth prêt : Canon branché, DNP prête avec du papier, deux formats, 120 Go libres.</summary>
    private static readonly ReadinessInput Ready = new()
    {
        CameraKind = CameraKind.Canon,
        CameraState = CameraState.Ready,
        CameraName = "Canon EOS 1300D",
        CameraMessage = "Prêt",
        BatteryLevel = 80,
        PrintEnabled = true,
        PrinterName = "DP-DS620",
        PrinterChecked = true,
        PrinterCanPrint = true,
        PrinterCondition = PrinterCondition.Ready,
        PrinterMessage = "Prête",
        RemainingPrints = 350,
        Formats = new[] { "10x15 paysage", "Marque-page" },
        FreeDiskBytes = 120L * 1024 * 1024 * 1024,
        DiskName = "C:",
    };

    private static ReadinessItem Item(ReadinessInput input, ReadinessArea area) =>
        Readiness.Evaluate(input).Single(i => i.Area == area);

    [Fact]
    public void Ready_booth_is_all_green()
    {
        var items = Readiness.Evaluate(Ready);

        Assert.Equal(new[] { ReadinessArea.Camera, ReadinessArea.Printer, ReadinessArea.Paper, ReadinessArea.Formats, ReadinessArea.Disk },
            items.Select(i => i.Area));
        Assert.All(items, i => Assert.Equal(ReadinessLevel.Ok, i.Level));
        Assert.Equal("Tout est prêt", Readiness.Summary(items));
        Assert.Equal(ReadinessLevel.Ok, Readiness.Overall(items));
        Assert.Equal("Canon EOS 1300D prêt, batterie à 80 %.", items[0].Detail);
        Assert.Equal("350 tirages restants.", items[2].Detail);
        Assert.Equal("10x15 paysage, Marque-page.", items[3].Detail);
        Assert.Equal("120 Go libres (disque C:).", items[4].Detail.Replace(' ', ' '));
    }

    [Theory]
    [InlineData(CameraState.Disconnected, ReadinessLevel.Error)]
    [InlineData(CameraState.Error, ReadinessLevel.Error)]
    [InlineData(CameraState.Connecting, ReadinessLevel.Info)]
    public void Camera_not_ready_is_reported(CameraState state, ReadinessLevel level) =>
        Assert.Equal(level, Item(Ready with { CameraState = state, CameraMessage = "Appareil occupé" }, ReadinessArea.Camera).Level);

    [Fact]
    public void Camera_error_shows_its_message() =>
        Assert.Equal("Appareil occupé", Item(Ready with { CameraState = CameraState.Error, CameraMessage = "Appareil occupé" }, ReadinessArea.Camera).Detail);

    [Theory]
    [InlineData(15, ReadinessLevel.Warning)]
    [InlineData(20, ReadinessLevel.Warning)]
    [InlineData(21, ReadinessLevel.Ok)]
    [InlineData(-1, ReadinessLevel.Ok)]
    public void Low_battery_is_a_warning(int battery, ReadinessLevel level) =>
        Assert.Equal(level, Item(Ready with { BatteryLevel = battery }, ReadinessArea.Camera).Level);

    [Fact]
    public void Mains_power_is_mentioned() =>
        Assert.Equal("Canon EOS 1300D prêt, sur secteur.", Item(Ready with { BatteryLevel = -1 }, ReadinessArea.Camera).Detail);

    [Fact]
    public void Demo_camera_and_missing_Canon_SDK_are_reported()
    {
        Assert.Equal(ReadinessLevel.Warning, Item(Ready with { DemoMode = true }, ReadinessArea.Camera).Level);
        Assert.Equal(ReadinessLevel.Warning, Item(Ready with { CameraKind = CameraKind.Demo }, ReadinessArea.Camera).Level);
        // Depuis la 3.1.0, le SDK Canon ne manque que s'il est le pilote choisi.
        Assert.Equal(ReadinessLevel.Error, Item(Ready with { CameraDriver = CameraDriver.CanonSdk, CanonSdkMissing = true, CameraState = CameraState.Error },
            ReadinessArea.Camera).Level);
        // En démonstration, le SDK Canon ne sert pas.
        Assert.Equal(ReadinessLevel.Warning, Item(Ready with { CameraKind = CameraKind.Demo, CanonSdkMissing = true }, ReadinessArea.Camera).Level);
    }

    [Fact]
    public void Disabled_printing_is_only_an_information()
    {
        var items = Readiness.Evaluate(Ready with { PrintEnabled = false, EventPrintLimit = 10, EventPrints = 10 });

        Assert.Equal(ReadinessLevel.Info, items.Single(i => i.Area == ReadinessArea.Printer).Level);
        Assert.DoesNotContain(items, i => i.Area is ReadinessArea.Paper or ReadinessArea.PrintLimit);
        Assert.Equal("Tout est prêt", Readiness.Summary(items));
    }

    [Fact]
    public void Printer_problems_are_reported()
    {
        Assert.Equal(ReadinessLevel.Error, Item(Ready with { PrinterName = null }, ReadinessArea.Printer).Level);
        Assert.Equal(ReadinessLevel.Info, Item(Ready with { PrinterChecked = false }, ReadinessArea.Printer).Level);
        Assert.Equal(ReadinessLevel.Info, Item(Ready with { PrintToPdf = true }, ReadinessArea.Printer).Level);

        var stopped = Item(Ready with { PrinterCanPrint = false, PrinterCondition = PrinterCondition.Error, PrinterMessage = "Capot ouvert" }, ReadinessArea.Printer);
        Assert.Equal(ReadinessLevel.Error, stopped.Level);
        Assert.Equal("Capot ouvert", stopped.Detail);
        Assert.Equal(ReadinessLevel.Warning, Item(Ready with { PrinterCondition = PrinterCondition.Warning, PrinterMessage = "Ruban bientôt fini" }, ReadinessArea.Printer).Level);
        // Sans imprimante interrogée, le papier n'est pas encore connu.
        Assert.DoesNotContain(Readiness.Evaluate(Ready with { PrinterChecked = false }), i => i.Area == ReadinessArea.Paper);
    }

    [Fact]
    public void Paper_counter_is_reported()
    {
        Assert.Equal(ReadinessLevel.Error, Item(Ready with { RemainingPrints = 0, LowMedia = true }, ReadinessArea.Paper).Level);
        var low = Item(Ready with { RemainingPrints = 1, LowMedia = true }, ReadinessArea.Paper);
        Assert.Equal(ReadinessLevel.Warning, low.Level);
        Assert.StartsWith("1 tirage restant", low.Detail);
        Assert.Equal(ReadinessLevel.Info, Item(Ready with { RemainingPrints = null }, ReadinessArea.Paper).Level);
    }

    [Fact]
    public void Event_without_format_is_an_error() =>
        Assert.Equal(ReadinessLevel.Error, Item(Ready with { Formats = Array.Empty<string>() }, ReadinessArea.Formats).Level);

    [Theory]
    [InlineData(0, 100, ReadinessLevel.Ok)]
    [InlineData(89, 100, ReadinessLevel.Ok)]
    [InlineData(90, 100, ReadinessLevel.Warning)]
    [InlineData(100, 100, ReadinessLevel.Warning)]
    public void Print_limit_is_reported_when_set(int prints, int limit, ReadinessLevel level) =>
        Assert.Equal(level, Item(Ready with { EventPrints = prints, EventPrintLimit = limit }, ReadinessArea.PrintLimit).Level);

    [Theory]
    [InlineData(250L * 1024 * 1024, ReadinessLevel.Error, "Plus que 250 Mo libres (disque C:) : le photobooth ne lance plus de session")]
    [InlineData(300L * 1024 * 1024, ReadinessLevel.Warning, "300 Mo libres (disque C:), soit 1 session")]
    [InlineData(1536L * 1024 * 1024, ReadinessLevel.Warning, "1,5 Go libres (disque C:), soit environ 120 sessions")]
    [InlineData(5L * 1024 * 1024 * 1024, ReadinessLevel.Ok, "5 Go libres (disque C:)")]
    public void Free_disk_space_is_reported(long free, ReadinessLevel level, string start)
    {
        var item = Item(Ready with { FreeDiskBytes = free }, ReadinessArea.Disk);
        Assert.Equal(level, item.Level);
        Assert.StartsWith(start, item.Detail.Replace(' ', ' '));
        Assert.DoesNotContain(" Mo", item.Detail);
        Assert.DoesNotContain(" Go", item.Detail);
    }

    [Fact]
    public void Update_is_an_information_and_does_not_count()
    {
        var items = Readiness.Evaluate(Ready with { AvailableUpdate = "3.1.0" });

        Assert.Equal(ReadinessLevel.Info, items.Single(i => i.Area == ReadinessArea.Update).Level);
        Assert.Equal("Tout est prêt", Readiness.Summary(items));
    }

    [Fact]
    public void Summary_counts_the_points_to_check()
    {
        var one = Readiness.Evaluate(Ready with { BatteryLevel = 10 });
        Assert.Equal("1 point à vérifier", Readiness.Summary(one));
        Assert.Equal(ReadinessLevel.Warning, Readiness.Overall(one));

        var two = Readiness.Evaluate(Ready with { BatteryLevel = 10, Formats = Array.Empty<string>() });
        Assert.Equal("2 points à vérifier", Readiness.Summary(two));
        Assert.Equal(ReadinessLevel.Error, Readiness.Overall(two));
    }
}

/// <summary>Version 3.0.0 : place libre sur le disque des photos, avant de démarrer le photobooth et avant chaque session.</summary>
public sealed class DiskSpaceTests
{
    private const long Mo = 1024L * 1024;

    [Theory]
    [InlineData(0, false)]
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(2048, true)]
    public void A_session_needs_300_MB(long freeMegabytes, bool allowed) =>
        Assert.Equal(allowed, DiskSpace.AllowsSession(freeMegabytes * Mo));

    [Fact]
    public void Starting_is_advised_with_2_GB() =>
        Assert.Equal(2048 * Mo, DiskSpace.StartMinimumBytes);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(299, 0)]
    [InlineData(300, 1)]
    [InlineData(309, 1)]
    [InlineData(310, 2)]
    [InlineData(2048, 175)]
    public void Sessions_left_before_the_pause_are_estimated(long freeMegabytes, long sessions) =>
        Assert.Equal(sessions, DiskSpace.EstimatedSessions(freeMegabytes * Mo));

    [Theory]
    [InlineData(250, "aucune session")]
    [InlineData(300, "1 session")]
    [InlineData(390, "10 sessions")]
    [InlineData(2048, "environ 170 sessions")]
    [InlineData(5120, "environ 480 sessions")]
    public void Sessions_text_is_rounded(long freeMegabytes, string text) =>
        Assert.Equal(text, DiskSpace.SessionsText(freeMegabytes * Mo));

    [Fact]
    public void Network_folder_is_not_checked() =>
        Assert.Null(DiskSpace.Probe(@"\\serveur\partage\Photos"));

    [Fact]
    public void Local_folder_gives_free_space_and_drive()
    {
        var probe = DiskSpace.Probe(Path.GetTempPath());

        Assert.True(probe.HasValue);
        Assert.True(probe!.Value.FreeBytes > 0);
        if (OperatingSystem.IsWindows()) Assert.Matches("^[A-Za-z]:$", probe.Value.DriveName);
    }
}
