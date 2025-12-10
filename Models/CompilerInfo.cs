namespace StockfishCompiler.Models;

public class CompilerInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // gcc, clang, msvc, mingw
    public string Version { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    
    /// <summary>
    /// Contains the error message if the compiler failed validation (e.g., missing DLLs).
    /// Null or empty if the compiler is valid and available.
    /// </summary>
    public string? ValidationError { get; set; }

    public override string ToString() => DisplayName;
}
