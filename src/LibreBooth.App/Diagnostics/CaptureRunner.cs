using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibreBooth.App.Admin;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Kiosk;
using LibreBooth.App.Services;
using LibreBooth.Core.Config;
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
            await SnapVersion30(services, kiosk, folder, Note);

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

    /// <summary>Les pages de l'admin, dans l'ordre du menu, avec le nom de leur image.</summary>
    private static readonly (AdminPage Page, string Name)[] AdminPages =
    {
        (AdminPage.Home, "12-admin-accueil"), (AdminPage.Event, "13-admin-evenement"), (AdminPage.Customization, "14-admin-personnalisation"),
        (AdminPage.Camera, "16-admin-appareil"), (AdminPage.Print, "17-admin-impression"), (AdminPage.Screens, "18-admin-ecrans"),
        (AdminPage.System, "19-admin-systeme"),
    };

    private static async Task SnapAdmin(AppServices services, KioskViewModel kiosk, string folder)
    {
        // Chaque page en clair, en sombre, puis en clair avec la police d'icônes de Windows 10 (Segoe MDL2 Assets).
        // Une fenêtre par variante : dessinées hors écran, les pages gardent les couleurs et la police de leur création.
        try
        {
            foreach (var (appearance, windows10, variant) in new[]
                     {
                         (AdminAppearance.Light, false, ""), (AdminAppearance.Dark, false, "-sombre"), (AdminAppearance.Light, true, "-w10"),
                     })
            {
                AdminTheme.Apply(appearance);
                if (windows10) Application.Current.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
                else Application.Current.Resources.Remove("IconFont");

                // Comme au lancement de LibreBooth : la page Accueil et le bouton « Démarrer le photobooth ».
                var admin = new AdminWindow(services, kiosk);
                admin.LoadForCapture();
                await Task.Delay(500);
                if (admin.Content is FrameworkElement content)
                {
                    admin.Content = null;
                    foreach (var (page, name) in AdminPages)
                    {
                        admin.ShowPage(page);
                        // L'aperçu et les vignettes de la personnalisation se préparent en arrière-plan.
                        await Task.Delay(page == AdminPage.Customization ? 4000 : 400);
                        Render(content, Path.Combine(folder, name + variant + ".png"), 1600, 1000);
                    }
                    if (variant.Length == 0) await SnapCustomization(services, admin, content, folder);
                }
                admin.CloseWithoutPrompt();
            }
        }
        finally
        {
            Application.Current.Resources.Remove("IconFont");
            AdminTheme.Apply(services.Settings.AdminAppearance);
        }
    }

    /// <summary>Personnalisation : la suite des réglages, l'aperçu de l'accueil, les modèles, puis « Mes propres images ».</summary>
    private static async Task SnapCustomization(AppServices services, AdminWindow admin, FrameworkElement content, string folder)
    {
        const string customization = "14-admin-personnalisation";
        admin.ShowPage(AdminPage.Customization);
        foreach (var (offset, suffix) in new[] { (820.0, "b"), (1640.0, "c"), (2460.0, "d") })
        {
            admin.CustomizationView.ScrollSettings(offset);
            await Task.Delay(300);
            Render(content, Path.Combine(folder, customization + suffix + ".png"), 1600, 1000);
        }
        admin.CustomizationView.ShowAttractPreviewForCapture();
        await Task.Delay(1500);
        Render(content, Path.Combine(folder, customization + "e-apercu-accueil.png"), 1600, 1000);

        admin.CustomizationView.ShowTemplatesForCapture();
        await Task.Delay(1500);
        admin.CustomizationView.ShowTemplatesForCapture();
        Render(content, Path.Combine(folder, "15-admin-modeles.png"), 1600, 1000);

        // Mode « Mes propres images », avec le gabarit du 10x15 importé comme fond.
        var guide = Path.Combine(folder, "fond-essai-10x15.png");
        DesignGuide.WritePng(TemplateSeeder.CreatePostcard(), guide);
        CustomizationStore.ImportBackground(services.CurrentEvent.Folder, services.Customization, DesignStyles.Postcard, guide, 1800, 1200);
        admin.CustomizationView.LoadFromEvent();
        admin.CustomizationView.ShowFormatPreviewForCapture();
        admin.CustomizationView.ScrollSettings(0);
        await Task.Delay(3000);
        Render(content, Path.Combine(folder, customization + "f-mes-propres-images.png"), 1600, 1000);
        CustomizationStore.RemoveBackground(services.CurrentEvent.Folder, services.Customization, DesignStyles.Postcard);
        services.Customization.Design.OwnImages = false;
        services.SaveCustomization();
        admin.CustomizationView.LoadFromEvent();
        admin.CustomizationView.ScrollSettings(0);
    }

    /// <summary>
    /// Nouveautés 1.4.0 : vérification de l'installation, code PIN du lancement, choix « admin ou quitter » et
    /// accueil du photobooth avec son bouton rond.
    /// </summary>
    private static async Task SnapVersion14(AppServices services, KioskViewModel kiosk, string folder, Action<string> note)
    {
        await SnapVerification(services, folder, "", note);
        SnapPin(services, folder, "");
        SnapLeave(folder, "");

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
            await SnapChooser(events, services.CurrentEvent.Folder, atStartup, Path.Combine(folder, name + ".png"));
        }
        note("Choix de l'événement : " + string.Join(" | ", services.Events.ListAll().Select(e => $"{e.Name} → {Path.GetFileName(e.Folder)}")));
        note("Dossier de l'événement en cours : " + string.Join(", ",
            Directory.GetFileSystemEntries(services.CurrentEvent.Folder).Select(p => Path.GetFileName(p))));
    }

    /// <summary>
    /// Mises à jour, page Système (assez haute pour voir toute la partie « Mises à jour ») : à jour, nouvelle version,
    /// copie décompressée ; puis, depuis la 3.1.0, une pré-version proposée, une pré-version installée (avec l'accueil
    /// qui le rappelle), et les deux à la fois.
    /// </summary>
    private static async Task SnapUpdates(AppServices services, KioskViewModel kiosk, string folder, Action<string> note)
    {
        var newer = FakeRelease(new Version(3, 2, 0), prerelease: false, 89_067_000);
        var preview = FakeRelease(new Version(3, 2, 0), prerelease: true, 89_067_000);
        // Dernière version stable plus ancienne que celle-ci : la version en marche passe pour une pré-version.
        var stable = FakeRelease(new Version(3, 0, 0), prerelease: false, 89_049_000);
        foreach (var (update, returnTo, prereleases, installed, name) in new (UpdateInfo?, UpdateInfo?, bool, bool, string)[]
                 {
                     (null, null, false, true, "25-mises-a-jour-a-jour"),
                     (newer, null, false, true, "25b-mise-a-jour-disponible"),
                     (newer, null, false, false, "25c-mise-a-jour-copie-decompressee"),
                     (preview, null, true, true, "25d-pre-version-disponible"),
                     (preview, null, false, true, "25e-pre-version-non-demandee"),
                     (null, stable, false, true, "25f-pre-version-installee"),
                     (preview, stable, true, true, "25g-pre-version-installee-et-plus-recente"),
                 })
        {
            services.Settings.IncludePrereleases = prereleases;
            AppServices.InstalledCopyForCapture = installed;
            services.ShowUpdateForCapture(update, returnTo);
            var views = new List<(AdminPage Page, AdminAppearance Appearance, string Suffix)> { (AdminPage.System, AdminAppearance.Light, "") };
            if (returnTo != null) views.Add((AdminPage.Home, AdminAppearance.Light, "-accueil"));
            // Mise en garde et retour à la version stable : aussi en sombre.
            if (returnTo != null && update != null) views.Add((AdminPage.System, AdminAppearance.Dark, "-sombre"));
            foreach (var (page, appearance, suffix) in views)
            {
                AdminTheme.Apply(appearance);
                var admin = new AdminWindow(services, kiosk);
                admin.LoadForCapture();
                admin.ShowPage(page);
                await Task.Delay(400);
                var file = name + suffix;
                if (admin.Content is FrameworkElement content)
                {
                    admin.Content = null;
                    Render(content, Path.Combine(folder, file + ".png"), 1600, page == AdminPage.System ? 1900 : 1000);
                }
                if (page == AdminPage.System)
                {
                    note($"Mises à jour ({file}) : {admin.UpdateStatusText.Text} | bouton : "
                         + (admin.InstallUpdateButton.Visibility == Visibility.Visible ? admin.InstallUpdateButton.Content : "aucun")
                         + $" | mise en garde : {(admin.PrereleaseWarning.Visibility == Visibility.Visible ? admin.PrereleaseWarningText.Text : "aucune")}"
                         + $" | retour : {(admin.ReturnToStablePanel.Visibility == Visibility.Visible ? $"{admin.ReturnToStableText.Text} [{(admin.ReturnToStableButton.Visibility == Visibility.Visible ? admin.ReturnToStableButton.Content : "sans bouton")}]" : "aucun")}"
                         + $" | interrupteur pré-versions : {admin.PrereleasesSwitch.IsChecked}"
                         + $" | bandeau : {(admin.UpdateBanner.Visibility == Visibility.Visible ? admin.UpdateBannerText.Text : "caché")}");
                }
                else
                {
                    note($"Accueil ({file}) : {admin.ReadinessSummaryText.Text} | "
                         + string.Join(" | ", admin.ReadinessItemsForCapture.Select(i => $"{i.Title} ({i.Level}) : {i.Detail}")));
                }
                admin.CloseWithoutPrompt();
            }
        }
        AdminTheme.Apply(services.Settings.AdminAppearance);
        services.Settings.IncludePrereleases = false;
        AppServices.InstalledCopyForCapture = null;
        services.ShowUpdateForCapture(null);
    }

    /// <summary>Une version fictive publiée sur GitHub, pour les captures des mises à jour.</summary>
    private static UpdateInfo FakeRelease(Version version, bool prerelease, long size)
    {
        var number = version.ToString(3);
        var page = new Uri($"https://github.com/loisgoncalves/LibreBooth/releases/tag/v{number}");
        var installer = $"LibreBooth-{number}-Installation.exe";
        return new UpdateInfo(version, "v" + number,
            UpdateChecker.ShortNotes("**Exemple de nouveautés** : texte publié avec la version sur GitHub.\n\n- Première nouveauté\n- Deuxième nouveauté\n\n## Télécharger\n\n- fichiers"),
            page, new UpdateFile(installer, new Uri(page, installer), size, null), null, prerelease);
    }

    /// <summary>
    /// Nouveautés 3.0.0 : les confirmations LibreBooth (l'avertissement d'espace disque avant « Démarrer le
    /// photobooth », « Quitter LibreBooth ? ») en clair et en sombre, puis les autres fenêtres de l'admin en sombre.
    /// Une fenêtre neuve par apparence : dessinées hors écran, elles gardent les couleurs de leur création.
    /// Enfin, le disque presque plein.
    /// </summary>
    private static async Task SnapVersion30(AppServices services, KioskViewModel kiosk, string folder, Action<string> note)
    {
        await SnapDiskFull(services, kiosk, folder, note);
        try
        {
            foreach (var (appearance, suffix) in new[] { (AdminAppearance.Light, ""), (AdminAppearance.Dark, "-sombre") })
            {
                AdminTheme.Apply(appearance);
                SnapDialog(AdminWindow.LowDiskMessage(1536L * 1024 * 1024, "C:"), "Démarrer quand même", MessageBoxImage.Warning, false,
                    Path.Combine(folder, $"26-confirmation-espace-disque{suffix}.png"));
                SnapDialog("Quitter LibreBooth ?\n\nLe photobooth s'arrête : l'appareil photo et l'imprimante sont libérés.", "Quitter LibreBooth",
                    MessageBoxImage.Question, true, Path.Combine(folder, $"26b-confirmation-quitter{suffix}.png"));
                if (appearance != AdminAppearance.Dark) continue;

                await SnapVerification(services, folder, suffix, null);
                SnapPin(services, folder, suffix);
                SnapLeave(folder, suffix);
                await SnapChooser(services.Events, services.CurrentEvent.Folder, true, Path.Combine(folder, $"24-choix-evenement-au-lancement{suffix}.png"));
                await SnapEditor(services, folder, suffix);
            }
            note("Version 3.0.0 : confirmations en clair et en sombre ; vérification, code PIN, quitter, choix de l'événement et éditeur en sombre");
        }
        finally
        {
            AdminTheme.Apply(services.Settings.AdminAppearance);
        }
    }

    /// <summary>
    /// Disque presque plein (simulé : 250 Mo libres) : l'accueil du photobooth se met en pause et prévient l'animateur,
    /// un toucher ne lance pas de session, et l'accueil de l'admin le signale. Puis, la place « libérée », tout reprend.
    /// </summary>
    private static async Task SnapDiskFull(AppServices services, KioskViewModel kiosk, string folder, Action<string> note)
    {
        try
        {
            AppServices.FreeDiskBytesForCapture = 250L * 1024 * 1024;
            kiosk.ResumeAfterAdmin();
            await WaitUntil(() => kiosk.State == KioskState.Attract && !kiosk.Attract.IsReady, 5, "pause de l'accueil (disque presque plein)");
            kiosk.StartSession();
            await Task.Delay(300);
            note($"Disque presque plein : accueil prêt = {kiosk.Attract.IsReady}, avertissement « {kiosk.Attract.OperatorWarning} », "
                 + $"message « {kiosk.Attract.NotReadyMessage} », après un toucher : {kiosk.State}");
            if (kiosk.State != KioskState.Attract) throw new InvalidOperationException("Une session a démarré malgré le disque presque plein.");
            await Snap(kiosk, folder, "27-accueil-disque-presque-plein");

            var admin = new AdminWindow(services, kiosk);
            admin.LoadForCapture();
            await Task.Delay(500);
            if (admin.Content is FrameworkElement content)
            {
                admin.Content = null;
                Render(content, Path.Combine(folder, "27b-admin-accueil-disque-presque-plein.png"), 1600, 1000);
            }
            admin.CloseWithoutPrompt();
        }
        finally
        {
            AppServices.FreeDiskBytesForCapture = null;
            kiosk.ResumeAfterAdmin();
        }
        await WaitUntil(() => kiosk.Attract.IsReady, 5, "reprise des sessions une fois la place libérée");
        note($"Place libérée : accueil prêt = {kiosk.Attract.IsReady}, avertissement : {kiosk.Attract.OperatorWarning ?? "aucun"}");
    }

    private static async Task SnapVerification(AppServices services, string folder, string suffix, Action<string>? note)
    {
        var verification = new VerificationWindow(services, services.Paths);
        await verification.RunForCaptureAsync();
        if (verification.Content is FrameworkElement content)
        {
            verification.Content = null;
            Render(content, Path.Combine(folder, $"20-verification{suffix}.png"), 900, 760);
        }
        note?.Invoke("Vérification de l'installation : " + string.Join(" | ", verification.Results.Select(r => $"{r.Status} {r.Title}")));
        verification.Close();
    }

    private static void SnapPin(AppServices services, string folder, string suffix)
    {
        var pin = new PinWindow(services.Settings.AdminPin, "Code administrateur", "Code par défaut : 1234 (à changer dans la page Système de l'admin)", standalone: true);
        if (pin.Content is FrameworkElement content)
        {
            pin.Content = null;
            RenderNatural(content, Path.Combine(folder, $"21-code-pin-au-lancement{suffix}.png"));
        }
        pin.Close();
    }

    private static void SnapLeave(string folder, string suffix)
    {
        var leave = new LeaveBoothWindow();
        if (leave.Content is FrameworkElement content)
        {
            leave.Content = null;
            RenderNatural(content, Path.Combine(folder, $"22-quitter-le-photobooth{suffix}.png"));
        }
        leave.Close();
    }

    private static async Task SnapChooser(Core.Events.EventStore events, string currentFolder, bool atStartup, string path)
    {
        var chooser = new EventChooserWindow(events, currentFolder, atStartup);
        chooser.LoadForCapture();
        await Task.Delay(300);
        if (chooser.Content is FrameworkElement content)
        {
            chooser.Content = null;
            Render(content, path, 1080, 720);
        }
        chooser.Close();
    }

    private static void SnapDialog(string message, string confirmLabel, MessageBoxImage image, bool defaultConfirm, string path)
    {
        var dialog = AdminDialog.CreateForCapture(message, confirmLabel, "Annuler", image, defaultConfirm);
        if (dialog.Content is FrameworkElement content)
        {
            dialog.Content = null;
            RenderNatural(content, path, minWidth: 440);
        }
        dialog.Close();
    }

    /// <summary>Petite fenêtre (code PIN, choix, confirmation) dessinée à sa taille naturelle, avec ses propres couleurs.</summary>
    private static void RenderNatural(FrameworkElement content, string path, double minWidth = 0)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = content.DesiredSize;
        Render(content, path, (int)Math.Ceiling(Math.Max(minWidth, size.Width)), (int)Math.Ceiling(size.Height));
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

    private static async Task SnapEditor(AppServices services, string folder, string suffix = "")
    {
        var template = services.Templates.Find(TemplateSeeder.BookmarkId);
        if (template == null) return;
        var editor = new TemplateEditorWindow(services, template);
        await Task.Delay(300);
        if (editor.Content is FrameworkElement content)
        {
            editor.Content = null;
            Render(content, Path.Combine(folder, $"16-editeur-modele{suffix}.png"), 1600, 1000);
        }
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
