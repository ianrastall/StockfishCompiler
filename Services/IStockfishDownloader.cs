using StockfishCompiler.Models;

namespace StockfishCompiler.Services;

public interface IStockfishDownloader
{
    Task<SourceDownloadResult> DownloadSourceAsync(string version, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<bool> DownloadNeuralNetworkAsync(string sourceDirectory, BuildConfiguration? config = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default);
}

public class ReleaseInfo
{
    public string Version { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class SourceDownloadResult
{
    public string SourceDirectory { get; set; } = string.Empty;
    public string RootDirectory { get; set; } = string.Empty;
    public string TempDirectory { get; set; } = string.Empty;
}
