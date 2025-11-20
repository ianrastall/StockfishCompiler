using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Security; // for SecurityException
using System.Text;
using System.Text.RegularExpressions;
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

            // Download source
            _outputSubject.OnNext("Downloading Stockfish source...");
            downloadResult = await downloader.DownloadSourceAsync(configuration.SourceVersion, progress);
            var sourceDir = downloadResult.SourceDirectory;
            _progressSubject.OnNext(25);

            if (configuration.DownloadNetwork)
            {
                var networkReady = await downloader.DownloadNeuralNetworkAsync(sourceDir, configuration, progress);
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

            // Verify neural network files are in place
            VerifyNetworkFiles(sourceDir);

            // Compile
            _outputSubject.OnNext("Compiling Stockfish...");
            var result = await CompileStockfishAsync(sourceDir, configuration, _cts.Token);
            _progressSubject.OnNext(90);

            // Strip and copy
            if (result.Success && configuration.StripExecutable)
            {
                _outputSubject.OnNext("Stripping executable...");
                await StripExecutableAsync(sourceDir, configuration, _cts.Token);
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

    private async Task<CompilationResult> CompileStockfishAsync(string sourcePath, BuildConfiguration config, CancellationToken token)
    {
        var safeArch = SanitizeArchitecture(config.SelectedArchitecture?.Id);
        var safeJobs = SanitizeParallelJobs(config.ParallelJobs);
        var compType = GetCompType(config);
        var makeCmd = FindMakeCommand(config);
        var env = PrepareEnvironment(config);

        // Ensure PGO profile data lands in a short, writable path to avoid GCOV path explosions on Windows
        var profileDir = Path.Combine(Path.GetTempPath(), "sf_prof");
        try
        {
            if (Directory.Exists(profileDir))
                Directory.Delete(profileDir, true);
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
        _outputSubject.OnNext($"Config: Jobs={safeJobs}, Arch={safeArch}, Comp={compType}");

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
        process.StartInfo.ArgumentList.Add("profile-build");
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
        var makeCmd = FindMakeCommand(config);
        var env = PrepareEnvironment(config);

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
        if (config.SelectedCompiler == null) return "gcc";
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && config.SelectedCompiler.Type == "gcc" ? "mingw" : config.SelectedCompiler.Type;
    }

    private static string FindMakeCommand(BuildConfiguration config)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "make";
        if (config.SelectedCompiler?.Path != null)
        {
            var compilerPath = new DirectoryInfo(config.SelectedCompiler.Path);
            var msys2Root = compilerPath.Parent?.Parent;
            if (msys2Root != null && msys2Root.Exists)
            {
                var makePaths = new[]
                {
                    Path.Combine(msys2Root.FullName, "usr", "bin", "make.exe"),
                    Path.Combine(msys2Root.FullName, "mingw64", "bin", "mingw32-make.exe"),
                    Path.Combine(msys2Root.FullName, "mingw64", "bin", "make.exe")
                };
                foreach (var makePath in makePaths)
                    if (File.Exists(makePath)) return makePath;
            }
        }
        var commonMsys2Paths = new[] { @"C:\msys64", @"C:\msys2", @"D:\msys64", @"D:\msys2" };
        foreach (var msys2Path in commonMsys2Paths)
        {
            var makePaths = new[]
            {
                Path.Combine(msys2Path, "usr", "bin", "make.exe"),
                Path.Combine(msys2Path, "mingw64", "bin", "mingw32-make.exe"),
                Path.Combine(msys2Path, "mingw64", "bin", "make.exe")
            };
            foreach (var makePath in makePaths)
                if (File.Exists(makePath)) return makePath;
        }
        return "make";
    }

    private static bool DisableNetDependency(string sourceDirectory)
    {
        var makefilePath = Path.Combine(sourceDirectory, "Makefile");
        if (!File.Exists(makefilePath)) return false;
        var lines = File.ReadAllLines(makefilePath);
        var changed = false;
        var targetsToPatch = new[] { "profile-build:", "build:", "config-sanity:", "analyze:" };
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (var target in targetsToPatch)
            {
                if (lines[i].StartsWith(target, StringComparison.Ordinal))
                {
                    var updated = RemoveMakeDependency(lines[i], "net");
                    if (!string.Equals(updated, lines[i], StringComparison.Ordinal))
                    {
                        lines[i] = updated;
                        changed = true;
                    }
                    break;
                }
            }
        }
        if (changed) File.WriteAllLines(makefilePath, lines);
        return changed;
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

    private void VerifyNetworkFiles(string sourceDirectory)
    {
        var nnueFiles = Directory.GetFiles(sourceDirectory, "*.nnue");
        if (nnueFiles.Length > 0)
        {
            _outputSubject.OnNext($"Network files present: {string.Join(", ", nnueFiles.Select(Path.GetFileName))}");
        }
        else
        {
            _outputSubject.OnNext("Warning: No .nnue files found in source directory!");
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

    private static string RemoveMakeDependency(string line, string dependency)
    {
        var colonIndex = line.IndexOf(':');
        if (colonIndex < 0) return line;
        var targetPart = line[..(colonIndex + 1)];
        var rest = line[(colonIndex + 1)..];
        var tokens = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var filtered = tokens.Where(t => !t.Equals(dependency, StringComparison.Ordinal)).ToArray();
        var newRest = filtered.Length > 0 ? " " + string.Join(' ', filtered) : string.Empty;
        var updatedLine = targetPart + newRest;
        return updatedLine == line ? line : updatedLine;
    }

    private static Dictionary<string, string> PrepareEnvironment(BuildConfiguration config)
    {
        var env = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key != null && entry.Value != null)
                env[entry.Key.ToString()!] = entry.Value.ToString()!;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return env;
        var pathsToAdd = new List<string>();
        if (config.SelectedCompiler?.Path != null)
        {
            pathsToAdd.Add(config.SelectedCompiler.Path);
            var compilerPath = new DirectoryInfo(config.SelectedCompiler.Path);
            var msys2Root = compilerPath.Parent?.Parent;
            if (msys2Root != null && msys2Root.Exists)
            {
                var usrBin = Path.Combine(msys2Root.FullName, "usr", "bin");
                var mingw64Bin = Path.Combine(msys2Root.FullName, "mingw64", "bin");
                if (Directory.Exists(usrBin)) pathsToAdd.Add(usrBin);
                if (Directory.Exists(mingw64Bin)) pathsToAdd.Add(mingw64Bin);
            }
        }
        else
        {
            var commonMsys2Paths = new[] { @"C:\msys64", @"C:\msys2", @"D:\msys64", @"D:\msys2" };
            foreach (var msys2Path in commonMsys2Paths)
            {
                if (!Directory.Exists(msys2Path)) continue;
                var usrBin = Path.Combine(msys2Path, "usr", "bin");
                var mingw64Bin = Path.Combine(msys2Path, "mingw64", "bin");
                if (Directory.Exists(usrBin)) pathsToAdd.Add(usrBin);
                if (Directory.Exists(mingw64Bin)) pathsToAdd.Add(mingw64Bin);
                if (pathsToAdd.Count > 0) break;
            }
        }
        if (pathsToAdd.Count > 0)
        {
            var currentPath = env.GetValueOrDefault("PATH", string.Empty);
            env["PATH"] = string.Join(";", pathsToAdd) + ";" + currentPath;
        }
        return env;
    }

    private static string SanitizeArchitecture(string? arch)
    {
        if (string.IsNullOrWhiteSpace(arch)) return "x86-64";
        var validArchs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "x86-64","x86-64-vnni512","x86-64-vnni256","x86-64-avx512","x86-64-bmi2","x86-64-avx2","x86-64-sse41-popcnt","x86-64-ssse3","x86-64-sse3-popcnt","armv8","apple-silicon"
        };
        return validArchs.Contains(arch) ? arch : "x86-64";
    }

    private static int SanitizeParallelJobs(int jobs)
    {
        var max = Environment.ProcessorCount * 2;
        if (jobs < 1) return 1;
        if (jobs > max) return max;
        return jobs;
    }

    private static string ValidateOutputPath(string outputDirectory, string filename)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new SecurityException("Output directory not specified");

        var baseName = Path.GetFileName(filename);
        if (!string.Equals(baseName, filename, StringComparison.Ordinal)) throw new SecurityException("Invalid output file name");
        if (baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new SecurityException("Invalid characters in output file name");

        var ext = Path.GetExtension(baseName);
        if (!string.IsNullOrEmpty(ext) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Disallowed file extension");

        var normalizedDir = Path.GetFullPath(outputDirectory);
        var systemDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        if (systemDirs.Any(d => !string.IsNullOrEmpty(d) && normalizedDir.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            throw new SecurityException("Refusing to write into system directory");

        Directory.CreateDirectory(normalizedDir);
        var fullPath = Path.GetFullPath(Path.Combine(normalizedDir, baseName));
        if (!fullPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase)) throw new SecurityException("Path traversal detected");
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
