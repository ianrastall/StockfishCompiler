using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
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
    }
}
