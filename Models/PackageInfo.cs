using System.ComponentModel;

namespace AdbManager.Models;

/// <summary>Установленный на устройстве пакет.</summary>
public sealed class PackageInfo(string name, bool isSystem, string apkPath) : INotifyPropertyChanged
{
    private string? _label;
    private AppIcon? _icon;
    private bool _isDisabled;

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

    /// <summary>Приложение заморожено (pm disable-user): не запускается и скрыто из лаунчера.</summary>
    public bool IsDisabled
    {
        get => _isDisabled;
        set
        {
            _isDisabled = value;
            OnPropertyChanged(nameof(IsDisabled));
            OnPropertyChanged(nameof(IconOpacity));
        }
    }

    public double IconOpacity => IsDisabled ? 0.4 : 1.0;

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
