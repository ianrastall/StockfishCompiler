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
                services.AddTransient<IBuildService, BuildService>(); // Changed to Transient to avoid state corruption
                services.AddSingleton<IUserSettingsService, UserSettingsService>();

                // ViewModels
                services.AddSingleton<MainViewModel>();
                services.AddTransient<BuildViewModel>(); // Changed to Transient since BuildService is now Transient

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
    }
}
