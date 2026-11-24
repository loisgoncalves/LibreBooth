using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace LibreBooth.App.Infrastructure;

/// <summary>
/// Propriétés attachées lues par les styles de l'admin (Resources/AdminStyles.xaml) :
/// <c>infra:AdminUi.Glyph="&amp;#xE713;"</c> ajoute une icône à un bouton, une page du menu, une carte à choix ou une section repliable.
/// </summary>
public static class AdminUi
{
    /// <summary>Icône (caractère de la police IconFont : Segoe Fluent Icons, ou Segoe MDL2 Assets sous Windows 10).</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(AdminUi), new PropertyMetadata(null));

    public static string? GetGlyph(DependencyObject element) => (string?)element.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject element, string? value) => element.SetValue(GlyphProperty, value);

    /// <summary>Phrase d'explication sous le titre d'une carte à choix.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.RegisterAttached(
        "Description", typeof(string), typeof(AdminUi), new PropertyMetadata(null));

    public static string? GetDescription(DependencyObject element) => (string?)element.GetValue(DescriptionProperty);
    public static void SetDescription(DependencyObject element, string? value) => element.SetValue(DescriptionProperty, value);

    /// <summary>Arrondi des boutons (6 par défaut ; les pastilles du bas de l'admin sont rondes).</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(AdminUi), new PropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);

    /// <summary>Menu réduit aux icônes (fenêtre étroite) ; transmis aux éléments du menu.</summary>
    public static readonly DependencyProperty CompactProperty = DependencyProperty.RegisterAttached(
        "Compact", typeof(bool), typeof(AdminUi), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetCompact(DependencyObject element) => (bool)element.GetValue(CompactProperty);
    public static void SetCompact(DependencyObject element, bool value) => element.SetValue(CompactProperty, value);
}

/// <summary>
/// Contenu d'une page de l'admin : une pile qui prend toute la largeur disponible jusqu'à MaxWidth, collée à gauche.
/// Une StackPanel alignée à gauche prendrait la largeur de son contenu, et les cartes changeraient de largeur d'une page à l'autre.
/// </summary>
public sealed class PagePanel : StackPanel
{
    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(constraint);
        if (!double.IsInfinity(constraint.Width)) size.Width = Math.Max(size.Width, constraint.Width);
        return size;
    }
}

/// <summary>
/// Une ligne de réglage : titre et explication à gauche, contrôle à droite (interrupteur, liste, champ), ou dessous
/// quand <see cref="Stacked"/> est vrai. Le contrôle reçoit le titre comme nom pour l'accessibilité.
/// Son apparence est dans Resources/AdminStyles.xaml.
/// </summary>
public class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(null, OnLabelChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(null, OnLabelChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(
        nameof(Stacked), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    static SettingRow()
    {
        // La ligne elle-même ne prend pas le focus : seul son contrôle le prend.
        FocusableProperty.OverrideMetadata(typeof(SettingRow), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SettingRow), new FrameworkPropertyMetadata(false));
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Icône facultative devant le titre.</summary>
    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Contrôle sous le texte, sur toute la largeur (champs de texte longs, listes).</summary>
    public bool Stacked
    {
        get => (bool)GetValue(StackedProperty);
        set => SetValue(StackedProperty, value);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        NameContent();
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SettingRow)d).NameContent();

    private void NameContent()
    {
        if (Content is not DependencyObject element) return;
        if (!string.IsNullOrWhiteSpace(Title) && string.IsNullOrEmpty(AutomationProperties.GetName(element)))
        {
            AutomationProperties.SetName(element, Title);
        }
        if (!string.IsNullOrWhiteSpace(Description) && string.IsNullOrEmpty(AutomationProperties.GetHelpText(element)))
        {
            AutomationProperties.SetHelpText(element, Description);
        }
    }
}
