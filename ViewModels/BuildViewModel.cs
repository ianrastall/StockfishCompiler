using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows.Threading;
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
    private readonly Queue<string> _logQueue = new();
    private readonly DispatcherTimer _updateTimer;
    private bool _isDirty = false;
    private readonly object _logLock = new();

    private const int MaxOutputLines = 1000;

    public BuildViewModel(IBuildService buildService, MainViewModel mainViewModel, ILogger<BuildViewModel> logger)
    {
        _buildService = buildService;
        _mainViewModel = mainViewModel;
        _logger = logger;

        _logger.LogInformation("BuildViewModel initializing");

        StartBuildCommand = new AsyncRelayCommand(StartBuildAsync, () => !IsBuilding);
        CancelBuildCommand = new RelayCommand(CancelBuild, () => IsBuilding);

        // Setup UI update timer to throttle output updates (4 times per second max)
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _updateTimer.Tick += (s, e) =>
        {
            if (_isDirty)
            {
                _isDirty = false;
                lock (_logLock)
                {
                    BuildOutput = string.Join(Environment.NewLine, _logQueue);
                }
            }
        };
        _updateTimer.Start();

        // Marshal all observable subscriptions to UI thread
        var uiScheduler = new SynchronizationContextScheduler(SynchronizationContext.Current!);
        
        _subscriptions.Add(_buildService.Output
            .ObserveOn(uiScheduler)
            .Subscribe(line => 
            {
                AppendOutput(line);
            }));

        _subscriptions.Add(_buildService.Progress
            .ObserveOn(uiScheduler)
            .Subscribe(p => BuildProgress = p));
        
        _subscriptions.Add(_buildService.IsBuilding
            .ObserveOn(uiScheduler)
            .Subscribe(b => 
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

    private void AppendOutput(string line)
    {
        lock (_logLock)
        {
            _logQueue.Enqueue(line);
            while (_logQueue.Count > MaxOutputLines)
            {
                _logQueue.Dequeue();
            }
        }
        _isDirty = true;
    }

    private async Task StartBuildAsync()
    {
        // Clear output queue and display
        lock (_logLock)
        {
            _logQueue.Clear();
        }
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
            AppendOutput("\n==============================================");
            AppendOutput("Compilation successful!");
            AppendOutput("==============================================");
        }
        else
        {
            _logger.LogError("Build failed with exit code {ExitCode}", result.ExitCode);
            AppendOutput("\n==============================================");
            AppendOutput("Compilation failed!");
            AppendOutput($"Exit code: {result.ExitCode}");
            AppendOutput("==============================================");
        }
    }

    private void CancelBuild()
    {
        _logger.LogInformation("Build cancelled by user");
        _buildService.CancelBuild();
        AppendOutput("\n[Build cancelled by user]");
    }

    public void Dispose()
    {
        _updateTimer?.Stop();
        
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
        GC.SuppressFinalize(this);
    }
}
