using LibreBooth.App.Hardware;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Util;
using Serilog;

namespace LibreBooth.App.Hardware.Printing;

/// <summary>
/// Sonde DNP exécutée dans un processus séparé (LibreBooth.exe --dnp-probe). Elle charge cspstat.dll,
/// lit l'état et le compteur de tirages, écrit le résultat en JSON puis s'arrête. Seules des fonctions de
/// LECTURE sont appelées : jamais de mise à jour de firmware ni d'écriture de données couleur.
/// </summary>
internal static class DnpProbe
{
    private const string Dll = "cspstat.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int GetStatus(int portNumber);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int GetMediaCounter(int portNumber);

    /// <summary>
    /// Inventaire des imprimantes DNP. D'après le code de cspstat.dll (fin en « ret 8 ») : deux paramètres,
    /// une zone de texte à remplir et sa taille ; renvoie le nombre d'imprimantes trouvées.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, EntryPoint = "GetPrinterPortNum")]
    private static extern int GetPrinterPortNumRaw(IntPtr buffer, int bufferLength);

    // Signatures relevées dans le code de cspstat.dll (fins en « ret 4 » : 1 paramètre ; « ret 8 » : 2 paramètres).
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int GetMediaClass(int portNumber);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int GetInitialMediaCount(int portNumber);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int GetMediaCounterH(int portNumber);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int GetPQTY(int portNumber);

    /// <summary>Type de papier : la réponse de l'imprimante est recopiée dans <paramref name="buffer"/> (4 096 octets prévus).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, EntryPoint = "GetMedia")]
    private static extern int GetMediaRaw(int portNumber, IntPtr buffer);

