using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace StockfishCompiler.Helpers;

/// <summary>
/// Centralized process execution with timeout, cancellation, and robust error handling.
/// </summary>
public class ProcessExecutor
{
    private readonly ILogger<ProcessExecutor> _logger;

    public ProcessExecutor(ILogger<ProcessExecutor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Executes a process with comprehensive timeout and cancellation support.
    /// </summary>
    public async Task<ProcessResult> ExecuteAsync(
        ProcessStartInfo startInfo,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        int maxOutputLines = 10000)
    {
        var result = new ProcessResult
        {
            StartTime = DateTime.UtcNow
        };

        // Create linked cancellation token with timeout
        using var timeoutCts = timeout.HasValue 
            ? new CancellationTokenSource(timeout.Value) 
            : new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, 
            timeoutCts.Token);

        Process? process = null;
        try
        {
            // Ensure we can capture output
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            _logger.LogDebug(
                "Executing: {FileName} {Args} (Timeout: {Timeout})",
                startInfo.FileName,
                startInfo.Arguments,
                timeout?.ToString() ?? "None");

            process = Process.Start(startInfo);
            if (process == null)
            {
                result.Success = false;
                result.Error = "Failed to start process";
                return result;
            }

            result.ProcessId = process.Id;

            // Setup output capturing with size limits
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            var outputLineCount = 0;
            var errorLineCount = 0;
            var outputTruncated = false;
            var errorTruncated = false;

            async Task ReadStreamAsync(
                StreamReader reader, 
                StringBuilder builder, 
                bool isError)
            {
                try
                {
                    while (!reader.EndOfStream)
                    {
                        linkedCts.Token.ThrowIfCancellationRequested();
                        
                        var line = await reader.ReadLineAsync();
                        if (line == null) break;

                        var lineCount = isError ? errorLineCount : outputLineCount;
                        var truncated = isError ? errorTruncated : outputTruncated;

                        if (lineCount < maxOutputLines)
                        {
                            builder.AppendLine(line);
                            progress?.Report(line);
                            
                            if (isError)
                                errorLineCount++;
                            else
                                outputLineCount++;
                        }
                        else if (!truncated)
                        {
                            var msg = $"[Output truncated after {maxOutputLines} lines]";
                            builder.AppendLine(msg);
                            progress?.Report(msg);
                            
                            if (isError)
                                errorTruncated = true;
                            else
                                outputTruncated = true;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected during cancellation
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reading {Stream} stream", isError ? "error" : "output");
                }
            }

            // Read both streams concurrently
            var readStdOut = ReadStreamAsync(
                process.StandardOutput, 
                outputBuilder, 
                false);
            
            var readStdErr = ReadStreamAsync(
                process.StandardError, 
                errorBuilder, 
                true);

            // Register cancellation handler
            using var registration = linkedCts.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        _logger.LogInformation("Killing process {ProcessId} due to cancellation/timeout", process.Id);
                        KillProcessTree(process);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error killing process during cancellation");
                }
            });

            // Wait for process to exit
            await process.WaitForExitAsync(linkedCts.Token);

            // Wait for streams to finish reading (with short timeout)
            using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await Task.WhenAll(readStdOut, readStdErr).WaitAsync(streamCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Stream reading timed out after process exit");
            }

            result.ExitCode = process.ExitCode;
            result.Success = process.ExitCode == 0;
            result.StandardOutput = outputBuilder.ToString();
            result.StandardError = errorBuilder.ToString();
            result.OutputTruncated = outputTruncated;
            result.ErrorTruncated = errorTruncated;
            result.EndTime = DateTime.UtcNow;

            _logger.LogDebug(
                "Process {ProcessId} exited with code {ExitCode} after {Duration}ms",
                process.Id,
                process.ExitCode,
                (result.EndTime - result.StartTime).TotalMilliseconds);

            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            result.Success = false;
            result.TimedOut = true;
            result.Error = $"Process timed out after {timeout?.TotalSeconds ?? 0} seconds";
            result.EndTime = DateTime.UtcNow;
            
            _logger.LogWarning("Process {ProcessId} timed out after {Duration}",
                result.ProcessId,
                timeout?.ToString() ?? "unknown");
            
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Cancelled = true;
            result.Error = "Process cancelled by user";
            result.EndTime = DateTime.UtcNow;
            
            _logger.LogInformation("Process {ProcessId} cancelled", result.ProcessId);
            
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            result.Exception = ex;
            result.EndTime = DateTime.UtcNow;
            
            _logger.LogError(ex, "Error executing process");
            
            return result;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Executes a process and returns only the first line of output (useful for version checks).
    /// </summary>
    public async Task<string> ExecuteForFirstLineAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(10);
        var result = await ExecuteAsync(startInfo, null, cancellationToken, timeout, maxOutputLines: 100);
        
        if (!result.Success)
        {
            return string.Empty;
        }

        var output = !string.IsNullOrWhiteSpace(result.StandardOutput) 
            ? result.StandardOutput 
            : result.StandardError;

        return output.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Kills a process and its entire process tree.
    /// </summary>
    private void KillProcessTree(Process process)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use taskkill on Windows to kill tree
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (PlatformNotSupportedException)
                {
                    // Fall back to single process kill on older .NET versions
                    process.Kill();
                }
            }
            else
            {
                // On Unix, kill the process group
                try
                {
                    var killProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = "kill",
                        Arguments = $"-TERM -{process.Id}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    killProcess?.WaitForExit(1000);
                }
                catch
                {
                    // Fall back to single process kill
                    process.Kill();
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
    }
}

public class ProcessResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public Exception? Exception { get; set; }
    public int ProcessId { get; set; }
    public bool TimedOut { get; set; }
    public bool Cancelled { get; set; }
    public bool OutputTruncated { get; set; }
    public bool ErrorTruncated { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    
    public TimeSpan Duration => EndTime - StartTime;
    
    public string CombinedOutput => 
        string.IsNullOrWhiteSpace(StandardError) 
            ? StandardOutput 
            : $"{StandardOutput}\n\n=== STDERR ===\n{StandardError}";
}
