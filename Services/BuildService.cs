using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using StockfishCompiler.Constants;
using StockfishCompiler.Helpers;
using StockfishCompiler.Models;

namespace StockfishCompiler.Services;

public class BuildService(IStockfishDownloader downloader, ILogger<BuildService> logger) : IBuildService, IDisposable
{
    public IObservable<string> Output => _outputSubject.AsObservable();
    public IObservable<double> Progress => _progressSubject.AsObservable();
    public IObservable<bool> IsBuilding => _isBuildingSubject.AsObservable();

    private readonly Subject<string> _outputSubject = new();
    private readonly Subject<double> _progressSubject = new();
    private readonly Subject<bool> _isBuildingSubject = new();
    private readonly IStockfishDownloader _downloader = downloader;
    private readonly ILogger<BuildService> _logger = logger;
    private CancellationTokenSource? _cts;
    private Task<CompilationResult>? _activeBuildTask;
    private bool _disposed;

    private const int MaxOutputCharacters = 500_000; // safety cap

    /// <summary>
    /// Executes a file operation and optionally fails the build when it cannot complete.
    /// </summary>
    private void ExecuteFileOperation(Action operation, string operationName, bool critical = true)
    {
        try
        {
            operation();
        }
        catch (Exception ex)
        {
            var severity = critical ? "Error" : "Warning";
            _outputSubject.OnNext($"{severity}: {operationName} failed: {ex.Message}");
            if (critical)
            {
                throw;
            }
        }
    }

