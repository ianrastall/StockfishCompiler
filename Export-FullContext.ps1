param(
    [string]$Output = "full-context.txt"
)

# Explicit list of files to include (relative to repository root)
$files = @(
    "StockfishCompiler.csproj",
    "App.xaml",
    "App.xaml.cs",
    "MainWindow.xaml",
    "MainWindow.xaml.cs",
    "AssemblyInfo.cs",

    # Views
    "Views\BuildConfigurationView.xaml",
    "Views\BuildConfigurationView.xaml.cs",
    "Views\BuildProgressView.xaml",
    "Views\BuildProgressView.xaml.cs",
    "Views\CompilerSelectionView.xaml",
    "Views\CompilerSelectionView.xaml.cs",
    "Views\CompilerInstallerWindow.xaml",
    "Views\CompilerInstallerWindow.xaml.cs",

    # ViewModels
    "ViewModels\MainViewModel.cs",
    "ViewModels\BuildViewModel.cs",

    # Models
    "Models\ArchitectureInfo.cs",
    "Models\BuildConfiguration.cs",
    "Models\CompilationResult.cs",
    "Models\CompilerInfo.cs",

    # Services
    "Services\IArchitectureDetector.cs",
    "Services\ArchitectureDetector.cs",
    "Services\IBuildService.cs",
    "Services\BuildService.cs",
    "Services\ICompilerService.cs",
    "Services\CompilerService.cs",
    "Services\IStockfishDownloader.cs",
    "Services\StockfishDownloader.cs",
    "Services\CompilerInstallerService.cs",

    # Converters and Resources
    "Converters\ValueConverters.cs",
    "Resources\DarkTheme.xaml",

    # Scripts and docs helpful for context
    "README.md",
    "TROUBLESHOOTING.md",
    "BuildRelease.bat",
    "OpenLogs.bat"
)

# Create/overwrite output file
"" | Out-File -FilePath $Output -Encoding UTF8

foreach ($f in $files) {
    $fullPath = Join-Path -Path (Get-Location) -ChildPath $f
    $header = "==== BEGIN FILE: $f (" + $fullPath + ") ===="
    $footer = "==== END FILE: $f ===="

    Add-Content -Path $Output -Value $header

    if (Test-Path $fullPath) {
        Get-Content -Raw -Path $fullPath -Encoding UTF8 | Add-Content -Path $Output
    } else {
        Add-Content -Path $Output -Value "[MISSING FILE]"
    }

    Add-Content -Path $Output -Value $footer
    Add-Content -Path $Output -Value ""
}

Write-Host "Wrote full context to $Output"