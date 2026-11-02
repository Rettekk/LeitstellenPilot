using System.Threading.Tasks;
using Avalonia.Controls;
using LeitstellenPilot.ViewModels;

namespace LeitstellenPilot.Views;

public partial class MainWindow : Window
{
    private bool _closeAllowed;
    private bool _closing;
    private Task? _closePreparation;
    public MainWindow()
    {
        InitializeComponent();
        Closing += async (_, args) =>
        {
            if (_closeAllowed) return;
            args.Cancel = true;
            if (_closing) return;
            try { await PrepareToCloseAsync(); }
            finally { Close(); }
        };
    }
    public Task PrepareToCloseAsync() => _closePreparation ??= PrepareToCloseCoreAsync();
    private async Task PrepareToCloseCoreAsync()
    {
        _closing = true;
        try { if (DataContext is MainWindowViewModel model) await model.DisposeAsync(); }
        finally { _closeAllowed = true; }
    }
}
