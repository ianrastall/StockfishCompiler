using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Extensions.Logging;
using StockfishCompiler.Models;
using StockfishCompiler.Helpers;

namespace StockfishCompiler.Services;

public class CompilerService(ILogger<CompilerService> logger) : ICompilerService
{
    private static readonly string[] PathCandidates = ["g++", "clang++", "gcc", "clang"];

    public async Task<List<CompilerInfo>> DetectCompilersAsync()
    {
        logger.LogInformation("Starting comprehensive compiler detection");
        List<CompilerInfo> compilers = [];

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            compilers.AddRange(await DetectMSYS2CompilersAsync());
            compilers.AddRange(await DetectGitForWindowsCompilersAsync());
            compilers.AddRange(await DetectVisualStudioCompilersAsync());
            compilers.AddRange(await DetectMinGWStandaloneAsync());
            compilers.AddRange(await DetectPathCompilersAsync());
        }
        else
        {
            compilers.AddRange(await DetectUnixCompilersAsync());
        }

        logger.LogInformation("Total compilers found before validation: {Count}", compilers.Count);
        
        // Filter out compilers that aren't actually functional
        var validCompilers = new List<CompilerInfo>();
        foreach (var compiler in compilers)
        {
            if (compiler.IsAvailable)
            {
                validCompilers.Add(compiler);
            }
            else
            {
                logger.LogWarning("Excluding non-functional compiler: {DisplayName} - {ValidationError}", 
                    compiler.DisplayName, compiler.ValidationError);
            }
        }
        
        logger.LogInformation("Compilers after validation: {Count}", validCompilers.Count);
        
        // Distinct by Path + Name
        var uniqueCompilers = validCompilers
            .GroupBy(c => c.Path + "|" + c.Name)
            .Select(g => g.First())
            .ToList();

