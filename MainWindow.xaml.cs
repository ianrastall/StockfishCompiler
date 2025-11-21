using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using StockfishCompiler.ViewModels;

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
                MessageBox.Show(
                    "No logs directory found yet. Logs will be created when the application runs.",
                    "Logs",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
        }

        private void CopyBuildOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var buildVm = App.Services?.GetService<BuildViewModel>();
                var output = buildVm?.BuildOutput;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Clipboard.SetText(output);
                    MessageBox.Show(
                        "Build output copied to clipboard.",
                        "Copy Output",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
                else
                {
                    MessageBox.Show(
                        "No build output available yet. Start a build first.",
                        "Copy Output",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to copy output:\n{ex.Message}",
                    "Copy Output",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }
    }
}
