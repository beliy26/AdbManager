using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdbManager.Apk;
using AdbManager.Models;

namespace AdbManager.Services;

/// <summary>
/// Загружает названия и иконки приложений с устройства и кэширует их в
/// %LOCALAPPDATA%\AdbManager\icons (ключ — устройство + путь к APK, который меняется при обновлении).
/// </summary>
public sealed class AppMetadataService(AdbService adb)
{
    private const int CacheVersion = 2;

    private static readonly string CacheRoot = Path.Combine(AdbService.AppDataDir, "icons");

    private static string CacheDir(string serial, PackageInfo package)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(serial + "|" + package.Name + "|" + package.ApkPath));
        return Path.Combine(CacheRoot, Convert.ToHexString(hash)[..20]);
    }

    public AppMetadata? LoadCached(string serial, PackageInfo package)
    {
        var dir = CacheDir(serial, package);
        var metaPath = Path.Combine(dir, "meta.json");
        try
        {
            if (!File.Exists(metaPath)) return null;
            var meta = JsonSerializer.Deserialize<AppMetadata>(File.ReadAllText(metaPath));
            if (meta is null || meta.Version != CacheVersion) return null;

            foreach (var layer in new[] { meta.Icon?.Background, meta.Icon?.Foreground })
                if (layer?.File is { } file)
                    layer.FullPath = Path.Combine(dir, file);
            return meta;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Читает название и иконку с устройства. При ошибке связи возвращает null (в кэш не пишется).</summary>
    public async Task<AppMetadata?> FetchAsync(string serial, PackageInfo package, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(package.ApkPath)) return null;

        RemoteApk baseApk;
        Dictionary<string, byte[]> files;
        try
        {
            baseApk = await RemoteApk.OpenAsync(adb, serial, package.ApkPath, ct);
            files = await baseApk.ReadAsync(["AndroidManifest.xml", "resources.arsc"], ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }

        var meta = new AppMetadata { Version = CacheVersion };
        try
        {
            var manifest = AxmlParser.Parse(files["AndroidManifest.xml"]);
            var table = files.TryGetValue("resources.arsc", out var arsc) ? new ResTable(arsc) : null;
            var resolver = new ApkResolver(new ApkSource(baseApk, table), c => LoadSplitsAsync(serial, package, c), ct);

            try { meta.Label = resolver.GetLabel(manifest); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* без названия */ }

            var dir = CacheDir(serial, package);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            try { meta.Icon = SaveIcon(await resolver.GetIconAsync(manifest), dir); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* без иконки */ }

            File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* не удалось разобрать манифест — покажем имя пакета */ }

        return meta;
    }

    /// <summary>Split APK с ресурсами под плотности экрана (приложения из Google Play).</summary>
    private async Task<IReadOnlyList<ApkSource>> LoadSplitsAsync(string serial, PackageInfo package, CancellationToken ct)
    {
        var result = new List<ApkSource>();
        var paths = await adb.GetApkPathsAsync(serial, package.Name, ct);
        foreach (var path in paths.Where(p => p != package.ApkPath && Path.GetFileName(p).Contains("dpi")))
        {
            try
            {
                var apk = await RemoteApk.OpenAsync(adb, serial, path, ct);
                var arsc = apk.Contains("resources.arsc") ? await apk.ReadAsync("resources.arsc", ct) : null;
                result.Add(new ApkSource(apk, arsc is null ? null : new ResTable(arsc)));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* пропускаем битый split */ }
        }
        return result;
    }

    private static AppIcon? SaveIcon(IconLayer? layer, string dir) => layer switch
    {
        AdaptiveLayer a => new AppIcon
        {
            Adaptive = true,
            Background = SaveLayer(a.Background, dir, "bg"),
            Foreground = SaveLayer(a.Foreground, dir, "fg"),
        },
        RasterLayer or SvgLayer => new AppIcon { Foreground = SaveLayer(layer, dir, "icon") },
        _ => null,
    };

    private static IconLayerFile? SaveLayer(IconLayer? layer, string dir, string name)
    {
        string? file = null;
        switch (layer)
        {
            case ColorLayer c:
                return new IconLayerFile { Color = c.Argb, Inset = c.Inset };
            case RasterLayer r:
                file = name + r.Extension;
                File.WriteAllBytes(Path.Combine(dir, file), r.Data);
                break;
            case SvgLayer s:
                file = name + ".svg";
                File.WriteAllText(Path.Combine(dir, file), s.Svg);
                break;
            default:
                return null;
        }
        return new IconLayerFile
        {
            File = file,
            FullPath = Path.Combine(dir, file),
            IsSvg = layer is SvgLayer,
            Inset = layer.Inset,
        };
    }
}
