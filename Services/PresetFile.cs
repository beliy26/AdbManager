using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdbManager.Services;

/// <summary>
/// Файл пресетов отметки (*.json): <c>{"format": "adbmanager-presets", "version": 1, "presets": [...]}</c>.
/// При импорте принимается и один пресет без обёртки, массив пресетов, а также текстовый список
/// имён пакетов (*.txt, по одному в строке) — из него получается пресет с именем файла.
/// </summary>
public static class PresetFile
{
    public const string Format = "adbmanager-presets";

    private static readonly Regex PackageNameRe = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица без \uXXXX
    };

    private sealed class Envelope
    {
        public string Format { get; set; } = PresetFile.Format;
        public int Version { get; set; } = 1;
        public List<SelectionPreset> Presets { get; set; } = [];
    }

    public static async Task ExportAsync(string path, IEnumerable<SelectionPreset> presets)
    {
        var json = JsonSerializer.Serialize(new Envelope { Presets = presets.ToList() }, Options);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>Читает пресеты из файла; некорректные имена пакетов отбрасываются. Ошибка формата — <see cref="FormatException"/>.</summary>
    public static async Task<List<SelectionPreset>> ImportAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path);
        List<SelectionPreset> presets;
        if (!text.TrimStart().StartsWith('{') && !text.TrimStart().StartsWith('['))
        {
            presets = [new SelectionPreset { Name = Path.GetFileNameWithoutExtension(path), Packages = text.Split('\n').ToList() }];
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                presets = root.ValueKind == JsonValueKind.Array
                    ? root.Deserialize<List<SelectionPreset>>(Options) ?? []
                    : root.TryGetProperty("presets", out _) || root.TryGetProperty("Presets", out _)
                        ? root.Deserialize<Envelope>(Options)?.Presets ?? []
                        : [root.Deserialize<SelectionPreset>(Options)!];
            }
            catch (JsonException ex)
            {
                throw new FormatException("Файл не похож на пресеты ADB Manager: " + ex.Message, ex);
            }
        }

        foreach (var p in presets)
        {
            p.Name = (p.Name ?? "").Trim();
            p.Description = (p.Description ?? "").Trim();
            p.Packages = (p.Packages ?? []).Select(n => n.Trim()).Where(n => PackageNameRe.IsMatch(n))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (p.Created == default) p.Created = DateTime.Now;
        }
        return presets.Where(p => p.Packages.Count > 0).ToList();
    }

    /// <summary>Имя файла из названия пресета без запрещённых символов.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length > 0 ? s : "presets";
    }
}
