using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockfishCompiler.Helpers;
using StockfishCompiler.Models;

namespace StockfishCompiler.Services;

public class StockfishDownloader : IStockfishDownloader
{
    private readonly HttpClient _httpClient;
    private const string GitHubApiUrl = "https://api.github.com/repos/official-stockfish/Stockfish/releases/latest";
    private const string GitHubReleasesApiUrl = "https://api.github.com/repos/official-stockfish/Stockfish/releases";
    private const string MasterZipUrl = "https://github.com/official-stockfish/Stockfish/archive/refs/heads/master.zip";
    private static readonly string CacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StockfishCompiler", "cache", "sources");
    private static readonly string[] NetworkMirrors =
    [
        "https://tests.stockfishchess.org/api/nn/{0}",
        "https://github.com/official-stockfish/networks/raw/master/{0}"
    ];
    private static readonly Regex NetworkMacroRegex = new("#define\\s+EvalFileDefaultName\\w*\\s+\"(nn-[a-z0-9]{12}\\.nnue)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex RelaxedNetworkNameRegex = new("nn-[a-z0-9]{6,}\\.nnue", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex NetworkFileNameRegex = new("nn-([a-f0-9]{12})\\.nnue", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private const long MaxDownloadSize = 500L * 1024 * 1024; // 500 MB safety cap

    public StockfishDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;

        if (_httpClient.Timeout < TimeSpan.FromMinutes(5))
        {
            _httpClient.Timeout = TimeSpan.FromMinutes(10);
        }

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("StockfishCompiler/1.0");
        }
    }

    public async Task<List<StockfishVersionInfo>> GetAvailableVersionsAsync(CancellationToken cancellationToken = default)
    {
        var versions = new List<StockfishVersionInfo>
        {
            new()
            {
                Id = "master",
                DisplayName = "Development (master branch)",
                Tag = "master",
                Description = "Latest development code - may be unstable",
                IsRecommended = false,
                IsCompatible = true,
                MinimumCppStandard = "C++20",
                NnueRequirement = NeuralNetworkRequirement.Required,
                HasClassicalEval = false,
                MajorVersion = 99, // Use high number for sorting
                CompatibilityNotes = "Requires modern compiler (GCC 10+ or Clang 11+)"
            },
            new()
            {
                Id = "stable",
                DisplayName = "Latest Stable Release",
                Tag = "latest",
                Description = "Most recent official release (recommended)",
                IsRecommended = true,
                IsCompatible = true,
                MinimumCppStandard = "C++17",
                NnueRequirement = NeuralNetworkRequirement.Required,
                HasClassicalEval = false,
                MajorVersion = 98, // Use high number for sorting
                CompatibilityNotes = null
            }
        };

        try
        {
            using var response = await _httpClient.GetAsync(GitHubReleasesApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    if (!release.TryGetProperty("tag_name", out var tagNameProp))
                        continue;

                    var tagName = tagNameProp.GetString();
                    if (string.IsNullOrWhiteSpace(tagName))
                        continue;

                    // Parse version from tag (e.g., "sf_17", "sf_17.1", "sf_18")
                    var versionMatch = Regex.Match(tagName, @"sf_(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                    if (!versionMatch.Success)
                        continue;

                    var versionNumber = versionMatch.Groups[1].Value;
                    var releaseName = release.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    var publishedAt = release.TryGetProperty("published_at", out var publishedProp) ? publishedProp.GetString() : null;

                    // Parse major version for classification
                    var majorVersionStr = versionNumber.Split('.')[0];
                    if (!int.TryParse(majorVersionStr, out var majorVersion))
                        continue;

                    // Classify version based on research findings
                    var versionInfo = ClassifyStockfishVersion(majorVersion, versionNumber, tagName, releaseName, publishedAt);
                    versions.Add(versionInfo);
                }
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - we already have master and stable
            System.Diagnostics.Debug.WriteLine($"Failed to fetch GitHub releases: {ex.Message}");
        }

        // Keep only versions we consider buildable (NNUE era and newer), plus master/stable aliases.
        versions = versions
            .Where(v => v.MajorVersion >= 12 || string.Equals(v.Id, "master", StringComparison.OrdinalIgnoreCase) || string.Equals(v.Id, "stable", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.MajorVersion)
            .ThenByDescending(v => v.Id)
            .ToList();

        return versions;
    }

    private static StockfishVersionInfo ClassifyStockfishVersion(
        int majorVersion, 
        string versionNumber, 
        string tagName, 
        string? releaseName, 
        string? publishedAt)
    {
        var displayName = !string.IsNullOrWhiteSpace(releaseName) 
            ? releaseName 
            : $"Stockfish {versionNumber}";

        var publishedInfo = !string.IsNullOrWhiteSpace(publishedAt) 
            ? $"Released {(DateTime.TryParse(publishedAt, out var date) ? date.ToString("MMMM yyyy") : publishedAt)}" 
            : "Release date unknown";

        // Classify based on version number according to research
        StockfishVersionInfo versionInfo;

        if (majorVersion >= 16)
        {
            // SF 16+: Pure neural, requires C++17/C++20, no classical eval
            versionInfo = new StockfishVersionInfo
            {
                Id = tagName,
                DisplayName = displayName,
                Tag = tagName,
                Description = $"{publishedInfo} - Pure neural evaluation",
                IsRecommended = false,
                IsCompatible = true,
                MinimumCppStandard = "C++17",
                NnueRequirement = NeuralNetworkRequirement.Required,
                HasClassicalEval = false,
                MajorVersion = majorVersion,
                CompatibilityNotes = majorVersion >= 17 
                    ? null 
                    : "Requires network file. May need compiler updates (GCC 10+ or Clang 11+)"
            };
        }
        else if (majorVersion >= 12 && majorVersion <= 15)
        {
            // SF 12-15: Hybrid era, NNUE + classical
            versionInfo = new StockfishVersionInfo
            {
                Id = tagName,
                DisplayName = displayName,
                Tag = tagName,
                Description = $"{publishedInfo} - Hybrid evaluation (NNUE + classical)",
                IsRecommended = false,
                IsCompatible = true,
                MinimumCppStandard = "C++11",
                NnueRequirement = NeuralNetworkRequirement.Optional,
                HasClassicalEval = true,
                MajorVersion = majorVersion,
                CompatibilityNotes = "Can compile without NNUE but performance will be significantly lower"
            };
        }
        else if (majorVersion >= 10 && majorVersion < 12)
        {
            // SF 10-11: Classical era, potentially compatible
            versionInfo = new StockfishVersionInfo
            {
                Id = tagName,
                DisplayName = displayName,
                Tag = tagName,
                Description = $"{publishedInfo} - Classical evaluation only",
                IsRecommended = false,
                IsCompatible = true,
                MinimumCppStandard = "C++11",
                NnueRequirement = NeuralNetworkRequirement.None,
                HasClassicalEval = true,
                MajorVersion = majorVersion,
                CompatibilityNotes = "? Build scripts from SF 17.1 may need adjustments for this version"
            };
        }
        else
        {
            // SF 9 and below: Likely incompatible build system
            versionInfo = new StockfishVersionInfo
            {
                Id = tagName,
                DisplayName = displayName,
                Tag = tagName,
                Description = $"{publishedInfo} - Legacy version",
                IsRecommended = false,
                IsCompatible = false,
                MinimumCppStandard = "C++11",
                NnueRequirement = NeuralNetworkRequirement.None,
                HasClassicalEval = true,
                MajorVersion = majorVersion,
                CompatibilityNotes = "?? This version uses significantly different build scripts and is unlikely to compile correctly with this tool"
            };
        }

        return versionInfo;
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(GitHubApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;
            return new ReleaseInfo
            {
                Version = root.GetProperty("tag_name").GetString() ?? "unknown",
                Url = root.GetProperty("zipball_url").GetString() ?? string.Empty,
                Name = root.GetProperty("name").GetString() ?? "Latest Release"
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<SourceDownloadResult> DownloadSourceAsync(string version, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report($"Downloading Stockfish {version}...");

        var tempDir = Path.Combine(Path.GetTempPath(), $"stockfish_build_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var zipPath = Path.Combine(tempDir, "stockfish.zip");
        var cachePath = Path.Combine(CacheRoot, $"{version}.zip");
        
        // Determine download URL based on version
        string url;
        if (version == "master")
        {
            url = MasterZipUrl;
        }
        else if (version == "stable" || version == "latest")
        {
            var release = await GetLatestReleaseAsync(cancellationToken);
            url = release?.Url ?? MasterZipUrl;
        }
        else
        {
            // Specific version tag (e.g., "sf_17", "sf_17.1")
            url = $"https://github.com/official-stockfish/Stockfish/archive/refs/tags/{version}.zip";
        }

        // Prefer cached source if present; otherwise download and store in cache for offline reuse.
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
        {
            progress?.Report("Using cached source archive.");
            File.Copy(cachePath, zipPath, overwrite: true);
        }
        else
        {
            await SafeDownloadToFileAsync(url, zipPath, progress, cancellationToken);
            try
            {
                Directory.CreateDirectory(CacheRoot);
                File.Copy(zipPath, cachePath, overwrite: true);
                progress?.Report($"Cached source at: {cachePath}");
            }
            catch
            {
                // Cache write is best-effort; ignore failures.
            }
        }

        var downloadResult = new SourceDownloadResult
        {
            TempDirectory = tempDir
        };

        if (!string.IsNullOrEmpty(downloadResult.ExpectedSha256))
        {
            progress?.Report("Verifying source integrity...");
            if (!await VerifyFileHashAsync(zipPath, downloadResult.ExpectedSha256))
                throw new SecurityException("Source code hash mismatch - possible tampering");
        }

        progress?.Report("Extracting source code...");
        SafeExtractToDirectory(zipPath, tempDir);

        var extractedDirs = Directory.GetDirectories(tempDir).Where(d => d.Contains("Stockfish", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (extractedDirs.Length == 0)
            throw new Exception("Could not find extracted Stockfish directory");

        var rootDir = extractedDirs[0];
        var sourceDir = Path.Combine(rootDir, "src");
        if (!Directory.Exists(sourceDir))
            throw new Exception($"Source directory not found: {sourceDir}");

        progress?.Report($"Source extracted to: {sourceDir}");
        downloadResult.SourceDirectory = sourceDir;
        downloadResult.RootDirectory = rootDir;
        return downloadResult;
    }

    public async Task<bool> DownloadNeuralNetworkAsync(string sourceDirectory, BuildConfiguration? config = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("Preparing NNUE neural networks...");

        var networkFiles = DetectNetworkFileNames(sourceDirectory);
        if (networkFiles.Count == 0)
        {
            var existingNetworks = Directory.GetFiles(sourceDirectory, "*.nnue", SearchOption.AllDirectories);
            if (existingNetworks.Length > 0)
            {
                var shortNames = existingNetworks.Select(Path.GetFileName).Where(n => !string.IsNullOrWhiteSpace(n));
                progress?.Report($"Found existing NNUE file(s): {string.Join(", ", shortNames)}");
                return true;
            }

            progress?.Report("Could not determine default network files - build will attempt to download them.");
            return false;
        }

        var overallSuccess = true;

        foreach (var networkFile in networkFiles)
        {
            var destination = Path.Combine(sourceDirectory, networkFile);
            if (File.Exists(destination))
            {
                if (await ValidateNetworkFileAsync(destination, networkFile, cancellationToken))
                {
                    progress?.Report($"{networkFile} already present and validated.");
                    continue;
                }

                progress?.Report($"{networkFile} exists but failed validation - re-downloading.");
            }

            var downloaded = false;
            foreach (var urlTemplate in NetworkMirrors)
            {
                var url = string.Format(urlTemplate, networkFile);
                try
                {
                    progress?.Report($"Downloading {networkFile} from {url}...");
                    
                    var tempNetPath = destination + ".tmp";
                    await SafeDownloadToFileAsync(url, tempNetPath, progress, cancellationToken);

                    if (!await ValidateNetworkFileAsync(tempNetPath, networkFile, cancellationToken))
                    {
                        progress?.Report($"Downloaded {networkFile} from {url} failed validation.");
                        File.Delete(tempNetPath);
                        continue;
                    }

                    var sizeMb = new FileInfo(tempNetPath).Length / 1024d / 1024d;
                    File.Move(tempNetPath, destination, true);
                    progress?.Report($"Saved {networkFile} ({sizeMb:F1} MB).");
                    downloaded = true;
                    break;
                }
                catch (Exception ex)
                {
                    progress?.Report($"Failed to download {networkFile} from {url} ({ex.Message}).");
                }
            }

            if (!downloaded)
            {
                overallSuccess = false;
                progress?.Report($"Unable to download {networkFile} - make will retry during build.");
            }
        }

        return overallSuccess;
    }

    private async Task SafeDownloadToFileAsync(string url, string destinationPath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        if (totalBytes > MaxDownloadSize)
            throw new InvalidOperationException($"Download too large: {totalBytes} bytes");

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if (totalRead > MaxDownloadSize)
                throw new InvalidOperationException("Download exceeded maximum size");
        }
    }

    private static void SafeExtractToDirectory(string zipPath, string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var destDirFullPath = Path.GetFullPath(destinationDirectory);
        
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var completeFileName = Path.GetFullPath(Path.Combine(destDirFullPath, entry.FullName));

            // More robust check using Path.GetRelativePath
            try
            {
                var relativePath = Path.GetRelativePath(destDirFullPath, completeFileName);
                if (relativePath.StartsWith("..", StringComparison.Ordinal))
                {
                    throw new IOException($"Zip Slip vulnerability detected: {entry.FullName}");
                }
            }
            catch (ArgumentException)
            {
                throw new IOException($"Invalid path in zip: {entry.FullName}");
            }

            var directory = Path.GetDirectoryName(completeFileName);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            entry.ExtractToFile(completeFileName, overwrite: true);
        }
    }

    private static List<string> DetectNetworkFileNames(string sourceDirectory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new[]
        {
            Path.Combine(sourceDirectory, "evaluate.h"),
            Path.Combine(sourceDirectory, "nnue", "evaluate.h")
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            var contents = File.ReadAllText(candidate);
            foreach (Match match in NetworkMacroRegex.Matches(contents))
            {
                if (match.Success)
                    names.Add(match.Groups[1].Value);
            }

            if (names.Count == 0)
            {
                foreach (Match match in RelaxedNetworkNameRegex.Matches(contents))
                {
                    if (match.Success)
                        names.Add(match.Value);
                }
            }
        }

        if (names.Count == 0)
        {
            // Fallback to any NNUE files already present anywhere in the source tree
            foreach (var file in Directory.GetFiles(sourceDirectory, "*.nnue", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }

        if (names.Count == 0 && File.Exists(Path.Combine(sourceDirectory, "Makefile")))
        {
            // Stockfish changes macro names occasionally; use the classic default as a last resort
            names.Add("nn-1c0000000000.nnue");
        }

        return names.ToList();
    }

    private static async Task<bool> ValidateNetworkFileAsync(string filePath, string fileName, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length < 1_000_000)
            return false;

        var match = NetworkFileNameRegex.Match(fileName);
        if (!match.Success)
            return true;

        var expectedPrefix = match.Groups[1].Value.ToLowerInvariant();

        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexString(await sha256.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
        return hash.StartsWith(expectedPrefix, StringComparison.Ordinal);
    }

    private void EnsureUserAgent()
    {
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("StockfishCompiler/1.0");
    }

    private static async Task<bool> VerifyFileHashAsync(string filePath, string expectedSha256)
    {
        var normalizedExpected = expectedSha256.Replace(" ", string.Empty).ToLowerInvariant();
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexString(await sha256.ComputeHashAsync(stream)).ToLowerInvariant();
        return hash.Equals(normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }
}
