using System.Text;

namespace StockfishCompiler.Models;

public class CompilationResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string ErrorOutput { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>
    /// Parsed compilation errors (error messages extracted from output)
    /// </summary>
    public List<CompilationError> Errors { get; set; } = new();

    /// <summary>
    /// Parsed compilation warnings
    /// </summary>
    public List<CompilationError> Warnings { get; set; } = new();

    /// <summary>
    /// Summary of what failed (if applicable)
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Gets a user-friendly error summary
    /// </summary>
    public string GetErrorSummary()
    {
        if (Success)
            return "Build completed successfully";

        var summary = new StringBuilder();
        summary.AppendLine($"Build failed with exit code {ExitCode}");

        if (!string.IsNullOrWhiteSpace(FailureReason))
        {
            summary.AppendLine($"Reason: {FailureReason}");
        }

        if (Errors.Count > 0)
        {
            summary.AppendLine($"\nErrors ({Errors.Count}):");
            foreach (var error in Errors.Take(5))
            {
                summary.AppendLine($"  • {error.Message}");
            }
            if (Errors.Count > 5)
            {
                summary.AppendLine($"  ... and {Errors.Count - 5} more errors");
            }
        }

        if (Warnings.Count > 0)
        {
            summary.AppendLine($"\nWarnings ({Warnings.Count}):");
            foreach (var warning in Warnings.Take(3))
            {
                summary.AppendLine($"  • {warning.Message}");
            }
            if (Warnings.Count > 3)
            {
                summary.AppendLine($"  ... and {Warnings.Count - 3} more warnings");
            }
        }

        return summary.ToString();
    }
}

public class CompilationError
{
    public string File { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    public string Message { get; set; } = string.Empty;
    public ErrorSeverity Severity { get; set; }
    public string RawLine { get; set; } = string.Empty;
}

public enum ErrorSeverity
{
    Note,
    Warning,
    Error,
    Fatal
}
