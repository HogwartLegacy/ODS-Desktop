using System.Windows.Controls;
using Ods.Desktop.ViewModels;

namespace Ods.Desktop.Views.Pages;

public partial class HomePage : Page
{
    public HomePage(HomeViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
