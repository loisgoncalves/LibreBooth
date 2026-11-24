using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Services;
using LibreBooth.Core.Layouts;
using Microsoft.Win32;
using Serilog;

namespace LibreBooth.App.Admin;

/// <summary>
/// Liste des modèles (page Personnalisation, section « Modèles et cadres PNG ») : import d'un cadre PNG, modification,
/// copie et suppression. Après chaque changement, <see cref="TemplatesChanged"/> prévient la page, qui se recharge.
/// </summary>
public partial class TemplatesPanel : UserControl
{
    private readonly ILogger _log = Log.ForContext<TemplatesPanel>();
    private readonly ObservableCollection<TemplateItem> _templates = new();
    private AppServices? _services;

    public TemplatesPanel()
    {
        InitializeComponent();
        TemplatesList.ItemsSource = _templates;
    }

    /// <summary>Un modèle a été importé, modifié, copié ou supprimé.</summary>
    public event EventHandler? TemplatesChanged;

    private AppServices Services => _services ?? throw new InvalidOperationException("Liste des modèles non initialisée.");

    public void Initialize(AppServices services) => _services = services;

    /// <summary>Recharge la liste, avec un aperçu de chaque modèle aux couleurs de l'événement.</summary>
    public void Load()
    {
        if (_services == null) return;
        _templates.Clear();
        var context = Services.CurrentPreviewContext();
        foreach (var template in Services.Templates.LoadAll())
        {
            ImageSource? preview = null;
            try
            {
                preview = ImageLoader.FromFile(TemplatePreview.GetOrCreate(Services.Compositor, template, context), decodePixelHeight: 208);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Aperçu du modèle {Template}", template.Id);
            }

            var offered = Services.Customization.Templates.Contains(template.Id, StringComparer.OrdinalIgnoreCase);
            _templates.Add(new TemplateItem(template, preview, offered));
        }
    }

    private void AfterTemplatesChanged()
    {
        Services.EnsureActiveTemplates();
        TemplatesChanged?.Invoke(this, EventArgs.Empty);
    }

    private TemplateItem? SelectedTemplate()
    {
        if (TemplatesList.SelectedItem is TemplateItem item) return item;
        ShowError("Choisissez d'abord un modèle dans la liste.");
        return null;
    }

    private void ImportTemplate_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var dialog = new OpenFileDialog { Title = "Choisir un cadre PNG", Filter = "Images PNG (*.png)|*.png" };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            var template = Services.Templates.ImportFrame(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
            var message = template.ShotCount == 0
                ? "Aucune zone transparente trouvée : ajoutez les zones photo à la main."
                : $"{template.ShotCount} zone(s) photo détectée(s) automatiquement : vérifiez-les puis enregistrez.";
            new TemplateEditorWindow(Services, template, message) { Owner = owner }.ShowDialog();
            AfterTemplatesChanged();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Import du cadre impossible");
            ShowError("Import du cadre impossible : " + ex.Message);
        }
    }

    private void EditTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate() is not { } item) return;
        new TemplateEditorWindow(Services, item.Template) { Owner = Window.GetWindow(this) }.ShowDialog();
        AfterTemplatesChanged();
    }

    private void DuplicateTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate() is not { } item) return;
        Services.Templates.Duplicate(item.Template);
        AfterTemplatesChanged();
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate() is not { } item) return;
        if (!AdminDialog.Ask(Window.GetWindow(this),
                $"Supprimer le modèle « {item.Name} » ?\n\nIl sera déplacé dans le dossier Modeles\\_corbeille.",
                "Supprimer", image: MessageBoxImage.Warning, defaultConfirm: false))
        {
            return;
        }
        Services.Templates.Delete(item.Template);
        AfterTemplatesChanged();
    }

    private void OpenTemplatesFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(Services.Templates.Root);

    private void ShowError(string message) =>
        AdminDialog.Show(Window.GetWindow(this), message, "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Warning);
}

public sealed class TemplateItem
{
    public TemplateItem(Template template, ImageSource? preview, bool offered)
    {
        Template = template;
        Preview = preview;
        var layout = template.Layout;
        Details = $"{template.ShotCount} photo(s) · {layout.OutputWidth} × {layout.OutputHeight} px · profil d'impression « {layout.PrintProfile} »"
                  + (layout.IsCustomizable ? " · personnalisable" : " · cadre PNG")
                  + (layout.DuplicateStrip ? " · imprimé deux fois puis coupé" : "")
                  + (layout.BlackAndWhite ? " · noir et blanc" : "")
                  + (offered ? " · proposé pour cet événement" : "");
    }

    public Template Template { get; }
    public ImageSource? Preview { get; }
    public string Name => Template.Name;
    public string Details { get; }

    /// <summary>Ce que lit un lecteur d'écran pour la ligne de la liste : le nom du modèle et ses caractéristiques.</summary>
    public override string ToString() => $"{Name}, {Details}";
}
