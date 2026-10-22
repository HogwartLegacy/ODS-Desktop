using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Ods.Core;
using Ods.Core.Catalog;
using Ods.Core.Diagnostics;
using Ods.Desktop.ViewModels;
using Ods.Desktop.Views;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Ods.Desktop.Services;

/// <summary>Composition root: the engine, the view models and app-wide services.</summary>
public sealed class AppHost
{
    private bool _quitting;

    public AppHost(OdsEngine engine, App app)
    {
        Engine = engine;
        Application = app;
        Updates = new UpdateService(engine);
        Snackbar = new SnackbarService { DefaultTimeOut = TimeSpan.FromSeconds(6) };
        Dialogs = new ContentDialogService();
        Services = ServiceCatalog.Load(engine.Paths.ExtensionsCatalogFile, engine.Paths.NativeServicesFile);
        Shell = new ShellViewModel(this);
        Onboarding = new OnboardingViewModel(this);
        Home = new HomeViewModel(this);
        Chat = new ChatViewModel(this);
        Models = new ModelsViewModel(this);
        Extensions = new ExtensionsViewModel(this);
        Logs = new LogsViewModel(this);
        Settings = new SettingsViewModel(this);
    }

    public OdsEngine Engine { get; }

    public App Application { get; }

    public UpdateService Updates { get; }

    public ISnackbarService Snackbar { get; }

    public ContentDialogService Dialogs { get; }

    public ServiceCatalog Services { get; }

    public ShellViewModel Shell { get; }

    public OnboardingViewModel Onboarding { get; }

    public HomeViewModel Home { get; }

    public ChatViewModel Chat { get; }

    public ModelsViewModel Models { get; }

    public ExtensionsViewModel Extensions { get; }

    public LogsViewModel Logs { get; }

    public SettingsViewModel Settings { get; }

    public MainWindow? Window { get; set; }

    public bool IsQuitting => _quitting;

    public static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public async Task StartAsync()
    {
        if (Engine.SettingsStore.RecoveredFrom is string damaged)
        {
            Notify("Settings were reset", $"The settings file was damaged and was kept as {damaged}.", ControlAppearance.Caution);
        }
        if (!Engine.Settings.OnboardingCompleted)
        {
            Shell.IsOnboarding = true;
            return;
        }
        await Shell.StartupAsync();
    }

    public void ApplyTheme(string theme)
    {
        var applicationTheme = theme switch
        {
            "light" => ApplicationTheme.Light,
            "system" => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
            _ => ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(applicationTheme, WindowBackdropType.Mica, updateAccent: false);
        var accent = applicationTheme == ApplicationTheme.Dark ? Color.FromRgb(0xB5, 0x6D, 0xFF) : Color.FromRgb(0x7C, 0x2C, 0xFF);
        ApplicationAccentColorManager.Apply(accent, applicationTheme);
    }

    public void Navigate(Type page) => Window?.Navigate(page);

    /// <summary>A short message in the window, or a tray notification when ODS runs in the background.</summary>
    public void Notify(string title, string message, ControlAppearance appearance = ControlAppearance.Secondary)
    {
        OnUi(() =>
        {
            if (Window is { IsVisible: true })
            {
                Snackbar.Show(title, message, appearance, null, TimeSpan.FromSeconds(6));
            }
            AppLog.Info($"Notice: {title}. {message}");
        });
    }

    public void ShowError(string title, string message) => Notify(title, message, ControlAppearance.Danger);

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void OpenFolder(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }

    public static void ShowInFolder(string file) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", file }, UseShellExecute = false });

    /// <summary>Quit for real: stop the model server, then exit (the tray icon goes with the window).</summary>
    public async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        AppLog.Info("Quitting ODS.");
        await Engine.DisposeAsync();
        Window?.Close();
        Application.Shutdown();
    }
}
