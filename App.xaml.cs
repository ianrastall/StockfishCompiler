using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Windows;
using StockfishCompiler.Services;
using StockfishCompiler.ViewModels;

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
                CleanupStaleTempDirectories();

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

                // Services
                services.AddSingleton<ICompilerService, CompilerService>();
                services.AddSingleton<ICompilerInstallerService, CompilerInstallerService>();
                services.AddSingleton<IArchitectureDetector, ArchitectureDetector>();
                services.AddSingleton<IBuildService, BuildService>(); // Singleton to match BuildViewModel lifetime
                services.AddSingleton<IUserSettingsService, UserSettingsService>();

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
                Log.Information("Application startup complete");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application failed to start");
                MessageBox.Show(
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
            Services?.Dispose();
            Log.CloseAndFlush();
            base.OnExit(e);
        }

        private void Application_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unhandled exception occurred");
            MessageBox.Show(
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
            var sentinelDir = Path.Combine(Path.GetTempPath(), $"sf_sentinel_{Process.GetCurrentProcess().Id}");

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
