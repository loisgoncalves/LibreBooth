using System.Globalization;
using LibreBooth.Core.Admin;
using LibreBooth.Core.Config;
using LibreBooth.Core.Hardware;

namespace LibreBooth.Core.Tests;

/// <summary>Version 3.1.0 : réglage du pilote du Canon (Windows par défaut, SDK Canon en secours).</summary>
public sealed class CameraDriverSettingTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Windows_driver_is_the_default_and_is_kept_in_config()
    {
        Assert.Equal(CameraDriver.Windows, new AppSettings().Camera.Driver);
        Assert.Equal(CameraDriver.Windows, new AppSettings { Camera = { Driver = (CameraDriver)9 } }.Normalize().Camera.Driver);

        var path = Path.Combine(_temp.Root, "config.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { Camera = { Driver = CameraDriver.CanonSdk } });

        Assert.Contains("\"Driver\": \"CanonSdk\"", File.ReadAllText(path));
        Assert.Equal(CameraDriver.CanonSdk, store.Load().Camera.Driver);
    }

    /// <summary>
    /// Un booth mis à jour depuis la 3.0 (ou avant) passe au pilote de Windows, ses autres réglages gardés. L'option
    /// retirée « Copie sur la carte SD » est ignorée, puis disparaît du fichier au prochain enregistrement.
    /// </summary>
    [Fact]
    public void Config_of_version_3_0_switches_to_the_Windows_driver()
    {
        var path = Path.Combine(_temp.Root, "config.json");
        File.WriteAllText(path, """{ "Version": 1, "AdminPin": "4321", "Camera": { "Kind": "Canon", "SaveToCardToo": true, "Rotation": 90 } }""");
        var store = new SettingsStore(path);

        var settings = store.Load();

        Assert.Equal("4321", settings.AdminPin);
        Assert.Equal(CameraKind.Canon, settings.Camera.Kind);
        Assert.Equal(CameraDriver.Windows, settings.Camera.Driver);
        Assert.Equal(90, settings.Camera.Rotation);

        store.Save(settings);
        Assert.DoesNotContain("SaveToCardToo", File.ReadAllText(path));
    }
}

/// <summary>Version 3.1.0 : le SDK Canon n'est réclamé à l'accueil de l'admin que s'il est choisi.</summary>
public sealed class CameraDriverReadinessTests
{
    private static readonly ReadinessInput Ready = new()
    {
        CameraKind = CameraKind.Canon,
        CameraState = CameraState.Ready,
        CameraName = "Canon EOS 1300D",
        BatteryLevel = 100,
        PrintEnabled = true,
        PrinterName = "DP-DS620",
        PrinterChecked = true,
        PrinterCanPrint = true,
        PrinterCondition = PrinterCondition.Ready,
        RemainingPrints = 350,
        Formats = new[] { "10x15 paysage" },
        FreeDiskBytes = 120L * 1024 * 1024 * 1024,
        DiskName = "C:",
    };

    private static ReadinessItem Camera(ReadinessInput input) =>
        Readiness.Evaluate(input).Single(i => i.Area == ReadinessArea.Camera);

    [Fact]
    public void Missing_SDK_does_not_matter_with_the_Windows_driver()
    {
        var item = Camera(Ready with { CameraDriver = CameraDriver.Windows, CanonSdkMissing = true });

        Assert.Equal(ReadinessLevel.Ok, item.Level);
        Assert.Equal("Canon EOS 1300D prêt, batterie à 100 %.", item.Detail);
    }

    [Fact]
    public void Missing_SDK_is_an_error_when_it_is_the_chosen_driver()
    {
        var item = Camera(Ready with { CameraDriver = CameraDriver.CanonSdk, CanonSdkMissing = true, CameraState = CameraState.Error });

        Assert.Equal(ReadinessLevel.Error, item.Level);
        Assert.Contains("SDK Canon manque", item.Detail);
        Assert.Contains("pilote de Windows", item.Detail);
    }

