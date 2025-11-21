using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Extensions.Logging;
using StockfishCompiler.Models;

namespace StockfishCompiler.Services;

public class ArchitectureDetector : IArchitectureDetector
{
    private static readonly string[] X64BaseFeatures = ["sse4.1", "popcnt", "avx2", "bmi2"];
    private static readonly string[] Arm64BaseFeatures = ["neon", "popcnt"];
    private readonly ILogger<ArchitectureDetector> _logger;

    public ArchitectureDetector(ILogger<ArchitectureDetector> logger)
    {
        _logger = logger;
    }

    public async Task<ArchitectureInfo> DetectOptimalArchitectureAsync(CompilerInfo compiler)
    {
        try
        {
            var (features, detectedCpu) = await DetectCPUFeaturesDetailedAsync(compiler);
            _logger.LogDebug("Detected {Count} CPU features: {Features}", features.Count, string.Join(", ", features));
            _logger.LogDebug("Detected CPU name: {CpuName}", detectedCpu);
            
            var archId = DetermineOptimalArchitecture(features, detectedCpu);
            _logger.LogInformation("Determined optimal architecture: {Architecture}", archId);
            
            var all = await GetAvailableArchitecturesAsync();
            var matched = all.FirstOrDefault(a => a.Id.Equals(archId, StringComparison.OrdinalIgnoreCase));
            return matched ?? new ArchitectureInfo 
            { 
                Id = archId, 
                Name = archId, 
                Description = archId, 
                Category = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM" : "x86" 
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Architecture detection failed for {Compiler}, using fallback", compiler.DisplayName);
            
            // Fallback to safe generic architecture based on platform
            var fallbackId = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 
                ? "armv8" 
                : "x86-64";
            
            var all = await GetAvailableArchitecturesAsync();
            var fallback = all.FirstOrDefault(a => a.Id == fallbackId);
            
            return fallback ?? new ArchitectureInfo
            {
                Id = fallbackId,
                Name = fallbackId,
                Description = $"{fallbackId} (fallback - detection failed)",
                Category = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM" : "x86",
                IsRecommended = false
            };
        }
    }

    public async Task<List<string>> DetectCPUFeaturesAsync(CompilerInfo compiler)
    {
        var (features, _) = await DetectCPUFeaturesDetailedAsync(compiler);
        return features;
    }

    private async Task<(List<string> Features, string CpuName)> DetectCPUFeaturesDetailedAsync(CompilerInfo compiler)
    {
        try
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                _logger.LogInformation("ARM64 architecture detected, using base features");
                return (Arm64BaseFeatures.ToList(), "arm64");
            }

            if (compiler.Type is "gcc" or "mingw")
            {
                _logger.LogDebug("Using GCC feature detection for {Compiler}", compiler.DisplayName);
                return await DetectGccFeaturesAsync(compiler);
            }
            if (compiler.Type == "clang")
            {
                _logger.LogDebug("Using Clang feature detection for {Compiler}", compiler.DisplayName);
                return await DetectClangFeaturesAsync(compiler);
            }
            
            _logger.LogWarning("Unknown compiler type: {Type}, using fallback", compiler.Type);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Feature detection failed, using fallback features");
        }

