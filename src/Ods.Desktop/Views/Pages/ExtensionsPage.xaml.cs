using System.Windows.Controls;
using Ods.Desktop.ViewModels;

namespace Ods.Desktop.Views.Pages;

public partial class ExtensionsPage : Page
{
    public ExtensionsPage(ExtensionsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
