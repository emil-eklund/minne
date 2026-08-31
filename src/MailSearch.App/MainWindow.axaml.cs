using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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
