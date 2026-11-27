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

    /// <summary>
    /// Zone utile d'écran imposée pour les essais (/ecran LARGEURxHAUTEUR, avec /apercu ou /test) : simule un petit
    /// écran sur un grand. Sinon, la zone utile de l'écran de la fenêtre (sans la barre des tâches).
    /// </summary>
    public static Size? ScreenForTests { get; set; }

    /// <summary>
    /// Contenu dans une zone qui défile, boutons en bas toujours visibles. La fenêtre prend la taille de son contenu
    /// sans jamais dépasser l'écran : sur un petit écran (ou avec un texte agrandi dans Windows), elle s'arrête au bord
    /// et une barre de défilement apparaît à droite.
    /// </summary>
    public static void Arrange(Form form, Control content, Control buttons)
    {
        form.AutoSize = false;
        content.Dock = DockStyle.None;
        content.Location = Point.Empty;
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroller.Controls.Add(content);
        buttons.Dock = DockStyle.Bottom;
        // Ajoutés dans cet ordre, les boutons prennent le bas en premier, la zone qui défile remplit le reste.
        form.Controls.Add(scroller);
        form.Controls.Add(buttons);

        var fitting = false;
        void Fit(bool first)
        {
            if (fitting || form.IsDisposed || (!first && !form.IsHandleCreated)) return;
            fitting = true;
            try
            {
                var screen = Screen.FromControl(form).WorkingArea;
                var area = ScreenForTests is { } test ? new Rectangle(screen.Location, test) : screen;
                var frame = form.Size - form.ClientSize;
                var maxClient = new Size(area.Width - frame.Width, area.Height - frame.Height);
                var wanted = new Size(
                    content.PreferredSize.Width + form.Padding.Horizontal,
                    content.PreferredSize.Height + buttons.PreferredSize.Height + form.Padding.Vertical);
                if (wanted.Height > maxClient.Height) wanted.Width += SystemInformation.VerticalScrollBarWidth;
                form.ClientSize = new Size(Math.Min(wanted.Width, maxClient.Width), Math.Min(wanted.Height, maxClient.Height));

                // Au centre de l'écran à l'ouverture, puis toujours entièrement visible quand le contenu grandit
                // (sauf l'aperçu de /apercu, dessiné hors de l'écran).
                if (form.StartPosition != FormStartPosition.CenterScreen) return;
                var location = first
                    ? new Point(area.Left + (area.Width - form.Width) / 2, area.Top + (area.Height - form.Height) / 2)
                    : form.Location;
                form.Location = new Point(
                    Math.Max(area.Left, Math.Min(location.X, area.Right - form.Width)),
                    Math.Max(area.Top, Math.Min(location.Y, area.Bottom - form.Height)));
            }
            finally
            {
                fitting = false;
            }
        }
        form.Load += (_, _) => Fit(first: true);
        content.SizeChanged += (_, _) => Fit(first: false);
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
