using System.Diagnostics;
using LibreBooth.App.Hardware;
using LibreBooth.App.Hardware.Canon;
using LibreBooth.App.Hardware.Printing;
using LibreBooth.Core.Config;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Util;
using Serilog;

namespace LibreBooth.App.Diagnostics;

/// <summary>
/// Diagnostic du matériel (LibreBooth.exe --diagnostic [rapport.txt]) : pilote Canon, appareil, live view,
/// photo de test, imprimantes Windows et statut DNP. À lancer sur le PC du booth quand quelque chose coince,
/// LibreBooth étant fermé (un seul logiciel à la fois peut piloter l'appareil photo).
/// </summary>
internal static class HardwareDiagnostic
{
    /// <param name="toFiles">Les pages de test sont écrites par le pilote dans des fichiers .prn au lieu de sortir sur papier.</param>
    /// <param name="bookmarkPages">Si plus de 0 : seul le marque-page est imprimé, en autant de pages dans une même tâche.</param>
    public static async Task RunAsync(string reportPath, bool printTestPages = false, bool toFiles = false, int bookmarkPages = 0)
    {
        var lines = new List<string>();

        void Add(string line)
        {
            lines.Add(line);
            Log.Information("[diagnostic] {Line}", line);
        }

        try
        {
            Add($"Diagnostic LibreBooth {App.Version} – {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            Add($"Windows {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64" : "32")} bits) · LibreBooth en {(Environment.Is64BitProcess ? "64" : "32")} bits · .NET {Environment.Version}");
            Add($"Dossier du programme : {AppContext.BaseDirectory}");
            foreach (var dll in new[] { "EDSDK.dll", "EdsImage.dll", "cspstat.dll" })
            {
                var path = NativeDrivers.Find(dll);
                Add(path != null ? $"{dll} : présent (version {FileVersionInfo.GetVersionInfo(path).FileVersion}, {Path.GetDirectoryName(path)})" : $"{dll} : ABSENT (à copier dans {NativeDrivers.UserFolder})");
            }

            Add("");
            Add("== Appareil photo Canon ==");
            await CheckCameraAsync(Add, reportPath);

            Add("");
            Add("== Imprimantes installées dans Windows ==");
            var printers = new WindowsPrinterService();
            var names = printers.GetInstalledPrinters();
            if (names.Count == 0) Add("Aucune imprimante installée.");
            foreach (var name in names)
            {
                var queue = printers.GetQueueInfo(name);
                Add($"- {name} : {queue.Message} (pilote : {queue.DriverName ?? "?"}, port : {queue.PortName ?? "?"}, travaux en file : {queue.JobCount})");
                if (!DriverProfileBuilder.LooksLikeDnp(name, queue.DriverName)) continue;
                try
                {
                    var profiles = DriverProfileBuilder.CreateDnpProfiles(name);
                    if (profiles.Count == 0) Add("  Réglages automatiques DNP : ce pilote ne propose pas la coupe 2 pouces.");
                    foreach (var profile in profiles)
                    {
                        Add($"  Réglages automatiques « {profile.Name} » : {profile.Description} – vérifiés ({profile.DevMode.Length} octets)");
                    }
                }
                catch (Exception ex)
                {
                    Add("  Réglages automatiques DNP : ÉCHEC – " + ex.Message);
                }
            }

            Add("");
            Add("== Statut DNP (cspstat.dll) ==");
            var dnp = await new DnpStatusProvider().QueryAsync();
            Add($"{dnp.Condition} – {dnp.Message} – tirages restants : {dnp.RemainingPrints?.ToString() ?? "inconnu"} {dnp.Source}");

            if (printTestPages)
            {
                Add("");
                Add(toFiles ? "== Pages de test écrites dans des fichiers (rien n'est imprimé) ==" : "== Pages de test imprimées ==");
                var dnpPrinter = names.FirstOrDefault(n => DriverProfileBuilder.LooksLikeDnp(n, printers.GetQueueInfo(n).DriverName));
                if (dnpPrinter == null) Add("Aucune imprimante DNP installée : rien n'a été imprimé.");
                else await PrintTestPagesAsync(Add, printers, dnpPrinter, reportPath, toFiles, bookmarkPages);
            }
        }
        catch (Exception ex)
        {
            Add("Diagnostic interrompu : " + ex);
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllLinesAsync(reportPath, lines);
        }
    }

    /// <summary>Imprime un 10x15 puis un marque-page (coupe 2 pouces) avec les réglages automatiques, et suit leur passage.</summary>
    private static async Task PrintTestPagesAsync(Action<string> add, WindowsPrinterService printers, string printer, string reportPath, bool toFiles, int bookmarkPages)
    {
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "pages-de-test");
        var templatesFolder = Path.Combine(folder, "modeles");
        Directory.CreateDirectory(templatesFolder);
        var library = new TemplateLibrary(templatesFolder);
        TemplateSeeder.SeedIfNeeded(library);
        var compositor = new LayoutCompositor();
        var profiles = DriverProfileBuilder.CreateDnpProfiles(printer).ToDictionary(p => p.Name);
        var statusProvider = new DnpStatusProvider();
        var before = (await statusProvider.QueryAsync()).RemainingPrints;

        var jobs = new List<(string Profile, string TemplateId, string Label)>();
        if (bookmarkPages > 0)
        {
            jobs.Add((PrintProfiles.Strips, TemplateSeeder.BookmarkId, "marque-page"));
        }
        else
        {
            jobs.Add((PrintProfiles.Postcard, TemplateSeeder.PostcardId, "10x15"));
            jobs.Add((PrintProfiles.Strips, TemplateSeeder.BookmarkId, "marque-page"));
            // Sans papier, on écrit aussi le marque-page avec la coupe standard pour comparer les deux fichiers.
            if (toFiles) jobs.Add((PrintProfiles.Postcard, TemplateSeeder.BookmarkId, "marque-page-coupe-standard"));
        }

        foreach (var (profileName, templateId, label) in jobs)
        {
            if (!profiles.TryGetValue(profileName, out var profile))
            {
                add($"« {profileName} » : réglages automatiques indisponibles, page non imprimée.");
                continue;
            }
            var template = library.Find(templateId)!;
            var image = Path.Combine(folder, $"test-{label}.jpg");
            TemplatePreview.WriteSample(compositor, template, image, "Test LibreBooth");
            var file = toFiles ? Path.Combine(folder, $"test-{label}.prn") : null;
            var watch = Stopwatch.StartNew();
            var pages = bookmarkPages > 0 ? bookmarkPages : 1;
            await printers.PrintAsync(new PrintJobRequest(image, printer, profile.DevMode, pages, $"LibreBooth test {label}", file));
            add($"« {label} » ({pages} page(s) dans la tâche) avec les réglages « {profileName} » ({profile.Description}) : envoyé au spouleur en {watch.ElapsedMilliseconds} ms"
                + (file != null ? $" → {file}" : ""));

            // On attend que la file se vide (ou qu'une erreur apparaisse) avant la page suivante.
            var deadline = DateTime.UtcNow.AddSeconds(120);
            PrinterQueueInfo queue;
            do
            {
                await Task.Delay(1000);
                queue = printers.GetQueueInfo(printer);
            }
            while (queue.JobCount > 0 && !queue.HasError && DateTime.UtcNow < deadline);
            add(queue.HasError
                ? $"  ⚠ Erreur signalée : {queue.Message}"
                : queue.JobCount == 0 ? $"  Sorti de la file Windows en {watch.Elapsed.TotalSeconds:F0} s" : "  Toujours dans la file après 2 minutes");
        }

        await Task.Delay(TimeSpan.FromSeconds(15));
        var after = await statusProvider.QueryAsync();
        add($"Compteur DNP : {before?.ToString() ?? "?"} avant, {after.RemainingPrints?.ToString() ?? "?"} après ({after.Message})");
    }

