using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using StockfishCompiler.Services;

namespace StockfishCompiler.Views;

public partial class CompilerInstallerWindow : Window
{
    private readonly ICompilerInstallerService _installerService;

    public bool CompilerInstalled { get; private set; }
    public string? InstalledPath { get; private set; }

    public CompilerInstallerWindow(IServiceProvider serviceProvider)
    {
        InitializeComponent();
        _installerService = serviceProvider.GetRequiredService<ICompilerInstallerService>();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LogTextBox.Clear();

        var progress = new Progress<string>(message =>
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"{DateTime.Now:HH:mm:ss} - {message}\n");
                LogTextBox.ScrollToEnd();
            });
        });

        try
        {
            // Check if already installed
            var (installed, existingPath) = await _installerService.IsMSYS2InstalledAsync();

            if (installed && !string.IsNullOrWhiteSpace(existingPath))
            {
                CompilerInstalled = true;
                InstalledPath = existingPath;
                
                DarkMessageBox.Show(
                    $"MSYS2 is already installed at:\n\n{existingPath}\n\n" +
                    "Click OK to continue with compiler detection.",
                    "MSYS2 Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    this);
                
                DialogResult = true;
                return;
            }

            // Show installation instructions in log
            await _installerService.InstallMSYS2Async(progress);

            // Offer to open the download page
            var result = DarkMessageBox.Show(
                "MSYS2 is not installed.\n\n" +
                "Would you like to open the MSYS2 download page in your browser?\n\n" +
                "After installing MSYS2 and the required packages, restart this application.",
                "Install MSYS2",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                this);

            if (result == MessageBoxResult.Yes)
            {
                var url = _installerService.GetMSYS2DownloadUrl();
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"An error occurred:\n\n{ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                this);
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
