// -----------------------------------------------------------------------
// <copyright file="MainWindow.axaml.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Netclaw.Gui.ViewModels;

namespace Netclaw.Gui.Views;

public sealed partial class MainWindow : Window
{
    // ~80 ms delta coalescing: streamed tokens buffer in the block
    // viewmodels and flush on this cadence, so the markdown editor is not
    // invalidated per token.
    private readonly DispatcherTimer _flushTimer;

    public MainWindow()
    {
        InitializeComponent();
        _flushTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(80),
            DispatcherPriority.Background,
            (_, _) => (DataContext as MainWindowViewModel)?.FlushStreamingDeltas());
        _flushTimer.Start();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnSessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { SelectedSession: { } selected } vm)
            _ = vm.AttachSessionCommand.ExecuteAsync(selected);
    }

    private async void OnAttachFileClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach file to next message",
            AllowMultiple = false
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            await vm.AttachFileAsync(path);
    }

    private async void OnGrantFolderClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Grant folder access to this session",
            AllowMultiple = false
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            await vm.AddGrantAsync(path);
    }

    protected override void OnClosed(EventArgs e)
    {
        _flushTimer.Stop();
        (DataContext as MainWindowViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
