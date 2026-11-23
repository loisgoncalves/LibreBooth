using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;

namespace LibreBooth.App.Hardware.Demo;

/// <summary>Fausse caméra : live view animé et photos générées, pour tout tester sans appareil.</summary>
internal sealed class DemoCamera : ICamera
{
    private Timer? _liveTimer;
    private int _frame;
    private int _shots;
    private int _rendering;
    private volatile bool _liveView;
    private volatile bool _disposed;

    public string Name => "Appareil de démonstration";
    public CameraState State { get; private set; } = CameraState.Connecting;
    public string StatusMessage { get; private set; } = "Démarrage du mode démo…";
    public int? BatteryLevel => -1;
    public bool IsLiveViewActive => _liveView;

    public event EventHandler<CameraStatusEventArgs>? StatusChanged;
    public event EventHandler<LiveViewFrameEventArgs>? LiveViewFrame;

    public void Start()
    {
        _ = Task.Delay(400).ContinueWith(_ =>
        {
            if (_disposed) return;
            State = CameraState.Ready;
            StatusMessage = "Mode démo : appareil simulé prêt";
            StatusChanged?.Invoke(this, new CameraStatusEventArgs(State, StatusMessage));
        }, TaskScheduler.Default);
    }

    public Task StartLiveViewAsync()
    {
        _liveView = true;
        _liveTimer ??= new Timer(_ => RenderFrame(), null, 0, 50);
        return Task.CompletedTask;
    }

    public Task StopLiveViewAsync()
    {
        _liveView = false;
        _liveTimer?.Dispose();
        _liveTimer = null;
        return Task.CompletedTask;
    }

    private void RenderFrame()
    {
        if (!_liveView || Interlocked.Exchange(ref _rendering, 1) == 1) return;
        try
        {
            var phase = Interlocked.Increment(ref _frame) / 20.0;
            var jpeg = DemoImageFactory.RenderJpeg(960, 640, 1, "MODE DÉMO · " + DateTime.Now.ToString("HH:mm:ss"), 75, phase);
            LiveViewFrame?.Invoke(this, new LiveViewFrameEventArgs(jpeg));
        }
        catch
        {
            // Une image ratée du faux live view n'a aucune importance.
        }
        finally
        {
            Volatile.Write(ref _rendering, 0);
        }
    }

    public async Task<byte[]> CaptureAsync(bool allowAutofocus, CancellationToken cancellationToken)
    {
        if (State != CameraState.Ready) throw new CameraException("L'appareil de démonstration n'est pas prêt.");
        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        var shot = Interlocked.Increment(ref _shots);
        return await Task.Run(() => DemoImageFactory.RenderJpeg(3456, 2304, shot, $"Photo démo n°{shot}", 90, shot), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposed = true;
        StopLiveViewAsync();
    }
}