    [Fact]
    public void Camera_that_Windows_cannot_drive_shows_its_message()
    {
        const string message = "Appareil Canon détecté, mais Windows ne sait pas le piloter : choisissez « SDK Canon » dans admin › Appareil photo.";
        var item = Camera(Ready with { CameraState = CameraState.Error, CameraMessage = message });

        Assert.Equal(ReadinessLevel.Error, item.Level);
        Assert.Equal(message, item.Detail);
    }
}

/// <summary>Version 3.1.0 : pré-versions proposées sur demande, et rappel à l'accueil quand l'une d'elles est installée.</summary>
public sealed class PrereleaseSettingTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Previews_are_off_by_default_and_kept_in_config()
    {
        Assert.False(new AppSettings().IncludePrereleases);
        var path = Path.Combine(_temp.Root, "config.json");
        File.WriteAllText(path, """{ "Version": 1, "AdminPin": "4321", "CheckForUpdates": true }""");
        var store = new SettingsStore(path);
        Assert.False(store.Load().IncludePrereleases);

        store.Save(new AppSettings { IncludePrereleases = true });

        Assert.True(store.Load().IncludePrereleases);
    }

    [Fact]
    public void An_installed_preview_is_a_point_to_check_before_an_event()
    {
        var item = Readiness.Evaluate(new ReadinessInput { CurrentVersion = "3.2.0", StableToReturnTo = "3.1.0" })
            .Single(i => i.Area == ReadinessArea.Update);

        Assert.Equal(ReadinessLevel.Warning, item.Level);
        Assert.Equal("Pré-version installée", item.Title);
        Assert.Equal("LibreBooth 3.2.0 est une version d'essai : pour un événement, revenez à la version stable 3.1.0.", item.Detail);
    }

    [Fact]
    public void An_offered_preview_is_only_information()
    {
        var items = Readiness.Evaluate(new ReadinessInput { AvailableUpdate = "3.2.0", AvailableUpdateIsPrerelease = true });

        var item = items.Single(i => i.Area == ReadinessArea.Update);
        Assert.Equal(ReadinessLevel.Info, item.Level);
        Assert.Equal("La pré-version 3.2.0 (version d'essai) est disponible.", item.Detail);
        Assert.False(item.NeedsAttention);
    }
}

/// <summary>Version 3.1.0 : lecture des réponses d'un Canon EOS piloté sans le SDK Canon (protocole PTP des EOS).</summary>
public sealed class CanonPtpTests
{
    /// <summary>Commandes Canon que le pilote de Windows laisse passer pour un EOS 1300D (relevé du 24/11/2026).</summary>
    private const string Eos1300DOperations =
        "91CF 912C 91D0 912D 91D1 912E 91D2 912F 9130 9131 91D5 9132 9133 91D7 9134 91D8 9135 91D9 9136 91DA 9137 91DB "
        + "9138 91DC 9139 91DD 913A 91DE 913B 913C 913D 91E1 91E2 913F 91E3 9140 91E4 9141 91E5 9142 91E6 9143 91E7 91E8 "
        + "91E9 91EA 91EB 91EC 91ED 91EE 91EF 91F0 91F1 91F2 91F3 91F4 91F5 91F6 9153 91F7 9154 91F8 9155 91F9 9157 9158 "
        + "9159 915A 91FE 91FF 9160 902F 9033 9180 9181 9182 9183 9184 9185 9052 9053 9057 9058 9059 905A 9101 905F 9102 "
        + "9103 9104 9105 9106 9107 9108 9109 910A 910B 9068 910C 9069 906A 910E 906B 910F 906C 9110 906D 906E 906F 9113 "
        + "9114 9115 9116 9117 9118 911A 911B 911C 911D 91C0 911E 91C1 911F 91C2 9120 91C3 91C4 9121 9122 91C5 9123 91C6 "
        + "9124 91C7 91C8 91C9 91CA 9127 91CB 9128 91CC 9129 91CE 912B";

