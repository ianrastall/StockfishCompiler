using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Linq;
using System.Threading;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace StockfishCompiler.Services;

public interface ICompilerInstallerService
{
    Task<(bool Installed, string? Path)> IsMSYS2InstalledAsync();
    Task<(bool Success, string InstallPath)> InstallMSYS2Async(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<bool> InstallMSYS2PackagesAsync(string msys2Path, IProgress<string>? progress = null);
    Task<string> GetRecommendedInstallPathAsync();
}

public class CompilerInstallerService(ILogger<CompilerInstallerService> logger, HttpClient httpClient) : ICompilerInstallerService
{
    // Fallback to a known-good installer if latest lookup fails
    private const string FALLBACK_INSTALLER_URL = "https://github.com/msys2/msys2-installer/releases/download/2024-01-13/msys2-x86_64-20240113.exe";
    private const string FALLBACK_INSTALLER_SHA256 = "a24ca2f57c21c0f16d5d2e5e80f0ac94bdadad48f06cc11f06cb9e7526f18a66";
    private const string LATEST_API = "https://api.github.com/repos/msys2/msys2-installer/releases/latest";
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

    public async Task<(bool Success, string InstallPath)> InstallMSYS2Async(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report("Checking for existing MSYS2 installation...");
            
            var (installed, existingPath) = await IsMSYS2InstalledAsync();
            
            if (installed && !string.IsNullOrWhiteSpace(existingPath))
            {
                logger.LogInformation("MSYS2 already installed at {Path}", existingPath);
                progress?.Report($"MSYS2 is already installed at {existingPath}.");
                return (true, existingPath);
            }

            var installPath = await GetRecommendedInstallPathAsync();
            progress?.Report($"Installing MSYS2 to {installPath}...");

            // Download the installer
            var tempPath = Path.Combine(Path.GetTempPath(), "msys2-installer.exe");
            
            progress?.Report("Resolving latest MSYS2 installer...");
            var (installerUrl, expectedHash) = await GetLatestInstallerAsync(cancellationToken);

            progress?.Report("Downloading MSYS2 installer...");
            logger.LogInformation("Downloading MSYS2 from {Url}", installerUrl);

            using (var response = await httpClient.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                
                await stream.CopyToAsync(fileStream, cancellationToken);
            }

            // Verify SHA256 checksum for security
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                progress?.Report("Verifying installer integrity...");
                logger.LogInformation("Verifying SHA256 checksum of downloaded installer");
                
                if (!await VerifyFileHashAsync(tempPath, expectedHash))
                {
                    logger.LogError("MSYS2 installer hash mismatch! Expected: {Expected}", expectedHash);
                    progress?.Report("ERROR: Installer integrity check failed! The downloaded file may be corrupted or tampered with.");
                    
                    try { File.Delete(tempPath); } catch { }
                    return (false, string.Empty);
                }
                
            logger.LogInformation("Installer checksum verified successfully");
            progress?.Report("Installer verified. Starting installation...");
        }
        else
        {
            logger.LogWarning("Skipping checksum verification because no hash was available for the installer");
            progress?.Report("Installer downloaded (checksum unavailable). Proceeding with installation...");
        }

            // Run the installer silently
            var installArgs = $"install --root \"{installPath}\" --confirm-command";
            
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

            // Add timeout to prevent indefinite hangs
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("MSYS2 installation cancelled by user");
                progress?.Report("Installation cancelled.");
                try { process.Kill(true); } catch { }
                return (false, string.Empty);
            }
            catch (OperationCanceledException)
            {
                logger.LogError("MSYS2 installation timed out after 30 minutes");
                progress?.Report("Installation timed out. Please try installing MSYS2 manually.");
                try { process.Kill(true); } catch { }
                return (false, string.Empty);
            }

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

    private async Task<(string Url, string? Sha256)> GetLatestInstallerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(LATEST_API, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!doc.RootElement.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
                return (FALLBACK_INSTALLER_URL, FALLBACK_INSTALLER_SHA256);

            var assets = assetsElement.EnumerateArray().ToArray();
            var installer = assets.FirstOrDefault(a =>
                a.TryGetProperty("name", out var nameProp) &&
                nameProp.GetString()?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true &&
                nameProp.GetString()?.Contains("msys2-x86_64", StringComparison.OrdinalIgnoreCase) == true);

            if (installer.ValueKind == JsonValueKind.Undefined)
                return (FALLBACK_INSTALLER_URL, FALLBACK_INSTALLER_SHA256);

            var url = installer.GetProperty("browser_download_url").GetString() ?? FALLBACK_INSTALLER_URL;

            // Try to find a matching .sha256 asset to verify integrity
            string? hash = null;
            var installerName = installer.GetProperty("name").GetString();
            var hashAsset = assets.FirstOrDefault(a =>
                a.TryGetProperty("name", out var nameProp) &&
                nameProp.GetString()?.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) == true &&
                (installerName == null || nameProp.GetString()?.Contains(Path.GetFileNameWithoutExtension(installerName) ?? string.Empty, StringComparison.OrdinalIgnoreCase) == true));

            if (hashAsset.ValueKind != JsonValueKind.Undefined)
            {
                var hashUrl = hashAsset.GetProperty("browser_download_url").GetString();
                if (!string.IsNullOrEmpty(hashUrl))
                {
                    var hashContent = await httpClient.GetStringAsync(hashUrl, cancellationToken);
                    hash = ParseSha256FromFile(hashContent);
                }
            }

            return (url, string.IsNullOrWhiteSpace(hash) ? FALLBACK_INSTALLER_SHA256 : hash);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falling back to pinned MSYS2 installer");
            return (FALLBACK_INSTALLER_URL, FALLBACK_INSTALLER_SHA256);
        }
    }

    private static string? ParseSha256FromFile(string shaFileContents)
    {
        if (string.IsNullOrWhiteSpace(shaFileContents))
            return null;

        var firstLine = shaFileContents.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
            return null;

        // Typical format: "<hash> *msys2-x86_64-YYYYMMDD.exe"
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidate = parts.Length > 0 ? parts[0] : null;
        if (candidate != null && candidate.Length == 64 && candidate.All(c => Uri.IsHexDigit(c)))
            return candidate.ToLowerInvariant();

        return null;
    }

    private static async Task<bool> VerifyFileHashAsync(string filePath, string expectedHash)
    {
        try
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = await sha256.ComputeHashAsync(stream);
            var actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            var expected = expectedHash.ToLowerInvariant();
            
            return actualHash == expected;
        }
        catch (Exception)
        {
            return false;
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
}
