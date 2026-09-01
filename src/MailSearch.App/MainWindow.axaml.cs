using System.Diagnostics;
using Avalonia.Controls;
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
    }

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
