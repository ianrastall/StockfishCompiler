using System.IO;
using System.Runtime.InteropServices;
using StockfishCompiler.Models;

namespace StockfishCompiler.Helpers;

public static class MSYS2Helper
{
    public static string[] GetCommonMSYS2Paths() => new[]
    {
        @"C:\msys64",
        @"C:\msys2",
        @"D:\msys64",
        @"D:\msys2",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "msys64")
    };

    public static string? FindMSYS2Installation()
    {
        return GetCommonMSYS2Paths().FirstOrDefault(IsValidMSYS2Installation);
    }

    public static string? FindMakeExecutable(string? compilerPath = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "make";

        // Try to find make near the compiler
        if (!string.IsNullOrEmpty(compilerPath))
        {
            var compilerDir = new DirectoryInfo(compilerPath);
            var msys2Root = compilerDir.Parent?.Parent;
            
            if (msys2Root != null && msys2Root.Exists && IsValidMSYS2Installation(msys2Root.FullName))
            {
                var makePaths = new[]
                {
                    Path.Combine(msys2Root.FullName, "usr", "bin", "make.exe"),
                    Path.Combine(msys2Root.FullName, "mingw64", "bin", "make.exe"),
                    Path.Combine(msys2Root.FullName, "mingw64", "bin", "mingw32-make.exe")
                };

                foreach (var makePath in makePaths)
                {
                    if (File.Exists(makePath))
                        return makePath;
                }
            }
        }

        // Try common MSYS2 paths
        foreach (var msys2Path in GetCommonMSYS2Paths())
        {
            if (!Directory.Exists(msys2Path))
                continue;

            var makePaths = new[]
            {
                Path.Combine(msys2Path, "usr", "bin", "make.exe"),
                Path.Combine(msys2Path, "mingw64", "bin", "make.exe"),
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

        // Try compiler path first
        if (config?.SelectedCompiler?.Path != null)
        {
            var compilerDir = new DirectoryInfo(config.SelectedCompiler.Path);
            var msys2Root = compilerDir.Parent?.Parent;
            
            if (msys2Root != null && msys2Root.Exists && IsValidMSYS2Installation(msys2Root.FullName))
            {
                AddMSYS2PathsToList(msys2Root.FullName, pathsToAdd);
            }
        }

        // If no paths added, try common MSYS2 paths
        if (pathsToAdd.Count == 0)
        {
            var msys2Path = FindMSYS2Installation();
            if (msys2Path != null)
            {
                AddMSYS2PathsToList(msys2Path, pathsToAdd);
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

    private static void AddMSYS2PathsToList(string msys2Root, List<string> pathsToAdd)
    {
        var usrBin = Path.Combine(msys2Root, "usr", "bin");
        var mingw64Bin = Path.Combine(msys2Root, "mingw64", "bin");
        
        if (Directory.Exists(usrBin))
            pathsToAdd.Add(usrBin);
        if (Directory.Exists(mingw64Bin))
            pathsToAdd.Add(mingw64Bin);
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
