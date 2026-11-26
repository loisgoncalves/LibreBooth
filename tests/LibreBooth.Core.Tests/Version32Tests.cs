using LibreBooth.Core.Admin;
using LibreBooth.Core.Config;
using LibreBooth.Core.Hardware;

namespace LibreBooth.Core.Tests;

/// <summary>
/// Version 3.2.0 : à 0 tirage restant, l'impression part quand même et l'invité dit si sa photo est sortie.
/// « Oui » : plus de question jusqu'au prochain rouleau. Deux « Non » : rouleau déclaré vide, impression suspendue.
/// </summary>
public sealed class PaperCheckTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(0, PaperReport.None, true)]
    [InlineData(-3, PaperReport.None, true)]
    [InlineData(1, PaperReport.None, false)]
    [InlineData(350, PaperReport.None, false)]
    [InlineData(null, PaperReport.None, false)]
    // Un invité a déjà répondu pour ce rouleau : on ne redemande pas.
    [InlineData(0, PaperReport.StillPrinting, false)]
    [InlineData(0, PaperReport.Empty, false)]
    public void The_guest_is_asked_only_when_the_counter_is_at_zero(int? remaining, PaperReport report, bool asked) =>
        Assert.Equal(asked, PaperCheck.ShouldAsk(remaining, report));

    [Fact]
    public void Only_an_empty_roll_stops_printing()
    {
        Assert.True(PaperCheck.BlocksPrinting(PaperReport.Empty));
        Assert.False(PaperCheck.BlocksPrinting(PaperReport.StillPrinting));
        Assert.False(PaperCheck.BlocksPrinting(PaperReport.None));
    }

    [Theory]
    [InlineData(350, PaperReport.Empty, true)]
    [InlineData(1, PaperReport.StillPrinting, true)]
    [InlineData(0, PaperReport.Empty, false)]
    [InlineData(0, PaperReport.StillPrinting, false)]
    [InlineData(null, PaperReport.Empty, false)]
    [InlineData(350, PaperReport.None, false)]
    public void The_answers_are_forgotten_once_the_counter_goes_back_up(int? remaining, PaperReport report, bool forgotten) =>
        Assert.Equal(forgotten, PaperCheck.ShouldForget(remaining, report));

    [Fact]
    public void The_question_waits_for_every_copy_to_come_out()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), PaperCheck.QuestionDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(32), PaperCheck.QuestionDelay(2));
        Assert.Equal(PaperCheck.QuestionDelay(1), PaperCheck.QuestionDelay(0));
    }

    [Theory]
    [InlineData("LibreBooth 20261126-153012-ab12", true)]
    [InlineData("LibreBooth 20261126-153012-ab12 (nouvel essai)", true)]
    [InlineData("LibreBooth page de test", true)]
    [InlineData("Document Word", false)]
    [InlineData(null, false)]
    public void Only_librebooth_jobs_are_cancelled(string? name, bool cancelled) =>
        Assert.Equal(cancelled, PaperCheck.IsLibreBoothJob(name));

    [Fact]
    public void The_answer_is_kept_in_config_and_nothing_by_default()
    {
        Assert.Equal(PaperReport.None, new AppSettings().Print.PaperReport);
        var path = Path.Combine(_temp.Root, "config.json");
        File.WriteAllText(path, """{ "Version": 1, "Print": { "PrintedSinceMediaChange": 12 } }""");
        var store = new SettingsStore(path);
        Assert.Equal(PaperReport.None, store.Load().Print.PaperReport);

        var settings = new AppSettings();
        settings.Print.PaperReport = PaperReport.Empty;
        store.Save(settings);

        Assert.Equal(PaperReport.Empty, store.Load().Print.PaperReport);
        Assert.Contains("\"Empty\"", File.ReadAllText(path));
    }

    [Fact]
    public void An_unknown_answer_in_config_is_ignored()
    {
        var settings = new AppSettings();
        settings.Print.PaperReport = (PaperReport)42;
        Assert.Equal(PaperReport.None, settings.Normalize().Print.PaperReport);
    }
}

public sealed class PaperReadinessTests
{
    private static readonly ReadinessInput Ready = new()
    {
        CameraState = CameraState.Ready,
        PrinterName = "DP-DS620",
        PrinterChecked = true,
        PrinterCanPrint = true,
        PrinterCondition = PrinterCondition.Ready,
        PrinterMessage = "Prête",
        RemainingPrints = 0,
        LowMedia = true,
        Formats = new[] { "10x15 paysage" },
    };

    private static ReadinessItem Paper(ReadinessInput input) => Readiness.Evaluate(input).Single(i => i.Area == ReadinessArea.Paper);

    [Fact]
    public void A_counter_at_zero_asks_for_a_new_roll()
    {
        var item = Paper(Ready);
        Assert.Equal(ReadinessLevel.Error, item.Level);
        Assert.Contains("demandera au prochain invité", item.Detail);
        Assert.True(item.OffersNewRoll);
    }

    [Fact]
    public void A_guest_who_got_the_photo_turns_it_into_a_warning()
    {
        var item = Paper(Ready with { PaperReport = PaperReport.StillPrinting });
        Assert.Equal(ReadinessLevel.Warning, item.Level);
        Assert.Contains("les impressions continuent", item.Detail);
        Assert.True(item.OffersNewRoll);
    }

    [Fact]
    public void A_roll_declared_empty_is_an_error_even_with_an_unknown_counter()
    {
        var item = Paper(Ready with { PaperReport = PaperReport.Empty, RemainingPrints = null });
        Assert.Equal(ReadinessLevel.Error, item.Level);
        Assert.Contains("signalé par un invité", item.Detail);
        Assert.True(item.OffersNewRoll);
    }

    [Fact]
    public void A_normal_roll_offers_no_new_roll_button()
    {
        Assert.False(Paper(Ready with { RemainingPrints = 200, LowMedia = false }).OffersNewRoll);
        Assert.False(Paper(Ready with { RemainingPrints = 10, LowMedia = true }).OffersNewRoll);
    }

    [Fact]
    public void In_test_mode_the_paper_shows_only_when_the_check_is_in_play()
    {
        var pdf = Ready with { PrintToPdf = true, RemainingPrints = 600, LowMedia = false };
        Assert.DoesNotContain(Readiness.Evaluate(pdf), i => i.Area == ReadinessArea.Paper);
        Assert.Equal(ReadinessLevel.Error, Paper(pdf with { PaperReport = PaperReport.Empty }).Level);
        Assert.Equal(ReadinessLevel.Error, Paper(pdf with { RemainingPrints = 0 }).Level);
    }
}
