using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace LibreBooth.Installer;

/// <summary>Ce que la désinstallation supprime en plus du programme.</summary>
internal sealed class UninstallChoices
{
    /// <summary>Réglages, modèles, personnalisations des événements, pilotes ajoutés (SDK Canon) et journaux.</summary>
    public bool RemoveAppData { get; set; }

    /// <summary>Photos de tous les événements (le dossier Evenements entier) ; seulement avec <see cref="RemoveAppData"/>.</summary>
    public bool RemovePhotos { get; set; }
}

/// <summary>
/// Fenêtre de désinstallation. LibreBooth est retiré ; deux cases, décochées au départ, suppriment en plus les
/// fichiers de LibreBooth, puis les photos (avec une confirmation à part). Tout part dans la Corbeille de Windows.
/// </summary>
internal sealed class UninstallForm : Form
{
    private readonly UserDataSummary _data;
    private readonly bool _hasData;
    private readonly bool _hasEvents;
    private readonly CheckBox _appDataCheck = new()
    {
        Text = "Supprimer aussi les réglages et les fichiers de LibreBooth",
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 0),
    };
    private readonly Label _appDataDetail = FormParts.Paragraph("", left: 20, bottom: 0);
    private readonly CheckBox _photosCheck = new()
    {
        Text = "Supprimer aussi les photos de tous les événements",
        AutoSize = true,
        Margin = new Padding(20, 12, 0, 0),
    };
    private readonly Label _photosDetail = FormParts.Paragraph("", left: 40, bottom: 0);
    private readonly Button _uninstallButton = new() { Text = "Désinstaller", AutoSize = true, MinimumSize = new Size(120, 32) };
    private readonly Button _cancelButton = new() { Text = "Annuler", AutoSize = true, MinimumSize = new Size(110, 32) };

    public UninstallChoices Choices { get; } = new();

    public UninstallForm(UserDataSummary data, UninstallChoices initial)
    {
        _data = data;
        _hasData = data.Roots.Count > 0;
        _hasEvents = data.Events > 0;
        FormParts.Setup(this, "Désinstallation de LibreBooth");
        MinimizeBox = false;

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        layout.Controls.Add(FormParts.Header($"Désinstallation de LibreBooth {Installation.Version}"));
        layout.Controls.Add(FormParts.Paragraph(
            "LibreBooth va être retiré de ce PC : le programme, ses raccourcis et son inscription dans Windows. "
            + "Le pilote de l'imprimante DNP reste installé.", bottom: 14));

        _appDataDetail.ForeColor = Color.DimGray;
        _photosDetail.ForeColor = Color.DimGray;
        if (_hasData)
        {
            var folders = data.Roots.Count == 1 ? "Dossier : " + data.Roots[0] : "Dossiers : " + string.Join(" ; ", data.Roots);
            _appDataDetail.Text = "Réglages, modèles, personnalisations des événements, pilotes ajoutés (SDK Canon) et journaux.\n" + folders;
            _photosDetail.Text = PhotosSummary() + " : tout le dossier « Evenements ».";
        }
        else
        {
            _appDataCheck.Enabled = false;
            _appDataDetail.Text = "Aucun réglage ni aucune photo de LibreBooth n'a été trouvé dans votre profil Windows.";
        }
        _photosCheck.Visible = _photosDetail.Visible = _hasEvents;
        _appDataCheck.Checked = _hasData && initial.RemoveAppData;
        _photosCheck.Checked = _appDataCheck.Checked && initial.RemovePhotos;
        _photosCheck.Enabled = _appDataCheck.Checked;

        layout.Controls.Add(_appDataCheck);
        layout.Controls.Add(_appDataDetail);
        layout.Controls.Add(_photosCheck);
        layout.Controls.Add(_photosDetail);
        layout.Controls.Add(new Label
        {
            Text = "Ce qui est supprimé part dans la Corbeille de Windows : vous pouvez encore le récupérer tant qu'elle n'est pas vidée. "
                   + "Sans case cochée, vos photos, événements et réglages restent sur ce PC.",
            ForeColor = Color.DimGray,
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 16, 0, 0),
        });
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 16, 0, 0) };
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_uninstallButton);
        FormParts.Arrange(this, layout, buttons);

        CancelButton = _cancelButton;
        _appDataCheck.CheckedChanged += (_, _) =>
        {
            _photosCheck.Enabled = _appDataCheck.Checked;
            if (!_appDataCheck.Checked) _photosCheck.Checked = false;
        };
        _uninstallButton.Click += (_, _) => Confirm();
        _cancelButton.Click += (_, _) => Close();
    }

    /// <summary>« 3 événements, 245 photos et montages (1,2 Go) ».</summary>
    private string PhotosSummary()
    {
        var events = Count(_data.Events, "événement", "événements");
        return _data.Photos == 0
            ? events + ", sans photo"
            : $"{events}, {Count(_data.Photos, "photo", "photos et montages")} ({UserData.FormatSize(_data.PhotoBytes)})";
    }

    private static string Count(int n, string one, string many) =>
        n.ToString("N0", CultureInfo.GetCultureInfo("fr-FR")) + " " + (n <= 1 ? one : many);

    private void Confirm()
    {
        var removeAppData = _hasData && _appDataCheck.Checked;
        var removePhotos = removeAppData && _hasEvents && _photosCheck.Checked;
        if (removePhotos)
        {
            var answer = MessageBox.Show(this,
                "Supprimer les photos de tous les événements ?\n\n"
                + $"{PhotosSummary()} vont partir dans la Corbeille de Windows. "
                + "Pour les garder, copiez d'abord le dossier « Evenements » (clé USB, disque externe).\n\n"
                + "Supprimer les photos ?",
                "LibreBooth", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }
        Choices.RemoveAppData = removeAppData;
        Choices.RemovePhotos = removePhotos;
        DialogResult = DialogResult.OK;
        Close();
    }
}
