using System.Globalization;
using System.Security;
using System.Text;

namespace AdbManager.Apk;

internal sealed class ApkSource(RemoteApk zip, ResTable? table)
{
    public RemoteApk Zip { get; } = zip;
    public ResTable? Table { get; } = table;
}

internal abstract record IconLayer
{
    /// <summary>Отступ слоя (доля от размера) — из &lt;inset android:inset="x%"&gt;.</summary>
    public double Inset { get; init; }
}

internal sealed record ColorLayer(uint Argb) : IconLayer;
internal sealed record RasterLayer(byte[] Data, string Extension) : IconLayer;
internal sealed record SvgLayer(string Svg) : IconLayer;
internal sealed record AdaptiveLayer(IconLayer? Background, IconLayer? Foreground) : IconLayer;

/// <summary>
/// Достаёт из APK название приложения и иконку: разбирает манифест, ищет ресурсы в resources.arsc
/// (при необходимости — в split APK с плотностями экрана), adaptive-иконки и векторные drawable
/// превращает в слои, которые умеет показать UI (растр, SVG, цвет).
/// </summary>
internal sealed class ApkResolver(
    ApkSource baseSource,
    Func<CancellationToken, Task<IReadOnlyList<ApkSource>>> splitLoader,
    CancellationToken ct)
{
    private const int MaxDepth = 12;

    private readonly List<ApkSource> _sources = [baseSource];
    private bool _splitsLoaded;

    // ---------- Название ----------

    public string? GetLabel(AxmlElement manifest)
    {
        var attr = manifest.Child("application")?.Attr("label", Attrs.Label);
        if (attr is null) return null;
        return attr.IsReference ? ResolveString(attr.Data, 0) : attr.Raw;
    }

    internal string? ResolveStringAttr(AxmlAttr? attr) =>
        attr is null ? null : attr.IsReference ? ResolveString(attr.Data, 0) : attr.Raw;

    private string? ResolveString(uint id, int depth)
    {
        var table = baseSource.Table;
        if (depth > MaxDepth || table is null) return null;

        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var entries = table.Lookup(id);
        if (entries.Count == 0) return null;

        var best = entries
            .OrderBy(e => e.Config.Language == lang ? 0 : e.Config.Language.Length == 0 ? 1 : 2)
            .ThenBy(e => e.Config.Country.Length == 0 ? 0 : 1)
            .First();

        if (best.Type == ResValueType.String) return table.GetString(best.Data);
        if (best.IsReference) return ResolveString(best.Data, depth + 1);
        return null;
    }

    // ---------- Иконка ----------

    public async Task<IconLayer?> GetIconAsync(AxmlElement manifest)
    {
        var app = manifest.Child("application");
        if (app is null) return null;

        // Иконка может быть задана не у <application>, а только у activity лаунчера.
        var launcher = app.Children
            .Where(c => c.Name is "activity" or "activity-alias")
            .OrderByDescending(IsLauncherActivity);

        foreach (var element in launcher.Prepend(app))
        foreach (var attr in new[] { element.Attr("icon", Attrs.Icon), element.Attr("roundIcon", Attrs.RoundIcon) })
        {
            if (attr is not { IsReference: true }) continue;
            var layer = await ResolveDrawableAsync(attr.Data, 0);
            if (layer is RasterLayer or SvgLayer or AdaptiveLayer) return layer;
        }
        return null;
    }

    private static bool IsLauncherActivity(AxmlElement activity) =>
        activity.Children.Where(c => c.Name == "intent-filter").Any(f =>
            f.Children.Any(c => c.Name == "action" && c.Attr("name", Attrs.Name)?.Raw == "android.intent.action.MAIN") &&
            f.Children.Any(c => c.Name == "category" && c.Attr("name", Attrs.Name)?.Raw == "android.intent.category.LAUNCHER"));

    private async Task<List<(ApkSource Src, ResEntry Entry)>> LookupAsync(uint id, bool needFile)
    {
        var list = Collect(id);
        // Приложения из Google Play (App Bundle) хранят растровые иконки в split_config.*dpi.apk.
        if (needFile && !_splitsLoaded && !list.Any(IsUsable))
        {
            _splitsLoaded = true;
            try { _sources.AddRange(await splitLoader(ct)); }
            catch (OperationCanceledException) { throw; }
            catch { /* без split-ов */ }
            list = Collect(id);
        }
        return list.OrderBy(c => c.Entry.Config.Night).ToList();
    }

    private List<(ApkSource Src, ResEntry Entry)> Collect(uint id) =>
        _sources.Where(s => s.Table is not null)
            .SelectMany(s => s.Table!.Lookup(id).Select(e => (s, e)))
            .ToList();

    private static bool IsUsable((ApkSource Src, ResEntry Entry) c) =>
        c.Entry.Type != ResValueType.String || c.Src.Zip.Contains(c.Src.Table!.GetString(c.Entry.Data));

    private async Task<IconLayer?> ResolveDrawableAsync(uint id, int depth)
    {
        if (depth > MaxDepth) return null;
        if (FrameworkColors.TryGetValue(id, out var frameworkColor)) return new ColorLayer(frameworkColor);
        var candidates = await LookupAsync(id, needFile: true);

        var files = candidates
            .Where(c => c.Entry.Type == ResValueType.String)
            .Select(c => (c.Src, Path: c.Src.Table!.GetString(c.Entry.Data), c.Entry.Config))
            .Where(f => f.Src.Zip.Contains(f.Path))
            .ToList();

        // 1. Файлы: сначала растр максимальной плотности, затем XML (adaptive-icon, vector...).
        //    Имена бывают обфусцированы без расширения (res/QtC) — тип определяем по содержимому.
        var ordered = files
            .Select(f => (f.Src, f.Path, f.Config, Kind: FileKindByName(f.Path)))
            .Where(f => f.Kind != FileKind.Other)
            .OrderBy(f => f.Kind == FileKind.Xml ? 1 : 0)
            .ThenByDescending(f => DensityScore(f.Config.Density))
            .ThenByDescending(f => f.Config.Sdk)
            .Take(6);

        foreach (var f in ordered)
        {
            if (await f.Src.Zip.ReadAsync(f.Path, ct) is not { } bytes) continue;

            if (SniffRaster(bytes) is { } ext)
                return new RasterLayer(bytes, ext);

            if (bytes.Length > 8 && bytes[0] == 0x03 && bytes[1] == 0x00)
            {
                AxmlElement? el = null;
                try { el = AxmlParser.Parse(bytes); }
                catch (InvalidDataException) { }
                catch (ArgumentOutOfRangeException) { }
                if (el is not null && await FromXmlAsync(el, depth + 1) is { } layer)
                    return layer;
            }
        }

        // 2. Ссылки на другие ресурсы и цвета.
        foreach (var c in candidates.Where(c => c.Entry.IsReference))
            if (await ResolveDrawableAsync(c.Entry.Data, depth + 1) is { } layer)
                return layer;

        foreach (var c in candidates.Where(c => c.Entry.IsColor))
            return new ColorLayer(c.Entry.Data);

        return null;
    }

    private async Task<AxmlElement?> ReadXmlAsync(ApkSource src, string path)
    {
        try
        {
            var bytes = await src.Zip.ReadAsync(path, ct);
            return bytes is null ? null : AxmlParser.Parse(bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<IconLayer?> FromXmlAsync(AxmlElement el, int depth)
    {
        if (depth > MaxDepth) return null;
        switch (el.Name)
        {
            case "adaptive-icon":
            {
                var bg = Flatten(await ChildDrawableAsync(el.Child("background"), depth));
                var fg = Flatten(await ChildDrawableAsync(el.Child("foreground"), depth));
                return bg is null && fg is null ? null : new AdaptiveLayer(bg, fg);
            }
            case "vector":
            {
                var svg = await new SvgWriter(this, depth).WriteAsync(el);
                return svg is null ? null : new SvgLayer(svg);
            }
            case "bitmap":
            case "nine-patch":
                return el.Attr("src", Attrs.Src) is { IsReference: true } src
                    ? await ResolveDrawableAsync(src.Data, depth + 1)
                    : null;
            case "inset":
            {
                var inner = await ChildDrawableAsync(el, depth);
                var insetAttr = el.Attr("inset") ?? el.Attr("insetLeft", Attrs.InsetLeft);
                var inset = insetAttr?.Type == ResValueType.Fraction ? insetAttr.Float ?? 0 : 0;
                return inner is null ? null : inner with { Inset = Math.Clamp(inset, 0, 0.45) };
            }
            case "layer-list":
            case "selector":
            case "level-list":
                foreach (var item in el.Children.Where(c => c.Name == "item"))
                    if (await ChildDrawableAsync(item, depth) is { } layer)
                        return layer;
                return null;
            case "shape":
                return await ResolveColorAsync(el.Child("solid")?.Attr("color", Attrs.Color), depth) is uint argb
                    ? new ColorLayer(argb)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>Drawable из атрибута android:drawable или из вложенного элемента.</summary>
    private async Task<IconLayer?> ChildDrawableAsync(AxmlElement? el, int depth)
    {
        if (el is null) return null;
        var attr = el.Attr("drawable", Attrs.Drawable);
        if (attr is { IsReference: true }) return await ResolveDrawableAsync(attr.Data, depth + 1);
        if (attr is { IsColor: true }) return new ColorLayer(attr.Data);

        foreach (var child in el.Children)
            if (await FromXmlAsync(child, depth + 1) is { } layer)
                return layer;
        return null;
    }

    private static IconLayer? Flatten(IconLayer? layer) =>
        layer is AdaptiveLayer a ? a.Foreground ?? a.Background : layer;

    private enum FileKind { Raster, Xml, Unknown, Other }

    private static FileKind FileKindByName(string path)
    {
        if (path.EndsWith(".9.png", StringComparison.OrdinalIgnoreCase)) return FileKind.Other;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".webp" or ".jpg" or ".jpeg" => FileKind.Raster,
            ".xml" => FileKind.Xml,
            "" => FileKind.Unknown,
            _ => FileKind.Other,
        };
    }

    private static string? SniffRaster(byte[] b)
    {
        if (b.Length < 12) return null;
        if (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return ".png";
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            return ".webp";
        return null;
    }

    // Самые частые цвета фреймворка android (их нет в resources.arsc приложения).
    private static readonly Dictionary<uint, uint> FrameworkColors = new()
    {
        [0x0106000b] = 0xFFFFFFFF, // @android:color/white
        [0x0106000c] = 0xFF000000, // @android:color/black
        [0x0106000d] = 0x00000000, // @android:color/transparent
    };

    private static int DensityScore(int density) => density switch
    {
        0 => 160,              // по умолчанию = mdpi
        >= 0xFFFE => 150,      // anydpi / nodpi
        > 640 => 640 - density / 100,
        _ => density,
    };

    // ---------- Цвета (для векторов) ----------

    /// <summary>Возвращает ARGB (uint) или элемент &lt;gradient&gt;, либо null.</summary>
    internal async Task<object?> ResolveColorAsync(AxmlAttr? attr, int depth)
    {
        if (attr is null) return null;
        if (attr.IsColor) return attr.Data;
        if (attr.IsReference) return await ResolveColorResourceAsync(attr.Data, depth + 1);
        return null; // ?attr/... из темы — недоступно
    }

    private async Task<object?> ResolveColorResourceAsync(uint id, int depth)
    {
        if (depth > MaxDepth) return null;
        if (FrameworkColors.TryGetValue(id, out var frameworkColor)) return frameworkColor;
        var candidates = await LookupAsync(id, needFile: false);

        foreach (var c in candidates.Where(c => c.Entry.IsColor))
            return c.Entry.Data;

        foreach (var c in candidates.Where(c => c.Entry.IsReference))
            if (await ResolveColorResourceAsync(c.Entry.Data, depth + 1) is { } r)
                return r;

        foreach (var c in candidates.Where(c => c.Entry.Type == ResValueType.String))
        {
            var path = c.Src.Table!.GetString(c.Entry.Data);
            if (FileKindByName(path) is not (FileKind.Xml or FileKind.Unknown) || await ReadXmlAsync(c.Src, path) is not { } el)
                continue;
            if (el.Name == "gradient") return el;
            if (el.Name == "selector")
                foreach (var item in el.Children)
                    if (await ResolveColorAsync(item.Attr("color", Attrs.Color), depth + 1) is { } r)
                        return r;
        }
        return null;
    }
}

/// <summary>Конвертер Android VectorDrawable → SVG.</summary>
internal sealed class SvgWriter(ApkResolver resolver, int depth)
{
    private readonly StringBuilder _defs = new();
    private readonly StringBuilder _body = new();
    private int _ids;

    public async Task<string?> WriteAsync(AxmlElement vector)
    {
        var vw = vector.Attr("viewportWidth", Attrs.ViewportWidth)?.Float ?? 0;
        var vh = vector.Attr("viewportHeight", Attrs.ViewportHeight)?.Float ?? 0;
        if (vw <= 0 || vh <= 0) return null;

        await WriteChildrenAsync(vector);
        var alpha = vector.Attr("alpha", Attrs.Alpha)?.Float ?? 1;

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(vw)}\" height=\"{N(vh)}\" viewBox=\"0 0 {N(vw)} {N(vh)}\">" +
               $"<defs>{_defs}</defs><g opacity=\"{N(alpha)}\">{_body}</g></svg>";
    }

    private async Task WriteChildrenAsync(AxmlElement parent)
    {
        int openGroups = 0;
        foreach (var c in parent.Children)
        {
            switch (c.Name)
            {
                case "group":
                {
                    float F(string name, float def) => c.Attr(name)?.Float ?? def;
                    float px = F("pivotX", 0), py = F("pivotY", 0);
                    _body.Append($"<g transform=\"translate({N(F("translateX", 0) + px)} {N(F("translateY", 0) + py)}) " +
                                 $"rotate({N(F("rotation", 0))}) scale({N(F("scaleX", 1))} {N(F("scaleY", 1))}) " +
                                 $"translate({N(-px)} {N(-py)})\">");
                    await WriteChildrenAsync(c);
                    _body.Append("</g>");
                    break;
                }
                case "path":
                    await WritePathAsync(c);
                    break;
                case "clip-path":
                {
                    var d = resolver.ResolveStringAttr(c.Attr("pathData", Attrs.PathData));
                    if (string.IsNullOrWhiteSpace(d)) break;
                    var id = "c" + ++_ids;
                    _defs.Append($"<clipPath id=\"{id}\"><path d=\"{Esc(d)}\"/></clipPath>");
                    _body.Append($"<g clip-path=\"url(#{id})\">");
                    openGroups++;
                    break;
                }
            }
        }
        for (int i = 0; i < openGroups; i++) _body.Append("</g>");
    }

    private async Task WritePathAsync(AxmlElement c)
    {
        var d = resolver.ResolveStringAttr(c.Attr("pathData", Attrs.PathData));
        if (string.IsNullOrWhiteSpace(d)) return;

        _body.Append("<path d=\"").Append(Esc(d)).Append('"');

        await AppendPaintAsync("fill", c.Attr("fillColor", Attrs.FillColor), c.Attr("fillAlpha")?.Float ?? 1);
        if (c.Attr("fillType")?.Int == 1) _body.Append(" fill-rule=\"evenodd\"");

        if (await AppendPaintAsync("stroke", c.Attr("strokeColor", Attrs.StrokeColor), c.Attr("strokeAlpha")?.Float ?? 1))
        {
            _body.Append($" stroke-width=\"{N(c.Attr("strokeWidth", Attrs.StrokeWidth)?.Float ?? 0)}\"");
            _body.Append(c.Attr("strokeLineCap")?.Int switch { 1 => " stroke-linecap=\"round\"", 2 => " stroke-linecap=\"square\"", _ => "" });
            _body.Append(c.Attr("strokeLineJoin")?.Int switch { 1 => " stroke-linejoin=\"round\"", 2 => " stroke-linejoin=\"bevel\"", _ => "" });
            if (c.Attr("strokeMiterLimit")?.Float is { } miter) _body.Append($" stroke-miterlimit=\"{N(miter)}\"");
        }
        _body.Append("/>");
    }

    private async Task<bool> AppendPaintAsync(string property, AxmlAttr? attr, float alpha)
    {
        var paint = await resolver.ResolveColorAsync(attr, depth);

        if (paint is AxmlElement gradient)
        {
            var (id, fallback) = await WriteGradientAsync(gradient);
            if (id is not null)
            {
                _body.Append($" {property}=\"url(#{id})\" {property}-opacity=\"{N(alpha)}\"");
                return true;
            }
            paint = fallback;
        }

        if (paint is uint argb && (argb >> 24) * alpha > 0)
        {
            _body.Append($" {property}=\"#{argb & 0xFFFFFF:x6}\" {property}-opacity=\"{N((argb >> 24) / 255f * alpha)}\"");
            return true;
        }

        _body.Append($" {property}=\"none\"");
        return false;
    }

    /// <summary>Пишет linear/radial-градиент в defs. Возвращает id или цвет-заглушку.</summary>
    private async Task<(string? Id, uint? Fallback)> WriteGradientAsync(AxmlElement g)
    {
        var stops = new List<(float Offset, uint Color)>();
        foreach (var item in g.Children.Where(i => i.Name == "item"))
            if (await resolver.ResolveColorAsync(item.Attr("color", Attrs.Color), depth) is uint color)
                stops.Add((item.Attr("offset")?.Float ?? 0, color));

        if (stops.Count == 0)
        {
            foreach (var (name, offset) in new[] { ("startColor", 0f), ("centerColor", 0.5f), ("endColor", 1f) })
                if (await resolver.ResolveColorAsync(g.Attr(name), depth) is uint color)
                    stops.Add((offset, color));
        }
        if (stops.Count == 0) return (null, null);

        float F(string name) => g.Attr(name)?.Float ?? 0;
        var id = "g" + ++_ids;
        switch (g.Attr("type")?.Int ?? 0)
        {
            case 0:
                _defs.Append($"<linearGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" " +
                             $"x1=\"{N(F("startX"))}\" y1=\"{N(F("startY"))}\" x2=\"{N(F("endX"))}\" y2=\"{N(F("endY"))}\">");
                break;
            case 1:
                _defs.Append($"<radialGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" " +
                             $"cx=\"{N(F("centerX"))}\" cy=\"{N(F("centerY"))}\" r=\"{N(F("gradientRadius"))}\">");
                break;
            default: // sweep — в SVG нет, используем первый цвет
                return (null, stops[0].Color);
        }

        foreach (var (offset, color) in stops)
            _defs.Append($"<stop offset=\"{N(offset)}\" stop-color=\"#{color & 0xFFFFFF:x6}\" stop-opacity=\"{N((color >> 24) / 255f)}\"/>");
        _defs.Append(g.Attr("type")?.Int == 1 ? "</radialGradient>" : "</linearGradient>");
        return (id, null);
    }

    private static string N(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";
}
