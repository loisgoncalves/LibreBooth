using LibreBooth.Core.Config;

namespace LibreBooth.Core.Session;

/// <summary>Les écrans du photobooth, dans l'ordre du parcours de l'invité.</summary>
public enum KioskState
{
    Attract,
    ChooseTemplate,
    Shooting,
    Processing,
    Review,
    Email,
    Thanks,
    Error,
}

public sealed class StateChangedEventArgs : EventArgs
{
    public StateChangedEventArgs(KioskState previous, KioskState current)
    {
        Previous = previous;
        Current = current;
    }

    public KioskState Previous { get; }
    public KioskState Current { get; }
}

/// <summary>
/// Enchaînement des écrans. Les transitions impossibles sont ignorées (elles renvoient false) :
/// un double appui ou un délai qui arrive en retard ne peut pas casser le parcours.
/// </summary>
public sealed class SessionStateMachine
{
    public KioskState State { get; private set; } = KioskState.Attract;
    public int ShotCount { get; private set; }
    /// <summary>Numéro (à partir de 0) de la photo en cours.</summary>
    public int ShotIndex { get; private set; }
    public string? ErrorMessage { get; private set; }

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public bool ChooseTemplate() => State == KioskState.Attract && Go(KioskState.ChooseTemplate);

    public bool BeginShooting(int shotCount)
    {
        if (State is not (KioskState.Attract or KioskState.ChooseTemplate)) return false;
        if (shotCount <= 0) return Fail("Ce modèle ne contient aucune zone photo.");
        ShotCount = shotCount;
        ShotIndex = 0;
        return Go(KioskState.Shooting);
    }

    /// <returns>true quand c'était la dernière photo : on passe au montage.</returns>
    public bool ShotCompleted()
    {
        if (State != KioskState.Shooting) return false;
        ShotIndex++;
        if (ShotIndex < ShotCount) return false;
        Go(KioskState.Processing);
        return true;
    }

    public bool CompositionReady() => State == KioskState.Processing && Go(KioskState.Review);

    /// <summary>L'invité refait toute sa série depuis l'écran de revue (bouton « Recommencer »).</summary>
    public bool Retake()
    {
        if (State != KioskState.Review || ShotCount <= 0) return false;
        ShotIndex = 0;
        return Go(KioskState.Shooting);
    }

    public bool OpenEmail() => State == KioskState.Review && Go(KioskState.Email);

    public bool CloseEmail() => State == KioskState.Email && Go(KioskState.Review);

    public bool Finish() => (State is KioskState.Review or KioskState.Email) && Go(KioskState.Thanks);

    public bool Fail(string message)
    {
        ErrorMessage = message;
        return Go(KioskState.Error, force: true);
    }

    public bool Reset()
    {
        ShotCount = 0;
        ShotIndex = 0;
        ErrorMessage = null;
        return Go(KioskState.Attract, force: true);
    }

    /// <summary>Appelé quand l'invité n'a rien touché pendant le délai de l'écran courant.</summary>
    public bool OnTimeout() => State switch
    {
        KioskState.ChooseTemplate => Reset(),
        KioskState.Review => Go(KioskState.Thanks),
        KioskState.Email => Go(KioskState.Review),
        KioskState.Thanks => Reset(),
        KioskState.Error => Reset(),
        _ => false,
    };

    /// <summary>Délai d'inactivité de chaque écran (aucun pendant la prise de vue et le montage).</summary>
    public static TimeSpan? TimeoutFor(KioskState state, ScreenSettings screens) => state switch
    {
        KioskState.ChooseTemplate => TimeSpan.FromSeconds(screens.ChooseTimeoutSeconds),
        KioskState.Review => TimeSpan.FromSeconds(screens.ReviewTimeoutSeconds),
        KioskState.Email => TimeSpan.FromSeconds(screens.EmailTimeoutSeconds),
        KioskState.Thanks => TimeSpan.FromSeconds(screens.ThanksSeconds),
        KioskState.Error => TimeSpan.FromSeconds(screens.ErrorSeconds),
        _ => null,
    };

    private bool Go(KioskState next, bool force = false)
    {
        var previous = State;
        if (previous == next && !force) return false;
        State = next;
        StateChanged?.Invoke(this, new StateChangedEventArgs(previous, next));
        return true;
    }
}
