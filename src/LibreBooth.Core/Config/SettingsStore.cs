using LibreBooth.Core.Util;
using Serilog;

namespace LibreBooth.Core.Config;

/// <summary>Lecture et écriture de config.json.</summary>
public sealed class SettingsStore
{
    private readonly ILogger _log = Serilog.Log.ForContext<SettingsStore>();

    public SettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings().Normalize();
        try
        {
            return (JsonDefaults.Read<AppSettings>(FilePath) ?? new AppSettings()).Normalize();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Configuration illisible ({Path}) : copie de sauvegarde puis réglages par défaut", FilePath);
            try
            {
                File.Copy(FilePath, FilePath + ".illisible-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            }
            catch
            {
                // Rien de plus à faire : on repart des réglages par défaut.
            }
            return new AppSettings().Normalize();
        }
    }

    public void Save(AppSettings settings) => JsonDefaults.Write(FilePath, settings);
}
