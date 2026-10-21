using System.ComponentModel;

namespace AdbManager.Models;

/// <summary>Состояние пакета (PackageManager COMPONENT_ENABLED_STATE_*).</summary>
public enum PackageState
{
    /// <summary>Включено (DEFAULT / ENABLED).</summary>
    Enabled,

    /// <summary>Заморожено: DISABLED_USER (pm disable-user) или DISABLED.</summary>
    Frozen,

    /// <summary>
    /// DISABLED_UNTIL_USED: система сама остановила давно не используемое приложение
    /// (lastDisabledCaller = auto_disabler). Включается при первом запуске.
    /// </summary>
    Unused,
}

/// <summary>Установленный на устройстве пакет.</summary>
public sealed class PackageInfo(string name, bool isSystem, string apkPath) : INotifyPropertyChanged
{
    private string? _label;
    private AppIcon? _icon;
    private PackageState _state;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; } = name;
    public bool IsSystem { get; } = isSystem;
    public string ApkPath { get; } = apkPath;

    /// <summary>Название приложения из манифеста (загружается в фоне).</summary>
    public string? Label
    {
        get => _label;
        set
        {
            _label = value;
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public AppIcon? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            OnPropertyChanged(nameof(Icon));
        }
    }

    public PackageState State
    {
        get => _state;
        set
        {
            _state = value;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsFrozen));
            OnPropertyChanged(nameof(IsUnused));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IconOpacity));
        }
    }

    /// <summary>Заморожено (pm disable-user): не запускается и скрыто из лаунчера.</summary>
    public bool IsFrozen => State == PackageState.Frozen;

    /// <summary>Остановлено системой, так как давно не используется; включается при запуске.</summary>
    public bool IsUnused => State == PackageState.Unused;

    public double IconOpacity => State switch
    {
        PackageState.Frozen => 0.4,
        PackageState.Unused => 0.6,
        _ => 1.0,
    };

    /// <summary>Размер APK (байты), из dumpsys diskstats; null — нет данных.</summary>
    public long? AppSize { get; private set; }

    /// <summary>Размер данных, включая кэш.</summary>
    public long? DataSize { get; private set; }

    public long? CacheSize { get; private set; }

    public long? TotalSize => AppSize is null && DataSize is null ? null : (AppSize ?? 0) + (DataSize ?? 0);

    public string SizeText => TotalSize is { } total ? FormatSize(total) : "—";

    public string SizeToolTip => TotalSize is null
        ? "Нет данных о размере (Android ещё не посчитал его)"
        : $"Приложение: {FormatSize(AppSize ?? 0)}\nДанные: {FormatSize(DataSize ?? 0)}\nв т.ч. кэш: {FormatSize(CacheSize ?? 0)}";

    public void SetSizes(long? app, long? data, long? cache)
    {
        AppSize = app;
        DataSize = data;
        CacheSize = cache;
        OnPropertyChanged(nameof(TotalSize));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeToolTip));
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.#} {units[unit]}";
    }

    public string StatusText => State switch
    {
        PackageState.Frozen => "Заморожено",
        PackageState.Unused => "Не используется",
        _ => "",
    };

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Name : Label;

    public string KindText => IsSystem ? "Системное" : "Пользовательское";

    public string Glyph => IsSystem ? "\uE770" : "\uE74C"; // шестерёнка / приложение

    public void Apply(AppMetadata metadata)
    {
        Label = metadata.Label;
        Icon = metadata.Icon;
    }

    private void OnPropertyChanged(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
