using System.Buffers.Binary;

namespace LibreBooth.Core.Hardware;

/// <summary>
/// Bloc de réglages privé du pilote DNP DS620 (v2.x pour Windows 11), placé à la fin des réglages d'impression
/// (signature « MSFT », version 4, 132 octets). C'est ce bloc, et non l'option Windows « Coupe 2 pouces »,
/// que le pilote relit pour commander la coupe. La fenêtre du pilote le remplit quand on clique OK ; les
/// réglages fabriqués sans elle (par LibreBooth) doivent donc être complétés de la même façon.
/// Valeurs relevées le 22/11/2026 sur une DS620 (fenêtre validée en coupe 2 pouces puis en coupe standard).
/// </summary>
public static class DnpPrivateSettings
{
    private const int BlockSize = 132;
    private const uint Signature = 0x4D534654; // « MSFT »
    private const int Version = 4;
    private const int HeaderSize = 12;

    /// <summary>Valeur écrite par la fenêtre du pilote : 0 tant que le bloc n'a jamais été rempli.</summary>
    private const int SyncedField = 25;
    private const int SyncedValue = 7;
    private const int TwoInchField = 27;
    private const int CutModeField = 28;

    /// <summary>Le bloc DNP existe-t-il et n'a-t-il jamais été rempli par la fenêtre du pilote ?</summary>
    public static bool NeedsSync(byte[] devMode) => FindBlock(devMode) is { } offset && ReadField(devMode, offset, SyncedField) != SyncedValue;

    /// <summary>
    /// Remplit le bloc comme la fenêtre du pilote : coupe 2 pouces → (7, 1, 0), coupe standard → (7, 0, 3).
    /// Renvoie false (sans rien modifier) si le bloc n'a pas la forme connue.
    /// </summary>
    public static bool Sync(byte[] devMode, bool twoInchCut)
    {
        if (FindBlock(devMode) is not { } offset) return false;
        WriteField(devMode, offset, SyncedField, SyncedValue);
        WriteField(devMode, offset, TwoInchField, twoInchCut ? 1 : 0);
        WriteField(devMode, offset, CutModeField, twoInchCut ? 0 : 3);
        return true;
    }

    /// <summary>Coupe 2 pouces lue dans le bloc DNP, ou null si le bloc est absent ou jamais rempli.</summary>
    public static bool? ReadTwoInchCut(byte[] devMode)
    {
        if (FindBlock(devMode) is not { } offset || ReadField(devMode, offset, SyncedField) != SyncedValue) return null;
        return ReadField(devMode, offset, TwoInchField) == 1;
    }

    private static int? FindBlock(byte[] devMode)
    {
        if (devMode.Length < 72) return null;
        var publicSize = BinaryPrimitives.ReadUInt16LittleEndian(devMode.AsSpan(68));
        var offset = devMode.Length - BlockSize;
        if (offset < publicSize) return null;
        var span = devMode.AsSpan(offset);
        return BinaryPrimitives.ReadInt32LittleEndian(span) == BlockSize
               && BinaryPrimitives.ReadUInt32LittleEndian(span[4..]) == Signature
               && BinaryPrimitives.ReadInt32LittleEndian(span[8..]) == Version
            ? offset
            : null;
    }

    private static int ReadField(byte[] devMode, int block, int field) =>
        BinaryPrimitives.ReadInt32LittleEndian(devMode.AsSpan(block + HeaderSize + field * 4));

    private static void WriteField(byte[] devMode, int block, int field, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(devMode.AsSpan(block + HeaderSize + field * 4), value);
}
