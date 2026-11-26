using System.Runtime.InteropServices;
using LibreBooth.Core.Hardware;

namespace LibreBooth.App.Hardware.Printing;

/// <summary>Accès au spouleur d'impression de Windows : état de l'imprimante et des travaux en cours.</summary>
internal static class WinSpool
{
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetPrinter(IntPtr hPrinter, int level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumJobs(IntPtr hPrinter, int firstJob, int noJobs, int level, IntPtr pJob, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetJob(IntPtr hPrinter, uint jobId, int level, IntPtr pJob, uint command);

    private const uint JobControlDelete = 5;

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int DocumentProperties(IntPtr hwnd, IntPtr hPrinter, string pDeviceName, IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr pServerName;
        public IntPtr pPrinterName;
        public IntPtr pShareName;
        public IntPtr pPortName;
        public IntPtr pDriverName;
        public IntPtr pComment;
        public IntPtr pLocation;
        public IntPtr pDevMode;
        public IntPtr pSepFile;
        public IntPtr pPrintProcessor;
        public IntPtr pDatatype;
        public IntPtr pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobInfo1
    {
        public uint JobId;
        public IntPtr pPrinterName;
        public IntPtr pMachineName;
        public IntPtr pUserName;
        public IntPtr pDocument;
        public IntPtr pDatatype;
        public IntPtr pStatus;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint TotalPages;
        public uint PagesPrinted;
        public SystemTime Submitted;
    }

    private const uint AttributeWorkOffline = 0x00000400;

    private static readonly (uint Flag, string Message)[] PrinterErrors =
    {
        (0x00400000, "Le capot de l'imprimante est ouvert."),
        (0x00000010, "Plus de papier dans l'imprimante."),
        (0x00000008, "Bourrage papier."),
        (0x00000040, "Problème de papier."),
        (0x00000800, "Bac de sortie plein."),
        (0x00100000, "L'imprimante demande une intervention."),
        (0x00000080, "Imprimante hors ligne (éteinte ou câble USB débranché ?)."),
        (0x00001000, "Imprimante indisponible."),
        (0x00000002, "L'imprimante signale une erreur."),
        (0x00000001, "L'impression est en pause dans Windows."),
    };

    private const uint JobProblemFlags = 0x00000002 | 0x00000020 | 0x00000040 | 0x00000200 | 0x00000400;

    public static PrinterQueueInfo Query(string printerName)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            return new PrinterQueueInfo(false, true, $"Imprimante introuvable : {printerName}", 0);
        }

        try
        {
            GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return new PrinterQueueInfo(true, false, "État inconnu", 0);

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinter(handle, 2, buffer, needed, out _)) return new PrinterQueueInfo(true, false, "État inconnu", 0);
                var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
                var port = Marshal.PtrToStringUni(info.pPortName);
                var driver = Marshal.PtrToStringUni(info.pDriverName);

                foreach (var (flag, message) in PrinterErrors)
                {
                    if ((info.Status & flag) != 0) return new PrinterQueueInfo(true, true, message, (int)info.cJobs, port, driver);
                }
                if ((info.Attributes & AttributeWorkOffline) != 0)
                {
                    // Windows pose aussi cet attribut tout seul quand une imprimante USB est éteinte ou débranchée.
                    return new PrinterQueueInfo(true, true,
                        "Imprimante hors ligne : vérifiez qu'elle est allumée et branchée en USB.", (int)info.cJobs, port, driver);
                }

                var jobProblem = FirstJobProblem(handle);
                if (jobProblem != null) return new PrinterQueueInfo(true, true, jobProblem, (int)info.cJobs, port, driver);

                var state = info.cJobs > 0 ? $"Impression en cours ({info.cJobs} en file)" : "Prête";
                return new PrinterQueueInfo(true, false, state, (int)info.cJobs, port, driver);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>
    /// Supprime de la file Windows les travaux dont le nom vérifie <paramref name="match"/> (ceux de LibreBooth). Renvoie
    /// le nombre de travaux supprimés et ceux qui ont résisté (déjà partis vers l'imprimante, droits insuffisants…).
    /// </summary>
    public static (int Cancelled, int Failed) CancelJobs(string printerName, Func<string?, bool> match)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero)) return (0, 0);
        try
        {
            EnumJobs(handle, 0, 100, 1, IntPtr.Zero, 0, out var needed, out _);
            if (needed <= 0) return (0, 0);
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!EnumJobs(handle, 0, 100, 1, buffer, needed, out _, out var count)) return (0, 0);
                var size = Marshal.SizeOf<JobInfo1>();
                var cancelled = 0;
                var failed = 0;
                for (var i = 0; i < count; i++)
                {
                    var job = Marshal.PtrToStructure<JobInfo1>(buffer + i * size);
                    if (!match(Marshal.PtrToStringUni(job.pDocument))) continue;
                    if (SetJob(handle, job.JobId, 0, IntPtr.Zero, JobControlDelete)) cancelled++;
                    else failed++;
                }
                return (cancelled, failed);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    private static string? FirstJobProblem(IntPtr handle)
    {
        EnumJobs(handle, 0, 10, 1, IntPtr.Zero, 0, out var needed, out _);
        if (needed <= 0) return null;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumJobs(handle, 0, 10, 1, buffer, needed, out _, out var count)) return null;
            var size = Marshal.SizeOf<JobInfo1>();
            for (var i = 0; i < count; i++)
            {
                var job = Marshal.PtrToStructure<JobInfo1>(buffer + i * size);
                if ((job.Status & JobProblemFlags) == 0) continue;
                var text = Marshal.PtrToStringUni(job.pStatus);
                return string.IsNullOrWhiteSpace(text)
                    ? "Un travail d'impression est bloqué : vérifiez l'imprimante (papier, ruban, câble)."
                    : "Imprimante : " + text.Trim();
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
