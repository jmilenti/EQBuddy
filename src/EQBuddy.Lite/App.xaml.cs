using System.Windows;
using EQBuddy.Core;

namespace EQBuddy.Lite;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
