using System.Net.Http.Headers;
using System.Text.Json;

namespace AdbManager.Services;

/// <summary>Выпуск лаунчера на GitHub: версия, APK и страница релиза.</summary>
public sealed record LauncherRelease(string Version, string ApkName, string Url, long Size, string PageUrl);

/// <summary>
/// Сторонний лаунчер для Android TV без рекламы: последняя версия с GitHub Releases автора,
/// загрузка APK в %LOCALAPPDATA%\AdbManager\downloads.
/// </summary>
public sealed class TvLauncher
{
    /// <summary>Projectivy Launcher (github.com/spocky/miproja1): один APK на все процессоры.</summary>
    public static TvLauncher Projectivy { get; } = new("Projectivy Launcher", "com.spocky.projengmenu", "spocky/miproja1", () => Res.Get("ProjectivyDescription"));

    /// <summary>
    /// LTvLauncher (github.com/leanbitlab-org/LtvLauncher, GPL-3.0) — его ставит ЧистыйТВ. APK собраны
    /// отдельно под arm64-v8a, armeabi-v7a и universal.
    /// </summary>
    public static TvLauncher LTv { get; } = new("LTvLauncher", "com.leanbitlab.ltvL", "leanbitlab-org/LtvLauncher", () => Res.Get("LTvDescription"));

    public static IReadOnlyList<TvLauncher> All { get; } = [Projectivy, LTv];

    public string Name { get; }
    public string Package { get; }
    /// <summary>Описание для диалога установки (на языке интерфейса).</summary>
    public string Description => _description();
    public string ReleasesPage => $"https://github.com/{_repo}/releases";

    private readonly string _repo;
    private readonly Func<string> _description;

    private TvLauncher(string name, string package, string repo, Func<string> description)
    {
        Name = name;
        Package = package;
        _repo = repo;
        _description = description;
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub API без User-Agent отвечает 403.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AdbManager", "1.0"));
        return http;
    }

    private static readonly string[] KnownAbis = ["arm64-v8a", "armeabi-v7a", "armeabi", "x86_64", "x86"];

    /// <summary>
    /// Последний стабильный выпуск с APK. <paramref name="abis"/> — <c>ro.product.cpu.abilist</c> устройства:
    /// берётся сборка под первый подходящий процессор, иначе universal, иначе APK без процессора в имени.
    /// </summary>
    public async Task<LauncherRelease> GetLatestAsync(IReadOnlyList<string> abis, CancellationToken ct = default)
    {
        using var http = CreateClient();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{_repo}/releases/latest", ct));
        var root = doc.RootElement;

        var version = root.GetProperty("tag_name").GetString() ?? "";
        var page = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
        // Скачиваем только APK из релизов этого репозитория.
        var prefix = $"https://github.com/{_repo}/releases/download/";
        var apks = root.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? "",
                Size: a.GetProperty("size").GetInt64()))
            .Where(a => a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && a.Url.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        bool Has(string name, string abi) => name.Contains("-" + abi + "-", StringComparison.OrdinalIgnoreCase) ||
                                             name.Contains("_" + abi + ".", StringComparison.OrdinalIgnoreCase);
        var pick = abis.Select(abi => apks.FirstOrDefault(a => Has(a.Name, abi))).FirstOrDefault(a => a.Name is not null);
        if (pick.Name is null) pick = apks.FirstOrDefault(a => a.Name.Contains("universal", StringComparison.OrdinalIgnoreCase));
        if (pick.Name is null) pick = apks.FirstOrDefault(a => !KnownAbis.Any(abi => Has(a.Name, abi)));
        if (pick.Name is null)
            throw new InvalidOperationException(Res.Format("ReleaseNoApkFormat", version, page));
        return new LauncherRelease(version, pick.Name, pick.Url, pick.Size, page);
    }

    /// <summary>Скачивает APK (уже скачанный файл того же размера используется повторно).</summary>
    public async Task<string> DownloadAsync(LauncherRelease release, IProgress<double>? progress, CancellationToken ct = default)
    {
        // Имена APK у LTvLauncher не содержат версии — раскладываем по пакету и версии.
        var dir = Path.Combine(AdbService.AppDataDir, "downloads", Package, string.Concat(release.Version.Split(Path.GetInvalidFileNameChars())));
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
