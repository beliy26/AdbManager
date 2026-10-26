using System.Net.Http.Headers;
using System.Text.Json;

namespace AdbManager.Services;

/// <summary>Выпуск лаунчера на GitHub: версия, APK и страница релиза.</summary>
public sealed record LauncherRelease(string Version, string ApkName, string Url, long Size, string PageUrl);

/// <summary>
/// Projectivy Launcher (github.com/spocky/miproja1) — сторонний лаунчер для Android TV без рекламы:
/// последняя версия с GitHub Releases, загрузка APK в %LOCALAPPDATA%\AdbManager\downloads.
/// </summary>
public static class ProjectivyLauncher
{
    public const string Name = "Projectivy Launcher";
    public const string Package = "com.spocky.projengmenu";
    public const string ReleasesPage = "https://github.com/spocky/miproja1/releases";

    private const string LatestApi = "https://api.github.com/repos/spocky/miproja1/releases/latest";
    // Скачиваем только APK из релизов этого репозитория.
    private const string DownloadPrefix = "https://github.com/spocky/miproja1/releases/download/";

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub API без User-Agent отвечает 403.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AdbManager", "1.0"));
        return http;
    }

    /// <summary>Последний стабильный выпуск (не beta) с APK.</summary>
    public static async Task<LauncherRelease> GetLatestAsync(CancellationToken ct = default)
    {
        using var http = CreateClient();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi, ct));
        var root = doc.RootElement;

        var version = root.GetProperty("tag_name").GetString() ?? "";
        var page = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                return new LauncherRelease(version, name, url, asset.GetProperty("size").GetInt64(), page);
        }
        throw new InvalidOperationException(Res.Format("ReleaseNoApkFormat", version, page));
    }

    /// <summary>Скачивает APK (уже скачанный файл того же размера используется повторно).</summary>
    public static async Task<string> DownloadAsync(LauncherRelease release, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(AdbService.AppDataDir, "downloads");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(release.ApkName));
        if (File.Exists(path) && new FileInfo(path).Length == release.Size) return path;

        var temp = path + ".part";
        using (var http = CreateClient())
        using (var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.Size;

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(temp);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        if (new FileInfo(temp).Length != release.Size)
        {
            File.Delete(temp);
            throw new InvalidDataException(Res.Get("ApkDownloadIncomplete"));
        }
        File.Move(temp, path, overwrite: true);
        return path;
    }
}
