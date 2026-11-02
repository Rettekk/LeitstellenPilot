using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using LeitstellenPilot.Services;
using LeitstellenPilot.ViewModels;
using LeitstellenPilot.Views;

namespace LeitstellenPilot;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var validators = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OfType<DataAnnotationsValidationPlugin>(BindingPlugins.DataValidators));
            foreach (var validator in validators) BindingPlugins.DataValidators.Remove(validator);
            var main = new MainWindow
            {
                DataContext = new MainWindowViewModel(config: new ConfigService(),
                    windowsAutoStart: StartupArguments.WindowsAutoStart)
            };
            desktop.MainWindow = main;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
