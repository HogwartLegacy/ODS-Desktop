using System.Windows.Controls;
using Ods.Desktop.ViewModels;

namespace Ods.Desktop.Views.Pages;

public partial class ModelsPage : Page
{
    public ModelsPage(ModelsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.Refresh();
    }
}
