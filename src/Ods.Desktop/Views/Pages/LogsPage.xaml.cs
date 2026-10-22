using System.Windows.Controls;
using Ods.Desktop.ViewModels;

namespace Ods.Desktop.Views.Pages;

public partial class LogsPage : Page
{
    public LogsPage(LogsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Appended += () =>
        {
            AppLogBox.ScrollToEnd();
            ServerLogBox.ScrollToEnd();
        };
        Loaded += (_, _) =>
        {
            AppLogBox.ScrollToEnd();
            ServerLogBox.ScrollToEnd();
        };
    }
}
