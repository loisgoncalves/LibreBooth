using System.Buffers.Binary;
using LibreBooth.Core.Hardware;

namespace LibreBooth.Core.Tests;

public sealed class DnpPrivateSettingsTests
{
    // Bloc privé relevé sur une DS620 (pilote Windows 11 v2.0.1.2), tel que LibreBooth le produisait (jamais rempli),
    // puis tel que la fenêtre du pilote le renvoie en coupe 2 pouces et en coupe standard.
    private static readonly int[] NeverFilled = { 0, 2, 2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, -1, 0, 0, 0, 1, 0, 0, 1 };
    private static readonly int[] WindowTwoInch = { 0, 2, 2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, -1, 0, 0, 7, 1, 1, 0, 1 };
    private static readonly int[] WindowStandard = { 0, 2, 2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, -1, 0, 0, 7, 1, 0, 3, 1 };

    /// <summary>Réglages d'impression de 1 276 octets : partie Windows (220), partie pilote, puis le bloc DNP de 132 octets.</summary>
    private static byte[] DevMode(int[] fields, uint signature = 0x4D534654)
    {
        var bytes = new byte[1276];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 220);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(70), 1056);
        var block = bytes.AsSpan(1144);
        BinaryPrimitives.WriteInt32LittleEndian(block, 132);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], signature);
        BinaryPrimitives.WriteInt32LittleEndian(block[8..], 4);
        for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(block[(12 + i * 4)..], fields[i]);
        return bytes;
    }

    [Fact]
    public void Settings_never_filled_by_the_driver_window_are_detected()
    {
        var devMode = DevMode(NeverFilled);

        Assert.True(DnpPrivateSettings.NeedsSync(devMode));
        Assert.Null(DnpPrivateSettings.ReadTwoInchCut(devMode));
        Assert.False(DnpPrivateSettings.NeedsSync(DevMode(WindowTwoInch)));
    }

    [Fact]
    public void Two_inch_cut_is_filled_exactly_like_the_driver_window()
    {
        var devMode = DevMode(NeverFilled);

        Assert.True(DnpPrivateSettings.Sync(devMode, twoInchCut: true));

        Assert.Equal(DevMode(WindowTwoInch), devMode);
        Assert.True(DnpPrivateSettings.ReadTwoInchCut(devMode));
    }

    [Fact]
    public void Standard_cut_is_filled_exactly_like_the_driver_window()
    {
        var devMode = DevMode(NeverFilled);

        Assert.True(DnpPrivateSettings.Sync(devMode, twoInchCut: false));

        Assert.Equal(DevMode(WindowStandard), devMode);
        Assert.False(DnpPrivateSettings.ReadTwoInchCut(devMode));
    }

    [Fact]
    public void Unknown_driver_settings_are_left_untouched()
    {
        var other = DevMode(NeverFilled, signature: 0x12345678);
        var copy = (byte[])other.Clone();

        Assert.False(DnpPrivateSettings.Sync(other, twoInchCut: true));
        Assert.False(DnpPrivateSettings.NeedsSync(other));
        Assert.Equal(copy, other);
        Assert.False(DnpPrivateSettings.Sync(new byte[40], twoInchCut: true));
    }
}
