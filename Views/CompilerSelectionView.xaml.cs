using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using StockfishCompiler.ViewModels;

namespace StockfishCompiler.Views
{
    public partial class CompilerSelectionView : UserControl
    {
        public CompilerSelectionView()
        {
            InitializeComponent();
            
            if (App.Services != null)
            {
                var mainVm = App.Services.GetService<MainViewModel>();
                if (mainVm != null)
                {
                    DataContext = mainVm;
                }
            }
        }

        private async void InstallCompiler_Click(object sender, RoutedEventArgs e)
        {
            var installerWindow = new CompilerInstallerWindow(App.Services)
            {
                Owner = Window.GetWindow(this)
            };

            var result = installerWindow.ShowDialog();
            if (result == true && installerWindow.CompilerInstalled && DataContext is MainViewModel vm)
            {
                vm.StatusMessage = "Re-running compiler detection after install...";
                await vm.DetectCompilersCommand.ExecuteAsync(null);
            }
        }
    }
}
