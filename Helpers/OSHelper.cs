using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace StockfishCompiler.Helpers;

public static class OSHelper
{
    public static string GetFriendlyOSName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetWindowsVersionName();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macOS";
        }
        
        return RuntimeInformation.OSDescription;
    }

    private static string GetWindowsVersionName()
    {
        var version = Environment.OSVersion.Version;
        var buildNumber = version.Build;

        try
        {
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                var productName = key.GetValue("ProductName")?.ToString();
                var displayVersion = key.GetValue("DisplayVersion")?.ToString();
                var releaseId = key.GetValue("ReleaseId")?.ToString();
                var rawBuild = key.GetValue("CurrentBuildNumber")?.ToString();

                if (int.TryParse(rawBuild, out var parsedBuild))
                {
                    buildNumber = parsedBuild;
                }

                if (!string.IsNullOrEmpty(productName))
                {
                    productName = productName.Replace("Microsoft ", "");

                    if (buildNumber >= 22000 && !productName.Contains("11"))
                    {
                        productName = "Windows 11";
                    }

                    var versionLabel = !string.IsNullOrEmpty(displayVersion)
                        ? displayVersion
                        : !string.IsNullOrEmpty(releaseId) ? releaseId : string.Empty;

                    if (string.IsNullOrWhiteSpace(versionLabel) && buildNumber > 0)
                    {
                        versionLabel = $"build {buildNumber}";
                    }

                    if (!string.IsNullOrWhiteSpace(versionLabel))
                    {
                        return $"{productName} {versionLabel}";
                    }
                    
                    return productName;
                }
            }
        }
        catch
        {
            // Fall back to basic detection
        }

        if (buildNumber >= 22000)
            return "Windows 11";

        if (version.Major == 10)
        {
            return "Windows 10";
        }
        else if (version.Major == 6)
        {
            if (version.Minor == 3)
                return "Windows 8.1";
            else if (version.Minor == 2)
                return "Windows 8";
            else if (version.Minor == 1)
                return "Windows 7";
        }

        return "Windows";
    }
}
