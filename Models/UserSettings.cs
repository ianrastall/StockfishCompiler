using System;

namespace StockfishCompiler.Models;

public class UserSettings
{
    public bool DownloadNetwork { get; set; } = true;
    public bool StripExecutable { get; set; } = true;
    public int ParallelJobs { get; set; } = Environment.ProcessorCount;
    public string OutputDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    public string SourceVersion { get; set; } = "stable";
}
