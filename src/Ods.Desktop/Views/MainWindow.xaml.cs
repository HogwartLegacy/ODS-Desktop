using System.ComponentModel;
using System.Windows;
using Ods.Desktop.Services;
using Ods.Desktop.Views.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace Ods.Desktop.Views;

public partial class MainWindow : FluentWindow
{
    private readonly AppHost _host;

    public MainWindow(AppHost host)
    {
        _host = host;
        DataContext = host;
        InitializeComponent();
        Navigation.SetPageProviderService(new PageProvider(host));
        host.Snackbar.SetSnackbarPresenter(Snackbars);
        host.Dialogs.SetDialogHost(DialogHost);
        if (Tray.Menu is { } menu) menu.DataContext = host;
        Loaded += (_, _) => Navigation.Navigate(typeof(HomePage));
    }

    public void Navigate(Type page)
    {
        ShowAndActivate();
        Navigation.Navigate(page);
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void UpdateTray()
    {
        var shell = _host.Shell;
        Tray.TooltipText = shell.ModelName is string model ? $"ODS · {shell.PillText} · {model}" : $"ODS · {shell.PillText}";
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_host.IsQuitting)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        var ui = _host.Engine.Settings.Ui;
        if (!ui.CloseToTray)
        {
            await _host.QuitAsync();
            return;
        }
        if (!ui.TrayHintShown)
        {
            var dialog = new ContentDialog
            {
                Title = "ODS keeps running",
                Content = "ODS stays in the notification area so your model and local API remain available. Right-click the ODS icon there and choose Quit ODS to stop it.",
                PrimaryButtonText = "Keep running",
                SecondaryButtonText = "Quit ODS",
                CloseButtonText = "Cancel",
            };
            var choice = await _host.Dialogs.ShowAsync(dialog, CancellationToken.None);
            if (choice == ContentDialogResult.None) return;
            ui.TrayHintShown = true;
            _host.Engine.SaveSettings();
            if (choice == ContentDialogResult.Secondary)
            {
                await _host.QuitAsync();
                return;
            }
        }
        Hide();
    }

    private void OnTrayOpen(object sender, RoutedEventArgs e) => ShowAndActivate();

    private void OnTrayChat(object sender, RoutedEventArgs e) => Navigate(typeof(ChatPage));

    private async void OnTrayQuit(object sender, RoutedEventArgs e) => await _host.QuitAsync();

    /// <summary>One instance of each page, built with its view model.</summary>
    private sealed class PageProvider(AppHost host) : INavigationViewPageProvider
    {
        private readonly Dictionary<Type, object> _pages = [];

        public object? GetPage(Type pageType)
        {
            if (_pages.TryGetValue(pageType, out var page)) return page;
            page = pageType.Name switch
            {
                nameof(HomePage) => new HomePage(host.Home),
                nameof(ChatPage) => new ChatPage(host.Chat),
                nameof(ModelsPage) => new ModelsPage(host.Models),
                nameof(ExtensionsPage) => new ExtensionsPage(host.Extensions),
                nameof(LogsPage) => new LogsPage(host.Logs),
                nameof(SettingsPage) => new SettingsPage(host.Settings),
                _ => throw new ArgumentException($"Unknown page {pageType.Name}."),
            };
            _pages[pageType] = page;
            return page;
        }
    }
}
