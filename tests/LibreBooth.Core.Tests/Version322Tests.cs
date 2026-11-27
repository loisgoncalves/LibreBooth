using LibreBooth.Core.Admin;
using LibreBooth.Core.Config;

namespace LibreBooth.Core.Tests;

/// <summary>
/// Version 3.2.2 : « Merci ! » 3 s (il suit désormais l'appui sur « Imprimer »), et un config.json resté à l'ancienne
/// valeur par défaut (6 s) est corrigé une fois.
/// </summary>
public sealed class ThanksDurationTests : IDisposable
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
    public void New_settings_thank_for_three_seconds()
    {
        var settings = new AppSettings().Normalize();
        Assert.Equal(3, settings.Screens.ThanksSeconds);
        Assert.Equal(3, AppSettings.CurrentVersion);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Old_default_is_corrected_once(int version)
    {
        var settings = Load($$"""{ "Version": {{version}}, "Screens": { "ThanksSeconds": 6 } }""");
        Assert.Equal(3, settings.Screens.ThanksSeconds);
        Assert.Equal(AppSettings.CurrentVersion, settings.Version);

        // Enregistré puis relu : 6 s remis à la main dans l'admin restent 6 s.
        settings.Screens.ThanksSeconds = 6;
        var store = new SettingsStore(Path.Combine(_temp.Root, "config.json"));
        store.Save(settings);
        Assert.Equal(6, store.Load().Screens.ThanksSeconds);
    }

    [Theory]
    [InlineData(2, 10)]
    [InlineData(3, 6)]
    public void Chosen_duration_is_kept(int version, int seconds)
    {
        Assert.Equal(seconds, Load($$"""{ "Version": {{version}}, "Screens": { "ThanksSeconds": {{seconds}} } }""").Screens.ThanksSeconds);
    }

    [Fact]
    public void Both_old_defaults_are_corrected_from_version_one()
    {
        var settings = Load("""{ "Version": 1, "Print": { "MediaCapacity": 700 }, "Screens": { "ThanksSeconds": 6 } }""");
        Assert.Equal(400, settings.Print.MediaCapacity);
        Assert.Equal(3, settings.Screens.ThanksSeconds);
    }
}

/// <summary>Version 3.2.2 : les fenêtres de l'admin plus grandes que l'écran y sont ramenées.</summary>
public sealed class WindowFitTests
{
    [Fact]
    public void Window_that_fits_is_unchanged()
    {
        var window = new WindowSize(900, 760, 640, 480);
        Assert.Equal(window, WindowFit.Fit(window, 1920, 1040));
    }

    [Fact]
    public void Tall_window_is_brought_back_on_a_small_screen()
    {
        // 1366 × 768 à 125 % : zone utile d'environ 1093 × 584.
        var fitted = WindowFit.Fit(new WindowSize(1440, 920, 980, 620), 1093, 584);
        Assert.Equal(new WindowSize(1053, 544, 980, 544), fitted);
    }

    [Fact]
    public void Minimum_size_never_exceeds_the_screen()
    {
        var fitted = WindowFit.Fit(new WindowSize(900, 760, 640, 480), 600, 400);
        Assert.True(fitted.Width <= 560 && fitted.MinWidth <= 560);
        Assert.True(fitted.Height <= 360 && fitted.MinHeight <= 360);
    }

    [Fact]
    public void Automatic_size_is_left_alone()
    {
        var fitted = WindowFit.Fit(new WindowSize(double.NaN, double.NaN, 0, 0), 800, 600);
        Assert.True(double.IsNaN(fitted.Width) && double.IsNaN(fitted.Height));
    }
}
