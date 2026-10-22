using System.Windows;
using System.Windows.Threading;
using Ods.Core;
using Ods.Core.Diagnostics;
using Ods.Core.Platform;
using Ods.Desktop.Services;
using Ods.Desktop.Views;

namespace Ods.Desktop;

public partial class App : Application
{
    private readonly string[] _args;
    private readonly SingleInstance _instance;

    public App(string[] args, SingleInstance instance)
    {
        _args = args;
        _instance = instance;
    }

    public static AppHost Host { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled exception; ODS is closing.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("A background task failed.", args.Exception);
            args.SetObserved();
        };

        OdsEngine engine;
        try
        {
            engine = OdsEngine.Create(OdsPaths.Default());
        }
        catch (EngineBusyException error)
        {
            MessageBox.Show(error.Message, "ODS", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(3);
            return;
        }

        Host = new AppHost(engine, this);
        Host.ApplyTheme(engine.Settings.Ui.Theme);
        var window = new MainWindow(Host);
        MainWindow = window;
        Host.Window = window;

        var background = _args.Contains("--background", StringComparer.OrdinalIgnoreCase) && engine.Settings.OnboardingCompleted;
        if (!background) window.Show();
        // A later launch either shows this window or, with --quit, shuts ODS down cleanly.
        _instance.Activated += args => Dispatcher.BeginInvoke(async () =>
        {
            if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase)) await Host.QuitAsync();
            else window.ShowAndActivate();
        });
        _instance.StartListening();

        await Host.StartAsync();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A UI bug must not take the model server down with it: log it, tell the user, keep running.
        AppLog.Error("Unexpected error in the ODS window.", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"ODS ran into an unexpected problem and recorded it in the log.\n\n{e.Exception.Message}\n\nYou can keep using ODS. Settings > Diagnostics can export a support bundle.",
            "ODS",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
