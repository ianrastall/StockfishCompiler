using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StockfishCompiler.Models;
using StockfishCompiler.Services;

namespace StockfishCompiler.ViewModels;

public partial class BuildViewModel : ObservableObject, IDisposable
{
    private readonly IBuildService _buildService;
    private readonly MainViewModel _mainViewModel;
    private readonly ILogger<BuildViewModel> _logger;
    private readonly List<IDisposable> _subscriptions = new();

    public BuildViewModel(IBuildService buildService, MainViewModel mainViewModel, ILogger<BuildViewModel> logger)
    {
        _buildService = buildService;
        _mainViewModel = mainViewModel;
        _logger = logger;

        _logger.LogInformation("BuildViewModel initializing");

        StartBuildCommand = new AsyncRelayCommand(StartBuildAsync, () => !IsBuilding);
        CancelBuildCommand = new RelayCommand(CancelBuild, () => IsBuilding);

        _subscriptions.Add(_buildService.Output.Subscribe(line => 
        {
            BuildOutput += line + Environment.NewLine;
        }));

        _subscriptions.Add(_buildService.Progress.Subscribe(p => BuildProgress = p));
        
        _subscriptions.Add(_buildService.IsBuilding.Subscribe(b => 
        {
            IsBuilding = b;
            StartBuildCommand.NotifyCanExecuteChanged();
            CancelBuildCommand.NotifyCanExecuteChanged();
        }));

        _logger.LogInformation("BuildViewModel initialized");
    }

    [ObservableProperty]
    private string buildOutput = string.Empty;

    [ObservableProperty]
    private double buildProgress = 0;

    [ObservableProperty]
    private bool isBuilding = false;

    public IAsyncRelayCommand StartBuildCommand { get; }
    public IRelayCommand CancelBuildCommand { get; }

    private async Task StartBuildAsync()
    {
        BuildOutput = string.Empty;
        BuildProgress = 0;

        _logger.LogInformation("Starting build process");

        var config = new BuildConfiguration
        {
            SelectedCompiler = _mainViewModel.SelectedCompiler,
            SelectedArchitecture = _mainViewModel.SelectedArchitecture,
            SourceVersion = _mainViewModel.SourceVersion,
            DownloadNetwork = _mainViewModel.DownloadNetwork,
            StripExecutable = _mainViewModel.StripExecutable,
            ParallelJobs = _mainViewModel.ParallelJobs,
            OutputDirectory = _mainViewModel.OutputDirectory
        };

        if (config.SelectedCompiler == null)
        {
            _logger.LogWarning("Build aborted - no compiler selected");
            BuildOutput = "Error: No compiler selected. Please go to Compiler Setup tab and detect compilers.\n";
            return;
        }

        if (config.SelectedArchitecture == null)
        {
            _logger.LogWarning("Build aborted - no architecture selected");
            BuildOutput = "Error: No architecture selected. Please go to Compiler Setup tab and detect architecture.\n";
            return;
        }

        _logger.LogInformation("Build configuration: Compiler={Compiler}, Arch={Arch}, Version={Version}, Jobs={Jobs}",
            config.SelectedCompiler.DisplayName,
            config.SelectedArchitecture.Id,
            config.SourceVersion,
            config.ParallelJobs);

        var result = await _buildService.BuildAsync(config);

        if (result.Success)
        {
            _logger.LogInformation("Build completed successfully");
            BuildOutput += "\n==============================================\n";
            BuildOutput += "Compilation successful!\n";
            BuildOutput += "==============================================\n";
        }
        else
        {
            _logger.LogError("Build failed with exit code {ExitCode}", result.ExitCode);
            BuildOutput += "\n==============================================\n";
            BuildOutput += "Compilation failed!\n";
            BuildOutput += $"Exit code: {result.ExitCode}\n";
            BuildOutput += "==============================================\n";
        }
    }

    private void CancelBuild()
    {
        _logger.LogInformation("Build cancelled by user");
        _buildService.CancelBuild();
        BuildOutput += "\n[Build cancelled by user]\n";
    }

    public void Dispose()
    {
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
        GC.SuppressFinalize(this);
    }
}
