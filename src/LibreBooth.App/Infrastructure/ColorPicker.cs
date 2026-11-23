using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LibreBooth.App.Infrastructure;

/// <summary>Fenêtre « Couleurs » de Windows (palette, nuancier et saisie RVB).</summary>
internal static class ColorPicker
{
    private const int CcRgbInit = 0x1;
    private const int CcFullOpen = 0x2;
    private const int CcAnyColor = 0x100;

    /// <summary>Couleurs personnalisées gardées d'une ouverture à l'autre.</summary>
    private static readonly int[] CustomColors = new int[16];

    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColorData
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public int RgbResult;
        public IntPtr CustomColors;
        public int Flags;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColorNative(ref ChooseColorData data);

    public static Color? Pick(Window? owner, Color initial)
    {
        var pinned = GCHandle.Alloc(CustomColors, GCHandleType.Pinned);
        try
        {
            var data = new ChooseColorData
            {
                StructSize = Marshal.SizeOf<ChooseColorData>(),
                Owner = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero,
                RgbResult = initial.R | (initial.G << 8) | (initial.B << 16),
                CustomColors = pinned.AddrOfPinnedObject(),
                Flags = CcRgbInit | CcFullOpen | CcAnyColor,
            };
            if (!ChooseColorNative(ref data)) return null;
            return Color.FromRgb((byte)(data.RgbResult & 0xFF), (byte)((data.RgbResult >> 8) & 0xFF), (byte)((data.RgbResult >> 16) & 0xFF));
        }
        finally
        {
            pinned.Free();
        }
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