    // Lectures seulement (« ret 8 » : port + zone de texte ; « ret 4 » : port).
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, EntryPoint = "GetFirmwVersion")]
    private static extern int GetFirmwVersionRaw(int portNumber, IntPtr buffer);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, EntryPoint = "GetRewindMode")]
    private static extern int GetRewindModeRaw(int portNumber);

    /// <summary>Firmware et mode de rembobinage lus dans l'imprimante (exploration seulement).</summary>
    private static string ReadCutterInfo(int port)
    {
        var parts = new List<string>();
        var buffers = Enumerable.Range(0, 3).Select(_ => Marshal.AllocHGlobal(4096)).ToArray();
        try
        {
            void Clear()
            {
                foreach (var buffer in buffers)
                {
                    for (var offset = 0; offset < 4096; offset += 4) Marshal.WriteInt32(buffer, offset, 0);
                }
            }

            string Text(IntPtr buffer) => Marshal.PtrToStringAnsi(buffer)?.Trim() ?? "";

            Clear();
            try { parts.Add($"GetFirmwVersion = {GetFirmwVersionRaw(port, buffers[0])} « {Text(buffers[0])} »"); }
            catch (Exception ex) { parts.Add("GetFirmwVersion : " + ex.GetType().Name); }
            try { parts.Add($"GetRewindMode = {GetRewindModeRaw(port)}"); }
            catch (Exception ex) { parts.Add("GetRewindMode : " + ex.GetType().Name); }
        }
        finally
        {
            foreach (var buffer in buffers) Marshal.FreeHGlobal(buffer);
        }
        return string.Join(" ; ", parts);
    }

    private static string ReadMedia(int port)
    {
        var buffer = Marshal.AllocHGlobal(4096);
        try
        {
            for (var offset = 0; offset < 4096; offset += 4) Marshal.WriteInt32(buffer, offset, 0);
            var result = GetMediaRaw(port, buffer);
            return result < 0 ? $"(erreur {result})" : Marshal.PtrToStringAnsi(buffer)?.Trim() ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Exploration (LibreBooth.exe --dnp-explore &lt;fichier&gt;) : appelle GetPrinterPortNum avec une grande zone
    /// mémoire vide et note ce que la DLL y écrit, puis l'état de chaque port. Sert à adapter LibreBooth à un modèle DNP.
    /// </summary>
    public static int Explore(string outputFile)
    {
        var report = new System.Text.StringBuilder();
        const int size = 8192;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var offset = 0; offset < size; offset += 4) Marshal.WriteInt32(buffer, offset, 0);
            var result = GetPrinterPortNumRaw(buffer, size - 1);
            report.AppendLine($"GetPrinterPortNum -> {result} (0x{result:X8})");
            report.Append("Zone mémoire (entiers) :");
            for (var i = 0; i < 64; i++)
            {
                if (i % 8 == 0) report.AppendLine().Append($"  +{i * 4,4}:");
                report.Append($" {Marshal.ReadInt32(buffer, i * 4):X8}");
            }
            report.AppendLine();
            var bytes = new byte[512];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            var text = new string(bytes.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
            report.AppendLine("Zone mémoire (texte) : " + text.TrimEnd('.'));
        }
        catch (Exception ex)
        {
            report.AppendLine("GetPrinterPortNum : " + ex.GetType().Name + " – " + ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        for (var port = 0; port < 12; port++)
        {
            try
            {
                var status = GetStatus(port);
                var media = GetMediaCounter(port);
                report.AppendLine($"Port {port} : GetStatus = 0x{status:X8}, GetMediaCounter = {media}");
                if (status < 0) continue;
                report.AppendLine($"  GetMedia = « {ReadMedia(port)} », GetMediaClass = {GetMediaClass(port)}, GetInitialMediaCount = {GetInitialMediaCount(port)}, "
                                  + $"GetMediaCounterH = {GetMediaCounterH(port)}, GetPQTY = {GetPQTY(port)}");
                report.AppendLine("  " + ReadCutterInfo(port));
            }
            catch (Exception ex)
            {
                report.AppendLine($"Port {port} : {ex.GetType().Name}");
            }
        }

        File.WriteAllText(outputFile, report.ToString());
        return 0;
    }

    public static int Run()
    {
        var result = new DnpProbeResult();
        try
        {
            // La DLL ne voit les imprimantes qu'après cet inventaire (vérifié sur une DS620) ;
            // la zone mémoire, large et vide, reçoit les informations qu'elle y écrit.
            var inventory = Marshal.AllocHGlobal(4096);
            try
            {
                for (var offset = 0; offset < 4096; offset += 4) Marshal.WriteInt32(inventory, offset, 0);
                result.PrinterCount = GetPrinterPortNumRaw(inventory, 4095);
            }
            finally
            {
                Marshal.FreeHGlobal(inventory);
            }

            for (var port = 0; port < Math.Clamp(result.PrinterCount, 4, 8); port++)
            {
                var status = GetStatus(port);
                if (DnpStatusCodes.IsKnown(status))
                {
                    result.Found = true;
                    result.Port = port;
                    result.Status = status;
                    result.Remaining = GetMediaCounter(port);
                    // Informations complémentaires : si l'une échoue (ancienne DLL), le statut principal reste valable.
                    try
                    {
                        result.RemainingHalf = GetMediaCounterH(port);
                        result.InitialCount = GetInitialMediaCount(port);
                        result.Media = ReadMedia(port);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        // Fonctions absentes de cette version de cspstat.dll.
                    }
                    break;
                }
                result.Tried.Add($"{port}:0x{status:X8}");
            }
            if (!result.Found) result.Error = "Aucune imprimante DNP n'a répondu (allumée et branchée en USB ?).";
        }
        catch (DllNotFoundException)
        {
            result.Error = "cspstat.dll introuvable à côté de LibreBooth.exe.";
        }
        catch (EntryPointNotFoundException)
        {
            result.Error = "Version de cspstat.dll incompatible.";
        }
        catch (Exception ex)
        {
            result.Error = "Lecture du statut DNP impossible : " + ex.Message;
        }

        try
        {
            Console.Out.Write(JsonSerializer.Serialize(result));
            Console.Out.Flush();
        }
        catch
        {
            return 3;
        }
        return 0;
    }
}

/// <summary>Lance la sonde DNP et traduit sa réponse pour l'admin et l'écran d'accueil.</summary>
internal sealed class DnpStatusProvider : IPrinterStatusProvider
{
    private readonly ILogger _log = Log.ForContext<DnpStatusProvider>();
    private string _lastLogged = "";

    public async Task<PrinterStatusInfo> QueryAsync(CancellationToken cancellationToken = default)
    {
        if (NativeDrivers.Find(NativeDrivers.DnpStatusFile) == null)
        {
            return PrinterStatusInfo.Unknown("cspstat.dll absent : statut DNP indisponible", "DNP");
        }

        var executable = Environment.ProcessPath;
        if (executable == null) return PrinterStatusInfo.Unknown("Statut DNP indisponible", "DNP");

        try
        {
            var start = new ProcessStartInfo(executable, "--dnp-probe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            // La sonde cherche cspstat.dll au même endroit que LibreBooth (dossier « Pilotes » compris).
            start.Environment[NativeDrivers.FolderVariable] = NativeDrivers.UserFolder;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Sonde DNP non lancée");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Déjà terminé.
                }
                return PrinterStatusInfo.Unknown("La lecture du statut DNP ne répond pas", "DNP");
            }

            var json = await output.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            {
                LogOnce($"Sonde DNP terminée avec le code {process.ExitCode}");
                return PrinterStatusInfo.Unknown($"Statut DNP indisponible (code {process.ExitCode})", "DNP");
            }

            var result = JsonSerializer.Deserialize<DnpProbeResult>(json, JsonDefaults.Options);
            if (result == null) return PrinterStatusInfo.Unknown("Réponse DNP illisible", "DNP");
            if (!result.Found)
            {
                LogOnce($"DNP non détectée : {result.Error} ({string.Join(", ", result.Tried)})");
                return PrinterStatusInfo.Unknown(result.Error ?? "Aucune imprimante DNP détectée", "DNP");
            }

            var (condition, message) = DnpStatusCodes.Describe(result.Status);
            var remaining = DnpStatusCodes.RemainingPostcards(result);
            var (mediaName, twoPerSheet) = DnpStatusCodes.DescribeMedia(result.Media);
            var media = result.Media == null
                ? ""
                : twoPerSheet
                    ? $", papier {mediaName} : {result.Remaining} feuilles = {remaining} tirages 10x15 (rouleau de {result.InitialCount})"
                    : $", papier {mediaName} (rouleau de {result.InitialCount})";
            var source = $"DNP (port {result.Port}, code 0x{result.Status:X8}{media})";
            LogOnce($"DNP port {result.Port} : {message} (code 0x{result.Status:X8}{media}), tirages 10x15 restants : {remaining?.ToString() ?? "?"}");
            return new PrinterStatusInfo(condition, message, remaining, source, DateTime.Now);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Lecture du statut DNP impossible");
            return PrinterStatusInfo.Unknown("Statut DNP indisponible : " + ex.Message, "DNP");
        }
    }

    private void LogOnce(string message)
    {
        if (message == _lastLogged) return;
        _lastLogged = message;
        _log.Information("{Message}", message);
    }
}
