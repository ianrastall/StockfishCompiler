using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using StockfishCompiler.Models;
using StockfishCompiler.ViewModels;

namespace StockfishCompiler.Views
{
    public partial class BuildProgressView : UserControl
    {
        public BuildProgressView()
        {
            InitializeComponent();
            // Safe runtime resolution; skip during design-time or before App.Services initialized
            if (App.Services != null)
            {
                var vm = App.Services.GetService<BuildViewModel>();
                if (vm != null)
                    DataContext = vm;
            }
        }

        private async void StartBuildButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.Services == null)
                return;

            var buildVm = App.Services.GetService<BuildViewModel>();
            var mainVm = App.Services.GetService<MainViewModel>();
            if (buildVm == null || mainVm == null)
                return;

            var config = new BuildConfiguration
            {
                SelectedCompiler = mainVm.SelectedCompiler,
                SelectedArchitecture = mainVm.SelectedArchitecture,
                SourceVersion = mainVm.SourceVersion,
                DownloadNetwork = mainVm.DownloadNetwork,
                StripExecutable = mainVm.StripExecutable,
                EnablePgo = mainVm.EnablePgo,
                ParallelJobs = mainVm.ParallelJobs,
                OutputDirectory = mainVm.OutputDirectory
            };

            if (buildVm.StartBuildCommand.CanExecute(config))
            {
                await buildVm.StartBuildCommand.ExecuteAsync(config);
            }
        }
    }
}
