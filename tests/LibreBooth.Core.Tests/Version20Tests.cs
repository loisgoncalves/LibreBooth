using LibreBooth.Core.Config;
using LibreBooth.Core.Design;
using LibreBooth.Core.Events;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Util;
using SkiaSharp;

namespace LibreBooth.Core.Tests;

/// <summary>Version 2.0.0 : dossiers d'événements simplifiés (Photos, Montages, Photos refusées, dossier caché).</summary>
public sealed class EventFoldersTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string EventsRoot => Path.Combine(_temp.Root, "Evenements");

    private static IEnumerable<string> Names(IEnumerable<string> paths) => paths.Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal);

    [Fact]
    public void New_event_folder_is_named_after_its_date_and_name()
    {
        var store = new EventStore(EventsRoot);

        var ev = store.Create("Mariage Julie et Tom", new DateTime(2026, 11, 23));

        Assert.Equal("2026-11-23 Mariage Julie et Tom", Path.GetFileName(ev.Folder));
        Assert.Equal(new[] { ".librebooth", "Montages", "Photos" }, Names(Directory.GetDirectories(ev.Folder)));
        Assert.Empty(Directory.GetFiles(ev.Folder));
        Assert.True(new DirectoryInfo(ev.DataFolder).Attributes.HasFlag(FileAttributes.Hidden));
        Assert.True(File.Exists(Path.Combine(ev.DataFolder, "event.json")));
    }

    [Theory]
    [InlineData("Soirée 20:00 / Tom?", "2026-11-23 Soirée 20-00 - Tom")]
    [InlineData("  Anniversaire   de Léa.  ", "2026-11-23 Anniversaire de Léa")]
    [InlineData("<*>", "2026-11-23 Mon événement")]
    public void Folder_name_keeps_the_name_without_forbidden_characters(string name, string expected) =>
        Assert.Equal(expected, EventStore.FolderNameFor(name, new DateTime(2026, 11, 23)));

    [Fact]
    public void The_name_printed_on_photos_stays_exactly_as_typed()
    {
        var ev = new EventStore(EventsRoot).Create("Soirée 20:00 / Tom?", new DateTime(2026, 11, 23));

        Assert.Equal("Soirée 20:00 / Tom?", ev.Name);
        Assert.Equal("2026-11-23 Soirée 20-00 - Tom", Path.GetFileName(ev.Folder));
    }

    [Fact]
    public void Same_name_and_date_get_a_numbered_folder_and_are_found_again()
    {
        var store = new EventStore(EventsRoot);
        store.Create("Anniversaire", new DateTime(2026, 6, 20));

        var second = store.Create("Anniversaire", new DateTime(2026, 6, 20));

        Assert.Equal("2026-06-20 Anniversaire (2)", Path.GetFileName(second.Folder));
        Assert.NotNull(store.FindSame("anniversaire ", new DateTime(2026, 6, 20)));
        Assert.Null(store.FindSame("Anniversaire", new DateTime(2026, 6, 21)));
    }

    [Fact]
    public void Session_records_live_in_the_hidden_folder_so_montages_only_hold_images()
    {
        var store = new EventStore(EventsRoot);
        var ev = store.Create("Soirée", new DateTime(2026, 6, 20));
        const string id = "20260620-210000-000";
        File.WriteAllText(Path.Combine(ev.CompositesFolder, id + ".jpg"), "montage");

        store.SaveSession(ev, new SessionRecord { Id = id, Composite = id + ".jpg" });
        store.UpdateSession(ev, id, r => r.Prints += 2);

        Assert.Equal(new[] { id + ".jpg" }, Names(Directory.GetFiles(ev.CompositesFolder)));
        Assert.True(File.Exists(Path.Combine(ev.DataFolder, "sessions", id + ".json")));
        Assert.Equal(2, Assert.Single(store.ListSessions(ev)).Prints);
    }

    [Fact]
    public void Renaming_an_event_renames_its_folder_and_keeps_everything()
    {
        var store = new EventStore(EventsRoot);
        var ev = store.Create("Mariage", new DateTime(2026, 6, 20));
        File.WriteAllText(Path.Combine(ev.OriginalsFolder, "1.jpg"), "photo");
        var customization = CustomizationStore.Load(ev.Folder);
        customization.Design.Phrase = "Merci !";
        CustomizationStore.Save(ev.Folder, customization);
        store.UpdateStats(ev, s => s.Sessions = 4);

        Assert.True(store.Rename(ev, "Mariage Julie et Tom", new DateTime(2026, 6, 21)));

        Assert.Equal("2026-06-21 Mariage Julie et Tom", Path.GetFileName(ev.Folder));
        Assert.Equal(new[] { "2026-06-21 Mariage Julie et Tom" }, Names(Directory.GetDirectories(EventsRoot)));
        Assert.True(File.Exists(Path.Combine(ev.OriginalsFolder, "1.jpg")));
        Assert.Equal("Merci !", CustomizationStore.Load(ev.Folder).Design.Phrase);
        var reopened = new EventStore(EventsRoot).Open(ev.Folder)!;
        Assert.Equal("Mariage Julie et Tom", reopened.Name);
        Assert.Equal(new DateTime(2026, 6, 21), reopened.Date);
        Assert.Equal(4, reopened.Info.Stats.Sessions);
    }

    [Fact]
    public void Changing_only_capital_letters_renames_the_folder_too()
    {
        var store = new EventStore(EventsRoot);
        var ev = store.Create("mariage", new DateTime(2026, 6, 20));

        Assert.True(store.Rename(ev, "Mariage", ev.Date));

        Assert.Equal(new[] { "2026-06-20 Mariage" }, Names(Directory.GetDirectories(EventsRoot)));
        Assert.Equal("2026-06-20 Mariage", Path.GetFileName(ev.Folder));
    }

    [Fact]
    public void Folder_keeps_its_name_while_a_file_inside_is_open_elsewhere()
    {
        var store = new EventStore(EventsRoot);
        var ev = store.Create("Mariage", new DateTime(2026, 6, 20));
        var before = ev.Folder;

        using (File.Open(Path.Combine(ev.OriginalsFolder, "ouverte.jpg"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(store.Rename(ev, "Mariage Julie et Tom", ev.Date));
        }

        Assert.Equal(before, ev.Folder);
        Assert.Equal("Mariage Julie et Tom", ev.Name);
        Assert.Equal("Mariage Julie et Tom", new EventStore(EventsRoot).Open(before)!.Name);
    }

    [Fact]
    public void Folders_that_are_not_events_are_not_listed_nor_touched()
    {
        var store = new EventStore(EventsRoot);
        store.Create("Mariage", new DateTime(2026, 6, 20));
        Directory.CreateDirectory(Path.Combine(EventsRoot, "Nouveau dossier"));
        Directory.CreateDirectory(Path.Combine(EventsRoot, "2026-06-19 Ancien nom", ".librebooth", "personnalisation", "apercus"));

        Assert.Equal("Mariage", Assert.Single(store.ListAll()).Name);
        Assert.Empty(Directory.GetDirectories(Path.Combine(EventsRoot, "Nouveau dossier")));
    }

    [Fact]
    public void Events_of_version_1_are_converted_without_losing_anything()
    {
        const string id = "20261122-200000-000";
        var old = Path.Combine(EventsRoot, "2026-11-22_mariage-julie-tom");
        Directory.CreateDirectory(old);
        JsonDefaults.Write(Path.Combine(old, "event.json"), new EventInfo
        {
            Name = "Mariage Julie & Tom",
            Date = new DateTime(2026, 11, 22),
            Stats = new EventStats { Sessions = 1, Photos = 3, Prints = 2 },
        });
        var originals = Directory.CreateDirectory(Path.Combine(old, "originaux")).FullName;
        foreach (var i in new[] { 1, 2, 3 }) File.WriteAllText(Path.Combine(originals, $"{id}_{i}.jpg"), "photo");
        var composites = Directory.CreateDirectory(Path.Combine(old, "montages")).FullName;
        File.WriteAllText(Path.Combine(composites, id + ".jpg"), "montage");
        JsonDefaults.Write(Path.Combine(composites, id + ".json"), new SessionRecord { Id = id, Composite = id + ".jpg", Prints = 2 });
        var refused = Directory.CreateDirectory(Path.Combine(old, "refusees")).FullName;
        File.WriteAllText(Path.Combine(refused, "20261122-195500-000_1.jpg"), "refusée");
        var customization = Directory.CreateDirectory(Path.Combine(old, "personnalisation")).FullName;
        File.WriteAllText(Path.Combine(customization, CustomizationStore.LogoFileName), "logo");
        JsonDefaults.Write(Path.Combine(customization, CustomizationStore.FileName), new EventCustomization { MaxCopies = 3 });
        Directory.CreateDirectory(Path.Combine(old, "outbox"));
        var sent = Directory.CreateDirectory(Path.Combine(old, "envoyes")).FullName;
        File.WriteAllText(Path.Combine(sent, "ancien.json"), "{}");
        var store = new EventStore(EventsRoot);

        var upgrade = Assert.Single(store.UpgradeOldEvents());

        var folder = Path.Combine(EventsRoot, "2026-11-22 Mariage Julie & Tom");
        Assert.Equal(new EventUpgrade(old, folder), upgrade);
        Assert.False(Directory.Exists(old));
        Assert.Equal(new[] { ".librebooth", "Montages", "Photos", "Photos refusées" }, Names(Directory.GetDirectories(folder)));
        Assert.Empty(Directory.GetFiles(folder));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(folder, "Photos")).Length);
        Assert.Equal(new[] { id + ".jpg" }, Names(Directory.GetFiles(Path.Combine(folder, "Montages"))));
        Assert.Single(Directory.GetFiles(Path.Combine(folder, "Photos refusées")));

        var ev = store.Open(folder)!;
        Assert.Equal("Mariage Julie & Tom", ev.Name);
        Assert.Equal(2, ev.Info.Stats.Prints);
        Assert.Equal(2, Assert.Single(store.ListSessions(ev)).Prints);
        Assert.Equal(3, CustomizationStore.Load(folder).MaxCopies);
        Assert.True(File.Exists(Path.Combine(CustomizationStore.FolderFor(folder), CustomizationStore.LogoFileName)));
        Assert.True(File.Exists(Path.Combine(ev.DataFolder, "ancien-envoi-email", "envoyes", "ancien.json")));
        Assert.Empty(new EventStore(EventsRoot).UpgradeOldEvents());
        Assert.Equal(folder, EventUpgrade.Follow(new[] { upgrade }, old.ToUpperInvariant()));
        Assert.Equal("ailleurs", EventUpgrade.Follow(new[] { upgrade }, "ailleurs"));
    }

    [Fact]
    public void An_old_event_without_its_information_keeps_its_folder_name()
    {
        var old = Path.Combine(EventsRoot, "2026-11-22_soiree");
        Directory.CreateDirectory(Path.Combine(old, "originaux"));
        File.WriteAllText(Path.Combine(old, "originaux", "1.jpg"), "photo");

        var upgrade = Assert.Single(new EventStore(EventsRoot).UpgradeOldEvents());

        Assert.Equal(old, upgrade.To);
        Assert.True(File.Exists(Path.Combine(old, "Photos", "1.jpg")));
        var ev = new EventStore(EventsRoot).Open(old)!;
        Assert.Equal("soiree", ev.Name);
        Assert.Equal(new DateTime(2026, 11, 22), ev.Date);
    }

    [Fact]
    public void Photos_in_folders_with_accents_go_into_the_montage()
    {
        var ev = new EventStore(EventsRoot).Create("Soirée d'Élodie", new DateTime(2026, 6, 20));
        Directory.CreateDirectory(ev.RefusedFolder);
        var photo = Images.WriteSolidJpeg(Path.Combine(ev.RefusedFolder, "photo été.jpg"), 640, 480, SKColors.Red);
        var templateFolder = _temp.Sub("modèle élégant");
        var layout = new LayoutDefinition { Width = 600, Height = 400, BackgroundColor = "#FFFFFF", Zones = { new PhotoZone(50, 50, 200, 150) } };
        JsonDefaults.Write(Path.Combine(templateFolder, TemplateLibrary.LayoutFileName), layout);

        using (var codec = SKCodec.Create(photo)) Assert.NotNull(codec);
        using var image = new LayoutCompositor().Compose(new Template(templateFolder, layout), new[] { photo },
            new CompositionContext(ev.Name, ev.Date));
        using var bitmap = SKBitmap.FromImage(image);

        Images.AssertColorNear(SKColors.Red, bitmap.GetPixel(150, 125));
    }
}

