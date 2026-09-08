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

    // Diagnostics auto refresh: the tick is a no-op while the pane is
    // collapsed (guard in the viewmodel), so the timer can run unconditionally.
    private readonly DispatcherTimer _diagnosticsTimer;

    // Auto-scroll follows the newest block only while the operator sits at
    // the bottom; a manual scroll up pauses following until they return.
    private bool _pinnedToBottom = true;

    public MainWindow()
    {
        InitializeComponent();
        _flushTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(80),
            DispatcherPriority.Background,
            (_, _) => (DataContext as MainWindowViewModel)?.FlushStreamingDeltas());
        _flushTimer.Start();
        _diagnosticsTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(DiagnosticsPaneViewModel.RefreshIntervalSeconds),
            DispatcherPriority.Background,
            (_, _) =>
            {
                var vm = DataContext as MainWindowViewModel;
                vm?.Diagnostics.OnRefreshTimerTick();
                // The session list shares the interval; its own guards skip
                // the request while disconnected or while one is in flight.
                _ = vm?.OnSessionListTimerTick();
            });
        _diagnosticsTimer.Start();
        HistoryScroll.ScrollChanged += OnHistoryScrollChanged;
    }

    private void OnHistoryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0)
        {
            // Content grew or shrank. Follow it only when pinned.
            if (_pinnedToBottom)
                HistoryScroll.ScrollToEnd();
            return;
        }

        // The offset moved without a content change — the operator scrolled.
        _pinnedToBottom = HistoryScroll.Offset.Y + HistoryScroll.Viewport.Height
            >= HistoryScroll.Extent.Height - 8;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnSessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Two list boxes (pinned / unpinned) share this handler, so read the
        // clicked item from the event instead of a shared bound property.
        if (ViewModel is { } vm && e.AddedItems is [SessionListItemViewModel selected, ..])
        {
            _ = vm.AttachSessionCommand.ExecuteAsync(selected);
            // The active row highlight comes from the viewmodel's IsActive
            // mark. The ListBox selection is transient: clearing it keeps the
            // pinned and unpinned lists from showing two highlights, and a
            // list reload cannot drop the mark. Posted so the clear does not
            // re-enter this handler mid-selection.
            if (sender is ListBox list)
                Dispatcher.UIThread.Post(() => list.SelectedItem = null);
        }
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
        _diagnosticsTimer.Stop();
        (DataContext as MainWindowViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
