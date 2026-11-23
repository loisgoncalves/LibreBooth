using LibreBooth.App.Hardware.Printing;
using LibreBooth.Core.Hardware;
using Serilog;

namespace LibreBooth.App.Services;

/// <summary>Bilan de l'imprimante affiché à l'opérateur et utilisé pour autoriser (ou non) l'impression.</summary>
public sealed record PrinterHealth(
    bool CanPrint,
    PrinterCondition Condition,
    string Message,
    int? RemainingPrints,
    bool LowMedia,
    string Details,
    DateTime CheckedAt);

/// <summary>
/// Surveille l'imprimante toutes les 30 secondes (et juste après chaque impression) :
/// file d'impression Windows + statut DNP (cspstat.dll). Sans statut DNP, le compteur logiciel prend le relais.
/// </summary>
public sealed class PrinterMonitor : IDisposable
{
    private readonly ILogger _log = Log.ForContext<PrinterMonitor>();
    private readonly AppServices _services;
    private readonly DnpStatusProvider _dnp = new();
    private Timer? _timer;
    private int _running;
    private string _lastMessage = "";

    public PrinterMonitor(AppServices services)
    {
        _services = services;
    }

    public PrinterHealth Current { get; private set; } =
        new(true, PrinterCondition.Unknown, "Vérification de l'imprimante…", null, false, "", DateTime.Now);

    public event EventHandler? Changed;

    public void Start() => _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));

    public void RefreshSoon(TimeSpan? delay = null) =>
        _ = Task.Delay(delay ?? TimeSpan.FromSeconds(5)).ContinueWith(_ => RefreshAsync(), TaskScheduler.Default);

    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            Current = await ComputeAsync().ConfigureAwait(false);
            if (Current.Message != _lastMessage)
            {
                _lastMessage = Current.Message;
                _log.Information("Imprimante : {Message} (impression possible : {CanPrint}, restant : {Remaining})",
                    Current.Message, Current.CanPrint, Current.RemainingPrints?.ToString() ?? "?");
            }
            await _services.Dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Vérification de l'imprimante impossible");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<PrinterHealth> ComputeAsync()
    {
        var settings = _services.Settings.Print;
        var printerName = _services.EffectivePrinterName;
        if (!settings.Enabled)
        {
            return new PrinterHealth(false, PrinterCondition.Unknown, "Impression désactivée", null, false, "", DateTime.Now);
        }
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return new PrinterHealth(false, PrinterCondition.Error, "Aucune imprimante choisie (admin > Impression)", null, false, "", DateTime.Now);
        }

        var queue = await Task.Run(() => _services.Printer.GetQueueInfo(printerName)).ConfigureAwait(false);
        PrinterStatusInfo? dnp = null;
        if (settings.UseDnpStatus && !_services.Options.PrintToPdf)
        {
            dnp = await _dnp.QueryAsync().ConfigureAwait(false);
        }

        int? remaining = dnp?.RemainingPrints ?? Math.Max(0, settings.MediaCapacity - settings.PrintedSinceMediaChange);
        var low = remaining.HasValue && remaining.Value <= settings.LowMediaWarning;
        var source = dnp?.RemainingPrints != null ? "compteur DNP" : "compteur LibreBooth";
        var details = $"File Windows : {queue.Message}"
                      + (queue.PortName != null ? $" (port {queue.PortName})" : "")
                      + (dnp != null ? $" · DNP : {dnp.Message} {dnp.Source}" : "")
                      + $" · Tirages restants ({source}) : {remaining?.ToString() ?? "?"}";

        if (!queue.Exists)
        {
            return new PrinterHealth(false, PrinterCondition.Error, $"Imprimante introuvable : {printerName}", remaining, low, details, DateTime.Now);
        }
        if (dnp is { Condition: PrinterCondition.Error })
        {
            return new PrinterHealth(false, PrinterCondition.Error, dnp.Message, remaining, low, details, DateTime.Now);
        }
        if (queue.HasError)
        {
            return new PrinterHealth(false, PrinterCondition.Error, queue.Message, remaining, low, details, DateTime.Now);
        }

        var condition = dnp?.Condition is PrinterCondition.Ready or PrinterCondition.Busy or PrinterCondition.Warning
            ? dnp.Condition
            : PrinterCondition.Ready;
        var message = dnp is { Condition: not PrinterCondition.Unknown } ? dnp.Message : queue.Message;
        return new PrinterHealth(true, condition, message, remaining, low, details, DateTime.Now);
    }

    public void Dispose() => _timer?.Dispose();
}
