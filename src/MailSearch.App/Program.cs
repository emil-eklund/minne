using Avalonia;
using MailSearch.Config;

namespace MailSearch.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The Windows uninstaller calls back in here rather than deleting a hard-coded path, so the
        // data directory is resolved the same way the app resolves it (--data-dir, MINNE_DATA, default).
        if (args.Contains("--purge-data"))
        {
            var paths = new DataPaths(DataDirArg(args));
            // --ask hands over to the confirmation window (see App.OnFrameworkInitializationCompleted),
            // but only when there is something to ask about: prompting about nothing is worse than
            // not prompting, and it would block the uninstall that is waiting on the answer.
            if (!args.Contains("--ask") || DataPurge.Measure(paths).TotalBytes == 0)
                return DataPurge.DeleteAll(paths).Count == 0 ? 0 : 1;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    public static string? DataDirArg(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--data-dir") return args[i + 1];
        return null;
    }
}
