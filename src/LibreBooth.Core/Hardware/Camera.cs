namespace LibreBooth.Core.Hardware;

public enum CameraState
{
    Disconnected,
    Connecting,
    Ready,
    Error,
}

public sealed class CameraStatusEventArgs : EventArgs
{
    public CameraStatusEventArgs(CameraState state, string message)
    {
        State = state;
        Message = message;
    }

    public CameraState State { get; }
    public string Message { get; }
}

public sealed class LiveViewFrameEventArgs : EventArgs
{
    public LiveViewFrameEventArgs(byte[] jpeg)
    {
        Jpeg = jpeg;
    }

    /// <summary>Image du live view au format JPEG.</summary>
    public byte[] Jpeg { get; }
}

/// <summary>Erreur d'appareil photo, avec un message lisible par l'opérateur.</summary>
public sealed class CameraException : Exception
{
    public CameraException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Un appareil photo : le Canon (par le pilote de Windows ou par le SDK Canon) ou la caméra de démonstration.</summary>
public interface ICamera : IDisposable
{
    string Name { get; }
    CameraState State { get; }
    string StatusMessage { get; }
    /// <summary>Batterie en %, -1 sur secteur, null si inconnue.</summary>
    int? BatteryLevel { get; }
    bool IsLiveViewActive { get; }

    /// <summary>Peut être déclenché depuis n'importe quel thread.</summary>
    event EventHandler<CameraStatusEventArgs>? StatusChanged;

    /// <summary>Déclenché depuis un thread interne, environ 20 fois par seconde pendant le live view.</summary>
    event EventHandler<LiveViewFrameEventArgs>? LiveViewFrame;

    /// <summary>Lance la recherche de l'appareil (puis la reconnexion automatique).</summary>
    void Start();

    Task StartLiveViewAsync();
    Task StopLiveViewAsync();

    /// <summary>Déclenche et renvoie la photo JPEG.</summary>
    Task<byte[]> CaptureAsync(bool allowAutofocus, CancellationToken cancellationToken);
}
