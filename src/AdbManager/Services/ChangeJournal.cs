using System.Text.Json;

namespace AdbManager.Services;

/// <summary>Запись журнала изменений на устройстве.</summary>
/// <param name="Action">freeze, uninstall, enable, restore, dns, install (установлено программой), home (смена главного экрана).</param>
/// <param name="OldValue">Для dns — прежние настройки «режим|сервер», для home — прежний компонент главного экрана.</param>
public sealed record JournalEntry(DateTime Time, string Action, string Package, string? Label, bool System, string? OldValue = null);

/// <summary>
/// Журнал изменений и бэкапы по устройству: %LOCALAPPDATA%\AdbManager\devices\&lt;устройство&gt;\.
/// Идея ЧистогоТВ: перед первым изменением сохраняется состояние устройства, каждое действие
/// записывается — и всё сделанное можно вернуть одной командой.
/// </summary>
public sealed class ChangeJournal
{
    public const string DnsKey = "private_dns";
    public const string HomeKey = "home_activity";

    private static readonly HashSet<string> BackedUpThisSession = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public string Directory { get; }
    private string FilePath => Path.Combine(Directory, "journal.jsonl");

    public ChangeJournal(string deviceKey)
    {
        var safe = string.Concat(deviceKey.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')).Trim('_');
        Directory = Path.Combine(AdbService.AppDataDir, "devices", safe.Length > 0 ? safe : "device");
    }

    public void Add(JournalEntry entry)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
    }

    public List<JournalEntry> ReadAll()
    {
        var result = new List<JournalEntry>();
        if (!File.Exists(FilePath)) return result;
        foreach (var line in File.ReadAllLines(FilePath))
        {
            try
            {
                if (line.Length > 0 && JsonSerializer.Deserialize<JournalEntry>(line) is { } e) result.Add(e);
            }
            catch (JsonException) { /* повреждённая строка */ }
        }
        return result;
    }

    /// <summary>
    /// Что можно вернуть: для каждого пакета берётся последнее действие. Заморозку, изменение DNS,
    /// смену главного экрана и установку программой можно отменить всегда, удаление — только у
    /// системных приложений (install-existing).
    /// </summary>
    public List<JournalEntry> GetRestorable()
    {
        var result = new List<JournalEntry>();
        foreach (var group in ReadAll().GroupBy(e => e.Package))
        {
            var entries = group.ToList();
            var last = entries[^1];
            if (!(last.Action is "freeze" or "dns" or "home" or "install" || (last.Action == "uninstall" && last.System))) continue;

            if (last.Action is "dns" or "home")
            {
                // Несколько смен подряд: возвращаем настройку, которая была до первой из них.
                var start = entries.FindLastIndex(e => e.Action == "restore") + 1;
                last = last with { OldValue = entries[start].OldValue };
            }
            result.Add(last);
        }
        return result.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>Бэкап состояния устройства перед первым изменением за сеанс работы программы.</summary>
    public async Task EnsureBackupAsync(AdbService adb, string serial)
    {
        if (!BackedUpThisSession.Add(Directory)) return;

        var dir = Path.Combine(Directory, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        System.IO.Directory.CreateDirectory(dir);
        foreach (var (file, command) in new[]
                 {
                     ("packages.txt", "pm list packages -f"),
                     ("packages-disabled.txt", "pm list packages -d"),
                     ("packages-uninstalled.txt", "pm list packages -u"),
                     ("getprop.txt", "getprop"),
                     ("settings-global.txt", "settings list global"),
                     ("settings-secure.txt", "settings list secure"),
                     ("settings-system.txt", "settings list system"),
                 })
        {
            var r = await adb.RunAsync(["-s", serial, "shell", command], quiet: true);
            await File.WriteAllTextAsync(Path.Combine(dir, file), r.Output);
        }
    }
}
