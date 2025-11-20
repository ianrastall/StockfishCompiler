using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using StockfishCompiler.Constants;
using StockfishCompiler.Helpers;
using StockfishCompiler.Models;

namespace StockfishCompiler.Services;

public class BuildService(IStockfishDownloader downloader) : IBuildService, IDisposable
{
    public IObservable<string> Output => _outputSubject.AsObservable();
    public IObservable<double> Progress => _progressSubject.AsObservable();
    public IObservable<bool> IsBuilding => _isBuildingSubject.AsObservable();

    private readonly Subject<string> _outputSubject = new();
    private readonly Subject<double> _progressSubject = new();
    private readonly Subject<bool> _isBuildingSubject = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private const int MaxOutputCharacters = 500_000; // safety cap

    public async Task<CompilationResult> BuildAsync(BuildConfiguration configuration)
    {
        _cts = new CancellationTokenSource();
        _isBuildingSubject.OnNext(true);
        _progressSubject.OnNext(0);
        SourceDownloadResult? downloadResult = null;

        try
        {
            var progress = new Progress<string>(msg => _outputSubject.OnNext(msg));
            var token = _cts.Token;

            // Download source
            _outputSubject.OnNext("Downloading Stockfish source...");
            downloadResult = await downloader.DownloadSourceAsync(configuration.SourceVersion, progress, token);
            var sourceDir = downloadResult.SourceDirectory;
            _progressSubject.OnNext(25);

            if (configuration.DownloadNetwork)
            {
                var networkReady = await downloader.DownloadNeuralNetworkAsync(sourceDir, configuration, progress, token);
                if (!networkReady)
                    _outputSubject.OnNext("Pre-download failed - make will attempt to fetch the network.");
                _progressSubject.OnNext(networkReady ? 40 : 30);
            }

            if (DisableNetDependency(sourceDir))
                _outputSubject.OnNext("Patched makefile to skip redundant net target.");
            if (NeutralizeNetScript(downloadResult.RootDirectory))
                _outputSubject.OnNext("Neutralized net.sh script to prevent redundant downloads.");
            if (PatchMakefileSaveTemps(sourceDir))
                _outputSubject.OnNext("Removed -save-temps flag to prevent network embedding issues.");
            if (CreatePlaceholderNetwork(sourceDir))
                _outputSubject.OnNext("Created placeholder network file for LTO linking.");

            // Verify neural network files are in place and decide build strategy
            var canUsePGO = VerifyNetworkFilesForPGO(sourceDir);
            var buildTarget = canUsePGO ? "profile-build" : "build";
            
            if (!canUsePGO)
                _outputSubject.OnNext("Warning: Valid neural network not found. Using standard build instead of PGO.");

            // Compile
            _outputSubject.OnNext($"Compiling Stockfish using '{buildTarget}' target...");
            var result = await CompileStockfishAsync(sourceDir, configuration, token, buildTarget);
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
            return result;
        }
        catch (OperationCanceledException)
        {
            return new CompilationResult { Success = false, Output = "Canceled", ExitCode = -1 };
        }
        finally
        {
            CleanupTempDirectoryWithRetry(downloadResult?.TempDirectory);
            _isBuildingSubject.OnNext(false);
        }
    }

    public void CancelBuild() => _cts?.Cancel();

    private async Task<CompilationResult> CompileStockfishAsync(string sourcePath, BuildConfiguration config, CancellationToken token, string buildTarget = BuildTargets.ProfileBuild)
    {
        var safeArch = SanitizeArchitecture(config.SelectedArchitecture?.Id);
        var safeJobs = SanitizeParallelJobs(config.ParallelJobs);
        var compType = GetCompType(config);
        var makeCmd = MSYS2Helper.FindMakeExecutable(config.SelectedCompiler?.Path);
        var env = MSYS2Helper.SetupEnvironment(config);

        // Ensure PGO profile data lands in a short, writable path to avoid GCOV path explosions on Windows
        // Use unique directory per build to avoid conflicts between concurrent builds
        var profileDir = Path.Combine(Path.GetTempPath(), $"sf_prof_{Guid.NewGuid():N}");
        try
        {
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

        foreach (var kvp in env)
            process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;

        process.Start();

        using var registration = token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
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

        await Task.WhenAll(readStdOut, readStdErr, waitExit);

        return new CompilationResult
        {
            Success = process.ExitCode == 0,
            Output = outputBuilder.ToString(),
            ExitCode = process.ExitCode
        };
    }

    private void AppendOutput(StringBuilder builder, string line)
    {
        builder.AppendLine(line);
        if (builder.Length > MaxOutputCharacters)
            builder.Remove(0, builder.Length - MaxOutputCharacters); // trim oldest
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
                Arguments = $"strip COMP={GetCompType(config)}",
                WorkingDirectory = sourcePath,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
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
        File.Copy(sourceExe, outputPath, true);
        _outputSubject.OnNext($"Executable saved to: {outputPath}");
    }

    private static string GetCompType(BuildConfiguration config)
    {
        if (config.SelectedCompiler == null) return CompilerType.GCC;
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && config.SelectedCompiler.Type == CompilerType.GCC 
            ? CompilerType.MinGW 
            : config.SelectedCompiler.Type;
    }

    private static bool DisableNetDependency(string sourceDirectory)
    {
        var makefilePath = Path.Combine(sourceDirectory, "Makefile");
        if (!File.Exists(makefilePath)) return false;
        
        var lines = File.ReadAllLines(makefilePath).ToList();
        bool changed = false;
        
        var targetsToPatch = new[] 
        { 
            BuildTargets.ProfileBuild, 
            BuildTargets.Build, 
            BuildTargets.ConfigSanity, 
            BuildTargets.Analyze 
        };

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmedLine = lines[i].TrimStart();
            
            // Check if this line defines one of our targets
            foreach (var target in targetsToPatch)
            {
                if (trimmedLine.StartsWith($"{target}:", StringComparison.Ordinal))
                {
                    // Process this line and any continuations
                    var lineIndicesToProcess = new List<int> { i };
                    
                    // Collect all lines that are part of this rule (handle line continuations)
                    int j = i;
                    while (j < lines.Count && lines[j].TrimEnd().EndsWith("\\", StringComparison.Ordinal))
                    {
                        j++;
                        if (j < lines.Count)
                        {
                            lineIndicesToProcess.Add(j);
                        }
                    }
                    
                    // Remove 'net' from all collected lines
                    foreach (var lineIdx in lineIndicesToProcess)
                    {
                        var line = lines[lineIdx];
                        
                        // Remove 'net' as a standalone dependency
                        // Match patterns: " net ", " net\", "net ", " net", or ":net"
                        if (line.Contains(" net ") || 
                            line.Contains(" net\\") ||
                            line.Contains($":{target.Split(':')[0]} net") ||
                            line.TrimEnd().EndsWith(" net"))
                        {
                            // Replace multiple patterns
                            var newLine = line
                                .Replace(" net ", " ")
                                .Replace(" net\\", "\\")
                                .Replace(" net\t", " ")
                                .Replace("\tnet ", "\t")
                                .Replace("\tnet\t", "\t");
                            
                            // Handle 'net' at the end of line (before potential backslash)
                            if (newLine.TrimEnd('\\').TrimEnd().EndsWith(" net"))
                            {
                                var endsWithBackslash = newLine.TrimEnd().EndsWith("\\");
                                newLine = newLine.TrimEnd('\\').TrimEnd();
                                newLine = newLine.Substring(0, newLine.Length - 4); // Remove " net"
                                if (endsWithBackslash)
                                {
                                    newLine += " \\";
                                }
                            }
                            
                            if (newLine != line)
                            {
                                lines[lineIdx] = newLine;
                                changed = true;
                            }
                        }
                    }
                    
                    break; // Move to next line after processing this target
                }
            }
        }
        
        if (changed)
        {
            File.WriteAllLines(makefilePath, lines);
            return true;
        }
        
        return false;
    }

