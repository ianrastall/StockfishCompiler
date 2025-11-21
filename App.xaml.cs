using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System.IO;
using System.Net.Http;
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
                services.AddSingleton<HttpClient>(sp =>
                {
                    var client = new HttpClient
                    {
                        Timeout = TimeSpan.FromMinutes(10)
                    };
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("StockfishCompiler/1.0");
                    return client;
                });
                services.AddSingleton<IStockfishDownloader, StockfishDownloader>();

                // Services
                services.AddSingleton<ICompilerService, CompilerService>();
                services.AddSingleton<ICompilerInstallerService, CompilerInstallerService>();
                services.AddSingleton<IArchitectureDetector, ArchitectureDetector>();
                services.AddTransient<IBuildService, BuildService>(); // Transient to avoid state corruption between builds
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
            var tempPath = Path.GetTempPath();
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
            var patterns = new[] { "stockfish_build_*", "sf_prof_*" };

            foreach (var pattern in patterns)
            {
                foreach (var dir in Directory.GetDirectories(tempPath, pattern))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        var lastWrite = info.LastWriteTimeUtc;
                        if (lastWrite < cutoff)
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
    }
}
