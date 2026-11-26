using LibreBooth.Core.Config;

namespace LibreBooth.Core.Tests;

/// <summary>
/// Version 3.2.1 : la capacité du rouleau par défaut passe de 700 à 400 tirages 10x15 (rouleau 15x20 de DS620), et
/// un config.json resté à l'ancienne valeur par défaut est corrigé une fois.
/// </summary>
public sealed class MediaCapacityTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private AppSettings Load(string json)
    {
        var file = Path.Combine(_temp.Root, "config.json");
        File.WriteAllText(file, json);
        return new SettingsStore(file).Load();
    }

    [Fact]
    public void New_settings_count_a_ds620_roll()
    {
        var settings = new AppSettings().Normalize();
        Assert.Equal(400, settings.Print.MediaCapacity);
        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
    }

    [Fact]
    public void Old_default_is_corrected_once()
    {
        var settings = Load("""{ "Version": 1, "Print": { "MediaCapacity": 700, "PrintedSinceMediaChange": 120 } }""");
        Assert.Equal(400, settings.Print.MediaCapacity);
        Assert.Equal(120, settings.Print.PrintedSinceMediaChange);
        Assert.Equal(AppSettings.CurrentVersion, settings.Version);

        // Enregistré puis relu : plus de correction, même si l'utilisateur remet 700 lui-même.
        settings.Print.MediaCapacity = 700;
        var store = new SettingsStore(Path.Combine(_temp.Root, "config.json"));
        store.Save(settings);
        Assert.Equal(700, store.Load().Print.MediaCapacity);
    }

    [Theory]
    [InlineData(1, 350)]
    [InlineData(1, 800)]
    [InlineData(2, 700)]
    public void Chosen_capacity_is_kept(int version, int capacity)
    {
        var settings = Load($$"""{ "Version": {{version}}, "Print": { "MediaCapacity": {{capacity}} } }""");
        Assert.Equal(capacity, settings.Print.MediaCapacity);
    }

    [Fact]
    public void Missing_capacity_uses_the_new_default()
    {
        Assert.Equal(400, Load("""{ "Version": 1, "Print": { "AutoPrint": true } }""").Print.MediaCapacity);
    }
}
