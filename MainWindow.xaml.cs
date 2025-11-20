using System.Diagnostics;
using System.IO;
using System.Windows;

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
    }
}