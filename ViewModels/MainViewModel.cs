using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StockfishCompiler.Models;
using StockfishCompiler.Services;
using StockfishCompiler.Helpers;

namespace StockfishCompiler.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ICompilerService _compilerService;
    private readonly IArchitectureDetector _architectureDetector;
    private readonly ILogger<MainViewModel> _logger;
    private readonly IUserSettingsService _userSettingsService;
    private UserSettings _userSettings = new();
    private bool _isRestoringSettings;

    public MainViewModel(
        ICompilerService compilerService, 
        IArchitectureDetector architectureDetector, 
        ILogger<MainViewModel> logger,
        IUserSettingsService userSettingsService)
    {
        _compilerService = compilerService;
        _architectureDetector = architectureDetector;
        _logger = logger;
        _userSettingsService = userSettingsService;

        _logger.LogInformation("MainViewModel initializing");

        DetectCompilersCommand = new AsyncRelayCommand(DetectCompilersAsync);
        DetectArchitectureCommand = new AsyncRelayCommand(DetectOptimalArchitectureAsync, () => SelectedCompiler != null);

        LoadUserSettings();
        _ = LoadAvailableArchitectures();
        
        _logger.LogInformation("MainViewModel initialized");
    }

    [ObservableProperty]
    private ObservableCollection<CompilerInfo> availableCompilers = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectArchitectureCommand))]
    private CompilerInfo? selectedCompiler;

    [ObservableProperty]
    private ObservableCollection<ArchitectureInfo> availableArchitectures = [];

    [ObservableProperty]
    private ArchitectureInfo? selectedArchitecture;

    [ObservableProperty]
    private string statusMessage = "Ready";

    [ObservableProperty]
    private bool isDetectingCompilers;

    [ObservableProperty]
    private bool isDetectingArchitecture;

    [ObservableProperty]
    private string sourceVersion = "stable";

    [ObservableProperty]
    private bool downloadNetwork = true;

    [ObservableProperty]
    private bool stripExecutable = true;

    [ObservableProperty]
    private int parallelJobs = Environment.ProcessorCount;

    [ObservableProperty]
    private string outputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    [ObservableProperty]
    private string detectionDetails = string.Empty;

    public IAsyncRelayCommand DetectCompilersCommand { get; }
    public IAsyncRelayCommand DetectArchitectureCommand { get; }

    private async Task DetectCompilersAsync()
    {
        try
        {
            IsDetectingCompilers = true;
            DetectionDetails = string.Empty;
            _logger.LogInformation("Starting compiler detection");
            StatusMessage = "Detecting compilers...";
            
            var compilers = await _compilerService.DetectCompilersAsync();
            
            AvailableCompilers = new ObservableCollection<CompilerInfo>(compilers);
            if (compilers.Count > 0)
            {
                SelectedCompiler = compilers[0];
                StatusMessage = $"Found {compilers.Count} compiler{(compilers.Count == 1 ? "" : "s")}";
                DetectionDetails = $"Found {compilers.Count} compiler(s). Check the logs for search details.";
            }
            else
            {
                StatusMessage = "No compilers found";
                DetectionDetails = BuildNoCompilersFoundMessage();
                _logger.LogWarning("No compilers found on system");
            }
            
            _logger.LogInformation("Found {Count} compilers", compilers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting compilers");
            StatusMessage = $"Error detecting compilers: {ex.Message}";
            DetectionDetails = $"Error: {ex.Message}";
        }
        finally
        {
            IsDetectingCompilers = false;
        }
    }

    private static string BuildNoCompilersFoundMessage()
    {
        var msg = "No C++ compilers were found on your system.\n\n";
        msg += "Fastest fix: click \"Download & Install MSYS2 (GCC)\" above to grab everything automatically.\n\n";
        msg += "Searched locations:\n";
        msg += "  - MSYS2 (C:\\msys64, D:\\msys64, etc.)\n";
        msg += "  - Git for Windows MinGW\n";
        msg += "  - Visual Studio Clang/LLVM\n";
        msg += "  - Standalone MinGW installations\n";
        msg += "  - System PATH\n\n";
        msg += "To compile Stockfish, you need MSYS2 with MinGW-w64:\n\n";
        msg += "1. Download MSYS2 from: https://www.msys2.org/\n";
        msg += "2. Install to default location (C:\\msys64)\n";
        msg += "3. Open MSYS2 MSYS terminal and run:\n";
        msg += "   pacman -Syu\n";
        msg += "   pacman -S mingw-w64-x86_64-gcc mingw-w64-x86_64-make\n\n";
        msg += "4. Then click 'Detect Compilers' again.";
        return msg;
    }

    private async Task DetectOptimalArchitectureAsync()
    {
        if (SelectedCompiler is null)
        {
            _logger.LogWarning("Cannot detect architecture - no compiler selected");
            StatusMessage = "Please select a compiler first";
            return;
        }
        
        try
        {
            IsDetectingArchitecture = true;
            _logger.LogInformation("Detecting optimal architecture for {Compiler}", SelectedCompiler.DisplayName);
            StatusMessage = "Detecting optimal CPU architecture...";
            
            var optimalArch = await _architectureDetector.DetectOptimalArchitectureAsync(SelectedCompiler);
            SelectedArchitecture = AvailableArchitectures.FirstOrDefault(a => a.Id == optimalArch.Id) ?? optimalArch;
            StatusMessage = $"Detected: {optimalArch.Name}";
            
            _logger.LogInformation("Detected architecture: {Architecture}", optimalArch.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting architecture");
            StatusMessage = $"Error detecting architecture: {ex.Message}";
        }
        finally
        {
            IsDetectingArchitecture = false;
        }
    }

    private async Task LoadAvailableArchitectures()
    {
        try
        {
            _logger.LogInformation("Loading available architectures");
            var list = await _architectureDetector.GetAvailableArchitecturesAsync();
            AvailableArchitectures = new ObservableCollection<ArchitectureInfo>(list);
            _logger.LogInformation("Loaded {Count} architectures", list.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading architectures");
        }
    }

    private void LoadUserSettings()
    {
        try
        {
            _isRestoringSettings = true;
            _userSettings = _userSettingsService.Load();

            DownloadNetwork = _userSettings.DownloadNetwork;
            StripExecutable = _userSettings.StripExecutable;
            ParallelJobs = _userSettings.ParallelJobs;

            if (!string.IsNullOrWhiteSpace(_userSettings.OutputDirectory))
                OutputDirectory = _userSettings.OutputDirectory;

            if (!string.IsNullOrWhiteSpace(_userSettings.SourceVersion))
                SourceVersion = _userSettings.SourceVersion;

            _logger.LogInformation("Loaded user settings from {Path}", _userSettingsService.SettingsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load user settings, using defaults");
        }
        finally
        {
            _isRestoringSettings = false;
        }
    }

    private void PersistUserSettings()
    {
        if (_isRestoringSettings)
            return;

        _userSettings.DownloadNetwork = DownloadNetwork;
        _userSettings.StripExecutable = StripExecutable;
        _userSettings.ParallelJobs = ParallelJobs;
        _userSettings.OutputDirectory = OutputDirectory;
        _userSettings.SourceVersion = SourceVersion;

        _userSettingsService.Save(_userSettings);
    }

    partial void OnDownloadNetworkChanged(bool value) => PersistUserSettings();
    partial void OnStripExecutableChanged(bool value) => PersistUserSettings();
    partial void OnParallelJobsChanged(int value) => PersistUserSettings();
    partial void OnOutputDirectoryChanged(string value) => PersistUserSettings();
    partial void OnSourceVersionChanged(string value) => PersistUserSettings();

    public string SystemInfo => $"{OSHelper.GetFriendlyOSName()} | {RuntimeInformation.ProcessArchitecture} | .NET {Environment.Version}";
}
