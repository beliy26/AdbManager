using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdbManager.Services;

/// <summary>
/// Состояние устройства (окно «Состояние устройства» и MCP): параметры, загрузка процессора, память, место,
/// батарея, температуры. Всё — одним вызовом adb: скрипт печатает разделы с метками <c>@@имя</c>. Чего нет
/// или что запрещено SELinux (частоты ядер, loadavg на новых Android), просто пропускается.
/// </summary>
public static partial class DeviceStatus
{
    private static readonly string[] PropKeys =
    [
        "ro.product.manufacturer", "ro.product.brand", "ro.product.model", "ro.product.device",
        "ro.build.version.release", "ro.build.version.sdk", "ro.build.version.security_patch", "ro.build.display.id",
        "ro.product.cpu.abilist", "ro.soc.manufacturer", "ro.soc.model", "ro.board.platform", "ro.hardware",
        "ro.build.characteristics",
    ];

    // /proc/stat читается дважды с интервалом в секунду — его даёт второй кадр top (-n 2 -d 1).
    private static readonly string Script = string.Join("\n",
        "s(){ echo \"@@$1\"; }",
        "s props; " + string.Join("; ", PropKeys.Select(k => "getprop " + k)),
        "s stat1; head -1 /proc/stat 2>/dev/null",
        "s top; top -b -n 2 -d 1 -m 10 2>/dev/null",
        "s stat2; head -1 /proc/stat 2>/dev/null",
        "s cores; for c in /sys/devices/system/cpu/cpu[0-9]*; do echo \"${c##*/cpu} $(cat $c/cpufreq/scaling_cur_freq 2>/dev/null) $(cat $c/cpufreq/cpuinfo_max_freq 2>/dev/null)\"; done",
        "s loadavg; cat /proc/loadavg 2>/dev/null",
        "s uptime; cat /proc/uptime 2>/dev/null",
        "s meminfo; cat /proc/meminfo 2>/dev/null",
        "s storage; for d in /data / /storage/*; do case $d in /storage/emulated|/storage/self) continue;; esac; " +
        "stat -f -c \"%b %f %a %S $d\" $d 2>/dev/null; done",
        "s screen; wm size 2>/dev/null; wm density 2>/dev/null",
        "s battery; dumpsys battery 2>/dev/null",
        "s thermal; dumpsys thermalservice 2>/dev/null");

    public static async Task<DeviceStatusInfo> ReadAsync(AdbService adb, string serial)
    {
        var r = await adb.RunAsync(["-s", serial, "shell", Script], quiet: true);
        var sections = Split(r.Output);
        List<string> Sec(string name) => sections.TryGetValue(name, out var l) ? l : [];

        return new DeviceStatusInfo(
            Device(Sec("props"), Sec("screen"), Sec("uptime")),
            Cpu(Sec("stat1"), Sec("stat2"), Sec("top"), Sec("cores"), Sec("loadavg")),
            Memory(Sec("meminfo")),
            Storage(Sec("storage")),
            Battery(Sec("battery")),
            Thermal(Sec("thermal")));
    }

