using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bobar.App.ViewModels;
using Bobar.App.Views;
using Bobar.Core;

namespace Bobar.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(BobarPaths.FromEnvironment());
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            if (Program.RunArg(desktop.Args ?? []) is { } run) vm.SelectRun(run);
            Program.ArgsReceived += args =>
            {
                if (Program.RunArg(args) is { } id) vm.SelectRun(id);
                window.BringToFront();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
