using LibreBooth.App.Hardware;
using LibreBooth.App.Hardware.Printing;

namespace LibreBooth.App;

/// <summary>Point d'entrée de LibreBooth.exe.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Fichiers des fabricants (Canon, DNP) posés dans le dossier « Pilotes » des données : avant tout le reste.
        NativeDrivers.Register(args);

        // Mode technique : lecture du statut DNP dans un processus séparé (un plantage du pilote
        // ne peut ainsi jamais faire tomber le photobooth).
        if (args.Any(a => string.Equals(a, "--dnp-probe", StringComparison.OrdinalIgnoreCase)))
        {
            return DnpProbe.Run();
        }
        if (args.Length >= 2 && string.Equals(args[0], "--dnp-explore", StringComparison.OrdinalIgnoreCase))
        {
            return DnpProbe.Explore(args[1]);
        }

        StartupOptions.Current = StartupOptions.Parse(args);
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
