using System.ComponentModel;

namespace AdbManager.Models;

/// <summary>Совет по пакету (база ЧистогоТВ и встроенная защита).</summary>
public enum PackageAdvice
{
    None,
    /// <summary>Реклама, телеметрия, мусор — безопасно отключить.</summary>
    Disable,
    /// <summary>Приложение, которым пользуются не все, — на усмотрение пользователя.</summary>
    Optional,
    /// <summary>В базе помечено «не трогать».</summary>
    Keep,
    /// <summary>Критичный системный компонент (встроенный список защиты).</summary>
    Protected,
}

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

    // ----- Совет из базы ЧистогоТВ / защита -----

    public PackageAdvice Advice { get; private set; }
    public string? AdviceTitle { get; private set; }
    public string? AdviceNote { get; private set; }

    /// <summary>Заморозка и удаление запрещены: критичный системный пакет или «не трогать» в базе.</summary>
    public bool IsBlocked => Advice is PackageAdvice.Protected or PackageAdvice.Keep;

    public bool IsAdviceDisable => Advice == PackageAdvice.Disable;
    public bool IsAdviceOptional => Advice == PackageAdvice.Optional;
    public bool IsAdviceBlocked => IsBlocked;

    public string AdviceText => Advice switch
    {
        PackageAdvice.Disable => "Можно отключить",
        PackageAdvice.Optional => "По желанию",
        PackageAdvice.Keep => "Не трогать",
        PackageAdvice.Protected => "Защищено",
        _ => "",
    };

    public string? AdviceToolTip => Advice == PackageAdvice.None
        ? null
        : string.Join(Environment.NewLine, new[] { AdviceTitle, AdviceNote }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public void SetAdvice(PackageAdvice advice, string? title, string? note)
    {
        Advice = advice;
        AdviceTitle = title;
        AdviceNote = note;
        foreach (var p in new[] { nameof(Advice), nameof(IsBlocked), nameof(IsAdviceDisable), nameof(IsAdviceOptional),
                     nameof(IsAdviceBlocked), nameof(AdviceText), nameof(AdviceToolTip) })
            OnPropertyChanged(p);
    }

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
