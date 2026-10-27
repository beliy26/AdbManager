using System.Numerics;
using AdbManager.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AdbManager.Controls;

/// <summary>
/// Показывает иконку приложения. Adaptive-иконка рисуется как в лаунчере: слои 108dp,
/// из которых виден центр 72dp (масштаб 1.5), с обрезкой по скруглённому квадрату.
/// </summary>
public sealed partial class AppIconView : Grid
{
    private const double AdaptiveScale = 108.0 / 72.0;
    private const int MaxCachedImages = 600;

    // Кэш декодированных изображений (все экземпляры живут в UI-потоке одного окна).
    private static readonly Dictionary<string, ImageSource> ImageCache = [];

    private int _version;

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(AppIcon), typeof(AppIconView),
        new PropertyMetadata(null, (d, _) => ((AppIconView)d).Rebuild()));

    public static readonly DependencyProperty FallbackGlyphProperty = DependencyProperty.Register(
        nameof(FallbackGlyph), typeof(string), typeof(AppIconView),
        new PropertyMetadata("\uE74C", (d, _) => ((AppIconView)d).Rebuild()));

    public AppIcon? Icon
    {
        get => (AppIcon?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string FallbackGlyph
    {
        get => (string)GetValue(FallbackGlyphProperty);
        set => SetValue(FallbackGlyphProperty, value);
    }

    public AppIconView()
    {
        Width = Height = 32;
        Loaded += (_, _) => { if (Children.Count == 0) Rebuild(); };
    }

    private double Size => double.IsNaN(Width) ? 32 : Width;

    private async void Rebuild()
    {
        int version = ++_version;
        var icon = Icon;
        Children.Clear();
        SetRoundedClip(false);

        if (icon is null || (icon.Foreground is null && icon.Background is null))
        {
            ShowFallback();
            return;
        }

        try
        {
            if (!icon.Adaptive)
            {
                var source = await LoadAsync(icon.Foreground, (int)(Size * 3));
                if (version != _version) return;
                if (source is null) { ShowFallback(); return; }
                Children.Add(new Image { Source = source, Stretch = Stretch.Uniform });
                return;
            }

            int px = (int)(Size * 4);
            var bgSource = await LoadAsync(icon.Background, px);
            var fgSource = await LoadAsync(icon.Foreground, px);
            if (version != _version) return;

            var bg = CreateLayer(icon.Background, bgSource);
            var fg = CreateLayer(icon.Foreground, fgSource);
            if (bg is null && fg is null) { ShowFallback(); return; }

            // Без фона adaptive-иконка выглядит странно — подкладываем белый.
            Children.Add(bg ?? new Rectangle { Fill = new SolidColorBrush(Microsoft.UI.Colors.White) });
            if (fg is not null) Children.Add(fg);
            SetRoundedClip(true);
        }
        catch
        {
            if (version != _version) return;
            Children.Clear();
            SetRoundedClip(false);
            ShowFallback();
        }
    }

    private UIElement? CreateLayer(IconLayerFile? layer, ImageSource? source)
    {
        if (layer is null) return null;

        if (layer.Color is uint argb)
        {
            return new Rectangle
            {
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(
                    (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)),
            };
        }

        if (source is null) return null;

        var scale = AdaptiveScale * (1 - 2 * layer.Inset);
        return new Image
        {
            Source = source,
            Stretch = Stretch.Fill,
            Width = Size,
            Height = Size,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale },
        };
    }

    private void ShowFallback()
    {
        Children.Add(new FontIcon
        {
            Glyph = FallbackGlyph,
            FontSize = Size * 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private void SetRoundedClip(bool enabled)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        if (!enabled)
        {
            visual.Clip = null;
            return;
        }

        var compositor = visual.Compositor;
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2((float)Size);
        geometry.CornerRadius = new Vector2((float)(Size * 0.28));
        visual.Clip = compositor.CreateGeometricClip(geometry);
    }

    private static async Task<ImageSource?> LoadAsync(IconLayerFile? layer, int pixelSize)
    {
        if (layer?.FullPath is not { } path || layer.Color is not null) return null;

        var key = path + "|" + pixelSize;
        if (ImageCache.TryGetValue(key, out var cached)) return cached;
        if (!File.Exists(path)) return null;

        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
        ImageSource result;
        if (layer.IsSvg)
        {
            var svg = new SvgImageSource { RasterizePixelWidth = pixelSize, RasterizePixelHeight = pixelSize };
            if (await svg.SetSourceAsync(stream) != SvgImageSourceLoadStatus.Success) return null;
            result = svg;
        }
        else
        {
            var bitmap = new BitmapImage { DecodePixelWidth = pixelSize };
            await bitmap.SetSourceAsync(stream);
            result = bitmap;
        }

        if (ImageCache.Count > MaxCachedImages) ImageCache.Clear();
        ImageCache[key] = result;
        return result;
    }
}
