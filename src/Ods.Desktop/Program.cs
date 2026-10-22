using Ods.Desktop.Services;
using Velopack;

namespace Ods.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack runs install, update and uninstall hooks here and exits for them.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => UninstallHooks.BeforeUninstall())
            .Run();

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.ActivateRunningInstance(args);
            return 0;
        }
        // "ODS.exe --quit" stops a running ODS; with none running there is nothing to do.
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase)) return 0;

        var app = new App(args, instance);
        app.InitializeComponent();
        return app.Run();
    }
}