    /// <summary>Événement « photo à envoyer au PC » reçu du 1300D, octet pour octet (photo de 1879 Ko).</summary>
    private const string RealTransferRequest =
        "30-00-00-00-86-C1-00-00-82-11-00-30-01-38-00-00-00-00-00-00-F5-5D-1D-00-80-11-00-30-00-00-00-00"
        + "-00-00-00-00-00-00-00-00-00-00-00-00-01-00-00-00";

    private static readonly byte[] Terminator = Words(8, 0);

    private static HashSet<ushort> Operations(string list) =>
        list.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(o => ushort.Parse(o, NumberStyles.HexNumber)).ToHashSet();

    private static byte[] Hex(string text) => Convert.FromHexString(text.Replace("-", ""));

    private static byte[] Words(params uint[] words) => words.SelectMany(BitConverter.GetBytes).ToArray();

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void The_EOS_1300D_is_usable_without_the_Canon_SDK()
    {
        var operations = Operations(Eos1300DOperations);

        Assert.Equal(144, operations.Count);
        Assert.True(CanonPtp.IsUsableEos(operations));
        Assert.Contains(CanonPtp.OpAfCancel, operations);
        Assert.Contains(CanonPtp.OpKeepDeviceOn, operations);
    }

    [Fact]
    public void A_camera_without_live_view_or_shutter_commands_is_not_usable()
    {
        var operations = Operations(Eos1300DOperations);

        Assert.False(CanonPtp.IsUsableEos(operations.Where(o => o != CanonPtp.OpGetViewFinderData).ToList()));
        Assert.False(CanonPtp.IsUsableEos(operations.Where(o => o is not (CanonPtp.OpRemoteReleaseOn or CanonPtp.OpRemoteRelease)).ToList()));
        // Boîtier plus ancien : le déclenchement simple suffit.
        Assert.True(CanonPtp.IsUsableEos(operations.Where(o => o != CanonPtp.OpRemoteReleaseOn).ToList()));
        Assert.False(CanonPtp.IsUsableEos(Array.Empty<ushort>()));
    }

    [Fact]
    public void Setting_payload_is_size_property_value()
    {
        Assert.Equal(new byte[] { 12, 0, 0, 0, 0x1C, 0xD1, 0, 0, 4, 0, 0, 0 },
            CanonPtp.PropertyPayload(CanonPtp.PropCaptureDestination, CanonPtp.DestinationPc));
    }

    [Fact]
    public void Real_transfer_request_of_the_1300D_is_read()
    {
        var events = CanonPtp.ParseEvents(Join(Hex(RealTransferRequest), Terminator));

        var single = Assert.Single(events);
        Assert.Equal(CanonPtp.EventRequestObjectTransfer, single.Type);
        var photo = CanonPtp.ObjectFrom(single);
        Assert.NotNull(photo);
        Assert.Equal(0x30001182u, photo!.Handle);
        Assert.Equal(CanonPtp.FormatJpeg, photo.Format);
        Assert.Equal(0x1D5DF5u, photo.Size);
        Assert.Equal("", photo.FileName);
        Assert.True(photo.IsJpeg);
        Assert.Equal("objet 0x30001182 (format 0x3801, 1879 Ko)", photo.ToString());
    }

    [Fact]
    public void Several_events_are_read_in_order()
    {
        var battery = Words(16, CanonPtp.EventPropValueChanged, CanonPtp.PropBatteryPower, 2);
        var status = Words(12, CanonPtp.EventCameraStatus, 1);
        var unknown = Words(12, 0xC1A7, 5);

        var events = CanonPtp.ParseEvents(Join(battery, status, unknown, Hex(RealTransferRequest), Terminator));

        Assert.Equal(new[] { CanonPtp.EventPropValueChanged, CanonPtp.EventCameraStatus, 0xC1A7u, CanonPtp.EventRequestObjectTransfer },
            events.Select(e => e.Type));
        Assert.Equal(CanonPtp.PropBatteryPower, events[0].Property);
        Assert.Equal(2u, events[0].Value);
        Assert.Equal(1u, events[1].Value);
        Assert.Null(CanonPtp.ObjectFrom(events[0]));
    }

