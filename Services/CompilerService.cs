using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Extensions.Logging;
using StockfishCompiler.Models;

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

        logger.LogInformation("Total compilers found before deduplication: {Count}", compilers.Count);
        
        // Distinct by Path + Name
        var uniqueCompilers = compilers
            .GroupBy(c => c.Path + "|" + c.Name)
            .Select(g => g.First())
            .ToList();

        logger.LogInformation("Unique compilers after deduplication: {Count}", uniqueCompilers.Count);
        return uniqueCompilers;
    }

    public Task<bool> ValidateCompilerAsync(CompilerInfo compiler)
    {
        var exe = compiler.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? compiler.Name : compiler.Name + ".exe";
        var fullPath = string.IsNullOrEmpty(compiler.Path) ? exe : Path.Combine(compiler.Path, exe);
        return Task.FromResult(File.Exists(fullPath));
    }

    public async Task<string> GetCompilerVersionAsync(string compilerPath)
    {
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

            using var process = Process.Start(psi);
            if (process == null)
                return string.Empty;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var firstLine = output.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            return firstLine;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to get version for {Path}", compilerPath);
            return string.Empty;
        }
    }

    private async Task<List<CompilerInfo>> DetectMSYS2CompilersAsync()
    {
        logger.LogInformation("Searching for MSYS2 installations");
        
        // Wrap drive scanning in Task.Run to avoid UI freeze on network/sleeping drives
        var possiblePaths = await Task.Run(() =>
        {
            var paths = new List<string>();
            
            try
            {
                // Search common locations across all drives
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .Select(d => d.Name.TrimEnd('\\'))
                    .ToList();

                logger.LogInformation("Searching drives: {Drives}", string.Join(", ", drives));

                foreach (var drive in drives)
                {
                    // Add root level msys2/msys64 paths
                    paths.Add(Path.Combine(drive, "msys64"));
                    paths.Add(Path.Combine(drive, "msys2"));
                    
                    // Check Program Files
                    paths.Add(Path.Combine(drive, "Program Files", "msys64"));
                    paths.Add(Path.Combine(drive, "Program Files", "msys2"));
                    
                    // Check tools directories
                    paths.Add(Path.Combine(drive, "tools", "msys64"));
                    paths.Add(Path.Combine(drive, "tools", "msys2"));
                }

                // Add user profile paths
                paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "msys64"));
                paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "msys2"));
                paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "msys64"));
                paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "msys2"));

                // Check environment variable
                var msysEnvVar = Environment.GetEnvironmentVariable("MSYS2_PATH");
                if (!string.IsNullOrEmpty(msysEnvVar))
                {
                    paths.Add(msysEnvVar);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error scanning drives for MSYS2");
            }
            
            // Remove duplicates and check which paths exist
            return paths.Distinct(StringComparer.OrdinalIgnoreCase)
                       .Where(Directory.Exists)
                       .ToList();
        });

        List<CompilerInfo> compilers = [];
        
        foreach (var basePath in possiblePaths)
        {
            logger.LogInformation("Found MSYS2 at: {Path}", basePath);
            
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
            @"C:\Program Files\Git\mingw64\bin",
            @"C:\Program Files (x86)\Git\mingw64\bin",
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
                    $@"C:\Program Files\Microsoft Visual Studio\{year}\{edition}\VC\Tools\Llvm\x64\bin",
                    $@"C:\Program Files\Microsoft Visual Studio\{year}\{edition}\VC\Tools\Llvm\bin",
                    $@"C:\Program Files (x86)\Microsoft Visual Studio\{year}\{edition}\VC\Tools\Llvm\x64\bin",
                    $@"C:\Program Files (x86)\Microsoft Visual Studio\{year}\{edition}\VC\Tools\Llvm\bin"
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
                    $@"C:\Program Files\Microsoft Visual Studio\{year}\{edition}\VC\Tools\MSVC",
                    $@"C:\Program Files (x86)\Microsoft Visual Studio\{year}\{edition}\VC\Tools\MSVC"
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
            @"C:\MinGW\bin",
            @"C:\MinGW-w64\bin",
            @"C:\mingw64\bin",
            @"D:\MinGW\bin",
            @"D:\mingw64\bin",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MinGW", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MinGW", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mingw64", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "mingw64", "bin")
        };

        // Check all drives for common MinGW locations
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.Name.TrimEnd('\\'));

        foreach (var drive in drives)
        {
            mingwPaths.Add(Path.Combine(drive, "MinGW", "bin"));
            mingwPaths.Add(Path.Combine(drive, "mingw64", "bin"));
            mingwPaths.Add(Path.Combine(drive, "MinGW-w64", "bin"));
        }

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
        var version = await GetCompilerVersionAsync(compilerFullPath);
        var shortVersion = version.Length > 50 ? version.Substring(0, 50) + "..." : version;
        
        return new CompilerInfo
        {
            Name = Path.GetFileName(compilerFullPath),
            Type = type,
            Version = shortVersion,
            Path = Path.GetDirectoryName(compilerFullPath) ?? string.Empty,
            DisplayName = $"{type.ToUpper()} - {Path.GetDirectoryName(compilerFullPath)}",
            IsAvailable = true
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
