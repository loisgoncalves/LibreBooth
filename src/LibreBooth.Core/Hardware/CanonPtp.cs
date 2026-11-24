using System.Buffers.Binary;
using System.Text;

namespace LibreBooth.Core.Hardware;

/// <summary>
/// Protocole PTP des Canon EOS, utilisé pour piloter l'appareil par le pilote intégré à Windows, sans le SDK Canon
/// (depuis la 3.1.0). Les codes viennent de gPhoto2 (libgphoto2, fichier ptp.h) et ont été essayés sur un EOS 1300D.
/// Ici, seulement les codes et la lecture des réponses de l'appareil, testables sans appareil : l'échange lui-même
/// se fait dans LibreBooth.App (CanonWindowsCamera).
/// </summary>
public static class CanonPtp
{
    // ---------------------------------------------------------------- Commandes

    public const ushort OpGetObject = 0x1009;
    public const ushort OpEosGetObject = 0x9104;
    public const ushort OpRemoteRelease = 0x910F;
    public const ushort OpSetDevicePropValueEx = 0x9110;
    public const ushort OpSetRemoteMode = 0x9114;
    public const ushort OpSetEventMode = 0x9115;
    public const ushort OpGetEvent = 0x9116;
    public const ushort OpTransferComplete = 0x9117;
    public const ushort OpPcHddCapacity = 0x911A;
    public const ushort OpKeepDeviceOn = 0x911D;
    public const ushort OpRemoteReleaseOn = 0x9128;
    public const ushort OpRemoteReleaseOff = 0x9129;
    public const ushort OpGetViewFinderData = 0x9153;
    /// <summary>Arrête une mise au point en cours (après un autofocus qui n'a pas abouti).</summary>
    public const ushort OpAfCancel = 0x9160;

    /// <summary>Déclencheur enfoncé à fond (mi-course puis complet d'un seul geste).</summary>
    public const uint ReleaseFull = 3;
    /// <summary>Second paramètre de RemoteReleaseOn : 0 avec autofocus, 1 sans.</summary>
    public const uint ReleaseWithAutofocus = 0;
    public const uint ReleaseWithoutAutofocus = 1;
    /// <summary>Paramètre de GetViewFinderData (taille de tampon demandée par gPhoto2).</summary>
    public const uint ViewFinderBuffer = 0x00200000;

    /// <summary>Commandes sans lesquelles l'appareil n'est pas un Canon EOS pilotable par LibreBooth.</summary>
    public static IReadOnlyList<ushort> RequiredOperations { get; } = new[]
    {
        OpSetRemoteMode, OpSetEventMode, OpGetEvent, OpSetDevicePropValueEx, OpTransferComplete, OpGetViewFinderData,
    };

    /// <summary>Un Canon EOS pilotable : les commandes indispensables, et un déclencheur (moderne ou ancien).</summary>
    public static bool IsUsableEos(IReadOnlyCollection<ushort> operations) =>
        RequiredOperations.All(operations.Contains) && (operations.Contains(OpRemoteReleaseOn) || operations.Contains(OpRemoteRelease));

    // ---------------------------------------------------------------- Réglages de l'appareil

    public const uint PropAeMode = 0xD105;
    public const uint PropFocusMode = 0xD108;
    public const uint PropBatteryPower = 0xD111;
    public const uint PropAvailableShots = 0xD11B;
    public const uint PropCaptureDestination = 0xD11C;
    public const uint PropImageFormat = 0xD120;
    public const uint PropEvfOutputDevice = 0xD1B0;
    public const uint PropEvfMode = 0xD1B1;

    /// <summary>Photos enregistrées sur la carte de l'appareil (réglage habituel hors LibreBooth).</summary>
    public const uint DestinationCard = 2;
    /// <summary>Photos envoyées au PC seulement (réglage de LibreBooth).</summary>
    public const uint DestinationPc = 4;
    /// <summary>Bit « PC » de la sortie du live view (le bit 1 est l'écran de l'appareil).</summary>
    public const uint EvfOutputPc = 2;

    // ---------------------------------------------------------------- Événements (réponse à GetEvent)

    public const uint EventRequestObjectTransfer = 0xC186;
    public const uint EventPropValueChanged = 0xC189;
    public const uint EventCameraStatus = 0xC18B;
    public const uint EventWillSoonShutdown = 0xC18D;

    /// <summary>Format PTP d'une photo JPEG (EXIF/JPEG).</summary>
    public const ushort FormatJpeg = 0x3801;

    // ---------------------------------------------------------------- Réponses

    public const ushort RcOk = 0x2001;
    public const ushort RcDeviceBusy = 0x2019;
    /// <summary>Réponse propre à Canon : « pas prêt » (image du live view pas encore là, appareil occupé…).</summary>
    public const ushort RcNotReady = 0xA102;

    /// <summary>Réponse de l'appareil en français, pour le journal et les messages.</summary>
    public static string Describe(ushort code) => code switch
    {
        RcOk => "OK",
        0x2002 => "erreur générale de l'appareil (0x2002)",
        0x2003 => "connexion à l'appareil non ouverte (0x2003)",
        0x2005 => "commande refusée par cet appareil (0x2005)",
        0x2009 => "photo introuvable (0x2009)",
        0x200C => "carte mémoire pleine (0x200C)",
        0x200F => "accès refusé (0x200F)",
        0x2013 => "carte mémoire absente (0x2013)",
        RcDeviceBusy => "appareil occupé (0x2019)",
        0x201D => "réglage refusé (0x201D)",
        RcNotReady => "appareil pas prêt (0xA102)",
        _ => $"réponse 0x{code:X4}",
    };

