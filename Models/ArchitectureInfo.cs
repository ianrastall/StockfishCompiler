namespace StockfishCompiler.Models;

public class ArchitectureInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // x86, ARM, PPC, etc.
    public bool IsRecommended { get; set; }
    public List<string> Features { get; set; } = [];

    public override string ToString() => Name;
}
