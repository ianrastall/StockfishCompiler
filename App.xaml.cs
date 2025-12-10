using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using StockfishCompiler.Helpers;
using StockfishCompiler.Services;
using StockfishCompiler.ViewModels;
using StockfishCompiler.Views;

namespace StockfishCompiler
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static ServiceProvider Services { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Set up logging first
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StockfishCompiler",
                "logs",
                $"app-{DateTime.Now:yyyy-MM-dd}.log"
            );

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Log.Information("Application starting...");
            Log.Information("Log file: {LogPath}", logPath);

            try
            {
                base.OnStartup(e);

                var services = new ServiceCollection();

                // Logging
                services.AddLogging(builder =>
                {
                    builder.AddSerilog();
                    builder.SetMinimumLevel(LogLevel.Debug);
                });

                // Configure HttpClient properly with timeout and user agent
                services.AddHttpClient<IStockfishDownloader, StockfishDownloader>(client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(10);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("StockfishCompiler/1.0");
                });

                // Register CompilerInstallerService (no longer needs HttpClient)
                services.AddSingleton<ICompilerInstallerService, CompilerInstallerService>();

                // Helpers
                services.AddSingleton<ProcessExecutor>();

                // Services
                services.AddSingleton<ICompilerService, CompilerService>();
                // Note: ICompilerInstallerService is registered above with AddHttpClient
                services.AddSingleton<IArchitectureDetector, ArchitectureDetector>();
                services.AddSingleton<IBuildService, BuildService>(); // Singleton to match BuildViewModel lifetime
                services.AddSingleton<IUserSettingsService, UserSettingsService>();
                services.AddSingleton<NetworkValidationManager>();

                // ViewModels
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<BuildViewModel>(); // Singleton to preserve state when switching tabs

                Services = services.BuildServiceProvider();

                Log.Information("Services configured successfully");

                var window = new MainWindow
                {
                    DataContext = Services.GetRequiredService<MainViewModel>()
                };

                Log.Information("MainWindow created, showing...");
                window.Show();

                // Run cleanup work off the UI thread so slow disk IO doesn't block startup
                _ = Task.Run(() =>
                {
                    try
                    {
                        CleanupStaleTempDirectories();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Background cleanup of temp directories failed");
                    }
                });

                Log.Information("Application startup complete");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application failed to start");
                DarkMessageBox.Show(
                    $"Application failed to start:\n\n{ex.Message}\n\nLog file: {logPath}",
                    "Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                Log.CloseAndFlush();
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("Application exiting with code {ExitCode}", e.ApplicationExitCode);
            
            try
            {
                // Dispose services in reverse dependency order
                DisposeServicesGracefully();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during service disposal");
            }
            
            Services?.Dispose();
            Log.CloseAndFlush();
            base.OnExit(e);
        }

        private static void DisposeServicesGracefully()
        {
            if (Services == null) return;

            // Track disposal for logging
            var disposalTimer = Stopwatch.StartNew();
            var disposedCount = 0;

            try
            {
                // 1. Cancel any active builds first
                var buildService = Services.GetService<IBuildService>();
                if (buildService != null)
                {
                    Log.Information("Cancelling active builds (fire-and-forget)...");
                    try
                    {
                        // Trigger cancellation without blocking UI thread
                        _ = buildService.CancelBuildAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Error triggering build cancellation during shutdown");
                    }

                    // Now dispose the build service
                    if (buildService is IDisposable disposableBuildService)
                    {
                        try
                        {
                            disposableBuildService.Dispose();
                            disposedCount++;
                            Log.Debug("Disposed BuildService");
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Error disposing BuildService");
                        }
                    }
                }

                // 2. Dispose ViewModels (they may hold references to services)
                var viewModelsToDispose = new object?[]
                {
                    Services.GetService<BuildViewModel>(),
                    Services.GetService<MainViewModel>()
                };

                foreach (var vm in viewModelsToDispose)
                {
                    if (vm is IDisposable disposableVm)
                    {
                        try
                        {
                            // Give each ViewModel 500ms to dispose
                            var disposeTask = Task.Run(() => disposableVm.Dispose());
                            if (!disposeTask.Wait(TimeSpan.FromMilliseconds(500)))
                            {
                                Log.Warning("ViewModel disposal timed out: {Type}", vm.GetType().Name);
                            }
                            else
                            {
                                disposedCount++;
                                Log.Debug("Disposed {Type}", vm.GetType().Name);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Error disposing {Type}", vm.GetType().Name);
                        }
                    }
                }

                // 3. Dispose remaining services
                var remainingDisposableServices = new object?[]
                {
                    Services.GetService<IUserSettingsService>(),
                    Services.GetService<ICompilerInstallerService>(),
                    Services.GetService<NetworkValidationManager>()
                };

                foreach (var service in remainingDisposableServices)
                {
                    if (service is IDisposable disposableService)
                    {
                        try
                        {
                            disposableService.Dispose();
                            disposedCount++;
                            Log.Debug("Disposed {Type}", service.GetType().Name);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Error disposing {Type}", service.GetType().Name);
                        }
                    }
                }

                disposalTimer.Stop();
                Log.Information(
                    "Graceful disposal complete: {Count} services disposed in {Duration}ms",
                    disposedCount,
                    disposalTimer.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fatal error during graceful disposal");
            }
        }

        private void Application_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unhandled exception occurred");
            DarkMessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nCheck logs for details.",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            e.Handled = true;
        }

        private static void CleanupStaleTempDirectories()
        {
            var patterns = new[] { "stockfish_build_*", "sf_prof_*" };
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
            var sentinelDir = Path.Combine(Path.GetTempPath(), $"sf_sentinel_{Environment.ProcessId}");

            try
            {
                Directory.CreateDirectory(sentinelDir);

                foreach (var pattern in patterns)
                {
                    foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), pattern))
                    {
                        try
                        {
                            var info = new DirectoryInfo(dir);
                            var hasActiveSentinel = Directory.GetDirectories(Path.GetTempPath(), "sf_sentinel_*")
                                .Where(s => !s.Equals(sentinelDir, StringComparison.OrdinalIgnoreCase))
                                .Any(IsDirectoryLockedByProcess);

                            if (!hasActiveSentinel && info.LastWriteTimeUtc < cutoff)
                            {
                                info.Delete(true);
                                Log.Information("Removed stale temp directory {TempDir}", dir);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Failed to remove stale temp directory {TempDir}", dir);
                        }
                    }
                }
            }
            finally
            {
                try { Directory.Delete(sentinelDir, true); } catch { }
            }
        }

        private static bool IsDirectoryLockedByProcess(string sentinelPath)
        {
            try
            {
                var processIdStr = Path.GetFileName(sentinelPath).Replace("sf_sentinel_", "");
                if (!int.TryParse(processIdStr, out var pid)) return false;

                using var process = Process.GetProcessById(pid);
                return process != null && process.StartTime < DateTime.Now;
            }
            catch
            {
                return false; // Process doesn't exist
            }
        }
    }
}
