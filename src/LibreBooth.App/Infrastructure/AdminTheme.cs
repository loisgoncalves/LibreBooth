using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LibreBooth.Core.Admin;
using LibreBooth.Core.Config;
using LibreBooth.Core.Design;
using Serilog;

namespace LibreBooth.App.Infrastructure;

/// <summary>
/// Apparence de l'admin, en clair ou en sombre (admin › Système › Apparence). Les couleurs de AdminPalette sont rangées
/// dans les ressources de l'application sous « Admin.Nom » (et « Admin.NomColor » pour la couleur seule) ; les styles
/// de Resources/AdminStyles.xaml les lisent en DynamicResource, si bien qu'un changement s'applique tout de suite à
/// toutes les fenêtres ouvertes. Les clés « Admin. » ne croisent pas celles du photobooth (Theme).
/// </summary>
internal static class AdminTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    /// <summary>Même réglage sous Windows 10 avant la version 20H1.</summary>
    private const int DwmUseImmersiveDarkModeOld = 19;
    private const uint WmNcActivate = 0x0086;

    private static readonly List<WeakReference<Window>> AttachedWindows = new();

    /// <summary>Couleurs en place, dans les dictionnaires fusionnés de l'application.</summary>
    private static ResourceDictionary? _colors;

    public static AdminAppearance Current { get; private set; } = AdminAppearance.Light;

    /// <summary>L'apparence vient de changer (les fenêtres qui dessinent elles-mêmes des couleurs se mettent à jour).</summary>
    public static event EventHandler? Changed;

    /// <summary>Apparence enregistrée dans config.json (clair si le fichier est absent ou illisible).</summary>
    public static AdminAppearance ReadSetting(string configFile)
    {
        try
        {
            return new SettingsStore(configFile).Load().AdminAppearance;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Apparence de l'admin illisible : clair par défaut");
            return AdminAppearance.Light;
        }
    }

    public static void Apply(AdminAppearance appearance)
    {
        if (Application.Current is not { } app) return;
        var colors = new ResourceDictionary();
        foreach (var (name, hex) in AdminPalette.For(appearance).Colors())
        {
            var color = Theme.ParseColor(hex, Colors.Magenta);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            colors["Admin." + name] = brush;
            colors["Admin." + name + "Color"] = color;
        }

        // Toutes les couleurs d'un coup : un seul passage dans les fenêtres ouvertes, au lieu d'un par couleur.
        var merged = app.Resources.MergedDictionaries;
        var index = _colors == null ? -1 : merged.IndexOf(_colors);
        if (index >= 0) merged[index] = colors;
        else merged.Add(colors);
        _colors = colors;

        var changed = Current != appearance;
        Current = appearance;
        if (!changed) return;
        foreach (var window in OpenWindows()) ApplyTitleBar(window, repaint: true);
        Log.Information("Apparence de l'admin : {Appearance}", appearance == AdminAppearance.Dark ? "sombre" : "clair");
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Fenêtre de l'admin : fond et texte aux couleurs de l'apparence, barre de titre claire ou sombre
    /// (Windows 10 20H1 et plus, Windows 11 ; ailleurs elle reste claire, sans erreur).
    /// </summary>
    public static void Attach(Window window)
    {
        FitToScreen(window);
        window.SetResourceReference(Control.BackgroundProperty, "Admin.Window");
        window.SetResourceReference(Control.ForegroundProperty, "Admin.Text");
        AttachedWindows.RemoveAll(w => !w.TryGetTarget(out var target) || ReferenceEquals(target, window));
        AttachedWindows.Add(new WeakReference<Window>(window));
        window.Closed += (_, _) => AttachedWindows.RemoveAll(w => !w.TryGetTarget(out var target) || ReferenceEquals(target, window));
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyTitleBar(window, repaint: true);
        }
        else
        {
            // Avant le premier affichage : la barre de titre naît directement dans la bonne couleur.
            window.SourceInitialized += (_, _) => ApplyTitleBar(window, repaint: false);
        }
    }

    /// <summary>Fenêtre plus grande que l'écran ramenée à sa zone utile (voir <see cref="WindowFit"/>).</summary>
    private static void FitToScreen(Window window)
    {
        var area = SystemParameters.WorkArea;
        var fitted = WindowFit.Fit(new WindowSize(window.Width, window.Height, window.MinWidth, window.MinHeight), area.Width, area.Height);
        window.MinWidth = fitted.MinWidth;
        window.MinHeight = fitted.MinHeight;
        if (!double.IsNaN(fitted.Width)) window.Width = fitted.Width;
        if (!double.IsNaN(fitted.Height)) window.Height = fitted.Height;
    }

    private static IEnumerable<Window> OpenWindows()
    {
        foreach (var reference in AttachedWindows.ToList())
        {
            if (reference.TryGetTarget(out var window)) yield return window;
        }
    }

    private static void ApplyTitleBar(Window window, bool repaint)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = Current == AdminAppearance.Dark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeOld, ref dark, sizeof(int));
            }
            if (repaint)
            {
                // Windows 10 ne redessine la barre de titre qu'au changement d'activation : on la redessine
                // deux fois, en finissant par l'état actuel de la fenêtre.
                var active = window.IsActive;
                SendMessage(handle, WmNcActivate, active ? IntPtr.Zero : new IntPtr(1), IntPtr.Zero);
                SendMessage(handle, WmNcActivate, active ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows sans barre de titre sombre : elle reste claire.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
