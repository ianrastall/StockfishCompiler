namespace StockfishCompiler.Models;

public class BuildConfiguration
{
    public CompilerInfo? SelectedCompiler { get; set; }
    public ArchitectureInfo? SelectedArchitecture { get; set; }
    public string SourceVersion { get; set; } = "stable"; // stable, development
    public bool DownloadNetwork { get; set; } = true;
    public bool StripExecutable { get; set; } = true;
    public bool EnablePgo { get; set; } = true;
    public int ParallelJobs { get; set; } = Environment.ProcessorCount;
    public string OutputDirectory { get; set; } = string.Empty;
}