        return GetFallbackFeatures();
    }

    private async Task<(List<string> Features, string CpuName)> DetectGccFeaturesAsync(CompilerInfo compiler)
    {
        var exe = Path.Combine(compiler.Path, compiler.Name);
        _logger.LogDebug("Running GCC detection: {Exe} -Q -march=native --help=target", exe);
        
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "-Q -march=native --help=target",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Force C (English) locale to ensure output parsing works on non-English systems
        psi.EnvironmentVariables["LC_ALL"] = "C";
        psi.EnvironmentVariables["LANG"] = "C";

        SetupEnvironmentForMSYS2(psi, compiler.Path);

        using var process = Process.Start(psi);
        if (process == null)
        {
            _logger.LogWarning("Failed to start GCC process for feature detection");
            return (X64BaseFeatures.ToList(), "unknown");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        
        await process.WaitForExitAsync();
        
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("GCC detection exited with code {ExitCode}", process.ExitCode);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("GCC stderr: {StdErr}", stderr.Substring(0, Math.Min(500, stderr.Length)));
            }
        }

        // If there were errors and no output, throw to trigger fallback
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException($"GCC detection failed with exit code {process.ExitCode}");
        }

        var features = new List<string>();
        string cpuName = "";
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("-march", StringComparison.Ordinal))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                cpuName = parts.LastOrDefault() ?? string.Empty;
            }
            if (trimmed.Contains("[enabled]"))
            {
                if (!trimmed.Contains("-m", StringComparison.Ordinal))
                    continue;

                var flag = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                  .FirstOrDefault(s => s.StartsWith("-m", StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(flag))
                {
                    var name = flag.Length > 2 ? flag[2..] : flag;
                    if (!string.IsNullOrWhiteSpace(name))
                        features.Add(name);
                }
            }
        }

        _logger.LogDebug("GCC detection found {Count} features", features.Count);
        var distinct = features.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0)
        {
            _logger.LogWarning("No target features detected from GCC output; falling back to base feature detection");
            return GetFallbackFeatures();
        }
        return (distinct, cpuName);
    }

    private async Task<(List<string> Features, string CpuName)> DetectClangFeaturesAsync(CompilerInfo compiler)
    {
        var exe = Path.Combine(compiler.Path, compiler.Name);
        _logger.LogDebug("Running Clang detection: {Exe} -E - -march=native -###", exe);
        
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "-E - -march=native -###",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Force C (English) locale to ensure output parsing works on non-English systems
        psi.EnvironmentVariables["LC_ALL"] = "C";
        psi.EnvironmentVariables["LANG"] = "C";

        SetupEnvironmentForMSYS2(psi, compiler.Path);

        using var process = Process.Start(psi);
        if (process == null)
        {
            _logger.LogWarning("Failed to start Clang process for feature detection");
            return (X64BaseFeatures.ToList(), "unknown");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        
        await process.WaitForExitAsync();
        
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var features = new List<string>();
        string cpuName = "";
        foreach (var line in stderr.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("-target-cpu"))
            {
                var parts = trimmed.Split('"');
                cpuName = parts.LastOrDefault() ?? string.Empty;
            }
            if (trimmed.Contains("-target-feature"))
            {
                var parts = trimmed.Split('"');
                var feat = parts.LastOrDefault();
                if (!string.IsNullOrEmpty(feat) && feat.StartsWith('+'))
                {
                    var name = feat.Length > 1 ? feat[1..] : feat;
                    if (!string.IsNullOrWhiteSpace(name))
                        features.Add(name);
                }
            }
        }

        _logger.LogDebug("Clang detection found {Count} features", features.Count);
        var distinct = features.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0)
        {
            _logger.LogWarning("No target features detected from Clang output; falling back to base feature detection");
            return GetFallbackFeatures();
        }
        return (distinct, cpuName);
    }

    private void SetupEnvironmentForMSYS2(ProcessStartInfo psi, string compilerPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var compilerDir = new DirectoryInfo(compilerPath);
        var msys2Root = compilerDir.Parent?.Parent;

        if (msys2Root != null && msys2Root.Exists)
        {
            var pathsToAdd = new List<string>();

            pathsToAdd.Add(compilerPath);

            var usrBin = Path.Combine(msys2Root.FullName, "usr", "bin");
            var mingw64Bin = Path.Combine(msys2Root.FullName, "mingw64", "bin");
            var mingw32Bin = Path.Combine(msys2Root.FullName, "mingw32", "bin");

            if (Directory.Exists(usrBin))
                pathsToAdd.Add(usrBin);
            if (Directory.Exists(mingw64Bin))
                pathsToAdd.Add(mingw64Bin);
            if (Directory.Exists(mingw32Bin))
                pathsToAdd.Add(mingw32Bin);

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var newPath = string.Join(";", pathsToAdd) + ";" + currentPath;
            psi.EnvironmentVariables["PATH"] = newPath;
            
            _logger.LogDebug("Set up MSYS2 environment with paths: {Paths}", string.Join("; ", pathsToAdd));
        }
        else
        {
            _logger.LogDebug("MSYS2 root not found for compiler path: {Path}", compilerPath);
        }
    }

    private static string DetermineOptimalArchitecture(List<string> features, string cpuName)
    {
        bool Has(params string[] req) => req.All(f => features.Contains(f));
        string cpu = cpuName.ToLowerInvariant();

        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return "armv8";

        if (Has("avx512vnni", "avx512dq", "avx512f", "avx512bw", "avx512vl"))
            return "x86-64-vnni256";
        if (Has("avx512f", "avx512bw"))
            return "x86-64-avx512";
        if (Has("bmi2") && cpu is not ("znver1" or "znver2"))
            return "x86-64-bmi2";
        if (Has("avx2"))
            return "x86-64-avx2";
        if (Has("sse4.1", "popcnt"))
            return "x86-64-sse41-popcnt";
        if (Has("ssse3"))
            return "x86-64-ssse3";
        if (Has("sse3", "popcnt"))
            return "x86-64-sse3-popcnt";
        return "x86-64";
    }

    private (List<string> Features, string CpuName) GetFallbackFeatures()
    {
        var features = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => X64BaseFeatures.ToList(),
            Architecture.Arm64 => Arm64BaseFeatures.ToList(),
            _ => new List<string>()
        };
        return (features, RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
    }

    public Task<List<ArchitectureInfo>> GetAvailableArchitecturesAsync()
    {
        List<ArchitectureInfo> list =
        [
            new() { Id = "x86-64", Name = "x86-64", Description = "Generic 64-bit", Category = "x86" },
            new() { Id = "x86-64-vnni512", Name = "x86-64 VNNI 512", Description = "Intel Sapphire Rapids+, AMD Zen 4+", Category = "x86" },
            new() { Id = "x86-64-vnni256", Name = "x86-64 VNNI 256", Description = "Intel Cascade Lake+", Category = "x86" },
            new() { Id = "x86-64-avx512", Name = "x86-64 AVX-512", Description = "Intel Skylake-X+", Category = "x86" },
            new() { Id = "x86-64-bmi2", Name = "x86-64 BMI2", Description = "Intel Haswell+ (NOT AMD Zen 1/2)", Category = "x86" },
            new() { Id = "x86-64-avx2", Name = "x86-64 AVX2", Description = "Intel Haswell+, AMD Zen+", Category = "x86" },
            new() { Id = "x86-64-sse41-popcnt", Name = "x86-64 SSE4.1+POPCNT", Description = "Intel Nehalem+", Category = "x86" },
            new() { Id = "x86-64-ssse3", Name = "x86-64 SSSE3", Description = "Intel Core 2+, some early x86-64", Category = "x86" },
            new() { Id = "x86-64-sse3-popcnt", Name = "x86-64 SSE3+POPCNT", Description = "Older x86-64 with SSE3 + POPCNT", Category = "x86" },
            new() { Id = "armv8", Name = "ARMv8", Description = "ARMv8 64-bit with popcnt and neon", Category = "ARM" },
            new() { Id = "apple-silicon", Name = "Apple Silicon", Description = "Apple M1/M2/M3", Category = "ARM" }
        ];
        return Task.FromResult(list);
    }
}
