using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using StockfishCompiler.ViewModels;
using StockfishCompiler.Views;

namespace StockfishCompiler
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StockfishCompiler",
                "logs"
            );

            if (Directory.Exists(logPath))
            {
                Process.Start("explorer.exe", logPath);
            }
            else
            {
                DarkMessageBox.Show(
                    "No logs directory found yet. Logs will be created when the application runs.",
                    "Logs",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    this
                );
            }
        }

        private void CopyBuildOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.Services == null)
                {
                    DarkMessageBox.Show(
                        "Application services not available.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        this
                    );
                    return;
                }
                
                var buildVm = App.Services.GetService<BuildViewModel>();
                var output = buildVm?.BuildOutput;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Clipboard.SetText(output);
                    DarkMessageBox.Show(
                        "Build output copied to clipboard.",
                        "Copy Output",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information,
                        this
                    );
                }
                else
                {
                    DarkMessageBox.Show(
                        "No build output available yet. Start a build first.",
                        "Copy Output",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information,
                        this
                    );
                }
            }
            catch (Exception ex)
            {
                DarkMessageBox.Show(
                    $"Failed to copy output:\n{ex.Message}",
                    "Copy Output",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this
                );
            }
        }
    }
}
