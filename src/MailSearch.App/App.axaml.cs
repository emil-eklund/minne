using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MailSearch.Config;

namespace MailSearch.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var dataDir = Program.DataDirArg(args);

            if (args.Contains("--purge-data"))
            {
                // Uninstall time: no index to open, just the question. Program.Main has already
                // ruled out the cases that need no window. Topmost because it would otherwise hide
                // behind the installer that started it — and the uninstall waits for an answer.
                desktop.MainWindow = new DeleteDataWindow(new DataPaths(dataDir), null,
                    "Minne has been uninstalled. Delete the mail index and downloaded models it left behind?")
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                };
            }
            else
            {
                var vm = new MainViewModel(dataDir);
                desktop.MainWindow = new MainWindow { DataContext = vm };
                desktop.Exit += (_, _) => vm.Dispose();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