    // ---------------------------------------------------------------- Données envoyées et reçues

    /// <summary>Données de SetDevicePropValueEx pour un réglage sur 32 bits : [taille = 12][réglage][valeur].</summary>
    public static byte[] PropertyPayload(uint property, uint value)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), property);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), value);
        return payload;
    }

    /// <summary>Réponse de GetEvent : une suite de blocs [taille][type][contenu], terminée par un bloc de type 0.</summary>
    public static IReadOnlyList<CanonPtpEvent> ParseEvents(ReadOnlySpan<byte> data)
    {
        var events = new List<CanonPtpEvent>();
        for (var offset = 0; offset + 8 <= data.Length;)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            if (type == 0 || size < 8 || size > (uint)(data.Length - offset)) break;
            events.Add(new CanonPtpEvent(type, data.Slice(offset + 8, (int)size - 8).ToArray()));
            offset += (int)size;
        }
        return events;
    }

    /// <summary>
    /// Photo que l'appareil demande d'envoyer au PC (événement 0xC186), positions des champs comme dans gPhoto2
    /// (ptp-pack.c). Null pour un autre événement, dont l'ajout d'un fichier sur sa carte (0xC181).
    /// </summary>
    public static CanonPtpObject? ObjectFrom(CanonPtpEvent e) =>
        e.Type == EventRequestObjectTransfer ? new CanonPtpObject(e.Word(0), e.Half(4), e.Word(12), e.Text(20)) : null;

    /// <summary>
    /// Image JPEG d'une réponse de GetViewFinderData : des blocs [taille][type][contenu], l'image étant le bloc de
    /// type 1. Si la réponse est présentée autrement, on prend tout ce qui suit le début d'un JPEG.
    /// </summary>
    public static byte[]? ExtractLiveViewJpeg(ReadOnlySpan<byte> data)
    {
        for (var offset = 0; offset + 8 <= data.Length;)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            if (size < 8 || size > (uint)(data.Length - offset)) break;
            if (type == 1 && size > 10 && data[offset + 8] == 0xFF && data[offset + 9] == 0xD8)
            {
                return data.Slice(offset + 8, (int)size - 8).ToArray();
            }
            offset += (int)size;
        }

        ReadOnlySpan<byte> start = [0xFF, 0xD8, 0xFF];
        var index = data.IndexOf(start);
        return index >= 0 ? data[index..].ToArray() : null;
    }

    /// <summary>
    /// Niveau de batterie (réglage 0xD111) en %, comme le lit gPhoto2 : 0 = faible, 5 = 25 %, 1 = 50 %, 4 = 75 %,
    /// 2 = 100 %. Null pour une valeur inconnue. « Faible » devient 10 %, sous le seuil d'alerte de l'admin.
    /// </summary>
    public static int? BatteryPercent(uint value) => value switch
    {
        0 => 10,
        5 => 25,
        1 => 50,
        4 => 75,
        2 => 100,
        _ => null,
    };
}

/// <summary>Un événement de l'appareil (réponse à GetEvent) : son type et son contenu brut.</summary>
public sealed class CanonPtpEvent
{
    private readonly byte[] _body;

    public CanonPtpEvent(uint type, byte[] body)
    {
        Type = type;
        _body = body;
    }

    public uint Type { get; }

    /// <summary>Réglage modifié (événement 0xC189).</summary>
    public uint Property => Word(0);

    /// <summary>Nouvelle valeur du réglage (événement 0xC189), ou état de l'appareil (événement 0xC18B).</summary>
    public uint Value => Type == CanonPtp.EventCameraStatus ? Word(0) : Word(4);

    /// <summary>Entier de 32 bits à cette position du contenu (0 au-delà de la fin).</summary>
    public uint Word(int offset) =>
        offset >= 0 && offset + 4 <= _body.Length ? BinaryPrimitives.ReadUInt32LittleEndian(_body.AsSpan(offset)) : 0;

    /// <summary>Entier de 16 bits à cette position du contenu (0 au-delà de la fin).</summary>
    public ushort Half(int offset) =>
        offset >= 0 && offset + 2 <= _body.Length ? BinaryPrimitives.ReadUInt16LittleEndian(_body.AsSpan(offset)) : (ushort)0;

    /// <summary>Texte terminé par un zéro à cette position (nom de fichier), vide s'il n'y en a pas.</summary>
    public string Text(int offset)
    {
        if (offset < 0 || offset >= _body.Length) return "";
        var span = _body.AsSpan(offset);
        var end = span.IndexOf((byte)0);
        if (end >= 0) span = span[..end];
        var text = Encoding.Latin1.GetString(span);
        return text.All(c => c is >= ' ' and <= '~') ? text : "";
    }
}

/// <summary>Photo que l'appareil demande d'envoyer au PC.</summary>
public sealed record CanonPtpObject(uint Handle, ushort Format, uint Size, string FileName)
{
    public bool IsJpeg => Format == CanonPtp.FormatJpeg
                          || FileName.EndsWith(".JPG", StringComparison.OrdinalIgnoreCase)
                          || FileName.EndsWith(".JPEG", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        (FileName.Length > 0 ? FileName : $"objet 0x{Handle:X8}") + $" (format 0x{Format:X4}, {Size / 1024} Ko)";
}
