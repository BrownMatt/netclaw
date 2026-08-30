// -----------------------------------------------------------------------
// <copyright file="MainWindow.axaml.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Avalonia.Controls;
using Netclaw.Gui.ViewModels;

namespace Netclaw.Gui.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
        => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as MainWindowViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