    private static Dictionary<string, List<string>> Split(string output)
    {
        var sections = new Dictionary<string, List<string>>();
        List<string>? current = null;
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("@@")) sections[raw[2..].Trim()] = current = [];
            else current?.Add(raw);
        }
        return sections;
    }

    // ----- Параметры -----

    private static DeviceParams Device(List<string> props, List<string> screen, List<string> uptime)
    {
        string P(string key) => Array.IndexOf(PropKeys, key) is var i and >= 0 && i < props.Count ? props[i].Trim() : "";

        var soc = string.Join(" ", new[] { P("ro.soc.manufacturer"), P("ro.soc.model") }.Where(s => s.Length > 0));
        // wm size: «Physical size: 1080x2340», при подмене ещё «Override size: …» — действует последняя.
        var size = screen.Select(l => SizeRe().Match(l)).LastOrDefault(m => m.Success);
        var density = screen.Select(l => DensityRe().Match(l)).LastOrDefault(m => m.Success);
        TimeSpan? up = uptime.FirstOrDefault()?.Split(' ')[0] is { } u &&
                       double.TryParse(u, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

        return new DeviceParams(
            P("ro.product.manufacturer"), P("ro.product.brand"), P("ro.product.model"), P("ro.product.device"),
            P("ro.build.version.release"), int.TryParse(P("ro.build.version.sdk"), out var sdk) ? sdk : null,
            P("ro.build.version.security_patch"), P("ro.build.display.id"), P("ro.build.characteristics"),
            soc.Length > 0 ? soc : P("ro.board.platform"), P("ro.hardware"),
            P("ro.product.cpu.abilist").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            size?.Groups[1].Value ?? "", density is null ? null : int.Parse(density.Groups[1].Value), up);
    }

    // ----- Процессор -----

    private static CpuStatus Cpu(List<string> stat1, List<string> stat2, List<string> top, List<string> cores, List<string> loadavg)
    {
        // Последний кадр top: от строки «Tasks:» до конца.
        var start = top.FindLastIndex(l => l.TrimStart().StartsWith("Tasks:"));
        var frame = start >= 0 ? top.Skip(start).Where(l => l.Trim().Length > 0).ToList() : [];

        var coreList = cores.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length > 0 && int.TryParse(f[0], out _))
            .OrderBy(f => int.Parse(f[0]))
            .ToList();
        var freqs = new List<CoreFrequency>();
        foreach (var f in coreList)
            if (f.Length >= 2 && long.TryParse(f[1], out var cur))
                freqs.Add(new CoreFrequency(int.Parse(f[0]), cur / 1000,
                    f.Length > 2 && long.TryParse(f[2], out var max) ? max / 1000 : null));

        var avg = loadavg.FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Таблица процессов — как её печатает top (первая строка — заголовок столбцов).
        var header = frame.FindIndex(l => l.Contains("PID") && l.Contains("CPU"));
        var processes = header >= 0 ? frame.Skip(header).Select(l => l.TrimEnd()).ToList() : [];

        return new CpuStatus(Load(stat1, stat2) ?? TopLoad(frame), coreList.Count, freqs,
            avg is { Length: >= 3 } ? avg.Take(3).ToArray() : [], processes);
    }

    /// <summary>Доля занятого времени по двум строкам «cpu user nice system idle iowait irq softirq steal…».</summary>
    private static double? Load(List<string> stat1, List<string> stat2)
    {
        static long[]? Parse(List<string> l) => l.FirstOrDefault() is { } line && line.StartsWith("cpu ")
            ? line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(long.Parse).ToArray()
            : null;
        if (Parse(stat1) is not { Length: >= 5 } a || Parse(stat2) is not { Length: >= 5 } b) return null;
        var total = b.Sum() - a.Sum();
        var idle = b[3] + b[4] - a[3] - a[4];
        return total > 0 ? Math.Round(100.0 * (total - idle) / total, 1) : null;
    }

    /// <summary>Запасной вариант, если /proc/stat закрыт: строка top «800%cpu 12%user 0%nice 10%sys 770%idle …».</summary>
    private static double? TopLoad(List<string> frame)
    {
        foreach (var line in frame)
        {
            var cpu = TopCpuRe().Match(line);
            var idle = TopIdleRe().Match(line);
            if (cpu.Success && idle.Success && double.Parse(cpu.Groups[1].Value) is var total and > 0)
                return Math.Round(100 * (1 - double.Parse(idle.Groups[1].Value) / total), 1);
        }
        return null;
    }

    // ----- Память -----

    private static MemoryStatus? Memory(List<string> meminfo)
    {
        var kb = new Dictionary<string, long>();
        foreach (var line in meminfo)
            if (MemRe().Match(line) is { Success: true } m) kb[m.Groups[1].Value] = long.Parse(m.Groups[2].Value);
        long? K(string key) => kb.TryGetValue(key, out var v) ? v : null;

        if (K("MemTotal") is not { } total || total == 0) return null;
        // MemAvailable — с ядра 3.14; на старых — оценка Free + Cached.
        var available = K("MemAvailable") ?? (K("MemFree") ?? 0) + (K("Cached") ?? 0);
        var swap = K("SwapTotal") ?? 0;
        return new MemoryStatus(total * 1024, available * 1024, swap * 1024, (swap - (K("SwapFree") ?? swap)) * 1024);
    }

    // ----- Место -----

    private static List<StorageVolume> Storage(List<string> lines)
    {
        var list = new List<StorageVolume>();
        foreach (var line in lines)
        {
            var f = line.Split(' ', 5);
            if (f.Length < 5 || !long.TryParse(f[0], out var blocks) || !long.TryParse(f[1], out var free) ||
                !long.TryParse(f[2], out var avail) || !long.TryParse(f[3], out var bsize) || blocks == 0)
                continue;
            var kind = f[4] switch { "/data" => StorageKind.Internal, "/" => StorageKind.System, _ => StorageKind.External };
            list.Add(new StorageVolume(f[4], kind, blocks * bsize, avail * bsize, (blocks - free) * bsize));
        }
        return list;
    }

    // ----- Батарея -----

    private static BatteryStatus? Battery(List<string> lines)
    {
        var v = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            var i = line.IndexOf(':');
            if (i > 0 && line.StartsWith("  ") && !line.StartsWith("   ")) v.TryAdd(line[..i].Trim(), line[(i + 1)..].Trim());
        }
        if (v.GetValueOrDefault("present") == "false" || !int.TryParse(v.GetValueOrDefault("level"), out var level)) return null;

        var status = v.GetValueOrDefault("status") switch
        {
            "2" => "charging", "3" => "discharging", "4" => "not charging", "5" => "full", _ => "unknown",
        };
        var health = v.GetValueOrDefault("health") switch
        {
            "2" => "good", "3" => "overheat", "4" => "dead", "5" => "over voltage", "6" => "failure", "7" => "cold", _ => "unknown",
        };
        var sources = new[] { ("AC powered", "ac"), ("USB powered", "usb"), ("Wireless powered", "wireless") }
            .Where(s => v.GetValueOrDefault(s.Item1) == "true").Select(s => s.Item2).ToArray();
        return new BatteryStatus(level, status, health, sources,
            int.TryParse(v.GetValueOrDefault("temperature"), out var t) ? t / 10.0 : null,
            int.TryParse(v.GetValueOrDefault("voltage"), out var mv) ? mv : null);
    }

    // ----- Температуры -----

    /// <summary>
    /// Из <c>dumpsys thermalservice</c> (Android 10+) раздел «Current temperatures from HAL»:
    /// <c>Temperature{mValue=34.5, mType=0, mName=CPU0, mStatus=0}</c>.
    /// </summary>
    private static List<(string Name, double Celsius)> Thermal(List<string> lines)
    {
        var list = new List<(string Name, double Celsius)>();
        var start = lines.FindIndex(l => l.Contains("Current temperatures from HAL"));
        if (start < 0) return list;
        foreach (var line in lines.Skip(start + 1))
        {
            var m = TempRe().Match(line);
            if (!m.Success) break;
            var name = m.Groups[2].Value;
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                value is > -50 and < 200 && list.All(x => x.Name != name))
                list.Add((name, Math.Round(value, 1)));
        }
        return list;
    }

    [GeneratedRegex(@"size:\s*(\d+x\d+)")] private static partial Regex SizeRe();
    [GeneratedRegex(@"density:\s*(\d+)")] private static partial Regex DensityRe();
    [GeneratedRegex(@"(\d+)%cpu")] private static partial Regex TopCpuRe();
    [GeneratedRegex(@"(\d+)%idle")] private static partial Regex TopIdleRe();
    [GeneratedRegex(@"^(\w+):\s+(\d+)\s*kB")] private static partial Regex MemRe();
    [GeneratedRegex(@"Temperature\{mValue=([-\d.]+),.*?mName=([^,}]+)")] private static partial Regex TempRe();
}

