using System.Text.Json;

namespace AdbManager.Services;

/// <summary>Настройки приложения в %LOCALAPPDATA%\AdbManager\settings.json.</summary>
public sealed class Settings
{
    public string? AdbPath { get; set; }
    public string? LastConnectAddress { get; set; }
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }

    /// <summary>Язык интерфейса: «ru-RU», «en-US» или null — по языкам Windows.</summary>
    public string? Language { get; set; }

    /// <summary>Версия, уведомление о которой закрыли, — о ней больше не напоминаем.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>MCP-сервер для агентов включён — запускается вместе с окном.</summary>
    public bool McpEnabled { get; set; }

    /// <summary>Порт MCP-сервера на localhost.</summary>
    public int McpPort { get; set; } = 47037;

    /// <summary>Устройства, сопряжённые по коду (беспроводная отладка Android 11+).</summary>
    public List<PairedDevice> PairedDevices { get; set; } = [];

    /// <summary>Названия моделей по серийному номеру и IP — чтобы показывать найденные в сети устройства по имени.</summary>
    public Dictionary<string, string> DeviceNames { get; set; } = [];

    /// <summary>Типы устройств (телефон, ТВ…) по тем же ключам, что и <see cref="DeviceNames"/>, — для иконок ещё до подключения.</summary>
    public Dictionary<string, Models.DeviceType> DeviceTypes { get; set; } = [];

    /// <summary>Пресеты отметки: именованные наборы пакетов, общие для всех устройств.</summary>
    public List<SelectionPreset> Presets { get; set; } = [];

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

/// <summary>Пресет отметки: название, описание и имена пакетов.</summary>
public sealed class SelectionPreset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Packages { get; set; } = [];
    public DateTime Created { get; set; }
}
