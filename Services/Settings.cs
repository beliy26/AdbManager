using System.Text.Json;

namespace AdbManager.Services;

/// <summary>Настройки приложения в %LOCALAPPDATA%\AdbManager\settings.json.</summary>
public sealed class Settings
{
    public string? AdbPath { get; set; }
    public string? LastConnectAddress { get; set; }
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }

    private static string FilePath => Path.Combine(AdbService.AppDataDir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception) { /* повреждённый файл — используем значения по умолчанию */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AdbService.AppDataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* настройки не критичны */ }
    }
}
