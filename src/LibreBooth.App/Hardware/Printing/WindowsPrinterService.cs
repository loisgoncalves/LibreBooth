using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using LibreBooth.App.Infrastructure;
using LibreBooth.Core.Hardware;
using Serilog;

namespace LibreBooth.App.Hardware.Printing;

/// <summary>
/// Impression par le pilote Windows de l'imprimante (DNP ou autre), avec les réglages du pilote mémorisés.
/// L'image remplit toute la page (sans bord) et pivote automatiquement si l'orientation de la page diffère.
/// </summary>
internal sealed class WindowsPrinterService : IPrinterService
{
    private readonly ILogger _log = Log.ForContext<WindowsPrinterService>();

    public IReadOnlyList<string> GetInstalledPrinters()
    {
        try
        {
            return PrinterSettings.InstalledPrinters.Cast<string>().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Liste des imprimantes indisponible");
            return Array.Empty<string>();
        }
    }

    public PrinterQueueInfo GetQueueInfo(string printerName)
    {
        try
        {
            return WinSpool.Query(printerName);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "État de l'imprimante illisible");
            return new PrinterQueueInfo(true, false, "État inconnu", 0);
        }
    }

    public Task PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StaTask.Run(() => Print(request), "LibreBooth impression");
    }

    private void Print(PrintJobRequest request)
    {
        using var image = LoadImage(request.ImagePath);
        using var document = new PrintDocument();
        document.DocumentName = request.JobName;
        document.PrintController = new StandardPrintController();
        document.PrinterSettings.PrinterName = request.PrinterName;
        if (!document.PrinterSettings.IsValid) throw new PrinterException($"Imprimante introuvable : {request.PrinterName}");

        var driverSettingsApplied = request.DriverSettings is { Length: > 0 } settings
                                    && DevModeHelper.Apply(document.PrinterSettings, document.DefaultPageSettings, settings);
        if (request.DriverSettings is { Length: > 0 } && !driverSettingsApplied)
        {
            _log.Warning("Réglages du pilote mémorisés non applicables à {Printer} : réglages par défaut utilisés", request.PrinterName);
        }
        if (!driverSettingsApplied)
        {
            document.DefaultPageSettings.Landscape = image.Width > image.Height;
        }

        document.PrinterSettings.Copies = 1;
        document.OriginAtMargins = false;
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        if (request.PrintToFilePath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.PrintToFilePath)!);
            document.PrinterSettings.PrintToFile = true;
            document.PrinterSettings.PrintFileName = request.PrintToFilePath;
        }

        var remaining = Math.Max(1, request.Copies);
        document.PrintPage += (_, e) =>
        {
            var graphics = e.Graphics!;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;

            // Toute la feuille, en 1/100e de pouce, depuis le coin du papier (et non de la zone imprimable).
            var page = e.PageBounds;
            var target = new RectangleF(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY, page.Width, page.Height);
            DrawImage(graphics, image, target, request.FitWholeImage);

            remaining--;
            e.HasMorePages = remaining > 0;
        };

        try
        {
            document.Print();
        }
        catch (InvalidPrinterException ex)
        {
            throw new PrinterException($"Imprimante introuvable ou non valide : {request.PrinterName}", ex);
        }
        catch (Win32Exception ex)
        {
            throw new PrinterException("Le pilote de l'imprimante a refusé l'impression : " + ex.Message, ex);
        }
        _log.Information("Travail envoyé au spouleur : {Job} ({Copies} page(s)) sur {Printer}", request.JobName, request.Copies, request.PrinterName);
    }

    private static Bitmap LoadImage(string path)
    {
        if (!File.Exists(path)) throw new PrinterException("Montage introuvable : " + path);
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var decoded = Image.FromStream(stream);
        return new Bitmap(decoded);
    }

    private static void DrawImage(Graphics graphics, Image image, RectangleF target, bool fitWholeImage)
    {
        var state = graphics.Save();
        try
        {
            var imageLandscape = image.Width >= image.Height;
            var pageLandscape = target.Width >= target.Height;
            var rotate = imageLandscape != pageLandscape && image.Width != image.Height;
            var area = target;
            if (rotate)
            {
                graphics.TranslateTransform(target.X + target.Width / 2f, target.Y + target.Height / 2f);
                graphics.RotateTransform(90);
                area = new RectangleF(-target.Height / 2f, -target.Width / 2f, target.Height, target.Width);
            }

            if (fitWholeImage)
            {
                var margin = Math.Min(area.Width, area.Height) * 0.03f;
                area = RectangleF.Inflate(area, -margin, -margin);
                var scale = Math.Min(area.Width / image.Width, area.Height / image.Height);
                var width = image.Width * scale;
                var height = image.Height * scale;
                var dest = new RectangleF(area.X + (area.Width - width) / 2f, area.Y + (area.Height - height) / 2f, width, height);
                graphics.DrawImage(image, dest);
            }
            else
            {
                // Remplissage sans bord : l'image couvre toute la feuille, le surplus est rogné.
                var scale = Math.Max(area.Width / image.Width, area.Height / image.Height);
                var sourceWidth = area.Width / scale;
                var sourceHeight = area.Height / scale;
                var source = new RectangleF((image.Width - sourceWidth) / 2f, (image.Height - sourceHeight) / 2f, sourceWidth, sourceHeight);
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                var destination = new[]
                {
                    new PointF(area.Left, area.Top),
                    new PointF(area.Right, area.Top),
                    new PointF(area.Left, area.Bottom),
                };
                graphics.DrawImage(image, destination, source, GraphicsUnit.Pixel, attributes);
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }
}
