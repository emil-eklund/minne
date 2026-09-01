using Avalonia.Controls;
using Avalonia.Interactivity;
using MailSearch.Config;

namespace MailSearch.App;

/// <summary>
/// Confirms emptying the data directory and reports what it freed. Shown from the Tools menu and,
/// with <c>--purge-data --ask</c>, by the Windows uninstaller — one dialog so both routes agree
/// about what "delete local data" means.
/// </summary>
public partial class DeleteDataWindow : Window
{
    private readonly DataPaths _paths = null!;
    private readonly Action? _releaseLocks;

    /// <summary>
    /// True once deleting has been attempted. The database connection is closed to make it possible,
    /// so the caller has to shut down afterwards — whether or not every file actually went.
    /// </summary>
    public bool Attempted { get; private set; }

    /// <summary>Avalonia's runtime XAML loader needs a public parameterless constructor.</summary>
    public DeleteDataWindow() => InitializeComponent();

    /// <param name="releaseLocks">
    /// Closes the database connection before the files go. Without it SQLite keeps mail.db open and
    /// the delete fails on Windows.
    /// </param>
    public DeleteDataWindow(DataPaths paths, Action? releaseLocks, string? intro) : this()
    {
        _paths = paths;
        _releaseLocks = releaseLocks;
        if (intro is not null) Intro.Text = intro;
        Location.Text = paths.Root;
        var usage = DataPurge.Measure(paths);
        Usage.Text = $"""
            mail index   {Size(usage.DatabaseBytes)}
            models       {Size(usage.ModelBytes)}
            other        {Size(usage.OtherBytes)}
            total        {Size(usage.TotalBytes)}
            """;
    }

    private void OnKeepClick(object? sender, RoutedEventArgs e) => Close();

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        DeleteButton.IsEnabled = false;
        Attempted = true;
        _releaseLocks?.Invoke();
        var failures = DataPurge.DeleteAll(_paths);
        if (failures.Count == 0)
        {
            Close();
            return;
        }

        // Almost always a second copy of the app holding mail.db open. Say so rather than pretending.
        Failures.Text = "Some files could not be deleted — close any other copy of Minne and try again:\n"
            + string.Join('\n', failures);
        Failures.IsVisible = true;
        KeepButton.Content = "Close";
        DeleteButton.IsEnabled = true;
    }

    /// <summary>Bytes at the precision a person cares about when deciding whether to free the space.</summary>
    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };
}
