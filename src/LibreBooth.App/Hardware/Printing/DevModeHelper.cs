using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;
using LibreBooth.Core.Hardware;

namespace LibreBooth.App.Hardware.Printing;

/// <summary>
/// Réglages du pilote d'imprimante (structure DEVMODE de Windows) : on ouvre la fenêtre du pilote DNP,
/// on mémorise tout ce qu'il renvoie (format, découpe 2 pouces, finition…) et on le réapplique à chaque impression.
/// </summary>
internal static class DevModeHelper
{
    private const int DmOutBuffer = 2;
    private const int DmInPrompt = 4;
    private const int DmInBuffer = 8;
    private const int IdOk = 1;
    private const uint GmemMoveable = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>Ouvre la fenêtre de réglages du pilote. Renvoie les nouveaux réglages, ou null si annulé.</summary>
    public static byte[]? ShowDriverDialog(IntPtr owner, string printerName, byte[]? current)
    {
        if (!WinSpool.OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            throw new PrinterException($"Imprimante introuvable : {printerName}");
        }

        try
        {
            var size = WinSpool.DocumentProperties(owner, handle, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0) throw new PrinterException("Le pilote de l'imprimante ne répond pas.");

            var output = Marshal.AllocHGlobal(size);
            var input = IntPtr.Zero;
            try
            {
                var mode = DmOutBuffer | DmInPrompt;
                if (current != null && IsForPrinter(current, printerName))
                {
                    input = Marshal.AllocHGlobal(Math.Max(size, current.Length));
                    Marshal.Copy(current, 0, input, current.Length);
                    mode |= DmInBuffer;
                }

                var result = WinSpool.DocumentProperties(owner, handle, printerName, output, input, mode);
                return result == IdOk ? ReadDevMode(output, size) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(output);
                if (input != IntPtr.Zero) Marshal.FreeHGlobal(input);
            }
        }
        finally
        {
            WinSpool.ClosePrinter(handle);
        }
    }

    /// <summary>Le pilote vérifie des réglages mémorisés et corrige ce qui n'est plus valable (ex. après une mise à jour).</summary>
    public static byte[]? Normalize(string printerName, byte[] saved)
    {
        if (!IsForPrinter(saved, printerName)) return null;
        if (!WinSpool.OpenPrinter(printerName, out var handle, IntPtr.Zero)) return null;
        try
        {
            var size = WinSpool.DocumentProperties(IntPtr.Zero, handle, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0) return null;
            var output = Marshal.AllocHGlobal(size);
            var input = Marshal.AllocHGlobal(Math.Max(size, saved.Length));
            try
            {
                Marshal.Copy(saved, 0, input, saved.Length);
                var result = WinSpool.DocumentProperties(IntPtr.Zero, handle, printerName, output, input, DmInBuffer | DmOutBuffer);
                return result == IdOk ? ReadDevMode(output, size) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(output);
                Marshal.FreeHGlobal(input);
            }
        }
        finally
        {
            WinSpool.ClosePrinter(handle);
        }
    }

    /// <summary>Applique des réglages mémorisés à une impression .NET.</summary>
    public static bool Apply(PrinterSettings printer, PageSettings page, byte[] devMode)
    {
        var data = Normalize(printer.PrinterName, devMode);
        if (data == null) return false;

        var memory = GlobalAlloc(GmemMoveable, (UIntPtr)data.Length);
        if (memory == IntPtr.Zero) throw new OutOfMemoryException("Mémoire insuffisante pour les réglages d'impression.");
        try
        {
            var pointer = GlobalLock(memory);
            Marshal.Copy(data, 0, pointer, data.Length);
            GlobalUnlock(memory);
            printer.SetHdevmode(memory);
            page.SetHdevmode(memory);
            return true;
        }
        finally
        {
            GlobalFree(memory);
        }
    }

    /// <summary>Nom de l'imprimante inscrit dans les réglages (tronqué à 31 caractères par Windows).</summary>
    public static string DeviceName(byte[] devMode) =>
        devMode.Length >= 64 ? Encoding.Unicode.GetString(devMode, 0, 64).Split('\0')[0] : "";

    public static bool IsForPrinter(byte[] devMode, string printerName)
    {
        var name = DeviceName(devMode);
        return name.Length > 0 && printerName.StartsWith(name, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ReadDevMode(IntPtr pointer, int bufferSize)
    {
        // DEVMODEW : dmSize à l'octet 68, dmDriverExtra à l'octet 70.
        var size = (ushort)Marshal.ReadInt16(pointer, 68);
        var extra = (ushort)Marshal.ReadInt16(pointer, 70);
        var total = size + extra;
        if (total <= 0 || total > bufferSize) total = bufferSize;
        var bytes = new byte[total];
        Marshal.Copy(pointer, bytes, 0, total);
        return bytes;
    }
}