public enum StorageKind { Internal, System, External }

public sealed record DeviceParams(string Manufacturer, string Brand, string Model, string Codename, string Android, int? Sdk,
    string SecurityPatch, string Build, string Characteristics, string Soc, string Hardware, string[] Abis,
    string Screen, int? DensityDpi, TimeSpan? Uptime);

public sealed record CoreFrequency(int Core, long Mhz, long? MaxMhz);

public sealed record CpuStatus(double? UsagePercent, int Cores, List<CoreFrequency> Frequencies, string[] LoadAverage,
    List<string> TopProcesses);

public sealed record MemoryStatus(long TotalBytes, long AvailableBytes, long SwapTotalBytes, long SwapUsedBytes)
{
    public double UsedPercent => Math.Round(100.0 * (TotalBytes - AvailableBytes) / TotalBytes, 1);
}

/// <summary>Свободно — доступное приложениям (без резерва root), занято — по всем блокам.</summary>
public sealed record StorageVolume(string Path, StorageKind Kind, long TotalBytes, long FreeBytes, long UsedBytes)
{
    public double UsedPercent => TotalBytes > 0 ? Math.Round(100.0 * UsedBytes / TotalBytes, 1) : 0;
}

/// <summary>Status и Health — коды, как в JSON для агентов: charging, discharging…, good, overheat….</summary>
public sealed record BatteryStatus(int LevelPercent, string Status, string Health, string[] PoweredBy, double? TemperatureC, int? VoltageMv);

