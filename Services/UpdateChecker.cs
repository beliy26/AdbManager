using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace AdbManager.Services;

/// <summary>Выпуск программы на GitHub: версия из тега (v1.2.3) и страница релиза.</summary>
public sealed record AppRelease(Version Version, string Tag, string PageUrl);

/// <summary>Проверка новой версии ADB Manager по GitHub Releases (последний стабильный выпуск).</summary>
public static class UpdateChecker
{
    public const string ReleasesPage = "https://github.com/beliy26/AdbManager/releases";
    private const string LatestApi = "https://api.github.com/repos/beliy26/AdbManager/releases/latest";

    /// <summary>Версия запущенной программы (<c>&lt;Version&gt;</c> из csproj), без номера ревизии.</summary>
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    /// <summary>Последний выпуск; null — если тег не похож на версию.</summary>
    public static async Task<AppRelease?> GetLatestAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub API без User-Agent отвечает 403.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AdbManager", CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi, ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        var page = root.TryGetProperty("html_url", out var u) && u.GetString() is { } url
                   && url.StartsWith(ReleasesPage, StringComparison.Ordinal) ? url : ReleasesPage;
        return new AppRelease(Normalize(version), tag, page);
    }

    // 1.5 и 1.5.0.0 — одна версия: сравниваем по трём числам.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
