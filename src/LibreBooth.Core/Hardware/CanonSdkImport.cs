using System.Diagnostics;
using System.IO.Compression;

namespace LibreBooth.Core.Hardware;

/// <summary>
/// SDK Canon EDSDK : il ne peut pas être fourni avec LibreBooth (il appartient à Canon). L'utilisateur le télécharge
/// chez Canon après une inscription gratuite, puis indique le zip reçu : on en extrait EDSDK.dll et EdsImage.dll en
/// version 32 bits (celle dont LibreBooth a besoin ; le SDK contient aussi une version 64 bits).
/// </summary>
public static class CanonSdkImport
{
    public const string Page = "https://www.canon-europe.com/business/imaging-solutions/sdk/";
    public static readonly string[] Files = { "EDSDK.dll", "EdsImage.dll" };

    /// <summary>
    /// Ajoute le SDK dans le dossier indiqué, depuis le zip téléchargé chez Canon (un zip dans le zip est aussi fouillé),
    /// depuis son dossier décompressé, ou depuis EDSDK.dll lui-même. Renvoie la version d'EDSDK.dll.
    /// </summary>
    public static string Import(string source, string targetFolder)
    {
        var found = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(source);
            SearchZip(zip, found, depth: 0);
        }
        else
        {
            var folder = Directory.Exists(source) ? source : Path.GetDirectoryName(source) ?? source;
            var option = Directory.Exists(source) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var path in Directory.EnumerateFiles(folder, "*.dll", option))
            {
                Consider(Path.GetFileName(path), File.ReadAllBytes(path), found);
            }
        }

        var missing = Files.Where(f => !found.ContainsKey(f)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException($"ce fichier ne contient pas {string.Join(" et ", missing)} en version 32 bits. "
                                           + "Choisissez le zip du SDK Canon EDSDK pour Windows, tel que téléchargé chez Canon.");
        }

        Directory.CreateDirectory(targetFolder);
        foreach (var file in Files) File.WriteAllBytes(Path.Combine(targetFolder, file), found[file]);
        return FileVersionInfo.GetVersionInfo(Path.Combine(targetFolder, Files[0])).FileVersion ?? "?";
    }

    private static void SearchZip(ZipArchive zip, Dictionary<string, byte[]> found, int depth)
    {
        foreach (var entry in zip.Entries)
        {
            if (Files.Any(f => string.Equals(entry.Name, f, StringComparison.OrdinalIgnoreCase)))
            {
                Consider(entry.Name, Read(entry), found);
            }
            else if (depth == 0 && entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var nested = new ZipArchive(new MemoryStream(Read(entry)), ZipArchiveMode.Read);
                SearchZip(nested, found, depth + 1);
            }
        }
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Consider(string name, byte[] data, Dictionary<string, byte[]> found)
    {
        var file = Files.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        if (file != null && !found.ContainsKey(file) && IsX86(data)) found[file] = data;
    }

    /// <summary>DLL Windows 32 bits : en-tête PE avec la machine i386 (0x014C).</summary>
    public static bool IsX86(byte[] data)
    {
        if (data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z') return false;
        var pe = BitConverter.ToInt32(data, 0x3C);
        if (pe < 0 || pe + 6 > data.Length || data[pe] != (byte)'P' || data[pe + 1] != (byte)'E') return false;
        return BitConverter.ToUInt16(data, pe + 4) == 0x014C;
    }
}
