using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StockfishCompiler.Models;

namespace StockfishCompiler.Services;

public class UserSettingsService : IUserSettingsService
{
    private readonly ILogger<UserSettingsService> _logger;
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public UserSettingsService(ILogger<UserSettingsService> logger)
    {
        _logger = logger;
        var appFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StockfishCompiler");
        Directory.CreateDirectory(appFolder);
        _settingsPath = Path.Combine(appFolder, "user-settings.json");
    }

    public string SettingsFilePath => _settingsPath;

    public UserSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("Settings root must be an object");
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Settings file corrupted, backing up and resetting to defaults");
                    var backupPath = _settingsPath + $".backup.{DateTime.Now:yyyyMMddHHmmss}";
                    try
                    {
                        File.Copy(_settingsPath, backupPath);
                        _logger.LogInformation("Backed up corrupted settings to {Path}", backupPath);
                    }
                    catch (Exception backupEx)
                    {
                        _logger.LogWarning(backupEx, "Could not backup corrupted settings");
                    }
                    
                    try
                    {
                        File.Delete(_settingsPath);
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogWarning(deleteEx, "Could not delete corrupted settings file");
                    }
                    
                    var sanitizedDefaults = Sanitize(new UserSettings());
                    Save(sanitizedDefaults);
                    return sanitizedDefaults;
                }

                var settings = JsonSerializer.Deserialize<UserSettings>(json);
                if (settings != null)
                    return Sanitize(settings);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load user settings; using defaults");
        }

        var defaults = Sanitize(new UserSettings());
        Save(defaults);
        return defaults;
    }

    public void Save(UserSettings settings)
    {
        try
        {
            var sanitized = Sanitize(settings);
            var json = JsonSerializer.Serialize(sanitized, _jsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save user settings to {Path}", _settingsPath);
        }
    }

    private static UserSettings Sanitize(UserSettings settings)
    {
        settings.ParallelJobs = settings.ParallelJobs <= 0 ? Environment.ProcessorCount : settings.ParallelJobs;
        settings.OutputDirectory = string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            : settings.OutputDirectory;
        settings.SourceVersion = string.IsNullOrWhiteSpace(settings.SourceVersion) ? "stable" : settings.SourceVersion;
        return settings;
    }
}
