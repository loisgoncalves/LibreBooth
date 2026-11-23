using System.Text;

namespace LibreBooth.Core.Util;

/// <summary>
/// Écriture de fichiers « tout ou rien » : on écrit dans un fichier temporaire puis on le renomme.
/// Un plantage ou une coupure de courant ne laisse jamais un fichier à moitié écrit.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static void WriteAllText(string path, string content) => WriteAllBytes(path, Utf8NoBom.GetBytes(content));

    public static void WriteAllBytes(string path, byte[] data)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");

        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(data, 0, data.Length);
            fs.Flush(flushToDisk: true);
        }

        try
        {
            File.Move(temp, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static bool TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch
        {
            // Fichier verrouillé : on réessaiera plus tard.
        }
        return false;
    }
}
