using StockfishCompiler.Models;

namespace StockfishCompiler.Services;

public interface ICompilerService
{
    Task<List<CompilerInfo>> DetectCompilersAsync();
    Task<bool> ValidateCompilerAsync(CompilerInfo compiler);
    Task<string> GetCompilerVersionAsync(string compilerPath);
}
