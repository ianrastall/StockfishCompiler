using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace StockfishCompiler.Services;

public interface ICompilerInstallerService
{
    Task<bool> IsMSYS2InstalledAsync();
    Task<(bool Success, string InstallPath)> InstallMSYS2Async(IProgress<string>? progress = null);
    Task<bool> InstallMSYS2PackagesAsync(string msys2Path, IProgress<string>? progress = null);
    Task<string> GetRecommendedInstallPathAsync();
}

public class CompilerInstallerService(ILogger<CompilerInstallerService> logger, HttpClient httpClient) : ICompilerInstallerService
{
    private const string MSYS2_INSTALLER_URL = "https://github.com/msys2/msys2-installer/releases/latest/download/msys2-x86_64-latest.exe";
    private const string DEFAULT_INSTALL_PATH = @"C:\msys64";
    
    public Task<bool> IsMSYS2InstalledAsync()
    {
        var commonPaths = new[]
        {
            @"C:\msys64",
            @"C:\msys2",
            @"D:\msys64",
            @"D:\msys2",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "msys64")
        };

        return Task.FromResult(commonPaths.Any(Directory.Exists));
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

    public async Task<(bool Success, string InstallPath)> InstallMSYS2Async(IProgress<string>? progress = null)
    {
        try
        {
            progress?.Report("Checking for existing MSYS2 installation...");
            
            if (await IsMSYS2InstalledAsync())
            {
                logger.LogInformation("MSYS2 already installed");
                progress?.Report("MSYS2 is already installed on your system.");
                return (true, DEFAULT_INSTALL_PATH);
            }

            var installPath = await GetRecommendedInstallPathAsync();
            progress?.Report($"Installing MSYS2 to {installPath}...");

            // Download the installer
            var tempPath = Path.Combine(Path.GetTempPath(), "msys2-installer.exe");
            
            progress?.Report("Downloading MSYS2 installer...");
            logger.LogInformation("Downloading MSYS2 from {Url}", MSYS2_INSTALLER_URL);

            using (var response = await httpClient.GetAsync(MSYS2_INSTALLER_URL, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                
                await stream.CopyToAsync(fileStream);
            }

            progress?.Report("Download complete. Starting installation...");

            // Run the installer silently
            var installArgs = $"install --root {installPath} --confirm-command";
            
            var startInfo = new ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = installArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                logger.LogError("Failed to start MSYS2 installer");
                return (false, string.Empty);
            }

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                logger.LogError("MSYS2 installation failed with exit code {ExitCode}", process.ExitCode);
                progress?.Report("Installation failed. Please try installing MSYS2 manually.");
                return (false, string.Empty);
            }

            // Clean up temp file
            try { File.Delete(tempPath); } catch { }

            progress?.Report("MSYS2 installed successfully!");
            
            // Install required packages
            progress?.Report("Installing C++ compilers...");
            await InstallMSYS2PackagesAsync(installPath, progress);

            return (true, installPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to install MSYS2");
            progress?.Report($"Installation error: {ex.Message}");
            return (false, string.Empty);
        }
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

            // Commands to run
            var commands = new[]
            {
                ("Updating core packages...", "-c \"pacman -Syu --noconfirm\""),
                ("Installing MinGW-w64 GCC compiler...", "-c \"pacman -S --noconfirm --needed mingw-w64-x86_64-gcc\""),
                ("Installing build tools...", "-c \"pacman -S --noconfirm --needed mingw-w64-x86_64-make mingw-w64-x86_64-toolchain\""),
                ("Installing Clang compiler (optional)...", "-c \"pacman -S --noconfirm --needed mingw-w64-clang-x86_64-clang\"")
            };

            foreach (var (message, command) in commands)
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
                        ["MSYSTEM"] = "MINGW64",
                        ["PATH"] = $"{Path.Combine(msys2Path, "mingw64", "bin")};{Path.Combine(msys2Path, "usr", "bin")}"
                    }
                };

                using var process = Process.Start(startInfo);
                if (process == null) continue;

                await process.WaitForExitAsync();
                
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
}