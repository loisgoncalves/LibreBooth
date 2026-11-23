using LibreBooth.Core.Config;
using LibreBooth.Core.Events;
using LibreBooth.Core.Util;

namespace LibreBooth.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Settings_round_trip_through_config_json()
    {
        var store = new SettingsStore(Path.Combine(_temp.Root, "config.json"));
        var settings = new AppSettings();
        settings.Camera.Rotation = 90;
        settings.Print.PrinterName = "DNP DS-RX1";
        settings.Print.DriverSettings["10x15"] = "AAEC";
        settings.AdminPin = "2468";

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(90, loaded.Camera.Rotation);
        Assert.Equal("DNP DS-RX1", loaded.Print.PrinterName);
        Assert.Equal("AAEC", loaded.Print.DriverSettings["10x15"]);
        Assert.Equal("2468", loaded.AdminPin);
    }

    [Fact]
    public void Missing_file_gives_default_settings()
    {
        var loaded = new SettingsStore(Path.Combine(_temp.Root, "absent.json")).Load();

        Assert.Equal("1234", loaded.AdminPin);
        Assert.Equal(CameraKind.Canon, loaded.Camera.Kind);
    }

    [Fact]
    public void Corrupt_file_is_kept_aside_and_defaults_are_used()
    {
        var path = Path.Combine(_temp.Root, "config.json");
        File.WriteAllText(path, "{ ceci n'est pas du JSON");

        var loaded = new SettingsStore(path).Load();

        Assert.Equal(3, loaded.Screens.CountdownSeconds);
        Assert.Single(Directory.GetFiles(_temp.Root, "config.json.illisible-*"));
    }

    [Fact]
    public void Out_of_range_values_are_corrected()
    {
        var settings = new AppSettings { AdminPin = "ab" };
        settings.Screens.CountdownSeconds = 0;
        settings.Print.AutoPrintCopies = 20;
        settings.Camera.Rotation = 45;

        settings.Normalize();

        Assert.Equal("1234", settings.AdminPin);
        Assert.Equal(1, settings.Screens.CountdownSeconds);
        Assert.Equal(10, settings.Print.AutoPrintCopies);
        Assert.Equal(0, settings.Camera.Rotation);
    }
}

public sealed class EventStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Creating_an_event_prepares_its_folders()
    {
        var store = new EventStore(Path.Combine(_temp.Root, "Evenements"));

        var ev = store.Create("Mariage d'Élodie & Hugo", new DateTime(2026, 6, 20));

        Assert.Equal("2026-06-20 Mariage d'Élodie & Hugo", Path.GetFileName(ev.Folder));
        Assert.True(Directory.Exists(ev.OriginalsFolder));
        Assert.True(Directory.Exists(ev.CompositesFolder));
        Assert.True(File.Exists(Path.Combine(ev.Folder, ".librebooth", "event.json")));
    }

    [Fact]
    public void Same_name_twice_gets_a_new_folder()
    {
        var store = new EventStore(Path.Combine(_temp.Root, "Evenements"));

        var first = store.Create("Anniversaire", new DateTime(2026, 6, 20));
        var second = store.Create("Anniversaire", new DateTime(2026, 6, 20));

        Assert.NotEqual(first.Folder, second.Folder);
        Assert.EndsWith(" (2)", second.Folder);
    }

    [Fact]
    public void Statistics_are_saved_on_disk()
    {
        var folder = Path.Combine(_temp.Root, "Evenements");
        var ev = new EventStore(folder).Create("Soirée", new DateTime(2026, 6, 20));
        new EventStore(folder).UpdateStats(ev.Folder, s =>
        {
            s.Sessions += 2;
            s.Prints += 3;
        });

        var reopened = new EventStore(folder).Open(ev.Folder)!;

        Assert.Equal(2, reopened.Info.Stats.Sessions);
        Assert.Equal(3, reopened.Info.Stats.Prints);
        Assert.Equal("Soirée", reopened.Name);
    }

    [Fact]
    public void Sessions_are_listed_newest_first()
    {
        var store = new EventStore(Path.Combine(_temp.Root, "Evenements"));
        var ev = store.Create("Soirée", DateTime.Today);
        store.SaveSession(ev, new SessionRecord { Id = "20260620-200000-000", Composite = "a.jpg" });
        store.SaveSession(ev, new SessionRecord { Id = "20260620-210000-000", Composite = "b.jpg" });

        var sessions = store.ListSessions(ev);

        Assert.Equal(new[] { "b.jpg", "a.jpg" }, sessions.Select(s => s.Composite));
    }
}

public sealed class TextUtilTests
{
    [Theory]
    [InlineData("Mariage d'Élodie & Hugo", "mariage-d-elodie-hugo")]
    [InlineData("  Soirée   d'entreprise 2026 ", "soiree-d-entreprise-2026")]
    [InlineData("", "sans-nom")]
    [InlineData("!!!", "sans-nom")]
    public void Slug_makes_safe_folder_names(string input, string expected)
    {
        Assert.Equal(expected, TextUtil.Slug(input));
    }

    [Fact]
    public void Variables_are_replaced()
    {
        var text = TextUtil.ApplyVariables("Photo de {evenement} le {date}", "Marie & Paul", new DateTime(2026, 6, 20));

        Assert.Equal("Photo de Marie & Paul le 20.06.2026", text);
    }
}