    /// <summary>Un fichier ajouté sur la carte (0xC181) n'est pas une photo à télécharger : seul 0xC186 en est une.</summary>
    [Fact]
    public void Photo_saved_on_the_card_is_not_taken_for_a_transfer()
    {
        // ObjectAddedEx : objet, stockage, format, …, taille (+20), dossier parent (+24), …, nom (+32).
        var body = new byte[48];
        BitConverter.GetBytes(0x30001190u).CopyTo(body, 0);
        BitConverter.GetBytes(CanonPtp.FormatJpeg).CopyTo(body, 8);
        "IMG_0042.JPG"u8.ToArray().CopyTo(body, 32);
        var record = Join(Words((uint)body.Length + 8, 0xC181), body, Terminator);

        var saved = Assert.Single(CanonPtp.ParseEvents(record));

        Assert.Equal(0xC181u, saved.Type);
        Assert.Null(CanonPtp.ObjectFrom(saved));
    }

    [Fact]
    public void Raw_file_is_not_taken_for_a_JPEG() =>
        Assert.False(new CanonPtpObject(1, 0xB103, 25_000_000, "IMG_0042.CR2").IsJpeg);

    [Theory]
    [InlineData("")]
    [InlineData("08-00-00-00")]
    [InlineData("40-00-00-00-89-C1-00-00-11-D1-00-00")]
    [InlineData("04-00-00-00-89-C1-00-00-11-D1-00-00-02-00-00-00")]
    public void Truncated_or_odd_answers_give_no_event_and_no_error(string data) =>
        Assert.Empty(CanonPtp.ParseEvents(data.Length == 0 ? Array.Empty<byte>() : Hex(data)));

    [Fact]
    public void Events_after_the_end_marker_are_ignored()
    {
        var events = CanonPtp.ParseEvents(Join(Terminator, Words(12, CanonPtp.EventCameraStatus, 1)));

        Assert.Empty(events);
    }

    [Fact]
    public void Live_view_image_is_taken_from_its_block()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9];
        var data = Join(Words((uint)jpeg.Length + 8, 1), jpeg, Words(12, 4, 0x1234), Terminator);

        Assert.Equal(jpeg, CanonPtp.ExtractLiveViewJpeg(data));
    }

    [Fact]
    public void Live_view_image_is_found_without_blocks()
    {
        byte[] data = [0, 0, 0, 1, 2, 0xFF, 0xD8, 0xFF, 0xDB, 9, 9];

        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 9, 9 }, CanonPtp.ExtractLiveViewJpeg(data));
        Assert.Null(CanonPtp.ExtractLiveViewJpeg(Words(12, 4, 0x1234)));
        Assert.Null(CanonPtp.ExtractLiveViewJpeg(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData(0u, 10)]
    [InlineData(5u, 25)]
    [InlineData(1u, 50)]
    [InlineData(4u, 75)]
    [InlineData(2u, 100)]
    [InlineData(3u, null)]
    [InlineData(0xFFFFFFFFu, null)]
    public void Battery_level_is_read_like_gPhoto2(uint value, int? percent) =>
        Assert.Equal(percent, CanonPtp.BatteryPercent(value));

    [Fact]
    public void Camera_answers_are_explained_in_French()
    {
        Assert.Equal("OK", CanonPtp.Describe(CanonPtp.RcOk));
        Assert.Contains("occupé", CanonPtp.Describe(CanonPtp.RcDeviceBusy));
        Assert.Contains("pas prêt", CanonPtp.Describe(CanonPtp.RcNotReady));
        Assert.Equal("réponse 0xA00F", CanonPtp.Describe(0xA00F));
    }
}
