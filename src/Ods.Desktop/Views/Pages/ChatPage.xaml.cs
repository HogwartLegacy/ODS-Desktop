using System.Windows.Controls;
using System.Windows.Input;
using Ods.Desktop.ViewModels;

namespace Ods.Desktop.Views.Pages;

public partial class ChatPage : Page
{
    private readonly ChatViewModel _viewModel;

    public ChatPage(ChatViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.ScrollRequested += () => Dispatcher.BeginInvoke(Transcript.ScrollToEnd);
        Loaded += (_, _) => InputBox.Focus();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (_viewModel.SendCommand.CanExecute(null)) _viewModel.SendCommand.Execute(null);
    }
}
