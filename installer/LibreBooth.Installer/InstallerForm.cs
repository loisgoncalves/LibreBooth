using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LibreBooth.Installer;

/// <summary>
/// Fenêtre d'installation : dossier, pilote DNP, raccourci sur le Bureau, puis progression. Elle permet aussi de
/// vérifier une installation existante, et propose la vérification complète à la fin de l'installation.
/// </summary>
internal sealed class InstallerForm : Form
{
    private readonly Payload? _payload;
    private readonly InstallOptions _options;
    private readonly TextBox _folderBox = new() { Width = 420 };
    private readonly Button _browseButton = new() { Text = "Parcourir…", AutoSize = true };
    private readonly CheckBox _driverCheck = new() { AutoSize = true };
    private readonly CheckBox _desktopCheck = new() { Text = "Créer un raccourci sur le Bureau", AutoSize = true, Checked = true };
    private readonly Button _verifyExistingButton = new() { Text = "Vérifier l'installation actuelle (sans réinstaller)", AutoSize = true, Visible = false };
    private readonly CheckBox _verifyCheck = new() { Text = "Vérifier que tout fonctionne : dossiers, pilotes, imprimante, appareil photo (recommandé)", AutoSize = true, Checked = true, Visible = false };
    private readonly CheckBox _launchCheck = new() { Text = "Lancer LibreBooth maintenant", AutoSize = true, Checked = false, Visible = false };
    private readonly ProgressBar _progress = new() { Width = 540, Height = 18 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly Button _installButton = new() { Text = "Installer", AutoSize = true, MinimumSize = new Size(110, 32) };
    private readonly Button _cancelButton = new() { Text = "Annuler", AutoSize = true, MinimumSize = new Size(110, 32) };
    private bool _done;

    public InstallerForm(Payload? payload, InstallOptions options)
    {
        _payload = payload;
        _options = options;

        Text = "Installation de LibreBooth";
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(20);
        BackColor = Color.White;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // Icône par défaut de Windows.
        }

        var driverInstalled = Installation.IsDriverInstalled();
        _driverCheck.Text = driverInstalled
            ? "Réinstaller le pilote de l'imprimante DNP DS620 (déjà installé sur ce PC)"
            : "Installer le pilote de l'imprimante DNP DS620";
        _driverCheck.Checked = options.InstallDriver;
        var withDriver = payload?.HasDriver == true;
        _driverCheck.Visible = !options.TestMode && withDriver;
        _folderBox.Text = options.InstallDir;
        _verifyExistingButton.Visible = Installation.VerifyShortcutPath(options.TestMode, options.InstallDir) != null;

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        layout.Controls.Add(Header());
        layout.Controls.Add(new Label
        {
            Text = withDriver
                ? "LibreBooth, le logiciel du photobooth, et le pilote de l'imprimante DNP DS620 vont être installés sur ce PC. "
                  + "L'appareil photo Canon n'a besoin d'aucun pilote Windows."
                : "LibreBooth, le logiciel du photobooth, va être installé sur ce PC.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 0, 0, withDriver ? 14 : 6),
        });
        if (!withDriver)
        {
            layout.Controls.Add(new Label
            {
                Text = "Les fichiers de Canon (SDK EDSDK) et le pilote de la DNP ne sont pas inclus : ils appartiennent à leurs fabricants. "
                       + @"À la fin, la vérification vous indique où les télécharger et où les copier (C:\Users\<nom>\LibreBooth\Pilotes).",
                ForeColor = Color.FromArgb(0x8A, 0x4B, 0x00),
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                Margin = new Padding(0, 0, 0, 14),
            });
        }
        layout.Controls.Add(new Label { Text = "Dossier d'installation :", AutoSize = true });
        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 10) };
        folderRow.Controls.Add(_folderBox);
        folderRow.Controls.Add(_browseButton);
        layout.Controls.Add(folderRow);
        layout.Controls.Add(_driverCheck);
        layout.Controls.Add(_desktopCheck);
        layout.Controls.Add(new Label
        {
            Text = "Vos photos, événements et réglages (dossier LibreBooth de votre profil, C:\\Users\\<nom>\\LibreBooth) ne sont jamais effacés, ni par une mise à jour, ni par la désinstallation.",
            ForeColor = Color.DimGray,
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 12, 0, 8),
        });
        layout.Controls.Add(_verifyExistingButton);
        layout.Controls.Add(_progress);
        layout.Controls.Add(_status);
        layout.Controls.Add(_verifyCheck);
        layout.Controls.Add(_launchCheck);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_installButton);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = _installButton;
        CancelButton = _cancelButton;
        _browseButton.Click += (_, _) => Browse();
        _installButton.Click += async (_, _) => await InstallOrFinishAsync();
        _cancelButton.Click += (_, _) => Close();
        _verifyExistingButton.Click += (_, _) => VerifyExisting();
        // Vérifier ou lancer LibreBooth : l'un ou l'autre (la vérification propose ensuite de lancer LibreBooth).
        _verifyCheck.CheckedChanged += (_, _) => { if (_verifyCheck.Checked) _launchCheck.Checked = false; };
        _launchCheck.CheckedChanged += (_, _) => { if (_launchCheck.Checked) _verifyCheck.Checked = false; };
    }

    /// <summary>Le logo de LibreBooth, le titre et l'auteur.</summary>
    private Control Header()
    {
        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
        var logo = LoadLogo();
        if (logo != null)
        {
            header.Controls.Add(new PictureBox
            {
                Image = logo,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(150, (int)Math.Round(150.0 * logo.Height / logo.Width)),
                Margin = new Padding(0, 0, 18, 0),
            });
        }
        var titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Anchor = AnchorStyles.Left };
        titles.Controls.Add(new Label
        {
            Text = $"Installation de LibreBooth {Installation.Version}",
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 18, 0, 4),
        });
        titles.Controls.Add(new Label
        {
            Text = $"Logiciel de photobooth gratuit · conçu par {Installation.Author}",
            ForeColor = Color.DimGray,
            AutoSize = true,
        });
        header.Controls.Add(titles);
        return header;
    }

    private static Image? LoadLogo()
    {
        try
        {
            using var stream = typeof(InstallerForm).Assembly.GetManifestResourceStream("LibreBooth.Installer.logo.png");
            return stream == null ? null : Image.FromStream(stream);
        }
        catch
        {
            return null;
        }
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Dossier où installer LibreBooth (un sous-dossier « LibreBooth » y sera créé)",
            SelectedPath = Path.GetDirectoryName(_folderBox.Text) ?? "",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _folderBox.Text = Path.Combine(dialog.SelectedPath, "LibreBooth");
    }

    private void VerifyExisting()
    {
        _status.Text = Installation.LaunchVerification(_options.TestMode, _options.InstallDir)
            ? "La vérification de l'installation actuelle s'ouvre dans une autre fenêtre."
            : "La vérification n'a pas pu être lancée : installez d'abord cette version.";
    }

    private async Task InstallOrFinishAsync()
    {
        if (_done)
        {
            if (_verifyCheck.Visible && _verifyCheck.Checked) Installation.LaunchVerification(_options.TestMode, _options.InstallDir);
            else if (_launchCheck.Visible && _launchCheck.Checked) LaunchLibreBooth();
            Close();
            return;
        }

        try
        {
            _options.InstallDir = Installation.NormalizeInstallDir(_folderBox.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Dossier invalide : " + ex.Message, "LibreBooth", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _folderBox.Text = _options.InstallDir;
        _options.InstallDriver = _driverCheck.Visible && _driverCheck.Checked;
        _options.DesktopShortcut = _desktopCheck.Checked;

        while (Installation.IsLibreBoothRunning())
        {
            var retry = MessageBox.Show(this, "LibreBooth est ouvert. Fermez-le (bouton « Quitter LibreBooth » de l'admin), puis cliquez sur Réessayer.",
                "LibreBooth", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
            if (retry != DialogResult.Retry) return;
        }

        SetEditing(false);
        _verifyExistingButton.Visible = false;
        try
        {
            var payload = _payload ?? throw new InvalidOperationException("Ce programme ne contient pas LibreBooth.");
            await Task.Run(() => Installation.Install(payload, _options, ReportProgress));
            _done = true;
            _status.Text = "LibreBooth est installé. Ouvrez-le avec le raccourci « LibreBooth » (Bureau ou menu Démarrer).";
            if (_options.InstallDriver) _status.Text += " Branchez la DS620 en USB et allumez-la : Windows l'installe avec son pilote.";
            _verifyCheck.Visible = Installation.VerifyShortcutPath(_options.TestMode, _options.InstallDir) != null;
            _launchCheck.Visible = !_options.TestMode;
            _installButton.Text = "Terminer";
            _installButton.Enabled = true;
            _cancelButton.Visible = false;
        }
        catch (Exception ex)
        {
            Installation.Log("Erreur d'installation : " + ex);
            _status.Text = "L'installation a échoué : " + ex.Message;
            MessageBox.Show(this, "L'installation a échoué :\n" + ex.Message + "\n\nDétails : " + Installation.LogFile,
                "LibreBooth", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetEditing(true);
        }
    }

    private void ReportProgress(int percent, string message)
    {
        if (IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            _progress.Value = Math.Max(0, Math.Min(100, percent));
            _status.Text = message;
        }));
    }

    private void SetEditing(bool enabled)
    {
        _folderBox.Enabled = enabled;
        _browseButton.Enabled = enabled;
        _driverCheck.Enabled = enabled;
        _desktopCheck.Enabled = enabled;
        _installButton.Enabled = enabled;
        _cancelButton.Enabled = enabled;
    }

    /// <summary>LibreBooth est lancé par l'Explorateur, donc sans les droits d'administrateur de l'installation.</summary>
    private void LaunchLibreBooth()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.Combine(_options.InstallDir, Installation.ExeName)}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Installation.Log("Lancement de LibreBooth impossible : " + ex.Message);
        }
    }
}
