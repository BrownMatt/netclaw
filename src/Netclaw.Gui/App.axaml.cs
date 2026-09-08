// -----------------------------------------------------------------------
// <copyright file="App.axaml.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Netclaw.Client;
using Netclaw.Configuration;
using Netclaw.Gui.Services;
using Netclaw.Gui.ViewModels;
using Netclaw.Gui.Views;

namespace Netclaw.Gui;

public sealed class App : Application
{
    public override void Initialize()
        => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = new NetclawPaths();
            var endpoint = DaemonApi.ResolveEndpoint(paths);
            var client = DaemonClientFactory.Create(endpoint, paths);
            var api = new DaemonApi(new SharedHttpClientFactory(), paths);
            var service = new DaemonSessionService(client, api);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(service, new AvaloniaUiDispatcher(), endpoint, TimeProvider.System),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private sealed class AvaloniaUiDispatcher : IUiDispatcher
    {
        public void Post(Action action) => Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// Hostless <see cref="IHttpClientFactory"/>: one shared handler so the
    /// GUI's occasional REST calls do not exhaust sockets.
    /// </summary>
    private sealed class SharedHttpClientFactory : IHttpClientFactory
    {
        private static readonly SocketsHttpHandler Handler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        public HttpClient CreateClient(string name)
            => new(Handler, disposeHandler: false);
    }
}