        logger.LogInformation("Unique compilers after deduplication: {Count}", uniqueCompilers.Count);
        return uniqueCompilers;
    }

    public async Task<bool> ValidateCompilerAsync(CompilerInfo compiler)
    {
        var exe = compiler.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? compiler.Name : compiler.Name + ".exe";
        var fullPath = string.IsNullOrEmpty(compiler.Path) ? exe : Path.Combine(compiler.Path, exe);
        
        if (!File.Exists(fullPath))
            return false;
            
        // Actually try to run the compiler to check for missing DLLs
        var (success, _, _) = await TryRunCompilerAsync(fullPath, compiler.Path);
        return success;
    }

    public async Task<string> GetCompilerVersionAsync(string compilerPath)
    {
        var (_, version, _) = await TryRunCompilerAsync(compilerPath, Path.GetDirectoryName(compilerPath));
        return version;
    }
    
    /// <summary>
    /// Attempts to run the compiler with --version and returns success status, version string, and any error message.
    /// </summary>
    private async Task<(bool Success, string Version, string? Error)> TryRunCompilerAsync(string compilerPath, string? binDirectory)
    {
        // First, check for critical DLLs that GCC/MinGW compilers need
        if (!string.IsNullOrEmpty(binDirectory) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var missingDlls = CheckForRequiredDlls(binDirectory);
            if (missingDlls.Count > 0)
            {
                var errorMsg = $"Missing required DLLs: {string.Join(", ", missingDlls)}";
                logger.LogWarning("Compiler at {Path} is missing DLLs: {Missing}", compilerPath, string.Join(", ", missingDlls));
                return (false, string.Empty, errorMsg);
            }
        }
        
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = compilerPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            // Set up environment to find DLLs - this is critical for MSYS2/MinGW compilers
            if (!string.IsNullOrEmpty(binDirectory) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.EnvironmentVariables["PATH"] = $"{binDirectory};{currentPath}";
            }

            using var process = Process.Start(psi);
            if (process == null)
                return (false, string.Empty, "Failed to start process");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                logger.LogWarning("Timed out while querying compiler version for {Path}", compilerPath);
                return (false, "Timed out", "Process timed out");
            }

            var output = await outputTask;
            var errorOutput = await errorTask;
            
            // Check for common DLL-related errors in stderr
            if (!string.IsNullOrEmpty(errorOutput))
            {
                var lowerError = errorOutput.ToLowerInvariant();
                if (lowerError.Contains("dll") || 
                    lowerError.Contains("not found") || 
                    lowerError.Contains("cannot find") ||
                    lowerError.Contains("0xc000007b") ||  // STATUS_INVALID_IMAGE_FORMAT
                    lowerError.Contains("side-by-side") ||
                    lowerError.Contains("initialization failed"))
                {
                    logger.LogWarning("Compiler at {Path} has DLL/dependency issues: {Error}", compilerPath, errorOutput.Trim());
                    return (false, string.Empty, $"Missing dependencies: {errorOutput.Trim()}");
                }
            }
            
            // Non-zero exit code usually indicates a problem
            if (process.ExitCode != 0)
            {
                // Some compilers return non-zero for --version but still work; check if we got version output
                if (string.IsNullOrWhiteSpace(output))
                {
                    var errorMsg = string.IsNullOrWhiteSpace(errorOutput) 
                        ? $"Exit code {process.ExitCode}" 
                        : errorOutput.Trim();
                    logger.LogWarning("Compiler at {Path} failed with exit code {ExitCode}: {Error}", 
                        compilerPath, process.ExitCode, errorMsg);
                    return (false, string.Empty, errorMsg);
                }
            }

            var firstLine = output.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            
            // Sanity check - version output should contain something recognizable
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return (false, string.Empty, "No version output received");
            }
            
            // Additional sanity check: version should contain "gcc" or "g++" or version number pattern
            var lowerVersion = firstLine.ToLowerInvariant();
            if (!lowerVersion.Contains("gcc") && !lowerVersion.Contains("g++") && 
                !lowerVersion.Contains("clang") && !System.Text.RegularExpressions.Regex.IsMatch(firstLine, @"\d+\.\d+"))
            {
                logger.LogWarning("Compiler at {Path} returned unexpected version output: {Version}", compilerPath, firstLine);
                return (false, string.Empty, $"Unexpected version output: {firstLine}");
            }
            
            return (true, firstLine, null);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2) // File not found
        {
            logger.LogDebug("Compiler not found at {Path}", compilerPath);
            return (false, string.Empty, "File not found");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 193) // Not a valid Win32 application  
        {
            logger.LogWarning("Invalid executable at {Path}: {Message}", compilerPath, ex.Message);
            return (false, string.Empty, "Not a valid executable");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 14001) // Side-by-side configuration error
        {
            logger.LogWarning("Side-by-side configuration error for {Path}: {Message}", compilerPath, ex.Message);
            return (false, string.Empty, "Side-by-side configuration error - DLL dependencies missing");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to run compiler at {Path}", compilerPath);
            return (false, string.Empty, ex.Message);
        }
    }
    
    /// <summary>
    /// Checks for the presence of critical DLLs required by GCC/MinGW compilers.
    /// </summary>
    private List<string> CheckForRequiredDlls(string binDirectory)
    {
        // Critical DLLs that GCC-based compilers need to run
        var requiredDlls = new[]
        {
            "libgcc_s_seh-1.dll",    // GCC runtime (64-bit SEH)
            "libstdc++-6.dll",        // C++ standard library
            "libwinpthread-1.dll"     // Windows pthread implementation
        };
        
        // Alternative names for 32-bit or different configurations
        var alternativeDlls = new Dictionary<string, string[]>
        {
            ["libgcc_s_seh-1.dll"] = ["libgcc_s_dw2-1.dll", "libgcc_s_sjlj-1.dll"]
        };
        
        var missing = new List<string>();
        
        foreach (var dll in requiredDlls)
        {
            var dllPath = Path.Combine(binDirectory, dll);
            if (File.Exists(dllPath))
                continue;
                
            // Check alternatives
            if (alternativeDlls.TryGetValue(dll, out var alternatives))
            {
                if (alternatives.Any(alt => File.Exists(Path.Combine(binDirectory, alt))))
                    continue;
            }
            
            missing.Add(dll);
        }
        
        return missing;
    }

    private async Task<List<CompilerInfo>> DetectMSYS2CompilersAsync()
    {
        logger.LogInformation("Searching for MSYS2 installations (focused scan)");
        
        var possiblePaths = await Task.Run(() =>
        {
            var paths = new List<string>();
            try
            {
                // Priority 1: Environment Variable override
                var msysEnvVar = Environment.GetEnvironmentVariable("MSYS2_PATH");
                if (!string.IsNullOrEmpty(msysEnvVar))
                {
                    paths.Add(msysEnvVar);
                }

                // Priority 2: System drive defaults
                var systemDriveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                paths.Add(Path.Combine(systemDriveRoot, "msys64"));
                paths.Add(Path.Combine(systemDriveRoot, "msys2"));
                paths.Add(Path.Combine(systemDriveRoot, "Program Files", "msys64"));
                paths.Add(Path.Combine(systemDriveRoot, "Program Files", "msys2"));
                paths.Add(Path.Combine(systemDriveRoot, "tools", "msys64"));
                paths.Add(Path.Combine(systemDriveRoot, "tools", "msys2"));
                
                // Priority 3: Other common drive letters
                foreach (var drive in new[] { "D:", "E:", "F:" })
                {
                    paths.Add(Path.Combine(drive, "msys64"));
                    paths.Add(Path.Combine(drive, "msys2"));
                }

                // Priority 4: User profile
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                {
                    paths.Add(Path.Combine(userProfile, "msys64"));
                    paths.Add(Path.Combine(userProfile, "msys2"));
                }

                // Priority 5: LocalAppData
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localAppData))
                {
                    paths.Add(Path.Combine(localAppData, "msys64"));
                    paths.Add(Path.Combine(localAppData, "msys2"));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error while assembling MSYS2 search paths");
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(p =>
                        {
                            try { return Directory.Exists(p); } catch { return false; }
                        })
                        .ToList();
        });

        List<CompilerInfo> compilers = [];
        
        foreach (var basePath in possiblePaths)
        {
            logger.LogInformation("Found MSYS2 candidate: {Path}", basePath);
            
            var msysPaths = new[]
            {
                Path.Combine(basePath, "mingw64", "bin"),
                Path.Combine(basePath, "mingw32", "bin"),
                Path.Combine(basePath, "ucrt64", "bin"),
                Path.Combine(basePath, "clang64", "bin"),
                Path.Combine(basePath, "clang32", "bin"),
                Path.Combine(basePath, "clangarm64", "bin")
            };

            foreach (var path in msysPaths.Where(Directory.Exists))
            {
                logger.LogDebug("Checking MSYS2 path: {Path}", path);
                
                var gcc = Path.Combine(path, "g++.exe");
                var clang = Path.Combine(path, "clang++.exe");

                if (File.Exists(gcc))
                {
                    logger.LogInformation("Found g++ at: {Path}", gcc);
                    var compilerInfo = await CreateCompilerInfoAsync(gcc, "gcc");
                    compilerInfo.DisplayName = $"MSYS2 GCC - {Path.GetFileName(Path.GetDirectoryName(path))} ({basePath})";
                    compilers.Add(compilerInfo);
                }
                if (File.Exists(clang))
                {
                    logger.LogInformation("Found clang++ at: {Path}", clang);
                    var compilerInfo = await CreateCompilerInfoAsync(clang, "clang");
                    compilerInfo.DisplayName = $"MSYS2 Clang - {Path.GetFileName(Path.GetDirectoryName(path))} ({basePath})";
                    compilers.Add(compilerInfo);
                }
            }
        }

        return compilers;
    }

    private async Task<List<CompilerInfo>> DetectGitForWindowsCompilersAsync()
    {
        logger.LogInformation("Searching for Git for Windows");
        List<CompilerInfo> compilers = [];

        var gitPaths = new[]
        {
            @"C:\\Program Files\\Git\\mingw64\\bin",
            @"C:\\Program Files (x86)\\Git\\mingw64\\bin",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "mingw64", "bin")
        };

        foreach (var path in gitPaths.Where(Directory.Exists))
        {
            logger.LogInformation("Found Git for Windows at: {Path}", path);
            
            var gcc = Path.Combine(path, "g++.exe");
            if (File.Exists(gcc))
            {
                logger.LogInformation("Found g++ in Git: {Path}", gcc);
                var compilerInfo = await CreateCompilerInfoAsync(gcc, "gcc");
                compilerInfo.DisplayName = $"Git for Windows GCC ({path})";
                compilers.Add(compilerInfo);
            }
        }

        return compilers;
    }

    private async Task<List<CompilerInfo>> DetectVisualStudioCompilersAsync()
    {
        logger.LogInformation("Searching for Visual Studio compilers");
        List<CompilerInfo> compilers = [];

        // Check for VS Build Tools and full VS installations
        var vsYears = new[] { "2022", "2019", "2017" };
        var vsEditions = new[] { "Community", "Professional", "Enterprise", "BuildTools", "Preview" };
        
        foreach (var year in vsYears)
        {
            foreach (var edition in vsEditions)
            {
                // Check for Clang/LLVM
                var clangPaths = new[]
                {
                    $@"C:\\Program Files\\Microsoft Visual Studio\\{year}\\{edition}\\VC\\Tools\\Llvm\\x64\\bin",
                    $@"C:\\Program Files\\Microsoft Visual Studio\\{year}\\{edition}\\VC\\Tools\\Llvm\\bin",
                    $@"C:\\Program Files (x86)\\Microsoft Visual Studio\\{year}\\{edition}\\VC\\Tools\\Llvm\\x64\\bin",
                    $@"C:\\Program Files (x86)\\Microsoft Visual Studio\\{year}\\{edition}\\VC\\Tools\\Llvm\\bin"
                };

                foreach (var path in clangPaths.Where(Directory.Exists))
                {
                    logger.LogInformation("Found Visual Studio Clang at: {Path}", path);
                    
                    var clang = Path.Combine(path, "clang++.exe");
                    if (File.Exists(clang))
                    {
                        logger.LogInformation("Found clang++ in VS: {Path}", clang);
                        var compilerInfo = await CreateCompilerInfoAsync(clang, "clang");
                        compilerInfo.DisplayName = $"Visual Studio {year} {edition} - Clang/LLVM";
                        compilers.Add(compilerInfo);
                    }
                }

                // Check for MSVC (cl.exe)
                var msvcPaths = new[]
                {
                    $@"C:\\Program Files\\Microsoft Visual Studio\\{year}\\{edition}\\VC\\Tools\\MSVC",
                    $@"C:\\Program Files (x86)\\Microsoft Visual Studio\\{year}\\{edition}\\VC\\Tools\\MSVC"
                };

                foreach (var basePath in msvcPaths.Where(Directory.Exists))
                {
                    try
                    {
                        var msvcVersionDirs = Directory.GetDirectories(basePath);
                        foreach (var versionDir in msvcVersionDirs)
                        {
                            var clPath = Path.Combine(versionDir, "bin", "Hostx64", "x64", "cl.exe");
                            if (File.Exists(clPath))
                            {
                                logger.LogInformation("Found MSVC cl.exe at: {Path}", clPath);
                                var version = await GetMSVCVersionAsync(clPath);
                                var compilerInfo = new CompilerInfo
                                {
                                    Name = "cl.exe",
                                    Type = "msvc",
                                    Version = version,
                                    Path = Path.GetDirectoryName(clPath) ?? string.Empty,
                                    DisplayName = $"Visual Studio {year} {edition} - MSVC ({Path.GetFileName(versionDir)})",
                                    IsAvailable = true
                                };
                                compilers.Add(compilerInfo);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Error checking MSVC path: {Path}", basePath);
                    }
                }
            }
        }

        return compilers;
    }

    private async Task<string> GetMSVCVersionAsync(string clPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = clPath,
                Arguments = "",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return "Unknown version";

            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            // MSVC outputs version info to stderr
            var firstLine = error.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            return firstLine.Length > 100 ? firstLine.Substring(0, 100) + "..." : firstLine;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to get MSVC version for {Path}", clPath);
            return "Unknown version";
        }
    }

    private async Task<List<CompilerInfo>> DetectMinGWStandaloneAsync()
    {
        logger.LogInformation("Searching for standalone MinGW");
        List<CompilerInfo> compilers = [];

        var mingwPaths = new List<string>
        {
            @"C:\\MinGW\\bin",
            @"C:\\MinGW-w64\\bin",
            @"C:\\mingw64\\bin",
            @"D:\\MinGW\\bin",
            @"D:\\mingw64\\bin",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MinGW", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MinGW", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mingw64", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "mingw64", "bin")
        };

        // Keep D: quick probes; avoid scanning all drives exhaustively
        foreach (var path in mingwPaths.Distinct().Where(Directory.Exists))
        {
            logger.LogInformation("Found MinGW at: {Path}", path);
            
            var gcc = Path.Combine(path, "g++.exe");
            if (File.Exists(gcc))
            {
                logger.LogInformation("Found g++ in MinGW: {Path}", gcc);
                var compilerInfo = await CreateCompilerInfoAsync(gcc, "gcc");
                compilerInfo.DisplayName = $"Standalone MinGW GCC ({path})";
                compilers.Add(compilerInfo);
            }
        }

        return compilers;
    }

    private async Task<List<CompilerInfo>> DetectPathCompilersAsync()
    {
        logger.LogInformation("Searching for compilers in PATH");
        List<CompilerInfo> compilers = [];

        foreach (var c in PathCandidates)
        {
            var path = await WhichAsync(c);
            if (!string.IsNullOrEmpty(path))
            {
                logger.LogInformation("Found {Command} in PATH: {Path}", c, path);
                var type = c.Contains("clang") ? "clang" : "gcc";
                var compilerInfo = await CreateCompilerInfoAsync(path, type);
                compilerInfo.DisplayName = $"{type.ToUpper()} in PATH - {Path.GetDirectoryName(path)}";
                compilers.Add(compilerInfo);
            }
        }
        return compilers;
    }

    private async Task<List<CompilerInfo>> DetectUnixCompilersAsync()
    {
        logger.LogInformation("Searching for Unix compilers");
        List<CompilerInfo> compilers = [];
        
        foreach (var c in PathCandidates)
        {
            var path = await WhichAsync(c);
            if (!string.IsNullOrEmpty(path))
            {
                logger.LogInformation("Found {Command}: {Path}", c, path);
                var type = c.Contains("clang") ? "clang" : "gcc";
                compilers.Add(await CreateCompilerInfoAsync(path, type));
            }
        }
        return compilers;
    }

    private async Task<CompilerInfo> CreateCompilerInfoAsync(string compilerFullPath, string type)
    {
        var directory = Path.GetDirectoryName(compilerFullPath);
        var (success, version, error) = await TryRunCompilerAsync(compilerFullPath, directory);
        
        var shortVersion = version.Length > 50 ? version.Substring(0, 50) + "..." : version;
        
        return new CompilerInfo
        {
            Name = Path.GetFileName(compilerFullPath),
            Type = type,
            Version = success ? shortVersion : "(unavailable)",
            Path = directory ?? string.Empty,
            DisplayName = $"{type.ToUpper()} - {directory ?? "Unknown Path"}",
            IsAvailable = success,
            ValidationError = error
        };
    }

    private static readonly string[] LineSeparators = ["\r", "\n"];
    
    private static async Task<string> WhichAsync(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return string.Empty;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var first = output.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return first ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
