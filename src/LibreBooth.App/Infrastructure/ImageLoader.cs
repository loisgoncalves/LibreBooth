using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibreBooth.Core.Hardware;
using Serilog;

namespace LibreBooth.App.Infrastructure;

/// <summary>Chargement d'images pour l'affichage, sans verrouiller les fichiers et utilisable depuis n'importe quel thread.</summary>
internal static class ImageLoader
{
    private const string OrientationQuery = "/app1/ifd/{ushort=274}";

    public static BitmapImage? FromFile(string? path, int decodePixelWidth = 0, int decodePixelHeight = 0)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return FromBytes(File.ReadAllBytes(path), decodePixelWidth, decodePixelHeight);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Image illisible : {Path}", path);
            return null;
        }
    }

    public static BitmapImage FromBytes(byte[] bytes, int decodePixelWidth = 0, int decodePixelHeight = 0)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
        else if (decodePixelHeight > 0) image.DecodePixelHeight = decodePixelHeight;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Photo de l'appareil remise à l'endroit (rotation forcée, sinon orientation enregistrée par l'appareil).</summary>
    public static BitmapSource? FromFileOriented(string path, int decodePixelHeight, int forcedRotation)
    {
        var image = FromFile(path, decodePixelHeight: decodePixelHeight);
        if (image == null) return null;
        var angle = forcedRotation != 0 ? forcedRotation : ReadExifRotation(path);
        if (angle == 0) return image;
        var rotated = new TransformedBitmap(image, new RotateTransform(angle));
        rotated.Freeze();
        return rotated;
    }

    private static int ReadExifRotation(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery(OrientationQuery))
            {
                return Convert.ToInt32(metadata.GetQuery(OrientationQuery)) switch
                {
                    3 => 180,
                    6 => 90,
                    8 => 270,
                    _ => 0,
                };
            }
        }
        catch
        {
            // Pas d'orientation lisible : on garde l'image telle quelle.
        }
        return 0;
    }
}

/// <summary>
/// Décode les images du live view sans jamais prendre de retard : si une image arrive alors que la
/// précédente n'est pas encore affichée, elle est simplement ignorée.
/// </summary>
internal sealed class LiveViewDecoder : IDisposable
{
    private readonly ICamera _camera;
    private readonly Dispatcher _dispatcher;
    private readonly Action<BitmapSource> _onFrame;
    private int _busy;
    private volatile bool _attached;

    public LiveViewDecoder(ICamera camera, Dispatcher dispatcher, Action<BitmapSource> onFrame)
    {
        _camera = camera;
        _dispatcher = dispatcher;
        _onFrame = onFrame;
    }

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _camera.LiveViewFrame += OnFrame;
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _camera.LiveViewFrame -= OnFrame;
    }

    public void Dispose() => Detach();

    private void OnFrame(object? sender, LiveViewFrameEventArgs e)
    {
        if (!_attached || Interlocked.Exchange(ref _busy, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            BitmapImage image;
            try
            {
                image = ImageLoader.FromBytes(e.Jpeg);
            }
            catch
            {
                // Image du live view abîmée : on attend la suivante.
                Volatile.Write(ref _busy, 0);
                return;
            }

            _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                try
                {
                    if (_attached) _onFrame(image);
                }
                finally
                {
                    Volatile.Write(ref _busy, 0);
                }
            });
        });
    }
}
