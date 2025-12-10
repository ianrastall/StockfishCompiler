using System.IO;
using System.Runtime.InteropServices;
using StockfishCompiler.Models;

namespace StockfishCompiler.Helpers;

public static class MSYS2Helper
{
    public static string[] GetCommonMSYS2Paths() =>
    [
        @"C:\msys64",
        @"C:\msys2",
        @"D:\msys64",
        @"D:\msys2",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "msys64"),
    ];

    public static string? FindMSYS2Installation()
    {
        return GetCommonMSYS2Paths().FirstOrDefault(IsValidMSYS2Installation);
    }

    public static string? FindMakeExecutable(string? compilerPath = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "make";

        // Try to find make near the compiler first - this ensures we use the same MSYS2 installation
        if (!string.IsNullOrEmpty(compilerPath))
        {
            var compilerDir = new DirectoryInfo(compilerPath);
            
            // Walk up to find MSYS2 root (compiler is in e.g. msys64/mingw64/bin)
            var potentialRoot = compilerDir.Parent?.Parent;
            
            if (potentialRoot != null && potentialRoot.Exists && IsValidMSYS2Installation(potentialRoot.FullName))
            {
                // Prefer usr/bin/make.exe as it's the standard location
                var usrMake = Path.Combine(potentialRoot.FullName, "usr", "bin", "make.exe");
                if (File.Exists(usrMake))
                    return usrMake;
                    
                // Also check mingw64/bin for mingw32-make
                var mingwMake = Path.Combine(potentialRoot.FullName, "mingw64", "bin", "mingw32-make.exe");
                if (File.Exists(mingwMake))
                    return mingwMake;
            }
        }

        // Try common MSYS2 paths as fallback
        foreach (var msys2Path in GetCommonMSYS2Paths())
        {
            if (!Directory.Exists(msys2Path))
                continue;

            var makePaths = new[]
            {
                Path.Combine(msys2Path, "usr", "bin", "make.exe"),
                Path.Combine(msys2Path, "mingw64", "bin", "mingw32-make.exe")
            };

            foreach (var makePath in makePaths)
            {
                if (File.Exists(makePath))
                    return makePath;
            }
        }

        return "make";
    }

    public static Dictionary<string, string> SetupEnvironment(BuildConfiguration? config = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        // Copy existing environment
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                env[key] = value;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return env;

        var pathsToAdd = new List<string>();
        string? msys2Root = null;

        // Try to determine MSYS2 root from compiler path - this ensures consistency
        if (config?.SelectedCompiler?.Path != null)
        {
            var compilerDir = new DirectoryInfo(config.SelectedCompiler.Path);
            var potentialRoot = compilerDir.Parent?.Parent;
            
            if (potentialRoot != null && potentialRoot.Exists && IsValidMSYS2Installation(potentialRoot.FullName))
            {
                msys2Root = potentialRoot.FullName;
            }
        }

        // If no root found from compiler, try common paths
        if (string.IsNullOrEmpty(msys2Root))
        {
            msys2Root = FindMSYS2Installation();
        }

        // Add MSYS2 paths if found
        if (!string.IsNullOrEmpty(msys2Root))
        {
            // Add paths in order of preference
            var usrBin = Path.Combine(msys2Root, "usr", "bin");
            var mingw64Bin = Path.Combine(msys2Root, "mingw64", "bin");
            
            // mingw64/bin should come first so mingw tools are preferred
            if (Directory.Exists(mingw64Bin))
                pathsToAdd.Add(mingw64Bin);
            if (Directory.Exists(usrBin))
                pathsToAdd.Add(usrBin);
                
            // Set MSYSTEM environment variable for proper MSYS2 operation
            env["MSYSTEM"] = "MINGW64";
            
            // Ensure HOME is set (some tools need it)
            if (!env.ContainsKey("HOME"))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProfile))
                    env["HOME"] = userProfile;
            }
        }

        // Update PATH if we found MSYS2 paths
        if (pathsToAdd.Count > 0)
        {
            var currentPath = env.GetValueOrDefault("PATH", string.Empty);
            env["PATH"] = string.Join(";", pathsToAdd) + ";" + currentPath;
        }

        return env;
    }

    public static bool IsValidMSYS2Installation(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        var usrBin = Path.Combine(path, "usr", "bin");
        var makeExe = Path.Combine(usrBin, "make.exe");
        var mingw64 = Path.Combine(path, "mingw64");

        return Directory.Exists(usrBin) && Directory.Exists(mingw64) && File.Exists(makeExe);
    }
}
