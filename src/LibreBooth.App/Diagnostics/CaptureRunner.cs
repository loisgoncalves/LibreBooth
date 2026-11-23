using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibreBooth.App.Admin;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Kiosk;
using LibreBooth.App.Services;
using LibreBooth.Core.Design;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Session;
using LibreBooth.Core.Updates;
using Serilog;

namespace LibreBooth.App.Diagnostics;

/// <summary>
/// Parcours de vérification automatique (LibreBooth.exe --captures &lt;dossier&gt;) : une vraie session complète
/// en mode démo (choix du format, prise de vue, montage, impression PDF, marque-page, erreur, admin),
/// avec une image de chaque écran, les montages de chaque thème et un rapport. Aucune fenêtre n'est affichée.
/// </summary>
internal static class CaptureRunner
{
    private const int Width = 1920;
    private const int Height = 1080;

    public static async Task RunAsync(AppServices services, KioskViewModel kiosk, string folder)
    {
        Directory.CreateDirectory(folder);
        var log = Log.ForContext(typeof(CaptureRunner));
        var report = new List<string> { $"Parcours de vérification LibreBooth – {DateTime.Now:dd.MM.yyyy HH:mm:ss}" };

        void Note(string line)
        {
            report.Add(line);
            log.Information("[vérification] {Line}", line);
        }

        try
        {
            // Les deux modèles proposés pour passer par l'écran de choix, 3 exemplaires au plus.
            services.Customization.Templates = services.Templates.LoadAll().Select(t => t.Id).ToList();
            services.Customization.MaxCopies = 3;
            services.Customization.Design.Phrase = "Merci d'être venus !";
            services.SaveCustomization();
            kiosk.RefreshFromSettings();

            await WaitUntil(() => services.Camera.State == CameraState.Ready && kiosk.Attract.IsReady, 20, "appareil prêt");
            await Snap(kiosk, folder, "01-accueil");

            kiosk.StartSession();
            await WaitUntil(() => kiosk.State == KioskState.ChooseTemplate, 20, "écran de choix du format");
            await Snap(kiosk, folder, "02-choix-du-format");

            var postcard = kiosk.Choose.Cards.First(c => c.Template.Id == TemplateSeeder.PostcardId);
            kiosk.Choose.ChooseCommand.Execute(postcard);
            await WaitUntil(() => kiosk.State == KioskState.Shooting && kiosk.Shooting.LiveImage != null && kiosk.Shooting.CountdownText == "2", 20, "compte à rebours");
            await Snap(kiosk, folder, "03-prise-de-vue");
            await WaitUntil(() => kiosk.Shooting.IsPreviewVisible, 20, "aperçu de la première photo");
            await Snap(kiosk, folder, "04-apercu-photo");
            await WaitUntil(() => kiosk.State == KioskState.Review, 90, "écran de revue");
            await Snap(kiosk, folder, "05-revue");

            // « Recommencer » : la série refusée part dans « refusees », puis une nouvelle série complète.
            if (!kiosk.Review.ShowRetakeButton) throw new InvalidOperationException("Le bouton Recommencer devrait être visible");
            var refused = kiosk.CurrentSession!;
            kiosk.Review.RetakeCommand.Execute(null);
            await WaitUntil(() => kiosk.State == KioskState.Shooting, 5, "nouvelle série après Recommencer");
            await WaitUntil(() => kiosk.State == KioskState.Review && kiosk.CurrentSession != refused, 90, "revue de la nouvelle série");
            await Snap(kiosk, folder, "05b-revue-apres-recommencer");
            Note($"Recommencer : série {refused.Id} rangée dans {refused.Event.RefusedFolder} "
                 + $"({string.Join(", ", Directory.GetFiles(refused.Event.RefusedFolder).Select(Path.GetFileName))}) ; "
                 + $"bouton encore visible : {(kiosk.Review.ShowRetakeButton ? "oui (ERREUR)" : "non")}");

            var session = kiosk.CurrentSession ?? throw new InvalidOperationException("Session introuvable");
            Note($"Montage 10x15 : {session.CompositePath}");
            Note($"Photos d'origine : {session.Photos.Count} ({string.Join(", ", session.Photos.Select(Path.GetFileName))})");

            await kiosk.PrintCurrentAsync(1);
            await Snap(kiosk, folder, "06-impression-lancee");
            Note($"Impressions PDF : {string.Join(", ", ListPdfs(session.Event.DemoPrintsFolder))}");

            kiosk.Finish();
            await WaitUntil(() => kiosk.State == KioskState.Thanks, 5, "écran de fin");
            await Snap(kiosk, folder, "09-merci");

            // Deuxième session avec un thème sombre : les écrans changent de couleurs.
            ApplyTheme(services, "fete-neon");
            kiosk.ResumeAfterAdmin();
            await WaitUntil(() => kiosk.State == KioskState.Attract, 5, "retour à l'accueil");
            await Snap(kiosk, folder, "09b-accueil-theme-neon");
            kiosk.StartSession();
            await WaitUntil(() => kiosk.State == KioskState.ChooseTemplate, 20, "choix du format (marque-page)");
            kiosk.Choose.ChooseCommand.Execute(kiosk.Choose.Cards.First(c => c.Template.Id == TemplateSeeder.BookmarkId));
            await WaitUntil(() => kiosk.State == KioskState.Review, 90, "revue du marque-page");
            kiosk.Review.IncreaseCopiesCommand.Execute(null);
            await Snap(kiosk, folder, "10-revue-marque-page");
            var stripSession = kiosk.CurrentSession!;
            Note($"Montage marque-page : {stripSession.CompositePath}");
            Note($"Exemplaires choisis : {kiosk.Review.Copies} ({kiosk.Review.CopiesDetail})");
            await kiosk.PrintCurrentAsync(kiosk.Review.Copies);
            Note($"Message après impression : {kiosk.Review.StatusMessage}");
            Note($"Impressions PDF : {string.Join(", ", ListPdfs(stripSession.Event.DemoPrintsFolder))}");

            kiosk.ShowError("Exemple : l'appareil photo a été débranché. Reconnexion automatique…");
            await WaitUntil(() => kiosk.State == KioskState.Error, 5, "écran d'erreur");
            await Snap(kiosk, folder, "11-erreur");

            ApplyTheme(services, ThemeCatalog.DefaultThemeId);
            kiosk.ResumeAfterAdmin();
            await SnapAdmin(services, kiosk, folder);
            await SnapEditor(services, folder);
            await SnapThemes(services, folder, Note);
            await SnapKioskThemes(services, kiosk, folder, Note);
            await SnapVersion14(services, kiosk, folder, Note);
            await SnapVersion20(services, kiosk, folder, Note);

            var stats = services.CurrentEvent.Info.Stats;
            Note($"Statistiques de l'événement : {stats.Sessions} sessions, {stats.Photos} photos, {stats.Prints} impressions, {stats.Retakes} série(s) recommencée(s)");
            Note("RÉSULTAT : parcours complet réussi");
        }
        catch (Exception ex)
        {
            Note("RÉSULTAT : ÉCHEC – " + ex.Message);
            throw;
        }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(folder, "rapport.txt"), report);
        }
    }

    private static IEnumerable<string> ListPdfs(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.pdf").Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length / 1024} Ko)")
            : Array.Empty<string>();

    private static async Task WaitUntil(Func<bool> condition, int seconds, string what)
    {
        var deadline = DateTime.Now.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.Now > deadline) throw new TimeoutException("Délai dépassé en attendant : " + what);
            await Task.Delay(50);
        }
    }

    private static async Task Snap(KioskViewModel kiosk, string folder, string name)
    {
        await Task.Delay(350);
        var host = new ContentControl
        {
            Content = kiosk.CurrentScreen,
            Foreground = (Brush)Application.Current.FindResource("ForegroundBrush"),
            FontFamily = (FontFamily)Application.Current.FindResource("KioskFont"),
        };
        var root = new Border
        {
            Background = (Brush)Application.Current.FindResource("BackgroundBrush"),
            Width = Width,
            Height = Height,
            Child = host,
        };
        Render(root, Path.Combine(folder, name + ".png"), Width, Height);
        host.Content = null;
    }

    private static async Task SnapAdmin(AppServices services, KioskViewModel kiosk, string folder)
    {
        // Comme au lancement de LibreBooth : bandeau d'accueil et bouton « Démarrer le photobooth ».
        var admin = new AdminWindow(services, kiosk);
        admin.LoadForCapture();
        await Task.Delay(500);
        if (admin.Content is not FrameworkElement content) return;
        admin.Content = null;
        foreach (var (index, name) in new[]
                 {
                     (0, "12-admin-evenement"), (1, "13-admin-personnalisation"), (3, "14-admin-impression"), (4, "15-admin-modeles"),
                     (5, "17-admin-ecrans"), (7, "19-admin-systeme"),
                 })
        {
            admin.AdminTabs.SelectedIndex = index;
            // L'aperçu et les vignettes de la personnalisation se préparent en arrière-plan.
            await Task.Delay(index == 1 ? 4000 : 300);
            Render(content, Path.Combine(folder, name + ".png"), 1600, 1000);
            if (index != 1) continue;
            foreach (var (offset, suffix) in new[] { (820.0, "b"), (1640.0, "c"), (2460.0, "d") })
            {
                admin.CustomizationTab.ScrollSettings(offset);
                await Task.Delay(300);
                Render(content, Path.Combine(folder, name + suffix + ".png"), 1600, 1000);
            }
            admin.CustomizationTab.ShowAttractPreviewForCapture();
            await Task.Delay(1500);
            Render(content, Path.Combine(folder, name + "e-apercu-accueil.png"), 1600, 1000);

            // Mode « Mes propres images », avec le gabarit du 10x15 importé comme fond.
            var guide = Path.Combine(folder, "fond-essai-10x15.png");
            DesignGuide.WritePng(TemplateSeeder.CreatePostcard(), guide);
            CustomizationStore.ImportBackground(services.CurrentEvent.Folder, services.Customization, DesignStyles.Postcard, guide, 1800, 1200);
            admin.CustomizationTab.LoadFromEvent();
            admin.CustomizationTab.ScrollSettings(0);
            await Task.Delay(3000);
            Render(content, Path.Combine(folder, name + "f-mes-propres-images.png"), 1600, 1000);
            CustomizationStore.RemoveBackground(services.CurrentEvent.Folder, services.Customization, DesignStyles.Postcard);
            services.Customization.Design.OwnImages = false;
            services.SaveCustomization();
            admin.CustomizationTab.LoadFromEvent();
            admin.CustomizationTab.ScrollSettings(0);
        }
        admin.CloseWithoutPrompt();
    }

    /// <summary>
    /// Nouveautés 1.4.0 : vérification de l'installation, code PIN du lancement, choix « admin ou quitter » et
    /// accueil du photobooth avec son bouton rond.
    /// </summary>
    private static async Task SnapVersion14(AppServices services, KioskViewModel kiosk, string folder, Action<string> note)
    {
        var verification = new VerificationWindow(services, services.Paths);
        await verification.RunForCaptureAsync();
        if (verification.Content is FrameworkElement verificationContent)
        {
            verification.Content = null;
            Render(verificationContent, Path.Combine(folder, "20-verification.png"), 900, 760);
        }
        note("Vérification de l'installation : " + string.Join(" | ", verification.Results.Select(r => $"{r.Status} {r.Title}")));
        verification.Close();

        var pin = new PinWindow(services.Settings.AdminPin, "Code administrateur", "Code par défaut : 1234 (à changer dans l'onglet Système)", standalone: true);
        if (pin.Content is FrameworkElement pinContent)
        {
            pin.Content = null;
            RenderToContent(pinContent, Path.Combine(folder, "21-code-pin-au-lancement.png"), new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x28)), Brushes.White);
        }
        pin.Close();

        var leave = new LeaveBoothWindow();
        if (leave.Content is FrameworkElement leaveContent)
        {
            leave.Content = null;
            RenderToContent(leaveContent, Path.Combine(folder, "22-quitter-le-photobooth.png"), new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x28)), Brushes.White);
        }
        leave.Close();

        kiosk.ResumeAfterAdmin();
        await WaitUntil(() => kiosk.State == KioskState.Attract && kiosk.IsIdle, 5, "accueil du photobooth");
        var booth = new MainWindow(services, kiosk, new BoothShell(services, kiosk));
        if (booth.Content is FrameworkElement boothContent)
        {
            booth.Content = null;
            boothContent.DataContext = kiosk;
            var root = new Border
            {
                Background = (Brush)Application.Current.FindResource("BackgroundBrush"),
                Width = Width,
                Height = Height,
                Child = boothContent,
            };
            TextElement.SetForeground(root, (Brush)Application.Current.FindResource("ForegroundBrush"));
            TextElement.SetFontFamily(root, (FontFamily)Application.Current.FindResource("KioskFont"));
            await Task.Delay(400);
            Render(root, Path.Combine(folder, "23-accueil-avec-bouton-quitter.png"), Width, Height);
            root.Child = null;
        }
        booth.AllowClose();
        booth.Close();
    }

    /// <summary>
    /// Nouveautés 2.0.0 : le choix de l'événement au lancement (liste remplie, puis vide) et depuis l'admin ; les mises à
    /// jour dans l'admin (à jour, nouvelle version pour une copie installée, puis pour une copie décompressée).
    /// </summary>
    private static async Task SnapVersion20(AppServices services, KioskViewModel kiosk, string folder, Action<string> note)
    {
        await SnapUpdates(services, kiosk, folder, note);

        // Un deuxième événement, avec la personnalisation du premier, pour que la liste montre plusieurs lignes.
        var other = services.Events.FindSame("Anniversaire de Léa", new DateTime(2026, 12, 5))
                    ?? services.Events.Create("Anniversaire de Léa", new DateTime(2026, 12, 5));
        CustomizationStore.CopyFrom(services.CurrentEvent.Folder, other.Folder);

        foreach (var (atStartup, events, name) in new[]
                 {
                     (true, services.Events, "24-choix-evenement-au-lancement"),
                     (false, services.Events, "24b-changer-evenement-depuis-l-admin"),
                     (true, new Core.Events.EventStore(Path.Combine(folder, "aucun-evenement")), "24c-choix-evenement-premier-lancement"),
                 })
        {
            var chooser = new EventChooserWindow(events, services.CurrentEvent.Folder, atStartup);
            chooser.LoadForCapture();
            await Task.Delay(300);
            if (chooser.Content is FrameworkElement content)
            {
                chooser.Content = null;
                Render(content, Path.Combine(folder, name + ".png"), 1080, 720);
            }
            chooser.Close();
        }
        note("Choix de l'événement : " + string.Join(" | ", services.Events.ListAll().Select(e => $"{e.Name} → {Path.GetFileName(e.Folder)}")));
        note("Dossier de l'événement en cours : " + string.Join(", ",
            Directory.GetFileSystemEntries(services.CurrentEvent.Folder).Select(p => Path.GetFileName(p))));
    }

    private static async Task SnapUpdates(AppServices services, KioskViewModel kiosk, string folder, Action<string> note)
    {
        var page = new Uri("https://github.com/loisgoncalves/LibreBooth/releases/tag/v2.1.0");
        var fake = new UpdateInfo(new Version(2, 1, 0), "v2.1.0",
            UpdateChecker.ShortNotes("**Exemple de nouveautés** : texte publié avec la version sur GitHub.\n\n- Première nouveauté\n- Deuxième nouveauté\n\n## Télécharger\n\n- fichiers"),
            page, new UpdateFile("LibreBooth-2.1.0-Installation.exe", new Uri(page, "LibreBooth-2.1.0-Installation.exe"), 66_477_360, null), null);
        foreach (var (update, installed, name) in new (UpdateInfo?, bool, string)[]
                 {
                     (null, true, "25-mises-a-jour-a-jour"),
                     (fake, true, "25b-mise-a-jour-disponible"),
                     (fake, false, "25c-mise-a-jour-copie-decompressee"),
                 })
        {
            AppServices.InstalledCopyForCapture = installed;
            services.ShowUpdateForCapture(update);
            var admin = new AdminWindow(services, kiosk);
            admin.LoadForCapture();
            admin.AdminTabs.SelectedItem = admin.SystemTab;
            await Task.Delay(400);
            if (admin.Content is FrameworkElement content)
            {
                admin.Content = null;
                Render(content, Path.Combine(folder, name + ".png"), 1600, 1000);
            }
            note($"Mises à jour ({name}) : {admin.UpdateStatusText.Text} | bouton : "
                 + (admin.InstallUpdateButton.Visibility == Visibility.Visible ? admin.InstallUpdateButton.Content : "aucun")
                 + $" | bandeau : {(admin.UpdateBanner.Visibility == Visibility.Visible ? admin.UpdateBannerText.Text : "caché")}");
            admin.CloseWithoutPrompt();
        }
        AppServices.InstalledCopyForCapture = null;
        services.ShowUpdateForCapture(null);
    }

    /// <summary>Petite fenêtre (code PIN, choix) dessinée à sa taille naturelle.</summary>
    private static void RenderToContent(FrameworkElement content, string path, Brush background, Brush foreground)
    {
        var root = new Border { Background = background, Child = content };
        TextElement.SetForeground(root, foreground);
        TextElement.SetFontFamily(root, new FontFamily("Segoe UI"));
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = root.DesiredSize;
        Render(root, path, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
        root.Child = null;
    }

    /// <summary>Montage complet de chaque thème sur les deux modèles, avec le nom de l'événement en cours.</summary>
    private static async Task SnapThemes(AppServices services, string folder, Action<string> note)
    {
        var themesFolder = Path.Combine(folder, "themes");
        Directory.CreateDirectory(themesFolder);
        var ev = services.CurrentEvent;
        foreach (var theme in ThemeCatalog.All)
        {
            var design = ThemeCatalog.CreateDesign(theme.Id);
            design.Phrase = "Merci d'être venus !";
            var context = new DesignContext(design, null);
            foreach (var template in services.Templates.LoadAll().Where(t => t.Layout.IsCustomizable))
            {
                var output = Path.Combine(themesFolder, $"{theme.Id}-{template.Id}.jpg");
                await Task.Run(() => TemplatePreview.WriteSample(services.Compositor, template, output, ev.Name, context, ev.Date));
            }
        }
        DesignGuide.WritePng(TemplateSeeder.CreatePostcard(), Path.Combine(themesFolder, "gabarit-10x15.png"));
        DesignGuide.WritePng(TemplateSeeder.CreateBookmark(), Path.Combine(themesFolder, "gabarit-marque-page.png"));
        note($"Thèmes : {ThemeCatalog.All.Count} × 2 montages et 2 gabarits dans {themesFolder}");
    }

    /// <summary>Reprend l'apparence d'un thème pour l'événement et l'applique aux écrans.</summary>
    private static void ApplyTheme(AppServices services, string themeId)
    {
        services.Customization.Design.CopyLookFrom(ThemeCatalog.CreateDesign(themeId));
        services.SaveCustomization();
        Theme.Apply(Application.Current, services.CurrentKioskPalette());
    }

    /// <summary>L'écran d'accueil avec les couleurs de chaque thème, puis avec une image de fond.</summary>
    private static async Task SnapKioskThemes(AppServices services, KioskViewModel kiosk, string folder, Action<string> note)
    {
        var screensFolder = Path.Combine(folder, "ecrans");
        Directory.CreateDirectory(screensFolder);
        foreach (var theme in ThemeCatalog.All)
        {
            ApplyTheme(services, theme.Id);
            kiosk.RefreshFromSettings();
            await Snap(kiosk, screensFolder, $"accueil-{theme.Id}");
            var palette = services.CurrentKioskPalette();
            note($"Écrans « {theme.Name} » : fond {palette.Background}, texte {palette.Foreground}, accent {palette.Accent} (texte {palette.AccentText}), police {palette.TitleFont}");
        }

        var photo = TemplatePreview.SamplePhotos(1)[0];
        CustomizationStore.ImportAttractBackground(services.CurrentEvent.Folder, services.Customization, photo);
        kiosk.RefreshFromSettings();
        await Snap(kiosk, screensFolder, "accueil-avec-image");
        CustomizationStore.RemoveAttractBackground(services.CurrentEvent.Folder, services.Customization);
        ApplyTheme(services, ThemeCatalog.DefaultThemeId);
        kiosk.RefreshFromSettings();
        note($"Écrans d'accueil : {ThemeCatalog.All.Count} thèmes et une image de fond dans {screensFolder}");
    }

    private static async Task SnapEditor(AppServices services, string folder)
    {
        var template = services.Templates.Find(TemplateSeeder.BookmarkId);
        if (template == null) return;
        var editor = new TemplateEditorWindow(services, template);
        await Task.Delay(300);
        if (editor.Content is not FrameworkElement content) return;
        editor.Content = null;
        Render(content, Path.Combine(folder, "16-editeur-modele.png"), 1600, 1000);
        editor.Close();
    }

    private static void Render(FrameworkElement element, string path, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
