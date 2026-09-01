using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace MailSearch.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => QueryBox.Focus();
        DataContextChanged += (_, _) =>
        {
            if (Vm is { } vm) vm.SignInPromptRequested += ShowSignInPrompt;
        };
    }

    /// <summary>Device-code sign-in instructions (URL + code) are too long for the status bar; show them copyable.</summary>
    private void ShowSignInPrompt(string message) => new Window
    {
        Title = "Microsoft sign-in",
        Width = 560,
        SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = new SelectableTextBlock
        {
            Text = message,
            Margin = new Avalonia.Thickness(16),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        },
    }.Show(this);

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is { } vm)
        {
            e.Handled = true;
            _ = vm.SearchNowAsync();
        }
    }

    private void OnSyncClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) _ = vm.SyncAsync();
    }

    private void OnCancelSyncClick(object? sender, RoutedEventArgs e) => Vm?.CancelSync();

    // ---- Tools menu ----

    private void OnFullResyncClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) _ = vm.FullResyncAsync();
    }

    private void OnReembedClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) _ = vm.ReembedAllAsync();
    }

    private void OnRebuildIndexClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) _ = vm.RebuildIndexAsync();
    }

    private void OnSignOutClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) _ = vm.SignOutAsync();
    }

    private async void OnDeleteDataClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.IsSyncing) return;
        // The dialog disposes the view model to release the database file, so once it has tried to
        // delete anything there is no index left to search and no connection to search it with.
        var dialog = new DeleteDataWindow(vm.Paths, vm.Dispose, null);
        await dialog.ShowDialog(this);
        if (dialog.Attempted && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private async void OnEvalClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick an evaluation query set",
            FileTypeFilter = [new FilePickerFileType("Eval set (JSON)") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        new EvalWindow(vm, path).Show(this);
    }

    private async void OnCreateEvalClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create example eval set",
            SuggestedFileName = "eval-queries.json",
            DefaultExtension = "json",
        });
        if (file?.TryGetLocalPath() is { } path) vm.CreateExampleEvalSet(path);
    }

    private async void OnCopyConfigClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(vm.GetRedactedConfigJson());
            vm.Status = "Copied config to the clipboard (API key redacted).";
        }
    }

    private void OnOpenConfigClick(object? sender, RoutedEventArgs e)
    {
        // Prefer the file itself; fall back to the folder when no .json handler is registered.
        if (Vm is { } vm && !OpenInShell(vm.ConfigFilePath)) OpenInShell(vm.DataDirectory);
    }

    private void OnOpenDataFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) OpenInShell(vm.DataDirectory);
    }

    private static bool OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e) => OpenSelected();

    private void OnOpenClick(object? sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        var link = Vm?.Selected?.WebLink;
        if (string.IsNullOrEmpty(link)) return;
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
        catch { /* no URL handler registered — nothing sensible to do */ }
    }

    private async void OnCopyIdClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Selected is { } item && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(item.CopyableId);
    }
}
