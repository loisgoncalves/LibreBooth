namespace LibreBooth.Core.Hardware;

/// <param name="DriverSettings">Réglages du pilote mémorisés (DEVMODE), ou null pour les réglages par défaut.</param>
/// <param name="PrintToFilePath">Mode démo : fichier PDF produit par « Microsoft Print to PDF ».</param>
/// <param name="FitWholeImage">Image entière avec marges (mode démo) au lieu d'une impression sans bord.</param>
public sealed record PrintJobRequest(
    string ImagePath,
    string PrinterName,
    byte[]? DriverSettings,
    int Copies,
    string JobName,
    string? PrintToFilePath = null,
    bool FitWholeImage = false);

public sealed record PrinterQueueInfo(bool Exists, bool HasError, string Message, int JobCount, string? PortName = null, string? DriverName = null);

public sealed class PrinterException : Exception
{
    public PrinterException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public interface IPrinterService
{
    IReadOnlyList<string> GetInstalledPrinters();
    Task PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default);
    PrinterQueueInfo GetQueueInfo(string printerName);
}

public enum PrinterCondition
{
    Unknown,
    Ready,
    Busy,
    Warning,
    Error,
}

public sealed record PrinterStatusInfo(PrinterCondition Condition, string Message, int? RemainingPrints, string Source, DateTime CheckedAt)
{
    public static PrinterStatusInfo Unknown(string message, string source = "") =>
        new(PrinterCondition.Unknown, message, null, source, DateTime.Now);
}

/// <summary>Lecture de l'état de l'imprimante (compteur de tirages, erreurs) : DNP via cspstat.dll.</summary>
public interface IPrinterStatusProvider
{
    Task<PrinterStatusInfo> QueryAsync(CancellationToken cancellationToken = default);
}
