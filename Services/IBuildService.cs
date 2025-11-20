using StockfishCompiler.Models;

namespace StockfishCompiler.Services;

public interface IBuildService
{
    IObservable<string> Output { get; }
    IObservable<double> Progress { get; }
    IObservable<bool> IsBuilding { get; }

    Task<CompilationResult> BuildAsync(BuildConfiguration configuration);
    void CancelBuild();
}
