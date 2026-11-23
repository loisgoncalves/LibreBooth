using System;
using System.Drawing;
using System.Windows.Forms;

namespace LibreBooth.Installer;

/// <summary>Ce que les fenêtres d'installation et de désinstallation ont en commun.</summary>
internal static class FormParts
{
    public static readonly Color Warning = Color.FromArgb(0x8A, 0x4B, 0x00);
    public static readonly Color Success = Color.FromArgb(0x1E, 0x7B, 0x34);
    public static readonly Color Error = Color.FromArgb(0xA4, 0x26, 0x2C);

    /// <summary>Fenêtre de dialogue blanche, à la taille de son contenu, avec l'icône de LibreBooth.</summary>
    public static void Setup(Form form, string title)
    {
        form.Text = title;
        form.Font = new Font("Segoe UI", 10f);
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.AutoSize = true;
        form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Padding = new Padding(20);
        form.BackColor = Color.White;
        try
        {
            form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // Icône par défaut de Windows.
        }
    }

    /// <summary>Le logo de LibreBooth, le titre et l'auteur.</summary>
    public static Control Header(string title)
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
            Text = title,
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

    /// <summary>Texte sur plusieurs lignes, à la largeur des fenêtres.</summary>
    public static Label Paragraph(string text, int left = 0, int top = 0, int bottom = 6) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(560 - left, 0),
        Margin = new Padding(left, top, 0, bottom),
    };

    private static Image? LoadLogo()
    {
        try
        {
            using var stream = typeof(FormParts).Assembly.GetManifestResourceStream("LibreBooth.Installer.logo.png");
            return stream == null ? null : Image.FromStream(stream);
        }
        catch
        {
            return null;
        }
    }
}
