using LibreBooth.Core.Config;
using LibreBooth.Core.Session;

namespace LibreBooth.Core.Tests;

public sealed class SessionStateMachineTests
{
    [Fact]
    public void Full_session_goes_through_every_screen()
    {
        var sm = new SessionStateMachine();
        var visited = new List<KioskState>();
        sm.StateChanged += (_, e) => visited.Add(e.Current);

        Assert.True(sm.BeginShooting(3));
        Assert.False(sm.ShotCompleted());
        Assert.False(sm.ShotCompleted());
        Assert.True(sm.ShotCompleted());
        Assert.Equal(KioskState.Processing, sm.State);
        Assert.True(sm.CompositionReady());
        Assert.True(sm.Finish());
        Assert.True(sm.OnTimeout());

        Assert.Equal(
            new[] { KioskState.Shooting, KioskState.Processing, KioskState.Review, KioskState.Thanks, KioskState.Attract },
            visited);
    }

    [Fact]
    public void Choice_screen_comes_before_shooting_when_several_templates_are_active()
    {
        var sm = new SessionStateMachine();

        Assert.True(sm.ChooseTemplate());
        Assert.Equal(KioskState.ChooseTemplate, sm.State);
        Assert.True(sm.BeginShooting(4));
        Assert.Equal(4, sm.ShotCount);
    }

    [Fact]
    public void Impossible_transitions_are_ignored()
    {
        var sm = new SessionStateMachine();

        Assert.False(sm.CompositionReady());
        Assert.False(sm.Finish());
        Assert.False(sm.ShotCompleted());
        Assert.False(sm.OnTimeout());
        Assert.Equal(KioskState.Attract, sm.State);

        sm.BeginShooting(2);
        Assert.False(sm.BeginShooting(2));
        Assert.False(sm.OnTimeout());
        Assert.Equal(KioskState.Shooting, sm.State);
    }

    [Fact]
    public void Timeouts_follow_the_guest_journey()
    {
        var sm = new SessionStateMachine();
        sm.ChooseTemplate();
        sm.OnTimeout();
        Assert.Equal(KioskState.Attract, sm.State);

        sm.BeginShooting(1);
        sm.ShotCompleted();
        sm.CompositionReady();
        sm.OnTimeout();
        Assert.Equal(KioskState.Thanks, sm.State);
        sm.OnTimeout();
        Assert.Equal(KioskState.Attract, sm.State);
    }

    [Fact]
    public void Error_from_any_screen_then_reset_returns_to_attract()
    {
        var sm = new SessionStateMachine();
        sm.BeginShooting(4);

        Assert.True(sm.Fail("Appareil déconnecté"));
        Assert.Equal(KioskState.Error, sm.State);
        Assert.Equal("Appareil déconnecté", sm.ErrorMessage);

        Assert.True(sm.OnTimeout());
        Assert.Equal(KioskState.Attract, sm.State);
        Assert.Null(sm.ErrorMessage);
        Assert.Equal(0, sm.ShotCount);
    }

    [Fact]
    public void Template_without_zone_is_an_error()
    {
        var sm = new SessionStateMachine();

        sm.BeginShooting(0);

        Assert.Equal(KioskState.Error, sm.State);
    }

    [Fact]
    public void No_timeout_while_shooting_or_processing()
    {
        var screens = new ScreenSettings();

        Assert.Null(SessionStateMachine.TimeoutFor(KioskState.Shooting, screens));
        Assert.Null(SessionStateMachine.TimeoutFor(KioskState.Processing, screens));
        Assert.Null(SessionStateMachine.TimeoutFor(KioskState.Attract, screens));
        Assert.Equal(TimeSpan.FromSeconds(screens.ReviewTimeoutSeconds), SessionStateMachine.TimeoutFor(KioskState.Review, screens));
    }
}
