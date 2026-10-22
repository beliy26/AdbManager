using System.Text.Json;

namespace AdbManager.Services;

/// <summary>Настройки приложения в %LOCALAPPDATA%\AdbManager\settings.json.</summary>
public sealed class Settings
{
    public string? AdbPath { get; set; }
    public string? LastConnectAddress { get; set; }
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }

    /// <summary>Устройства, сопряжённые по коду (беспроводная отладка Android 11+).</summary>
    public List<PairedDevice> PairedDevices { get; set; } = [];

    /// <summary>Названия моделей по серийному номеру и IP — чтобы показывать найденные в сети устройства по имени.</summary>
    public Dictionary<string, string> DeviceNames { get; set; } = [];

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

/// <summary>Сопряжённое устройство: IP, последний известный адрес подключения и серийный номер из mDNS.</summary>
public sealed class PairedDevice
{
    public string Host { get; set; } = "";
    public string? Address { get; set; }
    public string? Serial { get; set; }
}
