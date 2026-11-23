using System.Runtime.InteropServices;
using LibreBooth.Core.Config;

namespace LibreBooth.App.Hardware;

/// <summary>
/// Les fichiers des fabricants (SDK Canon : EDSDK.dll et EdsImage.dll ; API d'état DNP : cspstat.dll) ne sont pas
/// livrés avec la version publique de LibreBooth : ils appartiennent à Canon et à DNP. On peut les poser à côté de
/// LibreBooth.exe, ou, sans droits d'administrateur, dans le dossier « Pilotes » des données de LibreBooth
/// (C:\Users\&lt;nom&gt;\LibreBooth\Pilotes), que Windows ajoute alors à sa recherche de DLL.
/// </summary>
public static class NativeDrivers
{
    public const string FolderName = "Pilotes";
    public static readonly string[] CanonFiles = { "EDSDK.dll", "EdsImage.dll" };
    public const string DnpStatusFile = "cspstat.dll";
    /// <summary>Transmet le dossier « Pilotes » à la sonde DNP, lancée dans un processus séparé.</summary>
    public const string FolderVariable = "LIBREBOOTH_PILOTES";

    /// <summary>Dossier « Pilotes » où l'on conseille de copier les fichiers des fabricants.</summary>
    public static string UserFolder { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LibreBooth", FolderName);

    /// <summary>
    /// À appeler au tout début du programme (avant le premier appel au pilote Canon ou DNP). Le dossier des
    /// données imposé par --data est essayé en premier.
    /// </summary>
    public static void Register(string[] args)
    {
        try
        {
            var candidates = new List<string>();
            var dataIndex = Array.FindIndex(args, a => string.Equals(a, "--data", StringComparison.OrdinalIgnoreCase));
            if (dataIndex >= 0 && dataIndex + 1 < args.Length) candidates.Add(Path.GetFullPath(args[dataIndex + 1]));
            candidates.AddRange(AppPaths.DefaultCandidates());
            var folders = candidates.Select(root => Path.Combine(root, FolderName)).ToList();
            var inherited = Environment.GetEnvironmentVariable(FolderVariable);
            if (!string.IsNullOrWhiteSpace(inherited)) folders.Insert(0, inherited);
            UserFolder = folders.FirstOrDefault(Directory.Exists) ?? folders[0];
            if (Directory.Exists(UserFolder)) SetDllDirectory(UserFolder);
        }
        catch
        {
            // Sans dossier « Pilotes », seuls les fichiers posés à côté de LibreBooth.exe sont utilisés.
        }
    }

    /// <summary>Chemin d'un fichier de fabricant : à côté de LibreBooth.exe, sinon dans le dossier « Pilotes » ; null s'il manque.</summary>
    public static string? Find(string fileName)
    {
        foreach (var folder in new[] { AppContext.BaseDirectory, UserFolder })
        {
            var path = Path.Combine(folder, fileName);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);
}
