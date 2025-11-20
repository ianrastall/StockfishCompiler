namespace StockfishCompiler.Models;

public class CompilationResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public int ExitCode { get; set; }
}
