using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AdbManager.Models;

namespace AdbManager.Services;

/// <summary>
/// Рекомендации по пакетам: база ЧистогоТВ (Assets/Data/chistytv-packages.json, MIT —
/// см. THIRD-PARTY-NOTICES.md) и защита критичных системных пакетов от заморозки и удаления.
/// </summary>
public sealed partial class PackageAdvisor
{
    private sealed class Db
    {
        [JsonPropertyName("platforms")] public List<Platform> Platforms { get; set; } = [];
        [JsonPropertyName("packages")] public List<Entry> Packages { get; set; } = [];
    }

    private sealed class Platform
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("detect_packages")] public List<string>? DetectPackages { get; set; }
        [JsonPropertyName("detect_manufacturer")] public string? DetectManufacturer { get; set; }
    }

    private sealed class Entry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("platform")] public string Platform { get; set; } = "";
        [JsonPropertyName("action")] public string Action { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("launcher")] public bool Launcher { get; set; }
        [JsonPropertyName("override_protect")] public bool OverrideProtect { get; set; }
    }

    // Пакеты, без которых устройство не работает. Применяется только к системным пакетам:
    // пользовательское приложение (например, сторонняя клавиатура) удалить можно всегда.
    // Основа — список ЧистогоТВ, дополнен пакетами телефона.
    private static readonly Regex[] ProtectPatterns =
    [
        .. new[]
        {
            // ЧистыйТВ
            @"^android$", @"^com\.android\.systemui$", @"^com\.android\.settings$", @"^com\.android\.tv\.settings$",
            "packageinstaller", "inputmethod", "keyboard", "leanbackime", @"\.ime$", "tvinput", "tvcenter", @"tv\.service",
            "bluetooth", "remote", "factory", "fac$", @"\.hkcfac", @"^com\.android\.vending$", @"^com\.google\.android\.gms$",
            @"^com\.google\.android\.gsf$", "setupwraith", "setupwizard", "provision", "networkstack", "wifi", "webview",
            // Телефон
            @"^com\.android\.phone$", @"^com\.android\.server\.telecom$", @"^com\.android\.providers\.",
            "permissioncontroller", @"^com\.android\.shell$", "honeyboard",
            @"^com\.sec\.android\.app\.launcher$", @"^com\.google\.android\.apps\.nexuslauncher$",
        }.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    private readonly Db _db;
    private readonly Dictionary<string, Entry> _byId;

    private PackageAdvisor(Db db)
    {
        _db = db;
        _byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var e in db.Packages) _byId.TryAdd(e.Id, e);
    }

    public int Count => _db.Packages.Count;

    /// <summary>
    /// База ЧистогоТВ (в основном телевизоры) + свой список рекламы и телеметрии на телефонах
    /// (phone-packages.json). При совпадении пакета приоритет у первой базы.
    /// </summary>
    public static PackageAdvisor Load()
    {
        var merged = new Db();
        foreach (var file in new[] { "chistytv-packages.json", "phone-packages.json" })
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Data", file);
                if (JsonSerializer.Deserialize<Db>(File.ReadAllText(path)) is not { } db) continue;
                merged.Platforms.AddRange(db.Platforms);
                merged.Packages.AddRange(db.Packages);
            }
            catch
            {
                // без базы остаётся только защита
            }
        }
        return new PackageAdvisor(merged);
    }

    public static bool IsProtected(string package, bool isSystem) =>
        isSystem && ProtectPatterns.Any(p => p.IsMatch(package));

    /// <summary>Платформы (Яндекс ТВ, Google TV…), определённые по установленным пакетам и производителю.</summary>
    public IReadOnlyList<(string Id, string Name)> DetectPlatforms(IReadOnlySet<string> installed, string? manufacturer, string? brand)
    {
        var result = new List<(string, string)>();
        foreach (var p in _db.Platforms)
        {
            var match = p.DetectPackages?.Any(installed.Contains) == true;
            if (!match && !string.IsNullOrEmpty(p.DetectManufacturer))
            {
                var re = new Regex(p.DetectManufacturer, RegexOptions.CultureInvariant);
                match = re.IsMatch(manufacturer ?? "") || re.IsMatch(brand ?? "");
            }
            if (match) result.Add((p.Id, p.Name));
        }
        return result;
    }

    /// <summary>Совет для пакета с учётом найденных платформ (общие правила «any» действуют всегда).</summary>
    public void Apply(PackageInfo package, IReadOnlyCollection<string> platformIds)
    {
        _byId.TryGetValue(package.Name, out var entry);
        var applicable = entry is not null && (entry.Platform == "any" || platformIds.Contains(entry.Platform));

        if (IsProtected(package.Name, package.IsSystem) && !(applicable && entry!.OverrideProtect))
        {
            package.SetAdvice(PackageAdvice.Protected, "Критичный системный компонент",
                "Без него устройство может перестать работать — заморозка и удаление заблокированы.");
            return;
        }

        if (!applicable)
        {
            package.SetAdvice(PackageAdvice.None, null, null);
            return;
        }

        var (advice, fallbackNote) = entry!.Launcher
            ? (PackageAdvice.Keep, "штатный главный экран")
            : entry.Action switch
            {
                "disable" => (PackageAdvice.Disable, ""),
                "ask" => (PackageAdvice.Optional, ""),
                "keep" => (PackageAdvice.Keep, ""),
                _ => (PackageAdvice.None, ""),
            };
        var note = string.IsNullOrWhiteSpace(entry.Note) ? fallbackNote : entry.Note;
        package.SetAdvice(advice, entry.Title, note);
    }
}
