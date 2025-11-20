using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using StockfishCompiler.Services;

namespace StockfishCompiler.Views;

public partial class CompilerInstallerWindow : Window
{
    private readonly ICompilerInstallerService _installerService;
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
            var (success, installPath) = await Task.Run(() => 
                _installerService.InstallMSYS2Async(progress));

            if (success)
            {
                CompilerInstalled = true;
                InstalledPath = installPath;
                
                MessageBox.Show(
                    "Compiler installed successfully!\n\n" +
                    $"Location: {installPath}\n\n" +
                    "Click OK to continue with compiler detection.",
                    "Installation Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                
                DialogResult = true;
            }
            else
            {
                MessageBox.Show(
                    "Compiler installation failed.\n\n" +
                    "Please check the log for details or install MSYS2 manually.",
                    "Installation Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"An error occurred during installation:\n\n{ex.Message}",
                "Installation Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isInstalling = false;
            InstallButton.IsEnabled = true;
            InstallProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling)
        {
            var result = MessageBox.Show(
                "Installation is in progress. Are you sure you want to cancel?",
                "Cancel Installation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            
            if (result != MessageBoxResult.Yes) return;
        }

        DialogResult = false;
    }
}