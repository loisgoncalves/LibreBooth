using System.Runtime.InteropServices;

namespace LibreBooth.App.Hardware.Canon;

/// <summary>
/// Appels à EDSDK.dll (SDK Canon EOS 13.x, 32 bits, convention d'appel stdcall).
/// Seule la partie utile au photobooth est déclarée : connexion, live view, déclenchement, transfert.
/// </summary>
internal static class EdsdkNative
{
    private const string Dll = "EDSDK.dll";

    // Codes d'erreur utilisés dans le code (la liste complète est dans EdsdkErrors).
    public const uint EDS_ERR_OK = 0x00000000;
    public const uint EDS_ERR_NOT_SUPPORTED = 0x00000007;
    public const uint EDS_ERR_DEVICE_BUSY = 0x00000081;
    public const uint EDS_ERR_OBJECT_NOTREADY = 0x0000A102;

    // Propriétés
    public const uint PropID_ProductName = 0x00000002;
    public const uint PropID_BatteryLevel = 0x00000008;
    public const uint PropID_SaveTo = 0x0000000B;
    public const uint PropID_ImageQuality = 0x00000100;
    public const uint PropID_AEMode = 0x00000400;
    public const uint PropID_Evf_OutputDevice = 0x00000500;
    /// <summary>Fonction live view autorisée (1) ou non (0) dans le menu de l'appareil.</summary>
    public const uint PropID_Evf_Mode = 0x00000501;

    // Commandes
    public const uint CameraCommand_TakePicture = 0x00000000;
    public const uint CameraCommand_ExtendShutDownTimer = 0x00000001;
    public const uint CameraCommand_PressShutterButton = 0x00000004;

    public const int ShutterButton_OFF = 0x00000000;
    public const int ShutterButton_Completely = 0x00000003;
    public const int ShutterButton_Completely_NonAF = 0x00010003;

    public const uint SaveTo_Host = 2;
    public const uint SaveTo_Both = 3;

    public const uint EvfOutputDevice_TFT = 1;
    public const uint EvfOutputDevice_PC = 2;

    public const uint BatteryLevel_AC = 0xFFFFFFFF;

    // Événements
    public const uint PropertyEvent_All = 0x00000100;
    public const uint ObjectEvent_All = 0x00000200;
    public const uint ObjectEvent_DirItemRequestTransfer = 0x00000208;
    public const uint StateEvent_All = 0x00000300;
    public const uint StateEvent_Shutdown = 0x00000301;
    public const uint StateEvent_WillSoonShutDown = 0x00000303;
    public const uint StateEvent_CaptureError = 0x00000305;
    public const uint StateEvent_InternalError = 0x00000306;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct EdsDeviceInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szPortName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szDeviceDescription;

        public uint DeviceSubType;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EdsCapacity
    {
        public int NumberOfFreeClusters;
        public int BytesPerSector;
        public int Reset;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct EdsDirectoryItemInfo
    {
        public ulong Size;
        public int IsFolder;
        public uint GroupID;
        public uint Option;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szFileName;

        public uint Format;
        public uint DateTime;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint EdsObjectEventHandler(uint inEvent, IntPtr inRef, IntPtr inContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint EdsPropertyEventHandler(uint inEvent, uint inPropertyID, uint inParam, IntPtr inContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint EdsStateEventHandler(uint inEvent, uint inParameter, IntPtr inContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint EdsCameraAddedHandler(IntPtr inContext);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsInitializeSDK();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsTerminateSDK();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsRelease(IntPtr inRef);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsGetCameraList(out IntPtr outCameraListRef);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsGetChildCount(IntPtr inRef, out int outCount);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsGetChildAtIndex(IntPtr inRef, int inIndex, out IntPtr outRef);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsGetDeviceInfo(IntPtr inCameraRef, out EdsDeviceInfo outDeviceInfo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsOpenSession(IntPtr inCameraRef);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsCloseSession(IntPtr inCameraRef);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsSendCommand(IntPtr inCameraRef, uint inCommand, int inParam);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsSetCapacity(IntPtr inCameraRef, EdsCapacity inCapacity);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsGetPropertyData(IntPtr inRef, uint inPropertyID, int inParam, int inPropertySize, out uint outPropertyData);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsSetPropertyData(IntPtr inRef, uint inPropertyID, int inParam, int inPropertySize, ref uint inPropertyData);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsGetDirectoryItemInfo(IntPtr inDirItemRef, out EdsDirectoryItemInfo outDirItemInfo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsDownload(IntPtr inDirItemRef, ulong inReadSize, IntPtr outStream);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsDownloadComplete(IntPtr inDirItemRef);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsDownloadCancel(IntPtr inDirItemRef);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsCreateMemoryStream(ulong inBufferSize, out IntPtr outStream);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsGetPointer(IntPtr inStream, out IntPtr outPointer);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsGetLength(IntPtr inStream, out ulong outLength);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsCreateEvfImageRef(IntPtr inStreamRef, out IntPtr outEvfImageRef);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsDownloadEvfImage(IntPtr inCameraRef, IntPtr inEvfImageRef);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsSetCameraAddedHandler(EdsCameraAddedHandler inCameraAddedHandler, IntPtr inContext);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsSetObjectEventHandler(IntPtr inCameraRef, uint inEvent, EdsObjectEventHandler inObjectEventHandler, IntPtr inContext);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsSetPropertyEventHandler(IntPtr inCameraRef, uint inEvent, EdsPropertyEventHandler inPropertyEventHandler, IntPtr inContext);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsSetCameraStateEventHandler(IntPtr inCameraRef, uint inEvent, EdsStateEventHandler inStateEventHandler, IntPtr inContext);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint EdsGetEvent();
}
