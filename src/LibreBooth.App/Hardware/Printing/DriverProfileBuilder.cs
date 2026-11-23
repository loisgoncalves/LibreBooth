using System.Printing;
using System.Printing.Interop;
using System.Xml.Linq;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;

namespace LibreBooth.App.Hardware.Printing;

/// <summary>
/// Réglages automatiques des imprimantes DNP. Le pilote publie ses options dans le « Print Schema » de Windows
/// (dont CUTTERCONTROL : coupe standard ou coupe 2 pouces). On part des réglages par défaut du pilote
/// (papier 6x4, sans bord), on choisit la coupe, et Windows en fait un DEVMODE. Le pilote DS620 ne coupe
/// pourtant qu'en lisant son propre bloc privé, que seule sa fenêtre remplit : LibreBooth le remplit donc
/// comme elle (voir <see cref="DnpPrivateSettings"/>). Chaque réglage est relu pour vérifier la coupe.
/// </summary>
internal static class DriverProfileBuilder
{
    private const string PsfNamespace = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    private const string CutterFeature = "CUTTERCONTROL";
    private const string CutStandard = "CUT_STANDARD";
    private const string Cut2Inch = "CUT_2INCH";

    public sealed record Profile(string Name, byte[] DevMode, string Description);

    public static bool LooksLikeDnp(string printerName, string? driverName)
    {
        var text = (printerName + " " + driverName).ToUpperInvariant();
        return new[] { "DNP", "DAI NIPPON", "DS620", "DS-620", "DS-RX1", "DSRX1", "DS40", "DS80", "DS820", "QW410" }
            .Any(text.Contains);
    }

    /// <returns>Les profils « 10x15 » et « Bandelettes », ou une liste vide si le pilote ne propose pas la coupe 2 pouces.</returns>
    public static IReadOnlyList<Profile> CreateDnpProfiles(string printerName)
    {
        using var server = new LocalPrintServer();
        using var queue = server.GetPrintQueue(printerName);
        XDocument capabilities;
        using (var stream = queue.GetPrintCapabilitiesAsXml())
        {
            capabilities = XDocument.Load(stream);
        }

        XNamespace psf = PsfNamespace;
        var cutter = capabilities.Descendants(psf + "Feature")
            .FirstOrDefault(f => IsCutterFeature((string?)f.Attribute("name")));
        if (cutter == null) return Array.Empty<Profile>();

        var featureName = (string)cutter.Attribute("name")!;
        var prefix = featureName.Contains(':') ? featureName[..featureName.IndexOf(':')] : "";
        var featureNamespace = cutter.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(featureNamespace)) return Array.Empty<Profile>();

        var options = cutter.Elements(psf + "Option").Select(o => LocalName((string?)o.Attribute("name"))).ToList();
        if (!options.Contains(CutStandard) || !options.Contains(Cut2Inch)) return Array.Empty<Profile>();

        using var converter = new PrintTicketConverter(printerName, PrintTicketConverter.MaxPrintSchemaVersion);
        var baseTicket = queue.DefaultPrintTicket;
        var profiles = new List<Profile>();
        foreach (var (name, option, description) in new[]
                 {
                     (PrintProfiles.Postcard, CutStandard, "papier 6x4 sans bord, coupe standard"),
                     (PrintProfiles.Strips, Cut2Inch, "papier 6x4 sans bord, coupe 2 pouces (2 bandelettes)"),
                 })
        {
            var delta = BuildCutterTicket(prefix, featureNamespace, featureName, option);
            var merged = queue.MergeAndValidatePrintTicket(baseTicket, delta).ValidatedPrintTicket;
            var devMode = converter.ConvertPrintTicketToDevMode(merged, BaseDevModeType.UserDefault);
            var check = ReadCutter(converter, devMode);
            if (check != option)
            {
                throw new InvalidOperationException($"Le pilote n'a pas retenu la coupe {option} (relu : {check ?? "aucune"}).");
            }
            var synced = DnpPrivateSettings.Sync(devMode, option == Cut2Inch);
            profiles.Add(new Profile(name, devMode, description + (synced ? "" : " (bloc DNP inconnu : refaites ce réglage avec « Réglages du pilote… »)")));
        }
        return profiles;
    }

    /// <summary>Coupe enregistrée dans des réglages (CUT_STANDARD, CUT_2INCH), ou null si illisible.</summary>
    public static string? DescribeCutter(string printerName, byte[] devMode)
    {
        try
        {
            using var converter = new PrintTicketConverter(printerName, PrintTicketConverter.MaxPrintSchemaVersion);
            return ReadCutter(converter, devMode);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Windows préfixe les options des pilotes selon leur portée : la coupe de la DS620 s'appelle
    /// « DocumentCUTTERCONTROL » (option déclarée CUTTERCONTROL dans le pilote).
    /// </summary>
    private static bool IsCutterFeature(string? qualifiedName) =>
        LocalName(qualifiedName)?.EndsWith(CutterFeature, StringComparison.OrdinalIgnoreCase) == true;

    private static PrintTicket BuildCutterTicket(string prefix, string featureNamespace, string featureName, string option)
    {
        XNamespace psf = PsfNamespace;
        var document = new XDocument(
            new XElement(psf + "PrintTicket",
                new XAttribute(XNamespace.Xmlns + "psf", PsfNamespace),
                new XAttribute(XNamespace.Xmlns + prefix, featureNamespace),
                new XAttribute("version", "1"),
                new XElement(psf + "Feature",
                    new XAttribute("name", featureName),
                    new XElement(psf + "Option", new XAttribute("name", $"{prefix}:{option}")))));
        var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;
        return new PrintTicket(stream);
    }

    private static string? ReadCutter(PrintTicketConverter converter, byte[] devMode)
    {
        var ticket = converter.ConvertDevModeToPrintTicket(devMode);
        using var stream = ticket.GetXmlStream();
        var document = XDocument.Load(stream);
        XNamespace psf = PsfNamespace;
        var feature = document.Descendants(psf + "Feature")
            .FirstOrDefault(f => IsCutterFeature((string?)f.Attribute("name")));
        return LocalName((string?)feature?.Element(psf + "Option")?.Attribute("name"));
    }

    private static string? LocalName(string? qualifiedName) =>
        qualifiedName == null ? null : qualifiedName.Contains(':') ? qualifiedName[(qualifiedName.IndexOf(':') + 1)..] : qualifiedName;
}
