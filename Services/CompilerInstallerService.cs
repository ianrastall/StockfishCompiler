using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace StockfishCompiler.Services;

public interface ICompilerInstallerService
{
    Task<(bool Installed, string? Path)> IsMSYS2InstalledAsync();
    Task<(bool Success, string InstallPath)> InstallMSYS2Async(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<bool> InstallMSYS2PackagesAsync(string msys2Path, IProgress<string>? progress = null);
    Task<string> GetRecommendedInstallPathAsync();
    string GetMSYS2DownloadUrl();
}

public class CompilerInstallerService(ILogger<CompilerInstallerService> logger) : ICompilerInstallerService
{
    private const string MSYS2_DOWNLOAD_PAGE = "https://www.msys2.org/";
    private const string DEFAULT_INSTALL_PATH = @"C:\msys64";
    
    public Task<(bool Installed, string? Path)> IsMSYS2InstalledAsync()
    {
        var candidates = new[]
        {
            @"C:\msys64",
            @"C:\msys2",
            @"D:\msys64",
            @"D:\msys2",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "msys64")
        };

        foreach (var path in candidates.Where(Directory.Exists))
        {
            var gxx = Path.Combine(path, "mingw64", "bin", "g++.exe");
            var make = Path.Combine(path, "usr", "bin", "make.exe");
            if (File.Exists(gxx) && File.Exists(make))
            {
                return Task.FromResult<(bool Installed, string? Path)>((true, path));
            }
        }

        return Task.FromResult<(bool Installed, string? Path)>((false, null));
    }

    public Task<string> GetRecommendedInstallPathAsync()
    {
        // Check which drive has more space
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .OrderByDescending(d => d.AvailableFreeSpace)
            .ToList();

        foreach (var drive in drives)
        {
            var path = Path.Combine(drive.Name, "msys64");
            if (!Directory.Exists(path))
            {
                return Task.FromResult(path);
            }
        }

        return Task.FromResult(DEFAULT_INSTALL_PATH);
    }

    public string GetMSYS2DownloadUrl() => MSYS2_DOWNLOAD_PAGE;

    public Task<(bool Success, string InstallPath)> InstallMSYS2Async(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        // Check for existing installation first
        var (installed, existingPath) = IsMSYS2InstalledAsync().Result;
        
        if (installed && !string.IsNullOrWhiteSpace(existingPath))
        {
            logger.LogInformation("MSYS2 already installed at {Path}", existingPath);
            progress?.Report($"MSYS2 is already installed at {existingPath}.");
            return Task.FromResult((true, existingPath));
        }

        // Guide user to manual installation
        logger.LogInformation("MSYS2 not found. User needs to install manually from {Url}", MSYS2_DOWNLOAD_PAGE);
        progress?.Report("MSYS2 is not installed.");
        progress?.Report($"Please download and install MSYS2 from: {MSYS2_DOWNLOAD_PAGE}");
        progress?.Report("");
        progress?.Report("Installation steps:");
        progress?.Report("1. Download the installer from the MSYS2 website");
        progress?.Report("2. Run the installer and follow the prompts");
        progress?.Report("3. After installation, open 'MSYS2 MINGW64' from the Start menu");
        progress?.Report("4. Run: pacman -Syu");
        progress?.Report("5. Close and reopen 'MSYS2 MINGW64'");
        progress?.Report("6. Run: pacman -S mingw-w64-x86_64-gcc mingw-w64-x86_64-make");
        progress?.Report("7. Restart this application to detect the compiler");
        
        return Task.FromResult((false, string.Empty));
    }

    public async Task<bool> InstallMSYS2PackagesAsync(string msys2Path, IProgress<string>? progress = null)
    {
        try
        {
            // First, update the package database
            progress?.Report("Updating MSYS2 package database...");
            
            var bashPath = Path.Combine(msys2Path, "usr", "bin", "bash.exe");
            if (!File.Exists(bashPath))
            {
                logger.LogError("bash.exe not found at {Path}", bashPath);
                return false;
            }

            var defaultEnv = ResolveEnvironment(msys2Path, preferClang: false);
            var clangEnv = ResolveEnvironment(msys2Path, preferClang: true);

            // Commands to run
            var commands = new[]
            {
                ("Updating core packages...", "-c \"pacman -Syu --noconfirm\"", defaultEnv),
                ($"Installing {defaultEnv.SystemName} GCC compiler...", $"-c \"pacman -S --noconfirm --needed {defaultEnv.GccPackagePrefix}-gcc\"", defaultEnv),
                ("Installing build tools...", $"-c \"pacman -S --noconfirm --needed {defaultEnv.GccPackagePrefix}-make {defaultEnv.GccPackagePrefix}-toolchain\"", defaultEnv),
                ("Installing Clang compiler (optional)...", $"-c \"pacman -S --noconfirm --needed {clangEnv.ClangPackageName}\"", clangEnv)
            };

            foreach (var (message, command, env) in commands)
            {
                progress?.Report(message);
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = bashPath,
                    Arguments = command,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = msys2Path,
                    EnvironmentVariables =
                    {
                        ["MSYSTEM"] = env.SystemName,
                        ["PATH"] = $"{env.BinPath};{Path.Combine(msys2Path, "usr", "bin")}"
                    }
                };

                using var process = Process.Start(startInfo);
                if (process == null) continue;

                // Add timeout for each package installation
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("Package installation command timed out: {Command}", command);
                    try { process.Kill(true); } catch { }
                    continue;
                }
                
                if (process.ExitCode != 0)
                {
                    logger.LogWarning("Package installation command failed: {Command}", command);
                    // Continue anyway, some packages might be optional
                }
            }

            progress?.Report("Compiler installation complete!");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to install MSYS2 packages");
            progress?.Report($"Package installation error: {ex.Message}");
            return false;
        }
    }

    private static MsysEnvironment ResolveEnvironment(string msys2Path, bool preferClang)
    {
        var clangBin = Path.Combine(msys2Path, "clang64", "bin");
        if (preferClang && Directory.Exists(clangBin))
        {
            return new MsysEnvironment("CLANG64", clangBin, "mingw-w64-clang-x86_64", "mingw-w64-clang-x86_64-clang");
        }

        var ucrtBin = Path.Combine(msys2Path, "ucrt64", "bin");
        if (Directory.Exists(ucrtBin))
        {
            return new MsysEnvironment("UCRT64", ucrtBin, "mingw-w64-ucrt-x86_64", "mingw-w64-ucrt-x86_64-clang");
        }

        var mingwBin = Path.Combine(msys2Path, "mingw64", "bin");
        return new MsysEnvironment("MINGW64", mingwBin, "mingw-w64-x86_64", "mingw-w64-x86_64-clang");
    }

    private readonly record struct MsysEnvironment(string SystemName, string BinPath, string GccPackagePrefix, string ClangPackageName);
}
