using System.Runtime.InteropServices;
using LibreBooth.Core.Hardware;

namespace LibreBooth.App.Hardware.Canon;

// Accès aux appareils photo par le pilote intégré à Windows (WPD, « Windows Portable Devices »), sans pilote de
// fabricant : les commandes PTP de l'appareil passent par les « extensions MTP » du pilote de Microsoft, présent sur
// Windows 10 et 11. Interfaces COM déclarées d'après PortableDeviceApi.h et PortableDeviceTypes.h (SDK Windows) ;
// l'ordre des méthodes doit rester celui de ces fichiers.
// Attention : un tableau passé à une interface COM sans [MarshalAs(UnmanagedType.LPArray)] devient un SAFEARRAY, ce
// qui fait planter le programme. La liste des appareils passe donc par un pointeur brut.

[StructLayout(LayoutKind.Sequential)]
internal struct WpdPropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public WpdPropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

/// <summary>PROPVARIANT réduit aux entiers sur 32 bits (24 octets : assez grand en 32 comme en 64 bits).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct WpdPropVariant
{
    public const ushort VtUInt32 = 19;

    [FieldOffset(0)] public ushort VarType;
    [FieldOffset(8)] public uint UInt32Value;
}

[ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceManager
{
    void GetDevices(IntPtr deviceIds, ref uint count);
    void RefreshDeviceList();
    void GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr buffer, ref uint count);
    void GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr buffer, ref uint count);
    void GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr buffer, ref uint count);
    void GetDeviceProperty(IntPtr deviceId, IntPtr propertyName, IntPtr data, IntPtr dataSize, IntPtr type);
    void GetPrivateDevices(IntPtr deviceIds, IntPtr count);
}

[ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevice
{
    void Open([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IPortableDeviceValues clientInfo);
    void SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
    void Content(out IntPtr content);
    void Capabilities(out IntPtr capabilities);
    void Cancel();
    void Close();
    void Advise(uint flags, IntPtr callback, IntPtr parameters, out IntPtr cookie);
    void Unadvise(IntPtr cookie);
    void GetPnPDeviceID(out IntPtr deviceId);
}

[ComImport, Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropVariantCollection
{
    void GetCount(out uint count);
    void GetAt(uint index, ref WpdPropVariant value);
    void Add(ref WpdPropVariant value);
    void GetVarType(out ushort varType);
    void ChangeType(ushort varType);
    void Clear();
    void RemoveAt(uint index);
}

[ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValues
{
    void GetCount(out uint count);
    void GetAt(uint index, ref WpdPropertyKey key, ref WpdPropVariant value);
    void SetValue(ref WpdPropertyKey key, ref WpdPropVariant value);
    void GetValue(ref WpdPropertyKey key, out WpdPropVariant value);
    void SetStringValue(ref WpdPropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void GetStringValue(ref WpdPropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
    void SetUnsignedIntegerValue(ref WpdPropertyKey key, uint value);
    void GetUnsignedIntegerValue(ref WpdPropertyKey key, out uint value);
    void SetSignedIntegerValue(ref WpdPropertyKey key, int value);
    void GetSignedIntegerValue(ref WpdPropertyKey key, out int value);
    void SetUnsignedLargeIntegerValue(ref WpdPropertyKey key, ulong value);
    void GetUnsignedLargeIntegerValue(ref WpdPropertyKey key, out ulong value);
    void SetSignedLargeIntegerValue(ref WpdPropertyKey key, long value);
    void GetSignedLargeIntegerValue(ref WpdPropertyKey key, out long value);
    void SetFloatValue(ref WpdPropertyKey key, float value);
    void GetFloatValue(ref WpdPropertyKey key, out float value);
    void SetErrorValue(ref WpdPropertyKey key, int value);
    void GetErrorValue(ref WpdPropertyKey key, out int value);
    void SetKeyValue(ref WpdPropertyKey key, ref WpdPropertyKey value);
    void GetKeyValue(ref WpdPropertyKey key, out WpdPropertyKey value);
    void SetBoolValue(ref WpdPropertyKey key, int value);
    void GetBoolValue(ref WpdPropertyKey key, out int value);
    void SetIUnknownValue(ref WpdPropertyKey key, IntPtr value);
    void GetIUnknownValue(ref WpdPropertyKey key, out IntPtr value);
    void SetGuidValue(ref WpdPropertyKey key, ref Guid value);
    void GetGuidValue(ref WpdPropertyKey key, out Guid value);
    void SetBufferValue(ref WpdPropertyKey key, [MarshalAs(UnmanagedType.LPArray)] byte[] value, uint size);
    void GetBufferValue(ref WpdPropertyKey key, out IntPtr value, out uint size);
    void SetIPortableDeviceValuesValue(ref WpdPropertyKey key, IPortableDeviceValues value);
    void GetIPortableDeviceValuesValue(ref WpdPropertyKey key, out IPortableDeviceValues value);
    void SetIPortableDevicePropVariantCollectionValue(ref WpdPropertyKey key, IPortableDevicePropVariantCollection value);
    void GetIPortableDevicePropVariantCollectionValue(ref WpdPropertyKey key, out IPortableDevicePropVariantCollection value);
    void SetIPortableDeviceKeyCollectionValue(ref WpdPropertyKey key, IntPtr value);
    void GetIPortableDeviceKeyCollectionValue(ref WpdPropertyKey key, out IntPtr value);
    void SetIPortableDeviceValuesCollectionValue(ref WpdPropertyKey key, IntPtr value);
    void GetIPortableDeviceValuesCollectionValue(ref WpdPropertyKey key, out IntPtr value);
    void RemoveValue(ref WpdPropertyKey key);
    void CopyValuesFromPropertyStore(IntPtr store);
    void CopyValuesToPropertyStore(IntPtr store);
    void Clear();
}

/// <summary>Clés des commandes WPD (WPD_CLIENT_*, WPD_PROPERTY_COMMON_*, et extensions MTP du pilote).</summary>
internal static class WpdKeys
{
    private static readonly Guid Client = new("204D9F0C-2292-4080-9F42-40664E70F859");
    private static readonly Guid Common = new("F0422A9C-5DC8-4440-B5BD-5DF28835658A");
    private static readonly Guid Mtp = new("4D545058-1A2E-4106-A357-771E0819FC56");

    public static readonly WpdPropertyKey ClientName = new(Client, 2);
    public static readonly WpdPropertyKey ClientMajorVersion = new(Client, 3);
    public static readonly WpdPropertyKey ClientMinorVersion = new(Client, 4);
    public static readonly WpdPropertyKey ClientRevision = new(Client, 5);
    public static readonly WpdPropertyKey ClientSecurityQualityOfService = new(Client, 8);
    public static readonly WpdPropertyKey ClientDesiredAccess = new(Client, 9);

    public static readonly WpdPropertyKey CommandCategory = new(Common, 1001);
    public static readonly WpdPropertyKey CommandId = new(Common, 1002);
    public static readonly WpdPropertyKey HResult = new(Common, 1003);

    public static readonly WpdPropertyKey GetSupportedVendorOpcodes = new(Mtp, 11);
    public static readonly WpdPropertyKey ExecuteWithoutData = new(Mtp, 12);
    public static readonly WpdPropertyKey ExecuteWithDataToRead = new(Mtp, 13);
    public static readonly WpdPropertyKey ExecuteWithDataToWrite = new(Mtp, 14);
    public static readonly WpdPropertyKey ReadData = new(Mtp, 15);
    public static readonly WpdPropertyKey WriteData = new(Mtp, 16);
    public static readonly WpdPropertyKey EndDataTransfer = new(Mtp, 17);

    public static readonly WpdPropertyKey OperationCode = new(Mtp, 1001);
    public static readonly WpdPropertyKey OperationParams = new(Mtp, 1002);
    public static readonly WpdPropertyKey ResponseCode = new(Mtp, 1003);
    public static readonly WpdPropertyKey VendorOpcodes = new(Mtp, 1005);
    public static readonly WpdPropertyKey TransferContext = new(Mtp, 1006);
    public static readonly WpdPropertyKey TransferTotalDataSize = new(Mtp, 1007);
    public static readonly WpdPropertyKey TransferNumBytesToRead = new(Mtp, 1008);
    public static readonly WpdPropertyKey TransferNumBytesRead = new(Mtp, 1009);
    public static readonly WpdPropertyKey TransferNumBytesToWrite = new(Mtp, 1010);
    public static readonly WpdPropertyKey TransferData = new(Mtp, 1012);
    public static readonly WpdPropertyKey OptimalTransferBufferSize = new(Mtp, 1013);
}

/// <summary>Un appareil vu par le pilote de Windows.</summary>
internal sealed record WpdDeviceInfo(string Id, string FriendlyName, string Manufacturer, string Description)
{
    public bool IsCanon => $"{Manufacturer} {FriendlyName} {Description}".Contains("Canon", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Réponse de l'appareil à une commande PTP.</summary>
internal readonly record struct PtpResponse(ushort Code)
{
    public bool IsOk => Code == CanonPtp.RcOk;
}

internal static class Wpd
{
    private static readonly Guid ClsidPortableDeviceManager = new("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
    private static readonly Guid ClsidPortableDeviceValues = new("0c15d503-d017-47ce-9016-7b3f978721cc");
    private static readonly Guid ClsidPropVariantCollection = new("08a99e2f-6d6d-4b80-af5a-baf2bcbe4cb9");
    public static readonly Guid ClsidPortableDeviceFtm = new("f7c0039a-4762-488a-b4b3-760ef9a1ba9b");

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref WpdPropVariant value);

    public static T Create<T>(Guid clsid) where T : class =>
        (T)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;

    public static IPortableDeviceValues NewValues() => Create<IPortableDeviceValues>(ClsidPortableDeviceValues);

    public static IPortableDevicePropVariantCollection NewCollection() =>
        Create<IPortableDevicePropVariantCollection>(ClsidPropVariantCollection);

    /// <summary>Libère tout de suite un objet COM (sinon il attend le ramasse-miettes : des centaines par seconde en live view).</summary>
    public static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    /// <summary>Identifiants des appareils branchés (téléphones, appareils photo…), liste rafraîchie.</summary>
    public static List<string> ListDeviceIds()
    {
        var manager = Create<IPortableDeviceManager>(ClsidPortableDeviceManager);
        try
        {
            return ListDeviceIds(manager);
        }
        finally
        {
            Release(manager);
        }
    }

    /// <summary>Appareils branchés, avec leur nom et leur fabricant.</summary>
    public static List<WpdDeviceInfo> ListDevices()
    {
        var manager = Create<IPortableDeviceManager>(ClsidPortableDeviceManager);
        try
        {
            return ListDeviceIds(manager)
                .Select(id => new WpdDeviceInfo(id,
                    ReadString(manager.GetDeviceFriendlyName, id),
                    ReadString(manager.GetDeviceManufacturer, id),
                    ReadString(manager.GetDeviceDescription, id)))
                .ToList();
        }
        finally
        {
            Release(manager);
        }
    }

    private static List<string> ListDeviceIds(IPortableDeviceManager manager)
    {
        manager.RefreshDeviceList();
        uint count = 0;
        manager.GetDevices(IntPtr.Zero, ref count);
        var ids = new List<string>();
        if (count == 0) return ids;

        var buffer = Marshal.AllocHGlobal(IntPtr.Size * (int)count);
        try
        {
            for (var i = 0; i < count; i++) Marshal.WriteIntPtr(buffer, i * IntPtr.Size, IntPtr.Zero);
            manager.GetDevices(buffer, ref count);
            for (var i = 0; i < count; i++)
            {
                var pointer = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                if (pointer == IntPtr.Zero) continue;
                ids.Add(Marshal.PtrToStringUni(pointer) ?? "");
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return ids;
    }

    private delegate void StringGetter(string deviceId, IntPtr buffer, ref uint count);

    private static string ReadString(StringGetter getter, string deviceId)
    {
        try
        {
            uint count = 0;
            getter(deviceId, IntPtr.Zero, ref count);
            if (count == 0) return "";
            var buffer = Marshal.AllocHGlobal((int)count * 2);
            try
            {
                getter(deviceId, buffer, ref count);
                return Marshal.PtrToStringUni(buffer) ?? "";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static uint[] ReadUInts(IPortableDevicePropVariantCollection collection)
    {
        collection.GetCount(out var count);
        var values = new uint[count];
        for (uint i = 0; i < count; i++)
        {
            var value = new WpdPropVariant();
            collection.GetAt(i, ref value);
            values[i] = value.UInt32Value;
            PropVariantClear(ref value);
        }
        return values;
    }

    public static void SetGuid(this IPortableDeviceValues v, WpdPropertyKey key, Guid value) => v.SetGuidValue(ref key, ref value);
    public static void SetUInt(this IPortableDeviceValues v, WpdPropertyKey key, uint value) => v.SetUnsignedIntegerValue(ref key, value);
    public static void SetULong(this IPortableDeviceValues v, WpdPropertyKey key, ulong value) => v.SetUnsignedLargeIntegerValue(ref key, value);
    public static void SetString(this IPortableDeviceValues v, WpdPropertyKey key, string value) => v.SetStringValue(ref key, value);

    public static uint GetUInt(this IPortableDeviceValues v, WpdPropertyKey key)
    {
        v.GetUnsignedIntegerValue(ref key, out var value);
        return value;
    }

    public static uint? TryGetUInt(this IPortableDeviceValues v, WpdPropertyKey key)
    {
        try
        {
            return v.GetUInt(key);
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static ulong GetULong(this IPortableDeviceValues v, WpdPropertyKey key)
    {
        v.GetUnsignedLargeIntegerValue(ref key, out var value);
        return value;
    }

    public static string GetString(this IPortableDeviceValues v, WpdPropertyKey key)
    {
        v.GetStringValue(ref key, out var value);
        return value;
    }
}

/// <summary>
/// Une connexion à un appareil PTP par le pilote de Windows, avec les trois formes de commande PTP : sans données,
/// avec données à lire, avec données à envoyer. Un seul fil à la fois doit s'en servir.
/// </summary>
internal sealed class WpdPtpDevice : IDisposable
{
    private const uint DefaultChunk = 256 * 1024;
    /// <summary>Au-delà, la réponse est jugée aberrante (une photo JPEG fait moins de 15 Mo, un RAW moins de 60).</summary>
    private const ulong MaxTransfer = 128UL * 1024 * 1024;
    private const uint SecurityImpersonation = 0x00020000;
    private const uint GenericReadWrite = 0xC0000000;

    private readonly IPortableDevice _device;
    // Tampons gardés d'une commande à l'autre : le live view lit environ 20 images de 300 Ko par seconde.
    private byte[] _data = new byte[1024 * 1024];
    private byte[] _chunk = new byte[DefaultChunk];
    private bool _closed;

    private WpdPtpDevice(IPortableDevice device, string id)
    {
        _device = device;
        Id = id;
    }

    public string Id { get; }

    public static WpdPtpDevice Open(string deviceId, string clientName)
    {
        var device = Wpd.Create<IPortableDevice>(Wpd.ClsidPortableDeviceFtm);
        var client = Wpd.NewValues();
        try
        {
            var version = typeof(WpdPtpDevice).Assembly.GetName().Version ?? new Version(1, 0, 0);
            client.SetString(WpdKeys.ClientName, clientName);
            client.SetUInt(WpdKeys.ClientMajorVersion, (uint)Math.Max(0, version.Major));
            client.SetUInt(WpdKeys.ClientMinorVersion, (uint)Math.Max(0, version.Minor));
            client.SetUInt(WpdKeys.ClientRevision, (uint)Math.Max(0, version.Build));
            client.SetUInt(WpdKeys.ClientSecurityQualityOfService, SecurityImpersonation);
            client.SetUInt(WpdKeys.ClientDesiredAccess, GenericReadWrite);
            device.Open(deviceId, client);
            return new WpdPtpDevice(device, deviceId);
        }
        catch
        {
            Wpd.Release(device);
            throw;
        }
        finally
        {
            Wpd.Release(client);
        }
    }

    /// <summary>Commandes propres au fabricant que le pilote laisse passer (pour un Canon EOS : 0x9101 à 0x91FF…).</summary>
    public HashSet<ushort> VendorOperations()
    {
        var results = Send(NewCommand(WpdKeys.GetSupportedVendorOpcodes));
        try
        {
            var key = WpdKeys.VendorOpcodes;
            results.GetIPortableDevicePropVariantCollectionValue(ref key, out var collection);
            try
            {
                return Wpd.ReadUInts(collection).Select(v => (ushort)v).ToHashSet();
            }
            finally
            {
                Wpd.Release(collection);
            }
        }
        finally
        {
            Wpd.Release(results);
        }
    }

    /// <summary>Commande sans données.</summary>
    public PtpResponse Execute(ushort operation, params uint[] parameters)
    {
        var results = Send(NewOperation(WpdKeys.ExecuteWithoutData, operation, parameters));
        try
        {
            return ReadResponse(results);
        }
        finally
        {
            Wpd.Release(results);
        }
    }

    /// <summary>
    /// Commande dont l'appareil renvoie des données. <paramref name="data"/> pointe dans un tampon réutilisé : il n'est
    /// valable que jusqu'à la commande suivante (le copier pour le garder).
    /// </summary>
    public PtpResponse ExecuteRead(ushort operation, out ReadOnlyMemory<byte> data, params uint[] parameters)
    {
        data = ReadOnlyMemory<byte>.Empty;
        string context;
        ulong total;
        uint chunk;
        var start = Send(NewOperation(WpdKeys.ExecuteWithDataToRead, operation, parameters));
        try
        {
            context = start.GetString(WpdKeys.TransferContext);
            total = start.GetULong(WpdKeys.TransferTotalDataSize);
            chunk = start.TryGetUInt(WpdKeys.OptimalTransferBufferSize) ?? 0;
        }
        finally
        {
            Wpd.Release(start);
        }

        var ended = false;
        try
        {
            if (total > MaxTransfer) throw new InvalidDataException($"Réponse de l'appareil trop grande ({total} octets).");
            var length = (int)total;
            if (_data.Length < length) _data = new byte[length];
            if (chunk == 0) chunk = DefaultChunk;
            var chunkLength = (int)Math.Min(chunk, (uint)Math.Max(1, length));
            if (_chunk.Length < chunkLength) _chunk = new byte[chunkLength];

            var offset = 0;
            while (offset < length)
            {
                var wanted = (uint)Math.Min(length - offset, chunkLength);
                var read = NewCommand(WpdKeys.ReadData);
                read.SetString(WpdKeys.TransferContext, context);
                read.SetUInt(WpdKeys.TransferNumBytesToRead, wanted);
                // Le pilote exige un tampon de la taille demandée ; son contenu est ignoré.
                var bufferKey = WpdKeys.TransferData;
                read.SetBufferValue(ref bufferKey, _chunk, wanted);
                var got = Send(read);
                int copied;
                try
                {
                    var count = got.GetUInt(WpdKeys.TransferNumBytesRead);
                    got.GetBufferValue(ref bufferKey, out var pointer, out var size);
                    try
                    {
                        copied = (int)Math.Min(Math.Min(count, size), (uint)(length - offset));
                        if (copied > 0) Marshal.Copy(pointer, _data, offset, copied);
                    }
                    finally
                    {
                        if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
                    }
                }
                finally
                {
                    Wpd.Release(got);
                }
                if (copied == 0) break;
                offset += copied;
            }

            ended = true;
            var end = EndTransfer(context);
            try
            {
                var response = ReadResponse(end);
                data = new ReadOnlyMemory<byte>(_data, 0, offset);
                return response;
            }
            finally
            {
                Wpd.Release(end);
            }
        }
        finally
        {
            if (!ended) TryEndTransfer(context);
        }
    }

    /// <summary>Commande qui envoie des données à l'appareil (un réglage, par exemple).</summary>
    public PtpResponse ExecuteWrite(ushort operation, byte[] payload, params uint[] parameters)
    {
        var values = NewOperation(WpdKeys.ExecuteWithDataToWrite, operation, parameters);
        string context;
        try
        {
            values.SetULong(WpdKeys.TransferTotalDataSize, (ulong)payload.Length);
        }
        catch
        {
            Wpd.Release(values);
            throw;
        }
        var start = Send(values);
        try
        {
            context = start.GetString(WpdKeys.TransferContext);
        }
        finally
        {
            Wpd.Release(start);
        }

        var ended = false;
        try
        {
            var write = NewCommand(WpdKeys.WriteData);
            write.SetString(WpdKeys.TransferContext, context);
            write.SetUInt(WpdKeys.TransferNumBytesToWrite, (uint)payload.Length);
            var bufferKey = WpdKeys.TransferData;
            write.SetBufferValue(ref bufferKey, payload, (uint)payload.Length);
            Wpd.Release(Send(write));
            ended = true;
            var end = EndTransfer(context);
            try
            {
                return ReadResponse(end);
            }
            finally
            {
                Wpd.Release(end);
            }
        }
        finally
        {
            if (!ended) TryEndTransfer(context);
        }
    }

    private IPortableDeviceValues EndTransfer(string context)
    {
        var end = NewCommand(WpdKeys.EndDataTransfer);
        end.SetString(WpdKeys.TransferContext, context);
        return Send(end);
    }

    private void TryEndTransfer(string context)
    {
        try
        {
            Wpd.Release(EndTransfer(context));
        }
        catch (Exception)
        {
            // L'erreur d'origine est plus utile que celle-ci.
        }
    }

    /// <summary>Envoie une commande au pilote (et libère ses paramètres) ; une erreur du pilote devient une COMException.</summary>
    private IPortableDeviceValues Send(IPortableDeviceValues parameters)
    {
        IPortableDeviceValues results;
        try
        {
            _device.SendCommand(0, parameters, out results);
        }
        finally
        {
            Wpd.Release(parameters);
        }

        int hr;
        try
        {
            var key = WpdKeys.HResult;
            results.GetErrorValue(ref key, out hr);
        }
        catch
        {
            Wpd.Release(results);
            throw;
        }
        if (hr >= 0) return results;
        Wpd.Release(results);
        throw new COMException($"Commande refusée par le pilote Windows (0x{hr:X8})", hr);
    }

    private static IPortableDeviceValues NewCommand(WpdPropertyKey command)
    {
        var values = Wpd.NewValues();
        values.SetGuid(WpdKeys.CommandCategory, command.FormatId);
        values.SetUInt(WpdKeys.CommandId, command.PropertyId);
        return values;
    }

    private static IPortableDeviceValues NewOperation(WpdPropertyKey command, ushort operation, uint[] parameters)
    {
        var values = NewCommand(command);
        var collection = Wpd.NewCollection();
        try
        {
            values.SetUInt(WpdKeys.OperationCode, operation);
            foreach (var parameter in parameters)
            {
                var value = new WpdPropVariant { VarType = WpdPropVariant.VtUInt32, UInt32Value = parameter };
                collection.Add(ref value);
            }
            var key = WpdKeys.OperationParams;
            values.SetIPortableDevicePropVariantCollectionValue(ref key, collection);
            return values;
        }
        catch
        {
            Wpd.Release(values);
            throw;
        }
        finally
        {
            // La liste des paramètres garde sa propre référence.
            Wpd.Release(collection);
        }
    }

    private static PtpResponse ReadResponse(IPortableDeviceValues results) => new((ushort)results.GetUInt(WpdKeys.ResponseCode));

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            _device.Close();
        }
        catch (Exception)
        {
            // Appareil déjà débranché.
        }
        Wpd.Release(_device);
    }
}