/// <summary>Version 2.0.0 : l'envoi par e-mail est retiré.</summary>
public sealed class EmailRemovalTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Old_settings_are_still_read_and_their_email_part_is_dropped()
    {
        var path = Path.Combine(_temp.Root, "config.json");
        File.WriteAllText(path, """
            {
              "AdminPin": "2468",
              "Email": { "Enabled": true, "SmtpHost": "smtp.exemple.fr", "Username": "moi", "ProtectedPassword": "AQAAAA" },
              "Screens": { "EmailTimeoutSeconds": 60, "CountdownSeconds": 5 }
            }
            """);
        var store = new SettingsStore(path);

        var settings = store.Load();
        store.Save(settings);

        Assert.Equal("2468", settings.AdminPin);
        Assert.Equal(5, settings.Screens.CountdownSeconds);
        var saved = File.ReadAllText(path);
        Assert.DoesNotContain("Email", saved);
        Assert.DoesNotContain("smtp", saved, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Version 2.0.0 : le pavé tactile fait défiler selon l'ampleur du geste.</summary>
public sealed class WheelStepsTests
{
    [Fact]
    public void A_mouse_notch_still_scrolls_three_lines()
    {
        var pending = 0.0;
        Assert.Equal(-3, WheelSteps.TakeLines(ref pending, -120, 3));
        Assert.Equal(3, WheelSteps.TakeLines(ref pending, 120, 3));
        Assert.Equal(0, pending);
    }

    [Fact]
    public void Small_touchpad_moves_add_up_instead_of_each_scrolling_three_lines()
    {
        var pending = 0.0;
        var lines = Enumerable.Range(0, 12).Sum(_ => WheelSteps.TakeLines(ref pending, -10, 3));

        // 12 petits mouvements de 10 = un cran : 3 lignes en tout (WPF en faisait 36).
        Assert.Equal(-3, lines);
        Assert.Equal(0, pending, 6);
    }
}
