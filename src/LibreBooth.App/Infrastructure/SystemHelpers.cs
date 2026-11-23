using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Serilog;

namespace LibreBooth.App.Infrastructure;

/// <summary>Empêche Windows de mettre l'écran ou le PC en veille pendant que le photobooth tourne.</summary>
internal static class PowerManagement
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000,
    }

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    public static void KeepDisplayOn() =>
        SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);

    public static void Release() => SetThreadExecutionState(ExecutionState.Continuous);
}

/// <summary>Lancement automatique au démarrage de Windows (clé « Run » du compte utilisateur).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LibreBooth";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        if (enabled == IsEnabled()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        Log.Information("Lancement au démarrage de Windows : {Enabled}", enabled);
    }
}

/// <summary>Exécute une action sur un thread STA dédié (certains pilotes d'imprimante l'exigent).</summary>
internal static class StaTask
{
    public static Task Run(Action action, string name)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}

internal static class Shell
{
    public static void OpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ouverture du dossier impossible : {Folder}", folder);
        }
    }
}
