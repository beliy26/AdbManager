using System.Text.Json.Serialization;

namespace AdbManager.Models;

/// <summary>Иконка приложения в кэше: обычная (один слой) или adaptive (фон + передний план).</summary>
public sealed class AppIcon
{
    public bool Adaptive { get; set; }
    public IconLayerFile? Background { get; set; }
    public IconLayerFile? Foreground { get; set; }
}

/// <summary>Слой иконки: сплошной цвет либо файл (PNG/WebP/JPEG или SVG).</summary>
public sealed class IconLayerFile
{
    public uint? Color { get; set; }
    public string? File { get; set; }
    public bool IsSvg { get; set; }
    public double Inset { get; set; }

    [JsonIgnore]
    public string? FullPath { get; set; }
}

/// <summary>Название и иконка приложения (содержимое meta.json в кэше).</summary>
public sealed class AppMetadata
{
    public int Version { get; set; }
    public string? Label { get; set; }
    public AppIcon? Icon { get; set; }
}