    private bool NeutralizeNetScript(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) return false;
        var scriptPath = Path.Combine(rootDirectory, "scripts", "net.sh");
        if (!File.Exists(scriptPath)) return false;
        var scriptContent = """
#!/bin/sh
echo "Skipping Stockfish net target - neural networks pre-downloaded."
exit 0
""";
        File.WriteAllText(scriptPath, scriptContent.Replace("\r\n", "\n"));
        return true;
    }

    private static bool PatchMakefileSaveTemps(string sourceDirectory)
    {
        var makefilePath = Path.Combine(sourceDirectory, "Makefile");
        if (!File.Exists(makefilePath)) return false;
        
        var content = File.ReadAllText(makefilePath);
        var originalContent = content;
        
        // Remove -save-temps flag (as a standalone word, not adjacent whitespace)
        // Uses lookbehind (?<=\s) to ensure preceded by whitespace
        // Uses lookahead (?=\s|$) to ensure followed by whitespace or end of line
        // This preserves Makefile syntax and line continuations
        content = System.Text.RegularExpressions.Regex.Replace(
            content,
            @"(?<=\s)-save-temps(?=\s|$)",
            "",
            System.Text.RegularExpressions.RegexOptions.Multiline
        );
        
        if (content != originalContent)
        {
            File.WriteAllText(makefilePath, content);
            return true;
        }
        
        return false;
    }

    private static bool CreatePlaceholderNetwork(string sourceDirectory)
    {
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

    private void CleanupTempDirectoryWithRetry(string? tempDirectory)
    {
        if (string.IsNullOrWhiteSpace(tempDirectory)) return;
        if (!Directory.Exists(tempDirectory)) return;
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Directory.Delete(tempDirectory, true);
                _outputSubject.OnNext($"Cleaned up temporary Stockfish source directory.");
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
                _outputSubject.OnNext($"Warning: Could not delete temp directory {tempDirectory}: {ex.Message}");
                return;
            }
        }
        if (Directory.Exists(tempDirectory))
            _outputSubject.OnNext($"Warning: Temp directory persists: {tempDirectory}");
    }

    private static int SanitizeParallelJobs(int jobs)
    {
        var max = Environment.ProcessorCount * 2;
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

        var baseName = Path.GetFileName(filename);
        if (!string.Equals(baseName, filename, StringComparison.Ordinal)) 
            throw new SecurityException("Invalid output file name");
        if (baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) 
            throw new SecurityException("Invalid characters in output file name");

        var ext = Path.GetExtension(baseName);
        if (!string.IsNullOrEmpty(ext) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Disallowed file extension");

        var baseDir = new DirectoryInfo(Path.GetFullPath(outputDirectory));
        var systemDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        
        if (systemDirs.Any(d => !string.IsNullOrEmpty(d) && baseDir.FullName.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            throw new SecurityException("Refusing to write into system directory");

        Directory.CreateDirectory(baseDir.FullName);
        
        var fullPath = Path.GetFullPath(Path.Combine(baseDir.FullName, baseName));
        var targetFile = new FileInfo(fullPath);
        
        // Improved path traversal check using canonicalized paths
        if (targetFile.DirectoryName == null || !targetFile.DirectoryName.StartsWith(baseDir.FullName, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Path traversal detected");
            
        return fullPath;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Dispose();
        _outputSubject.Dispose();
        _progressSubject.Dispose();
        _isBuildingSubject.Dispose();
    }
}
