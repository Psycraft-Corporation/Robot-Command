using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RobotCommand.Bootstrap;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Views;
using RobotCommand.Views.Workspaces;

namespace RobotCommand;

public sealed partial class App : Application
{
    private IHost? _host;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private int _shutdownStarted;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        CrashDiagnostics.Install();
        Dispatcher.UIThread.UnhandledException += OnUiUnhandledException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _host = AppHost.Build(AppContext.BaseDirectory);

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Activated += (_, _) => _host?.Services.GetRequiredService<IManualControlWorkflow>().NotifyHostActivity(true);
            mainWindow.Deactivated += (_, _) => _host?.Services.GetRequiredService<IManualControlWorkflow>().NotifyHostActivity(false);
            mainWindow.Opened += OnMainWindowOpened;
            desktop.MainWindow = mainWindow;
            desktop.Exit += OnDesktopExit;
            Console.CancelKeyPress += OnConsoleCancelKeyPress;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Opened -= OnMainWindowOpened;
        }

        // Start hosted services only after the desktop window is foregrounded.
        // In particular, Windows requires Geolocator.RequestAccessAsync to be
        // called while the app is in the foreground. Do not synchronously block
        // the UI thread here: hosted services may legitimately schedule work on
        // Avalonia's dispatcher during startup.
        var host = _host;
        if (host is null)
        {
            return;
        }

        try
        {
            await host.StartAsync();
        }
        catch (Exception ex)
        {
            CrashDiagnostics.WriteException("FATAL", "Startup", ex);
            _desktop?.Shutdown(-1);
        }
    }

    private static void OnUiUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashDiagnostics.WriteException("FATAL", "Avalonia.UI", e.Exception);
        e.Handled = false;
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        Console.CancelKeyPress -= OnConsoleCancelKeyPress;
        var host = Interlocked.Exchange(ref _host, null);
        if (host is null)
        {
            return;
        }

        _ = CompleteShutdownAsync(host, e.ApplicationExitCode);
    }

    private static async Task CompleteShutdownAsync(IHost host, int exitCode)
    {
        CrashDiagnostics.Write("INFO", "Shutdown", "Application exit requested.");
        try
        {
            using var stopCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await host.StopAsync(stopCancellation.Token)
                .WaitAsync(stopCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CrashDiagnostics.Write("WARNING", "Shutdown", "Host shutdown exceeded the five-second grace period.");
        }
        catch (Exception ex)
        {
            CrashDiagnostics.WriteException("ERROR", "Shutdown", ex);
        }

        // A third-party stream/client should never be able to keep dotnet run
        // alive after the desktop window has closed.
        var disposeTask = Task.Run(host.Dispose);
        if (await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false) != disposeTask)
        {
            CrashDiagnostics.Write("WARNING", "Shutdown", "Host disposal exceeded the shutdown timeout.");
        }

        Environment.Exit(exitCode);
    }

    private void OnConsoleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        var desktop = _desktop;
        if (desktop is not null)
        {
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        }
    }
}
