using StockfishCompiler.Models;

namespace StockfishCompiler.Services;

public interface IArchitectureDetector
{
    Task<ArchitectureInfo> DetectOptimalArchitectureAsync(CompilerInfo compiler);
    Task<List<ArchitectureInfo>> GetAvailableArchitecturesAsync();
    Task<List<string>> DetectCPUFeaturesAsync(CompilerInfo compiler);
}
