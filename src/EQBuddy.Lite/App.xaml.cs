using System.IO;
using System.Windows;
using EQBuddy.Core;

namespace EQBuddy.Lite;

public partial class App : Application
{
    private static readonly string ErrorLog = AppPaths.File("error.log");

    /// <summary>Where CoreLog lands. Without this every CoreLog.Error in the app and the
    /// engine went to an unset sink and vanished — including the update failure whose
    /// banner tells you to go and read this file.</summary>
    public static void LogError(object? error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
            File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}\n\n");
        }
        catch { /* never crash on logging */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CoreLog.Sink = LogError;
        // Log-and-continue, same policy as the full app: a stats widget must never
        // take the game session down with it.
        DispatcherUnhandledException += (_, args) =>
        {
            CoreLog.Error(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CoreLog.Error(args.ExceptionObject);
    }
}
