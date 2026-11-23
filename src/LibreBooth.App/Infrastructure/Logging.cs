using Serilog;

namespace LibreBooth.App.Infrastructure;

/// <summary>Journal quotidien (dossier des données\logs\librebooth-AAAAMMJJ.log ; par défaut C:\Users\&lt;nom&gt;\LibreBooth), gardé 31 jours.</summary>
internal static class Logging
{
    public static string LogsFolder { get; private set; } = "";

    public static void Configure(string folder)
    {
        LogsFolder = folder;
        Directory.CreateDirectory(folder);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(folder, "librebooth-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public static string? LatestLogFile()
    {
        if (!Directory.Exists(LogsFolder)) return null;
        return Directory.GetFiles(LogsFolder, "librebooth-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Les dernières lignes du journal du jour (lecture sans bloquer l'écriture).</summary>
    public static string ReadTail(int maxLines = 400)
    {
        var file = LatestLogFile();
        if (file == null) return "(aucun journal pour l'instant)";
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > maxLines) lines.Dequeue();
            }
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return "Journal illisible : " + ex.Message;
        }
    }
}