    public async Task<CompilationResult> BuildAsync(BuildConfiguration configuration)
    {
        _cts = new CancellationTokenSource();
        _isBuildingSubject.OnNext(true);
        _progressSubject.OnNext(0);
        SourceDownloadResult? downloadResult = null;
        var buildTimer = Stopwatch.StartNew();

        try
        {
            var progress = new Progress<string>(msg => _outputSubject.OnNext(msg));
            var token = _cts.Token;

            // Download source
            _outputSubject.OnNext("Downloading Stockfish source...");
            downloadResult = await _downloader.DownloadSourceAsync(configuration.SourceVersion, progress, token);
            var sourceDir = downloadResult.SourceDirectory;
            _progressSubject.OnNext(25);

            if (configuration.DownloadNetwork)
            {
                var networkReady = await _downloader.DownloadNeuralNetworkAsync(sourceDir, configuration, progress, token);
                if (!networkReady)
                    _outputSubject.OnNext("Pre-download failed - make will attempt to fetch the network.");
                _progressSubject.OnNext(networkReady ? 40 : 30);
            }

            ExecuteFileOperation(() =>
            {
                if (CreatePlaceholderNetwork(sourceDir, configuration))
                    _outputSubject.OnNext("Created placeholder network file for LTO linking.");
            }, "Placeholder network creation", critical: false);

            // Verify neural network files are in place and decide build strategy
            var canUsePGO = VerifyNetworkFilesForPGO(sourceDir);
            var usePgo = configuration.EnablePgo && canUsePGO;

            if (usePgo)
            {
                ExecuteFileOperation(() =>
                {
                    if (BypassNetScriptIfNetworksPresent(downloadResult.RootDirectory))
                        _outputSubject.OnNext("Bypassing net.sh because networks are already validated.");
                }, "net.sh bypass", critical: false);
            }
            else
            {
                if (!configuration.EnablePgo)
                {
                    _outputSubject.OnNext("PGO disabled in build options. Using standard optimized build.");
                }
                else if (!canUsePGO)
                {
                    _outputSubject.OnNext("Warning: Valid neural network not found. Using standard build instead of PGO.");
                }
            }

            var buildTarget = usePgo ? BuildTargets.ProfileBuild : BuildTargets.Build;

            // Compile
            _outputSubject.OnNext($"Compiling Stockfish using '{buildTarget}' target...");
            _activeBuildTask = CompileStockfishAsync(sourceDir, configuration, token, buildTarget);
            CompilationResult result;
            try
            {
                result = await _activeBuildTask;
            }
            finally
            {
                _activeBuildTask = null;
            }
            _progressSubject.OnNext(90);

            // Strip and copy
            if (result.Success && configuration.StripExecutable)
            {
                _outputSubject.OnNext("Stripping executable...");
                await StripExecutableAsync(sourceDir, configuration, token);
            }

            if (result.Success)
            {
                _outputSubject.OnNext("Copying executable...");
                CopyExecutable(sourceDir, configuration);
            }

            _progressSubject.OnNext(100);
            _logger.LogInformation("Build completed in {Duration}s with result {Success}", buildTimer.Elapsed.TotalSeconds, result.Success);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new CompilationResult { Success = false, Output = "Canceled", ExitCode = -1 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Build failed after {Duration}s", buildTimer.Elapsed.TotalSeconds);
            _outputSubject.OnNext($"Build failed: {ex.Message}");
            return new CompilationResult { Success = false, Output = ex.ToString(), ExitCode = -1 };
        }
        finally
        {
            CleanupTempDirectoryWithRetry(downloadResult?.TempDirectory, "temporary Stockfish source directory");
            _isBuildingSubject.OnNext(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    public async Task CancelBuildAsync()
    {
        _cts?.Cancel();
        if (_activeBuildTask != null)
        {
            await Task.WhenAny(_activeBuildTask, Task.Delay(5000));
        }
    }

    private async Task<CompilationResult> CompileStockfishAsync(string sourcePath, BuildConfiguration config, CancellationToken token, string buildTarget = BuildTargets.ProfileBuild)
    {
        var safeArch = SanitizeArchitecture(config.SelectedArchitecture?.Id);
        var safeJobs = SanitizeParallelJobs(config.ParallelJobs);
        var compType = GetCompType(config);
        var makeCmd = MSYS2Helper.FindMakeExecutable(config.SelectedCompiler?.Path);
        var env = MSYS2Helper.SetupEnvironment(config);

        // Ensure PGO profile data lands in a short, writable path to avoid GCOV path explosions on Windows
        // Use unique directory per build to avoid conflicts between concurrent builds
        string? profileDir = null;
        try
        {
            profileDir = Path.Combine(Path.GetTempPath(), $"sf_prof_{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDir);
            env["PROFDIR"] = profileDir;
            env["GCOV_PREFIX"] = profileDir;
            env["GCOV_PREFIX_STRIP"] = "10";
            env["LLVM_PROFILE_FILE"] = Path.Combine(profileDir, "default_%m.profraw");
        }
        catch
        {
            // If we cannot manage the profile directory, let GCC fall back; build will still succeed without PGO gains
        }

        _outputSubject.OnNext($"Using make: {makeCmd}");
        _outputSubject.OnNext($"Config: Jobs={safeJobs}, Arch={safeArch}, Comp={compType}, Target={buildTarget}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = makeCmd,
                WorkingDirectory = sourcePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add($"-j{safeJobs}");
        process.StartInfo.ArgumentList.Add(buildTarget);
        process.StartInfo.ArgumentList.Add($"ARCH={safeArch}");
        process.StartInfo.ArgumentList.Add($"COMP={compType}");

        // Suppress noisy missing-profile warnings during profile-build; GCC will still fall back safely when data is absent.
        if (string.Equals(buildTarget, BuildTargets.ProfileBuild, StringComparison.OrdinalIgnoreCase))
        {
            process.StartInfo.ArgumentList.Add("EXTRAPROFILEFLAGS=-Wno-missing-profile");
        }

        foreach (var kvp in env)
            process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;

        process.Start();

        using var registration = token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Process already exited
                    }
                }
            }
            catch
            {
                // Ignore kill failures; process may have already exited
            }
        });

        var outputBuilder = new StringBuilder();
        Task ReadAsync(StreamReader reader) => Task.Run(async () =>
        {
            while (!reader.EndOfStream)
            {
                token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                AppendOutput(outputBuilder, line);
            }
        }, token);

        var readStdOut = ReadAsync(process.StandardOutput);
        var readStdErr = ReadAsync(process.StandardError);
        var waitExit = process.WaitForExitAsync(token);

        try
        {
            await Task.WhenAll(readStdOut, readStdErr, waitExit);

            return new CompilationResult
            {
                Success = process.ExitCode == 0,
                Output = outputBuilder.ToString(),
                ExitCode = process.ExitCode
            };
        }
        finally
        {
            // Clean up profile data even on failure/cancel to avoid cluttering %TEMP%
            CleanupTempDirectoryWithRetry(profileDir, "profile data directory");
        }
    }

    private void AppendOutput(StringBuilder builder, string line)
    {
        var isError = line.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
                      line.Contains("fatal:", StringComparison.OrdinalIgnoreCase) ||
                      line.Contains("warning:", StringComparison.OrdinalIgnoreCase);

        if (builder.Length > MaxOutputCharacters && !isError)
        {
            var content = builder.ToString();
            var firstNewLine = content.IndexOf('\n');
            if (firstNewLine > 0)
            {
                builder.Remove(0, firstNewLine + 1);
            }
        }

        builder.AppendLine(line);
        while (builder.Length > MaxOutputCharacters)
        {
            var content = builder.ToString();
            var idx = content.IndexOf('\n');
            if (idx < 0)
            {
                builder.Clear();
                break;
            }
            builder.Remove(0, idx + 1);
        }
        _outputSubject.OnNext(line);
    }

    private async Task StripExecutableAsync(string sourcePath, BuildConfiguration config, CancellationToken token)
    {
        var makeCmd = MSYS2Helper.FindMakeExecutable(config.SelectedCompiler?.Path);
        var env = MSYS2Helper.SetupEnvironment(config);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = makeCmd,
                WorkingDirectory = sourcePath,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("strip");
        process.StartInfo.ArgumentList.Add($"COMP={GetCompType(config)}");
        foreach (var kvp in env)
            process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
        process.Start();
        await process.WaitForExitAsync(token);
    }

    private void CopyExecutable(string sourcePath, BuildConfiguration config)
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "stockfish.exe" : "stockfish";
        var sourceExe = Path.Combine(sourcePath, exeName);
        if (!File.Exists(sourceExe))
        {
            _outputSubject.OnNext($"Warning: Could not find {sourceExe}");
            return;
        }
        var safeArch = SanitizeArchitecture(config.SelectedArchitecture?.Id);
        var outputNameRaw = $"stockfish_{safeArch}_{config.SourceVersion}{(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "")}";
        var outputPath = ValidateOutputPath(config.OutputDirectory, outputNameRaw);
        try
        {
            File.Copy(sourceExe, outputPath, true);
            _outputSubject.OnNext($"Executable saved to: {outputPath}");
        }
        catch (Exception ex)
        {
            _outputSubject.OnNext($"Failed to copy executable: {ex.Message}");
            throw;
        }
    }

