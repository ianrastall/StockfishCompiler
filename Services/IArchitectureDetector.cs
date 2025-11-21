using StockfishCompiler.Models;
using System.Threading;

namespace StockfishCompiler.Services;

public interface IArchitectureDetector
{
    Task<ArchitectureInfo> DetectOptimalArchitectureAsync(CompilerInfo compiler, CancellationToken cancellationToken = default);
    Task<List<ArchitectureInfo>> GetAvailableArchitecturesAsync();
    Task<List<string>> DetectCPUFeaturesAsync(CompilerInfo compiler, CancellationToken cancellationToken = default);
}