public sealed record DeviceStatusInfo(DeviceParams Device, CpuStatus Cpu, MemoryStatus? Memory, List<StorageVolume> Storage,
    BatteryStatus? Battery, List<(string Name, double Celsius)> Temperatures)
{
    /// <summary>JSON для агентов (MCP): пустые поля опускаются.</summary>
    public JsonObject ToJson(bool includeProcesses)
    {
        var d = Device;
        var device = new JsonObject();
        void Add(JsonObject o, string name, string value) { if (value.Length > 0) o[name] = value; }
        Add(device, "manufacturer", d.Manufacturer);
        Add(device, "brand", d.Brand);
        Add(device, "model", d.Model);
        Add(device, "codename", d.Codename);
        Add(device, "android", d.Android);
        if (d.Sdk is { } sdk) device["sdk"] = sdk;
        Add(device, "securityPatch", d.SecurityPatch);
        Add(device, "build", d.Build);
        Add(device, "characteristics", d.Characteristics);
        Add(device, "soc", d.Soc);
        Add(device, "hardware", d.Hardware);
        if (d.Abis.Length > 0) device["abis"] = Array(d.Abis);
        Add(device, "screen", d.Screen);
        if (d.DensityDpi is { } dpi) device["densityDpi"] = dpi;
        if (d.Uptime is { } t)
            device["uptime"] = t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : $"{t.Hours}h {t.Minutes}m";

        var cpu = new JsonObject();
        if (Cpu.UsagePercent is { } usage) cpu["usagePercent"] = usage;
        if (Cpu.Cores > 0) cpu["cores"] = Cpu.Cores;
        if (Cpu.Frequencies.Count > 0)
            cpu["frequencies"] = new JsonArray(Cpu.Frequencies.Select(f =>
            {
                var o = new JsonObject { ["core"] = f.Core, ["mhz"] = f.Mhz };
                if (f.MaxMhz is { } max) o["maxMhz"] = max;
                return (JsonNode)o;
            }).ToArray());
        if (Cpu.LoadAverage.Length > 0) cpu["loadAverage"] = Array(Cpu.LoadAverage);
        if (includeProcesses && Cpu.TopProcesses.Count > 0) cpu["topProcesses"] = Array(Cpu.TopProcesses);

        var result = new JsonObject { ["device"] = device, ["cpu"] = cpu };
        if (Memory is { } m)
        {
            var mem = new JsonObject { ["totalMb"] = Mb(m.TotalBytes), ["availableMb"] = Mb(m.AvailableBytes), ["usedPercent"] = m.UsedPercent };
            if (m.SwapTotalBytes > 0)
            {
                mem["swapTotalMb"] = Mb(m.SwapTotalBytes);
                mem["swapUsedMb"] = Mb(m.SwapUsedBytes);
            }
            result["memory"] = mem;
        }
        result["storage"] = new JsonArray(Storage.Select(s => (JsonNode)new JsonObject
        {
            ["path"] = s.Path,
            ["kind"] = s.Kind.ToString().ToLowerInvariant(),
            ["totalGb"] = Gb(s.TotalBytes),
            ["freeGb"] = Gb(s.FreeBytes),
            ["usedPercent"] = s.UsedPercent,
        }).ToArray());
        if (Battery is { } b)
        {
            var bat = new JsonObject { ["levelPercent"] = b.LevelPercent, ["status"] = b.Status };
            if (b.PoweredBy.Length > 0) bat["poweredBy"] = Array(b.PoweredBy);
            if (b.TemperatureC is { } bt) bat["temperatureC"] = bt;
            if (b.VoltageMv is { } mv) bat["voltageMv"] = mv;
            bat["health"] = b.Health;
            result["battery"] = bat;
        }
        if (Temperatures.Count > 0)
        {
            var temps = new JsonObject();
            foreach (var (name, c) in Temperatures) temps[name] = c;
            result["temperaturesC"] = temps;
        }
        return result;
    }

    private static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode)i).ToArray());
    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0);
    private static double Gb(long bytes) => Math.Round(bytes / 1073741824.0, 2);
}
