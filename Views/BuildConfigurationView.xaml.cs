using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using StockfishCompiler.ViewModels;

namespace StockfishCompiler.Views
{
    public partial class BuildConfigurationView : UserControl
    {
        public BuildConfigurationView()
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
    }
}
