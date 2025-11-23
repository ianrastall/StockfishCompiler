namespace StockfishCompiler.Models;

public class StockfishVersionInfo
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsRecommended { get; set; }
    public bool IsCompatible { get; set; } = true;
    
    /// <summary>
    /// Minimum C++ standard required (e.g., "C++11", "C++17", "C++20")
    /// </summary>
    public string MinimumCppStandard { get; set; } = "C++11";
    
    /// <summary>
    /// Neural network requirement level
    /// </summary>
    public NeuralNetworkRequirement NnueRequirement { get; set; } = NeuralNetworkRequirement.None;
    
    /// <summary>
    /// Indicates if this version uses the classical evaluation
    /// </summary>
    public bool HasClassicalEval { get; set; } = true;
    
    /// <summary>
    /// Additional warnings or notes for this version
    /// </summary>
    public string? CompatibilityNotes { get; set; }
    
    /// <summary>
    /// Major version number for grouping (e.g., 17, 16, 12)
    /// </summary>
    public int MajorVersion { get; set; }
    
    public override string ToString() => DisplayName;
}

public enum NeuralNetworkRequirement
{
    /// <summary>
    /// No neural network support (pre-SF 12)
    /// </summary>
    None,
    
    /// <summary>
    /// Neural network optional, classical fallback available (SF 12-15)
    /// </summary>
    Optional,
    
    /// <summary>
    /// Neural network required for operation (SF 16+)
    /// </summary>
    Required
}
