using StockfishCompiler.Models;

namespace StockfishCompiler.Services;

public interface IUserSettingsService
{
    UserSettings Load();
    void Save(UserSettings settings);
    string SettingsFilePath { get; }
}
