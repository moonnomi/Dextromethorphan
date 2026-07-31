using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.Performance;
using Dextromethorphan.App.UI;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.App.WindowsIntegration;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Playback;
using Dextromethorphan.Infrastructure.Audio;
using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dextromethorphan.App;

public partial class App : Application
{
    private readonly ConcurrentQueue<IReadOnlyList<string>> _pendingLaunchArguments = new();
    private SingleInstanceCoordinator? _singleInstance;
    private StartupRecoveryGuard? _startupRecoveryGuard;
    private readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureServices(services =>
        {
            services.AddSingleton<DeveloperDiagnostics>();
            services.AddSingleton<ArtworkPropertyUpdateBatcher>();
            services.AddSingleton<PersistentArtworkThumbnailStore>();
            services.AddSingleton<ArtworkImageService>();
            services.AddSingleton<PerformanceOverlayViewModel>();
            services.AddSingleton<DiagnosticsBundleExporter>();
            services.AddSingleton<UserDataBackupService>();
            services.AddSingleton<DatabaseRecoveryService>();
            services.AddSingleton<DuplicateDetectionService>();
            services.AddSingleton<AppPaths>();
            services.AddSingleton<IApplicationLog>(x =>
                new StructuredApplicationLog(x.GetRequiredService<AppPaths>()));
            services.AddSingleton<ISettingsService, JsonSettingsService>();
            services.AddSingleton<SqliteLibraryRepository>();
            services.AddSingleton<ILibraryRepository>(x => new DiagnosticLibraryRepository(
                x.GetRequiredService<SqliteLibraryRepository>(),
                x.GetRequiredService<DeveloperDiagnostics>()));
            services.AddSingleton<SqlitePlaylistRepository>();
            services.AddSingleton<IPlaylistRepository>(x => new DiagnosticPlaylistRepository(
                x.GetRequiredService<SqlitePlaylistRepository>(),
                x.GetRequiredService<DeveloperDiagnostics>()));
            services.AddSingleton<IPlaylistInterchangeService, PlaylistInterchangeService>();
            services.AddSingleton<IPlaylistFileService, PlaylistFileService>();
            services.AddSingleton<ITrackMetadataReader, TagLibMetadataReader>();
            services.AddSingleton<ArtworkCache>();
            services.AddSingleton<IArtworkCache>(x => new DiagnosticArtworkCache(
                x.GetRequiredService<ArtworkCache>(),
                x.GetRequiredService<DeveloperDiagnostics>()));
            services.AddSingleton<ILibraryScanner, LibraryScanner>();
            services.AddSingleton<IAudioEngine, WasapiAudioEngine>();
            services.AddSingleton<IPlaybackQueue, PlaybackQueue>();
            services.AddSingleton<ISleepTimerService, SleepTimerService>();
            services.AddSingleton<IShortcutService, WindowsShortcutService>();
            services.AddSingleton<ISystemMediaTransportService, SystemMediaTransportService>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        })
        .Build();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var benchmark = PerformanceBenchmarkOptions.Parse(e.Args);
        var paths = _host.Services.GetRequiredService<AppPaths>();
        var diagnostics = _host.Services.GetRequiredService<DeveloperDiagnostics>();
        var applicationLog = _host.Services.GetRequiredService<IApplicationLog>();
        var startupRecoveryGuard =
            _startupRecoveryGuard = new StartupRecoveryGuard(paths);
        var safeMode = startupRecoveryGuard.Begin(
            e.Args.Any(argument => argument.Equals(
                "--safe-mode",
                StringComparison.OrdinalIgnoreCase)));
        diagnostics.Configure(DeveloperDiagnosticsOptions.Parse(e.Args, benchmark), paths);
        var instanceHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root)))[..16];
        _singleInstance = new SingleInstanceCoordinator(
            $"Dextromethorphan-{Environment.UserName}-{instanceHash}");
        if (!await _singleInstance.AcquireOrForwardAsync(e.Args))
        {
            await applicationLog.CompleteAsync();
            Shutdown(0);
            return;
        }
        _singleInstance.ArgumentsReceived += SingleInstanceOnArgumentsReceived;
        applicationLog.Write(
            ApplicationLogLevel.Information,
            "startup",
            "process-started",
            new Dictionary<string, object?>
            {
                ["version"] = typeof(App).Assembly.GetName().Version?.ToString(),
                ["arguments"] = e.Args.Length
            });
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                diagnostics.Error("runtime", "appdomain.unhandled", exception, new Dictionary<string, object?> { ["terminating"] = args.IsTerminating });
                applicationLog.Write(
                    args.IsTerminating ? ApplicationLogLevel.Critical : ApplicationLogLevel.Error,
                    "runtime",
                    "appdomain-unhandled",
                    new Dictionary<string, object?> { ["terminating"] = args.IsTerminating },
                    exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            diagnostics.Error("runtime", "task.unobserved", args.Exception);
            applicationLog.Write(
                ApplicationLogLevel.Error,
                "runtime",
                "task-unobserved",
                exception: args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                diagnostics.Error("runtime", "dispatcher.unhandled", args.Exception,
                    MainWindow is MainWindow main ? new Dictionary<string, object?> { ["view"] = main.ViewModel.CurrentView } : null);
                applicationLog.Write(
                    ApplicationLogLevel.Error,
                    "runtime",
                    "dispatcher-unhandled",
                    MainWindow is MainWindow active
                        ? new Dictionary<string, object?> { ["view"] = active.ViewModel.CurrentView }
                        : null,
                    args.Exception);
                paths.EnsureCreated();
                File.AppendAllText(Path.Combine(paths.Logs, "errors.log"), $"[{DateTimeOffset.Now:O}] {args.Exception}\n\n");
            }
            catch { }
            var canContinue = ErrorContinuationPolicy.CanContinue(args.Exception);
            ErrorDialogResult result;
            try
            {
                result = ErrorDialog.Show(
                    MainWindow,
                    args.Exception,
                    paths.Logs,
                    canContinue,
                    canContinue
                        ? "The operation failed, but the application can continue safely."
                        : "Application state may be inconsistent. Restarting is recommended.");
            }
            catch
            {
                result = ErrorDialogResult.Exit;
            }
            args.Handled = true;
            switch (result)
            {
                case ErrorDialogResult.Continue when canContinue:
                    break;
                case ErrorDialogResult.Restart:
                    RestartApplication();
                    Shutdown(-1);
                    break;
                default:
                    Shutdown(-1);
                    break;
            }
        };
        await _host.StartAsync();
        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        if (safeMode)
            window.ViewModel.EnableSafeMode();
        await window.ViewModel.InitializeShellAsync();
        window.ApplySafeModePresentation();
        applicationLog.Write(ApplicationLogLevel.Information, "startup", "shell-ready");
        var firstContentRendered = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ContentRendered += (_, _) =>
        {
            var renderedAt = DateTimeOffset.UtcNow;
            if (firstContentRendered.TrySetResult(renderedAt))
                diagnostics.RecordDuration("startup", "process-to-first-render", renderedAt - new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero));
        };
        window.Show();
        EnqueueLaunchArguments(e.Args);
        DrainPendingLaunchArguments();
        if (PerformanceOverlayViewModel.IsRequested(e.Args))
            window.PerformanceOverlay.IsVisible = true;
        var windowShownAt = DateTimeOffset.UtcNow;
        window.BeginStartupPresentation();
        var processStartedAt = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        var libraryInitialization = window.ViewModel.InitializeLibraryAsync();
        await window.CompleteStartupPresentationAsync();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var interactiveAt = DateTimeOffset.UtcNow;
        diagnostics.RecordDuration("startup", "process-to-interactive", interactiveAt - processStartedAt);
        if (benchmark is null)
        {
            _ = ObserveLibraryInitializationAsync(
                window,
                libraryInitialization,
                diagnostics,
                applicationLog,
                startupRecoveryGuard,
                _host.Services.GetRequiredService<DatabaseRecoveryService>(),
                processStartedAt);
            return;
        }

        try
        {
            await libraryInitialization;
            var libraryReadyAt = DateTimeOffset.UtcNow;
            applicationLog.Write(ApplicationLogLevel.Information, "startup", "library-ready");
            startupRecoveryGuard.Complete();
            diagnostics.RecordDuration(
                "startup",
                "process-to-library-ready",
                libraryReadyAt - processStartedAt);
            var firstRenderedAt = firstContentRendered.Task.IsCompletedSuccessfully
                ? firstContentRendered.Task.Result
                : interactiveAt;
            await PerformanceBenchmarkRunner.RunAsync(
                window,
                benchmark,
                new StartupPerformanceTimestamps(processStartedAt, windowShownAt, firstRenderedAt, libraryReadyAt, interactiveAt),
                _host.Services.GetRequiredService<IAudioEngine>(),
                _host.Services.GetRequiredService<ISettingsService>());
        }
        catch (Exception exception)
        {
            var directory = Path.GetDirectoryName(benchmark.OutputPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(benchmark.OutputPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                error = exception.GetBaseException().Message,
                exception = exception.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await window.CloseAfterBenchmarkAsync();
        }
    }

    private void SingleInstanceOnArgumentsReceived(
        object? sender,
        IReadOnlyList<string> arguments)
    {
        EnqueueLaunchArguments(arguments);
        _ = Dispatcher.BeginInvoke(DrainPendingLaunchArguments);
    }

    private void EnqueueLaunchArguments(IReadOnlyList<string> arguments) =>
        _pendingLaunchArguments.Enqueue(arguments);

    private void DrainPendingLaunchArguments()
    {
        if (MainWindow is not MainWindow window) return;
        while (_pendingLaunchArguments.TryDequeue(out var arguments))
            ActivateAndOpen(window, arguments);
    }

    private async void ActivateAndOpen(
        MainWindow window,
        IReadOnlyList<string> arguments)
    {
        try
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            var targets = LaunchTargetParser.Extract(arguments);
            if (targets.Count > 0)
                await window.ViewModel.OpenLaunchTargetsAsync(targets);
        }
        catch (Exception exception)
        {
            _host.Services.GetRequiredService<IApplicationLog>().Write(
                ApplicationLogLevel.Warning,
                "launch",
                "forwarded-arguments-failed",
                exception: exception);
        }
    }

    private void RestartApplication()
    {
        try
        {
            if (_singleInstance is not null)
            {
                _singleInstance.ArgumentsReceived -= SingleInstanceOnArgumentsReceived;
                _singleInstance.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _singleInstance = null;
            }
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            var start = new ProcessStartInfo(executable) { UseShellExecute = true };
            foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
                start.ArgumentList.Add(argument);
            Process.Start(start);
        }
        catch { }
    }

    private static async Task ObserveLibraryInitializationAsync(
        MainWindow window,
        Task initialization,
        DeveloperDiagnostics diagnostics,
        IApplicationLog applicationLog,
        StartupRecoveryGuard startupRecoveryGuard,
        DatabaseRecoveryService recovery,
        DateTimeOffset processStartedAt)
    {
        try
        {
            await initialization;
            var readyAt = DateTimeOffset.UtcNow;
            diagnostics.RecordDuration(
                "startup",
                "process-to-library-ready",
                readyAt - processStartedAt);
            applicationLog.Write(ApplicationLogLevel.Information, "startup", "library-ready");
            startupRecoveryGuard.Complete();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            diagnostics.Error("startup", "library-initialize", exception);
            applicationLog.Write(
                ApplicationLogLevel.Error,
                "startup",
                "library-initialize",
                exception: exception);
            if (exception.GetBaseException()
                is not DatabaseCorruptionException)
            {
                window.ViewModel.ReportLibraryInitializationFailure(
                    exception);
                return;
            }

            try
            {
                var choice = await window.Dispatcher.InvokeAsync(() =>
                    DatabaseRecoveryDialog.Show(
                        window,
                        exception,
                        recovery.AvailableBackups.FirstOrDefault()));
                switch (choice)
                {
                    case DatabaseRecoveryChoice.RestoreBackup:
                        await recovery.RestoreLatestBackupAsync();
                        break;
                    case DatabaseRecoveryChoice.Rebuild:
                        await recovery.RebuildFromFilesAsync();
                        break;
                    default:
                        window.ViewModel.ReportLibraryInitializationFailure(
                            exception);
                        return;
                }
                await window.ViewModel.RetryLibraryInitializationAsync();
                startupRecoveryGuard.Complete();
            }
            catch (Exception recoveryException)
            {
                diagnostics.Error(
                    "startup",
                    "library-recovery",
                    recoveryException);
                window.ViewModel.ReportLibraryInitializationFailure(
                    recoveryException);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host.Services.GetRequiredService<DeveloperDiagnostics>().CompleteAsync().GetAwaiter().GetResult();
            _host.Services.GetRequiredService<IApplicationLog>().CompleteAsync().GetAwaiter().GetResult();
            if (_singleInstance is not null)
            {
                _singleInstance.ArgumentsReceived -= SingleInstanceOnArgumentsReceived;
                _singleInstance.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        finally
        {
            _host.Dispose();
            base.OnExit(e);
        }
    }
}