    private static string GetCompType(BuildConfiguration config)
    {
        if (config.SelectedCompiler == null) return CompilerType.GCC;
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && config.SelectedCompiler.Type == CompilerType.GCC 
            ? CompilerType.MinGW 
            : config.SelectedCompiler.Type;
    }
    private static bool CreatePlaceholderNetwork(string sourceDirectory, BuildConfiguration config)
    {
        if (config.EnablePgo)
            return false;

        // If valid networks are already present, nothing to do.
        if (Directory.GetFiles(sourceDirectory, "*.nnue").Any())
            return false;

        var targetNames = DetectNetworkNames(sourceDirectory);
        if (targetNames.Count == 0)
            targetNames.Add("nn-1c0000000000.nnue"); // fallback to classic default

        var dummyData = new byte[1024];
        dummyData[0] = 0x4E; // N
        dummyData[1] = 0x4E; // N
        dummyData[2] = 0x55; // U
        dummyData[3] = 0x45; // E

        var created = false;
        foreach (var name in targetNames)
        {
            var path = Path.Combine(sourceDirectory, name);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, dummyData);
                created = true;
            }
        }

        return created;
    }

    private static List<string> DetectNetworkNames(string sourceDirectory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new[]
        {
            Path.Combine(sourceDirectory, "evaluate.h"),
            Path.Combine(sourceDirectory, "nnue", "evaluate.h")
        };

        var regex = new Regex("#define\\s+EvalFileDefaultName\\w*\\s+\"(nn-[a-z0-9]{12}\\.nnue)\"", RegexOptions.IgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            var contents = File.ReadAllText(candidate);
            foreach (Match m in regex.Matches(contents))
            {
                if (m.Success)
                    names.Add(m.Groups[1].Value);
            }
        }

        return names.ToList();
    }

    private bool VerifyNetworkFilesForPGO(string sourceDirectory)
    {
        var nnueFiles = Directory.GetFiles(sourceDirectory, "*.nnue");
        
        if (nnueFiles.Length == 0)
        {
            _outputSubject.OnNext("No neural network files found.");
            return false;
        }

        // Check if we have at least one valid (non-tiny) network file
        // The placeholder is only 1KB, real networks are several MB
        const long MinValidNetworkSize = 100_000; // 100KB minimum
        
        var validNetworks = nnueFiles.Where(f => new FileInfo(f).Length >= MinValidNetworkSize).ToArray();
        
        if (validNetworks.Length > 0)
        {
            _outputSubject.OnNext($"Valid network files for PGO: {string.Join(", ", validNetworks.Select(Path.GetFileName))}");
            return true;
        }
        else
        {
            _outputSubject.OnNext($"Network files present but too small (placeholders only): {string.Join(", ", nnueFiles.Select(Path.GetFileName))}");
            return false;
        }
    }

    private void CleanupTempDirectoryWithRetry(string? tempDirectory, string description = "temporary directory")
    {
        if (string.IsNullOrWhiteSpace(tempDirectory)) return;
        if (!Directory.Exists(tempDirectory)) return;
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Directory.Delete(tempDirectory, true);
                _outputSubject.OnNext($"Cleaned up {description}.");
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                Thread.Sleep(250 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxRetries)
            {
                Thread.Sleep(250 * attempt);
            }
            catch (Exception ex)
            {
                _outputSubject.OnNext($"Warning: Could not delete {description} at {tempDirectory}: {ex.Message}");
                return;
            }
        }
        if (Directory.Exists(tempDirectory))
            _outputSubject.OnNext($"Warning: {description} persists: {tempDirectory}");
    }

    private bool BypassNetScriptIfNetworksPresent(string? rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            return false;

        var scriptPath = Path.Combine(rootDirectory, "scripts", "net.sh");
        if (!File.Exists(scriptPath))
            return false;

        var scriptContent = """
#!/bin/sh
echo "Networks already present; skipping net target."
exit 0
""";
        File.WriteAllText(scriptPath, scriptContent.Replace("\r\n", "\n"));
        return true;
    }

    private static int SanitizeParallelJobs(int jobs)
    {
        var max = Math.Min(Environment.ProcessorCount * 2, 32);
        if (jobs < 1) return 1;
        if (jobs > max) return max;
        return jobs;
    }

    private static string SanitizeArchitecture(string? arch)
    {
        if (string.IsNullOrWhiteSpace(arch)) return Architectures.X86_64;
        
        var validArchs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Architectures.X86_64,
            Architectures.X86_64_VNNI512,
            Architectures.X86_64_VNNI256,
            Architectures.X86_64_AVX512,
            Architectures.X86_64_BMI2,
            Architectures.X86_64_AVX2,
            Architectures.X86_64_SSE41_POPCNT,
            Architectures.X86_64_SSSE3,
            Architectures.X86_64_SSE3_POPCNT,
            Architectures.ARMV8,
            Architectures.APPLE_SILICON
        };
        
        return validArchs.Contains(arch) ? arch : Architectures.X86_64;
    }

    private static string ValidateOutputPath(string outputDirectory, string filename)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) 
            throw new SecurityException("Output directory not specified");

        var safeFilename = Path.GetFileName(filename);
        if (string.IsNullOrEmpty(safeFilename) || safeFilename.Length > 100)
            throw new SecurityException("Invalid filename");

        var invalidChars = Path.GetInvalidFileNameChars();
        if (safeFilename.Any(c => invalidChars.Contains(c)))
            throw new SecurityException("Filename contains invalid characters");

        var rootDir = Path.GetFullPath(outputDirectory);
        var allowedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.Combine(Path.GetTempPath(), "StockfishCompiler")
        }.Where(p => !string.IsNullOrWhiteSpace(p))
         .Select(Path.GetFullPath)
         .ToList();

        bool IsUnderRoot(string path, string root)
        {
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            return path.Length == normalizedRoot.Length ||
                   path[normalizedRoot.Length] == Path.DirectorySeparatorChar ||
                   path[normalizedRoot.Length] == Path.AltDirectorySeparatorChar;
        }

        if (!allowedRoots.Any(r => IsUnderRoot(rootDir, r)))
        {
            var systemDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).ToList();

            if (systemDirs.Any(r => IsUnderRoot(rootDir, r)))
                throw new SecurityException("Output directory cannot be a system directory");

            try
            {
                Directory.CreateDirectory(rootDir);
            }
            catch (Exception ex)
            {
                throw new SecurityException($"Output directory is not accessible: {ex.Message}");
            }

            return Path.Combine(rootDir, safeFilename);
        }

        Directory.CreateDirectory(rootDir);
        return Path.Combine(rootDir, safeFilename);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        // Cancel any ongoing operations
        _cts?.Cancel();
        _cts?.Dispose();
        
        // Complete observables before disposing (prevents memory leaks)
        _outputSubject.OnCompleted();
        _progressSubject.OnCompleted();
        _isBuildingSubject.OnCompleted();
        
        // Now dispose the subjects
        _outputSubject.Dispose();
        _progressSubject.Dispose();
        _isBuildingSubject.Dispose();
    }
}
