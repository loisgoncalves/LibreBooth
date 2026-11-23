using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace LibreBooth.Installer;

/// <summary>
/// SDK Canon EDSDK : il ne peut pas être fourni avec LibreBooth (il appartient à Canon). L'utilisateur le télécharge
/// chez Canon après une inscription gratuite, puis indique le zip reçu : on en extrait EDSDK.dll et EdsImage.dll
/// en version 32 bits (celle dont LibreBooth a besoin) et on les pose à côté de LibreBooth.exe.
/// </summary>
internal static class CanonSdk
{
    public const string Page = "https://www.canon-europe.com/business/imaging-solutions/sdk/";
    public static readonly string[] Files = { "EDSDK.dll", "EdsImage.dll" };

    public static bool IsPresent(string folder) => Files.All(f => File.Exists(Path.Combine(folder, f)));

    /// <summary>
    /// LibreBooth trouvera-t-il le SDK : à côté de LibreBooth.exe, ou dans le dossier « Pilotes » de ses données
    /// (C:\Users\&lt;nom&gt;\LibreBooth\Pilotes) ? Une installation d'essai ne regarde que son propre dossier.
    /// </summary>
    public static bool IsAvailable(string installDir, bool testMode)
    {
        if (IsPresent(installDir)) return true;
        if (testMode) return false;
        var drivers = UserDriversFolder();
        return drivers != null && IsPresent(drivers);
    }

    /// <summary>Dossier « Pilotes » que LibreBooth utilisera : même règle que LibreBooth, le premier qui existe.</summary>
    private static string? UserDriversFolder()
    {
        var roots = new List<string>();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents) && File.Exists(Path.Combine(documents, "LibreBooth", "config.json")))
        {
            roots.Add(Path.Combine(documents, "LibreBooth"));
        }
        foreach (var special in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData })
        {
            var root = Environment.GetFolderPath(special);
            if (!string.IsNullOrEmpty(root)) roots.Add(Path.Combine(root, "LibreBooth"));
        }
        return roots.Select(root => Path.Combine(root, "Pilotes")).FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Ajoute le SDK depuis le zip téléchargé chez Canon (un zip dans le zip est aussi fouillé), depuis son dossier
    /// décompressé, ou depuis EDSDK.dll lui-même. Renvoie la version d'EDSDK.dll.
    /// </summary>
    public static string Import(string source, string targetFolder)
    {
        var found = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(source);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
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
            throw new InvalidDataException("Ce fichier ne contient pas " + string.Join(" et ", missing)
                + " en version 32 bits. Choisissez le zip du SDK Canon EDSDK pour Windows, tel que téléchargé chez Canon.");
        }

        Directory.CreateDirectory(targetFolder);
        foreach (var file in Files) File.WriteAllBytes(Path.Combine(targetFolder, file), found[file]);
        var version = FileVersionInfo.GetVersionInfo(Path.Combine(targetFolder, "EDSDK.dll")).FileVersion ?? "?";
        Installation.Log($"SDK Canon ajouté dans {targetFolder} (EDSDK {version})");
        return version;
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
                using var inner = new MemoryStream(Read(entry));
                using var nested = new ZipArchive(inner, ZipArchiveMode.Read);
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

    /// <summary>DLL Windows 32 bits (en-tête PE, machine i386) : le SDK contient aussi une version 64 bits, inutilisable ici.</summary>
    public static bool IsX86(byte[] data)
    {
        if (data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z') return false;
        var pe = BitConverter.ToInt32(data, 0x3C);
        if (pe < 0 || pe + 6 > data.Length || data[pe] != (byte)'P' || data[pe + 1] != (byte)'E') return false;
        return BitConverter.ToUInt16(data, pe + 4) == 0x014C;
    }
}
