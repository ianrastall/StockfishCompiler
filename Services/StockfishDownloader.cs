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
    private const string MasterZipUrl = "https://github.com/official-stockfish/Stockfish/archive/refs/heads/master.zip";
    private static readonly string[] NetworkMirrors =
    [
        "https://tests.stockfishchess.org/api/nn/{0}",
        "https://github.com/official-stockfish/networks/raw/master/{0}"
    ];
    private static readonly Regex NetworkMacroRegex = new("#define\\s+EvalFileDefaultName\\w*\\s+\"(nn-[a-z0-9]{12}\\.nnue)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
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
        var url = version == "master" ? MasterZipUrl : (await GetLatestReleaseAsync(cancellationToken))?.Url ?? MasterZipUrl;

        await SafeDownloadToFileAsync(url, zipPath, progress, cancellationToken);

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
        if (!Path.EndsInDirectorySeparator(destDirFullPath))
            destDirFullPath += Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var completeFileName = Path.GetFullPath(Path.Combine(destDirFullPath, entry.FullName));

            if (!completeFileName.StartsWith(destDirFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Zip Slip vulnerability detected: Entry tries to write outside target directory.");
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