    private static async Task CheckCameraAsync(Action<string> add, string reportPath)
    {
        using var camera = new CanonCamera(() => new CameraSettings());
        camera.Start();

        // On laisse au pilote le temps de trouver l'appareil (la recherche est relancée toutes les 2 s).
        var deadline = DateTime.Now.AddSeconds(12);
        while (DateTime.Now < deadline && camera.State != CameraState.Ready && camera.State != CameraState.Error)
        {
            await Task.Delay(250);
        }
        add($"État : {camera.State} – {camera.StatusMessage}");
        if (camera.State != CameraState.Ready) return;

        add($"Modèle : {camera.Name}");
        add(camera.BatteryLevel switch
        {
            null => "Batterie : inconnue",
            -1 => "Alimentation : secteur",
            var level => $"Batterie : {level} %",
        });

        var frames = 0;
        long frameBytes = 0;
        void OnFrame(object? sender, LiveViewFrameEventArgs e)
        {
            Interlocked.Increment(ref frames);
            Interlocked.Add(ref frameBytes, e.Jpeg.Length);
        }

        camera.LiveViewFrame += OnFrame;
        try
        {
            // Comme pendant une vraie session : live view, photo pendant le live view, reprise du live view.
            var startWatch = Stopwatch.StartNew();
            await camera.StartLiveViewAsync();
            var firstDeadline = DateTime.UtcNow.AddSeconds(6);
            while (Volatile.Read(ref frames) == 0 && DateTime.UtcNow < firstDeadline) await Task.Delay(30);
            if (frames == 0)
            {
                add("Live view : aucune image reçue en 6 s");
                return;
            }
            var firstFrameMs = startWatch.ElapsedMilliseconds;
            var countAtStart = Volatile.Read(ref frames);
            var measure = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(4));
            var counted = Volatile.Read(ref frames) - countAtStart;
            var fps = counted / measure.Elapsed.TotalSeconds;
            add($"Live view : première image après {firstFrameMs} ms, puis {fps:F1} images par seconde (images de {Interlocked.Read(ref frameBytes) / Math.Max(1, frames) / 1024} Ko en moyenne)");

            var watch = Stopwatch.StartNew();
            var bytes = await camera.CaptureAsync(allowAutofocus: false, CancellationToken.None);
            var photo = Path.ChangeExtension(reportPath, ".jpg");
            await File.WriteAllBytesAsync(photo, bytes);
            add($"Photo pendant le live view : {bytes.Length / 1024} Ko reçus en {watch.ElapsedMilliseconds} ms → {photo}");

            var afterCapture = Volatile.Read(ref frames);
            await Task.Delay(TimeSpan.FromSeconds(3));
            var resumed = Volatile.Read(ref frames) - afterCapture;
            add(resumed > 0
                ? $"Live view après la photo : reprise OK ({resumed} images en 3 s)"
                : "Live view après la photo : NE REPREND PAS");
            await camera.StopLiveViewAsync();
        }
        catch (Exception ex)
        {
            add("Live view / photo : ÉCHEC – " + ex.Message);
        }
        finally
        {
            camera.LiveViewFrame -= OnFrame;
        }
    }
}
