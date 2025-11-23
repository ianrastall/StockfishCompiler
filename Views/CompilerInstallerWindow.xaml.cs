using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using StockfishCompiler.Services;

namespace StockfishCompiler.Views;

public partial class CompilerInstallerWindow : Window
{
    private readonly ICompilerInstallerService _installerService;
    private CancellationTokenSource? _cts;
    private bool _isInstalling;

    public bool CompilerInstalled { get; private set; }
    public string? InstalledPath { get; private set; }

    public CompilerInstallerWindow(IServiceProvider serviceProvider)
    {
        InitializeComponent();
        _installerService = serviceProvider.GetRequiredService<ICompilerInstallerService>();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling) return;

        _isInstalling = true;
        _cts = new CancellationTokenSource();
        InstallButton.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
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
            var (success, installPath) = await _installerService.InstallMSYS2Async(progress, _cts.Token);

            if (success)
            {
                CompilerInstalled = true;
                InstalledPath = installPath;
                
                DarkMessageBox.Show(
                    "Compiler installed successfully!\n\n" +
                    $"Location: {installPath}\n\n" +
                    "Click OK to continue with compiler detection.",
                    "Installation Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    this);
                
                DialogResult = true;
            }
            else
            {
                DarkMessageBox.Show(
                    "Compiler installation failed.\n\n" +
                    "Please check the log for details or install MSYS2 manually.",
                    "Installation Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
            }
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"An error occurred during installation:\n\n{ex.Message}",
                "Installation Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                this);
        }
        finally
        {
            _isInstalling = false;
            _cts?.Dispose();
            _cts = null;
            InstallButton.IsEnabled = true;
            InstallProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling)
        {
            var result = DarkMessageBox.Show(
                "Installation is in progress. Are you sure you want to cancel?",
                "Cancel Installation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                this);
            
            if (result != MessageBoxResult.Yes) return;

            _cts?.Cancel();
        }

        DialogResult = false;
    }
}
