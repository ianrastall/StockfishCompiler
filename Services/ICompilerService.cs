using StockfishCompiler.Models;

namespace StockfishCompiler.Services;

public interface ICompilerService
{
    /// <summary>
    /// Detects available C++ compilers on the system
    /// </summary>
    /// <returns>List of detected compiler information</returns>
    /// <remarks>
    /// Searches for compilers in the following locations:
    /// - MSYS2 installations (mingw64, ucrt64, clang64, etc.)
    /// - Git for Windows MinGW
    /// - Visual Studio Clang/LLVM and MSVC
    /// - Standalone MinGW installations
    /// - System PATH
    /// </remarks>
    Task<List<CompilerInfo>> DetectCompilersAsync();
    
    /// <summary>
    /// Validates that a compiler executable exists and is accessible
    /// </summary>
    /// <param name="compiler">The compiler information to validate</param>
    /// <returns>True if the compiler is valid and accessible, false otherwise</returns>
    Task<bool> ValidateCompilerAsync(CompilerInfo compiler);
    
    /// <summary>
    /// Gets the version information of a compiler
    /// </summary>
    /// <param name="compilerPath">Full path to the compiler executable</param>
    /// <returns>Version string from the compiler's --version output, or empty string if failed</returns>
    Task<string> GetCompilerVersionAsync(string compilerPath);
}
