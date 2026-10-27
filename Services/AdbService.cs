using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AdbManager.Models;

namespace AdbManager.Services;

public sealed record AdbResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;

    /// <summary>Объединённый вывод stdout + stderr без лишних пробелов.</summary>
    public string Text => string.Join(Environment.NewLine,
        new[] { Output.Trim(), Error.Trim() }.Where(s => s.Length > 0));
}

/// <summary>Свойства устройства из getprop.</summary>
/// <param name="AdbSecure">ro.adb.secure: «0» — adb по сети пускает любого без запроса «Разрешить отладку?».</param>
public sealed record DeviceProps(string Manufacturer, string Brand, string Model, string Android, int Sdk, string SerialNo, string AdbSecure,
    DeviceType Type)
{
    /// <summary>Стабильный ключ устройства (одинаковый по USB и Wi-Fi) — для журнала и бэкапов.</summary>
    public string Key => $"{Manufacturer}-{Model}-{SerialNo}";
}

/// <summary>Тонкая обёртка над adb.exe.</summary>
public sealed class AdbService
{
    private const string PlatformToolsUrl =
        "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

    // У части провайдеров dl.google.com отдаёт на «latest» 404 (идея ЧистогоТВ). Тогда берём закреплённую версию
    // с запасных хостов и сверяем SHA-256 — подменённый архив не пройдёт. Хэш — от архива, чья SHA-1 совпала
    // с репозиторием Google (repository2-3.xml).
    private const string PinnedPlatformToolsFile = "platform-tools_r37.0.1-win.zip";
    private const string PinnedPlatformToolsSha256 = "45F4D63113E895EBDE0C90F194099A4676B6AC653BD28D54314A9E022BBC1A99";
    private static readonly string[] PlatformToolsMirrors =
    [
        "https://dl.google.com/android/repository/",
        "https://redirector.gvt1.com/edgedl/android/repository/",
        "https://mirrors.cloud.tencent.com/AndroidSDK/",
    ];

    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbManager");

    private static string LocalPlatformToolsDir => Path.Combine(AppDataDir, "platform-tools");

    public string? AdbPath { get; set; }

    /// <summary>Сообщения о выполняемых командах (для журнала в UI).</summary>
    public event Action<string>? Log;

    // ---------- Поиск / загрузка adb ----------

    public static string? FindAdb(string? preferred)
    {
        var candidates = new List<string?> { preferred };

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(dir))
                candidates.Add(SafeCombine(dir.Trim().Trim('"'), "adb.exe"));

        foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(root))
                candidates.Add(SafeCombine(root, "platform-tools", "adb.exe"));
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(LocalPlatformToolsDir, "adb.exe"));

        return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }

    private static string? SafeCombine(params string[] parts)
    {
        try { return Path.Combine(parts); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Скачивает официальные platform-tools от Google в %LOCALAPPDATA%\AdbManager. Если последняя версия
    /// не скачалась — закреплённая версия с запасных хостов с проверкой SHA-256.
    /// </summary>
    public static async Task<string> DownloadPlatformToolsAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppDataDir);
        var zipPath = Path.Combine(AppDataDir, "platform-tools.zip");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var errors = new List<string>();
        var downloaded = false;
        try
        {
            await DownloadFileAsync(http, PlatformToolsUrl, zipPath, progress, ct);
            downloaded = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            errors.Add($"{new Uri(PlatformToolsUrl).Host}: {ex.Message}");
        }

        foreach (var mirror in PlatformToolsMirrors)
        {
            if (downloaded) break;
            var url = mirror + PinnedPlatformToolsFile;
            var host = new Uri(url).Host;
            try
            {
                await DownloadFileAsync(http, url, zipPath, progress, ct);
                await using (var zip = File.OpenRead(zipPath))
                {
                    var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(zip, ct));
                    downloaded = hash.Equals(PinnedPlatformToolsSha256, StringComparison.OrdinalIgnoreCase);
                }
                if (downloaded) break;
                errors.Add($"{host}: {Res.Get("ChecksumMismatch")}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                errors.Add($"{host}: {ex.Message}");
            }
            File.Delete(zipPath);
        }

        if (!downloaded)
            throw new InvalidOperationException(Res.Format("PlatformToolsAllSourcesFailedFormat",
                string.Join(Environment.NewLine, errors), Path.Combine(LocalPlatformToolsDir, "adb.exe")));

        if (Directory.Exists(LocalPlatformToolsDir))
            Directory.Delete(LocalPlatformToolsDir, recursive: true);

        // Архив содержит папку platform-tools в корне.
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, AppDataDir, overwriteFiles: true), ct);
        File.Delete(zipPath);

        var adb = Path.Combine(LocalPlatformToolsDir, "adb.exe");
        if (!File.Exists(adb))
            throw new FileNotFoundException(Res.Get("AdbNotInArchive"), adb);
        return adb;
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
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

    // ---------- Запуск команд ----------

    public async Task<AdbResult> RunAsync(IEnumerable<string> args, bool quiet = false, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(AdbPath) || !File.Exists(AdbPath))
            throw new InvalidOperationException(Res.Get("AdbNotFoundSpecify"));

        var psi = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        if (!quiet)
            Log?.Invoke("> adb " + string.Join(' ', psi.ArgumentList.Select(Quote)));

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже завершён */ }
            throw;
        }

        var result = new AdbResult(process.ExitCode, await stdout, await stderr);
        if (!quiet && result.Text.Length > 0 && result.Text.Length < 4000)
            Log?.Invoke(result.Text);
        return result;
    }

    private Task<AdbResult> RunAsync(params string[] args) => RunAsync((IEnumerable<string>)args);

    /// <summary>
    /// Выполняет shell-команду через <c>adb exec-out</c> и возвращает сырой stdout (бинарные данные).
    /// В журнал не пишется.
    /// </summary>
    public async Task<byte[]> ExecOutAsync(string serial, string command, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(AdbPath) || !File.Exists(AdbPath))
            throw new InvalidOperationException(Res.Get("AdbNotFound"));

        var psi = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(serial);
        psi.ArgumentList.Add("exec-out");
        psi.ArgumentList.Add(command);

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await copy;
            await stderr;
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже завершён */ }
            throw;
        }
        return output.ToArray();
    }

    private Task<AdbResult> RunOnDeviceAsync(string serial, params string[] args) =>
        RunAsync(new[] { "-s", serial }.Concat(args));

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    // ---------- Устройства ----------

    public Task<AdbResult> StartServerAsync() => RunAsync("start-server");

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(bool quiet = false)
    {
        var result = await RunAsync(["devices", "-l"], quiet);
        var devices = new List<DeviceInfo>();

        foreach (var raw in result.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith('*'))
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string Prop(string key) =>
                parts.FirstOrDefault(p => p.StartsWith(key + ":"))?[(key.Length + 1)..] ?? "";

            devices.Add(new DeviceInfo(parts[0], parts[1], Prop("model"), Prop("product")));
        }
        return devices;
    }

    /// <summary>Подключение по Wi-Fi: <c>adb connect host[:port]</c>.</summary>
    public Task<AdbResult> ConnectAsync(string address) => RunAsync("connect", address);

    /// <summary>Сопряжение (Android 11+, «Беспроводная отладка» → «Подключить по коду»).</summary>
    public Task<AdbResult> PairAsync(string address, string code) => RunAsync("pair", address, code);

    public Task<AdbResult> DisconnectAsync(string serial) => RunAsync("disconnect", serial);

    /// <summary>Переключает USB-устройство в режим TCP/IP на порту 5555.</summary>
    public Task<AdbResult> EnableTcpIpAsync(string serial) => RunOnDeviceAsync(serial, "tcpip", "5555");

    public async Task<string?> GetWifiIpAsync(string serial)
    {
        var r = await RunOnDeviceAsync(serial, "shell", "ip", "-f", "inet", "addr", "show", "wlan0");
        var match = System.Text.RegularExpressions.Regex.Match(r.Output, @"inet (\d+\.\d+\.\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    // ---------- Приложения ----------

    public async Task<IReadOnlyList<PackageInfo>> GetPackagesAsync(string serial, bool includeSystem)
    {
        var list = new List<PackageInfo>();

        var user = await RunOnDeviceAsync(serial, "shell", "pm", "list", "packages", "-f", "-3");
        if (!user.Success) throw new InvalidOperationException(user.Text);
        list.AddRange(ParsePackages(user.Output, isSystem: false));

        if (includeSystem)
        {
            var system = await RunOnDeviceAsync(serial, "shell", "pm", "list", "packages", "-f", "-s");
            list.AddRange(ParsePackages(system.Output, isSystem: true));
        }

        var states = await GetDisabledStatesAsync(serial);
        foreach (var p in list)
            p.State = states.GetValueOrDefault(p.Name, PackageState.Enabled);

        var sizes = await GetSizesAsync(serial);
        foreach (var p in list)
            if (sizes.TryGetValue(p.Name, out var s))
                p.SetSizes(s.App, s.Data, s.Cache);

        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Состояние отключённых пакетов. <c>pm list packages -d</c> не различает «заморожено» и
    /// «остановлено системой как неиспользуемое», поэтому для каждого отключённого пакета
    /// читаем enabled=N из dumpsys (одним вызовом adb).
    /// </summary>
    public async Task<Dictionary<string, PackageState>> GetDisabledStatesAsync(string serial)
    {
        const string script =
            "for p in $(pm list packages -d | cut -d: -f2); do " +
            "s=$(dumpsys package $p 2>/dev/null | grep -m1 -E 'User 0: .*enabled=' | grep -o ' enabled=[0-9]'); " +
            "echo \"$p$s\"; done";
        var r = await RunAsync(["-s", serial, "shell", script]);

        var result = new Dictionary<string, PackageState>();
        foreach (var raw in r.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var idx = line.IndexOf(" enabled=", StringComparison.Ordinal);
            var name = idx < 0 ? line : line[..idx];
            var code = idx < 0 ? -1 : line[^1] - '0';
            // 4 = DISABLED_UNTIL_USED; 2 (DISABLED) / 3 (DISABLED_USER) — заморожено.
            result[name] = code == 4 ? PackageState.Unused : PackageState.Frozen;
        }
        return result;
    }

    /// <summary>
    /// Размеры приложений из <c>dumpsys diskstats</c> (их периодически считает сама система,
    /// поэтому для только что установленных приложений данных может не быть).
    /// </summary>
    private async Task<Dictionary<string, (long App, long Data, long Cache)>> GetSizesAsync(string serial)
    {
        var result = new Dictionary<string, (long, long, long)>();
        try
        {
            var r = await RunAsync(["-s", serial, "shell", "dumpsys", "diskstats"], quiet: true);

            T[]? Array<T>(string key)
            {
                var line = r.Output.Split('\n').FirstOrDefault(l => l.StartsWith(key + ": ["));
                return line is null ? null : System.Text.Json.JsonSerializer.Deserialize<T[]>(line[(key.Length + 2)..]);
            }

            var names = Array<string>("Package Names");
            var app = Array<long>("App Sizes");
            var data = Array<long>("App Data Sizes");
            var cache = Array<long>("Cache Sizes");
            if (names is null || app is null || data is null) return result;

            for (int i = 0; i < names.Length && i < app.Length && i < data.Length; i++)
                result[names[i]] = (app[i], data[i], cache is not null && i < cache.Length ? cache[i] : 0);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            // Нет размеров — не критично.
        }
        return result;
    }

    /// <summary>
    /// Включает приложение, остановленное системой как неиспользуемое (DISABLED_UNTIL_USED).
    /// adb не может сменить это состояние через pm enable (SecurityException), но система
    /// сама включает приложение при запуске — поэтому запускаем его activity и сразу закрываем.
    /// </summary>
    public async Task<AdbResult> WakeAsync(string serial, string package)
    {
        var resolve = await RunOnDeviceAsync(serial, "shell", "cmd", "package", "resolve-activity", "--brief",
            "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", package);
        var component = resolve.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains('/'));
        if (component is null)
            return new AdbResult(1, "", Res.Get("AppNoLaunchActivity"));

        await RunOnDeviceAsync(serial, "shell", "am", "start", "-W", "-n", component);
        await RunOnDeviceAsync(serial, "shell", "am", "force-stop", package);

        var states = await GetDisabledStatesAsync(serial);
        return states.TryGetValue(package, out var state) && state == PackageState.Unused
            ? new AdbResult(1, "", Res.Get("AppNotEnabledAfterLaunch"))
            : new AdbResult(0, "enabled", "");
    }

    /// <summary>
    /// Замораживает (<c>pm disable-user</c>) или размораживает (<c>pm enable</c>) приложение
    /// для текущего пользователя. Данные приложения сохраняются.
    /// </summary>
    public async Task<AdbResult> SetFrozenAsync(string serial, string package, bool frozen)
    {
        var r = frozen
            ? await RunOnDeviceAsync(serial, "shell", "pm", "disable-user", "--user", "0", package)
            : await RunOnDeviceAsync(serial, "shell", "pm", "enable", "--user", "0", package);

        // При успехе pm пишет «Package x new state: disabled-user / enabled».
        var expected = frozen ? "disabled" : "enabled";
        if (r.Output.Contains("new state: " + expected, StringComparison.OrdinalIgnoreCase))
            return r;

        var error = r.Text.Contains("Shell cannot change component state", StringComparison.Ordinal)
            ? Res.Get("ShellCannotChangeState")
            : r.Text;
        return new AdbResult(r.ExitCode == 0 ? 1 : r.ExitCode, "", error);
    }

    // Формат строки: package:/data/app/~~abc==/com.example-xyz==/base.apk=com.example
    private static IEnumerable<PackageInfo> ParsePackages(string output, bool isSystem)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:")) continue;
            line = line["package:".Length..];

            var eq = line.LastIndexOf('=');
            if (eq < 0)
                yield return new PackageInfo(line, isSystem, "");
            else
                yield return new PackageInfo(line[(eq + 1)..], isSystem, line[..eq]);
        }
    }

    /// <summary>Основные свойства устройства одним вызовом adb.</summary>
    public async Task<DeviceProps> GetDevicePropsAsync(string serial)
    {
        string[] keys = ["ro.product.manufacturer", "ro.product.brand", "ro.product.model",
            "ro.build.version.release", "ro.build.version.sdk", "ro.serialno", "ro.adb.secure"];
        var script = string.Join("; ", keys.Select(k => "getprop " + k)) +
                     "; getprop ro.build.characteristics; pm has-feature android.software.leanback";
        var r = await RunAsync(["-s", serial, "shell", script], quiet: true);
        var lines = r.Output.Replace("\r", "").Split('\n');
        string V(int i) => i < lines.Length ? lines[i].Trim() : "";
        return new DeviceProps(V(0), V(1), V(2), V(3), int.TryParse(V(4), out var sdk) ? sdk : 0, V(5), V(6),
            DetectType(V(7), V(8) == "true"));
    }

    /// <summary>
    /// Тип по <c>ro.build.characteristics</c> («tv,nosdcard», «tablet», «phone», «default»…) и наличию leanback (Android TV).
    /// Часы и автомобили — <see cref="DeviceType.Unknown"/>.
    /// </summary>
    private static DeviceType DetectType(string characteristics, bool leanback)
    {
        var c = characteristics.Split(',', StringSplitOptions.TrimEntries);
        if (leanback || c.Contains("tv")) return DeviceType.Tv;
        if (c.Contains("watch") || c.Contains("automotive")) return DeviceType.Unknown;
        return c.Contains("tablet") ? DeviceType.Tablet : DeviceType.Phone;
    }

    // ---------- Защита: главный экран и входы ТВ (идеи CleanYourTV) ----------

    private static readonly System.Text.RegularExpressions.Regex PackageNameRe =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Отвечают на HOME, но лаунчером не являются: мастер настройки (на Google TV, получив Home, уводит
    // систему в цикл падений), настройки, заглушки FallbackHome и заводские тесты.
    private static readonly System.Text.RegularExpressions.Regex NotALauncherRe = new(
        @"^(com\.google\.android\.tungsten\.setupwraith|com\.android\.tv\.settings|com\.android\.settings|com\.android\.provision)/|" +
        @"/\S*FallbackHome$|factorytest|factory_test|factorymode",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Кто сейчас главный экран (Home) и есть ли ему замена — другой включённый лаунчер, а также пакеты,
    /// которые обслуживают входы ТВ (TvInputService). Одним вызовом adb.
    /// </summary>
    public async Task<(string? Home, bool HomeHasAlternative, HashSet<string> TvInputs)> GetGuardsAsync(string serial)
    {
        const string intent = "-a android.intent.action.MAIN -c android.intent.category.HOME";
        var script = $"cmd package resolve-activity --brief {intent} 2>/dev/null; echo ---; " +
                     $"cmd package query-activities --brief {intent} 2>/dev/null; echo ---; " +
                     "pm query-services --components -a android.media.tv.TvInputService 2>/dev/null";
        var r = await RunAsync(["-s", serial, "shell", script], quiet: true);
        var parts = r.Output.Replace("\r", "").Split("---\n");

        static IEnumerable<string> Components(string? text) => (text ?? "").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Contains('/') && PackageNameRe.IsMatch(l.Split('/')[0]));

        // Если лаунчеров несколько и ни один не выбран, отвечает ResolverActivity пакета android.
        var home = Components(parts.ElementAtOrDefault(0)).LastOrDefault()?.Split('/')[0];
        if (home == "android") home = null;

        var alternative = Components(parts.ElementAtOrDefault(1))
            .Where(c => !NotALauncherRe.IsMatch(c))
            .Select(c => c.Split('/')[0])
            .Any(p => p != home);

        var inputs = Components(parts.ElementAtOrDefault(2)).Select(c => c.Split('/')[0]).ToHashSet();
        return (home, alternative, inputs);
    }

    /// <summary>
    /// Текущий главный экран (компонент или null, если выбор не сделан) и все activity, отвечающие на HOME.
    /// </summary>
    public async Task<(string? Current, List<string> Candidates)> GetHomeActivitiesAsync(string serial)
    {
        const string intent = "-a android.intent.action.MAIN -c android.intent.category.HOME";
        var r = await RunAsync(["-s", serial, "shell",
            $"cmd package resolve-activity --brief {intent} 2>/dev/null; echo ---; cmd package query-activities --brief {intent} 2>/dev/null"], quiet: true);
        var parts = r.Output.Replace("\r", "").Split("---\n");

        static List<string> Components(string? text) => (text ?? "").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Contains('/') && PackageNameRe.IsMatch(l.Split('/')[0])).ToList();

        var current = Components(parts.ElementAtOrDefault(0)).LastOrDefault();
        if (current?.StartsWith("android/", StringComparison.Ordinal) == true) current = null; // окно выбора лаунчера
        return (current, Components(parts.ElementAtOrDefault(1)));
    }

    /// <summary>Делает activity главным экраном (<c>cmd package set-home-activity</c>, Android 7+).</summary>
    public async Task<AdbResult> SetHomeActivityAsync(string serial, string component)
    {
        var r = await RunOnDeviceAsync(serial, "shell", "cmd", "package", "set-home-activity", component);
        return r.Output.Contains("Success", StringComparison.OrdinalIgnoreCase) ? r : new AdbResult(r.ExitCode == 0 ? 1 : r.ExitCode, "", r.Text);
    }


    /// <summary>
    /// Пакеты, запрашивающие android.permission.TV_INPUT_HARDWARE, — программная часть входов HDMI/AV/тюнера.
    /// Если отключить такой пакет, вход видит кабель, но показывает «Нет сигнала», и вернуть картинку
    /// помогает только полное восстановление и холодная перезагрузка (опыт CleanYourTV).
    /// </summary>
    public async Task<HashSet<string>> GetTvInputHardwarePackagesAsync(string serial, IEnumerable<string> packages)
    {
        var names = packages.Where(p => PackageNameRe.IsMatch(p)).ToList();
        if (names.Count == 0) return [];
        // Только собственный блок «requested permissions»: иначе пакет с общим UID совпадёт по чужим разрешениям.
        var script = $"for p in {string.Join(' ', names)}; do dumpsys package $p 2>/dev/null | " +
                     "sed -n -E '/requested permissions:/,/(install|runtime) permissions:|User [0-9]+:/p' | " +
                     "grep -q TV_INPUT_HARDWARE && echo $p; done; true";
        var r = await RunAsync(["-s", serial, "shell", script], quiet: true);
        return r.Output.Split('\n').Select(l => l.Trim()).Where(names.Contains).ToHashSet();
    }

    // ---------- Аудит приватности (идея CleanYourTV) ----------

    /// <summary>
    /// Что устройство само называет голосовым ассистентом (secure-настройки) и какие пакеты по истории
    /// AppOps обращались к микрофону: пакет → «время назад» и признак «сейчас».
    /// </summary>
    public async Task<(Dictionary<string, List<string>> Voice, List<(string Package, string Ago, bool Running)> Microphone)>
        GetPrivacyAuditAsync(string serial)
    {
        string[] keys = ["voice_interaction_service", "voice_recognition_service", "assistant"];
        var settings = string.Join("; ", keys.Select(k => $"echo \"{k}=$(settings get secure {k})\""));
        var mic = "for p in $(pm list packages | cut -d: -f2); do " +
                  "r=$(appops get $p RECORD_AUDIO 2>/dev/null | grep -m1 'time='); " +
                  "[ -n \"$r\" ] && echo \"mic|$p|$r\"; done; true";
        var r = await RunAsync(["-s", serial, "shell", settings + "; " + mic], quiet: true);

        var voice = new Dictionary<string, List<string>>();
        var microphone = new List<(string, string, bool)>();
        foreach (var raw in r.Output.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("mic|", StringComparison.Ordinal))
            {
                var parts = line.Split('|', 3);
                if (parts.Length < 3) continue;
                var ago = System.Text.RegularExpressions.Regex.Match(parts[2], @"time=\+(\S+) ago");
                microphone.Add((parts[1], ago.Success ? ago.Groups[1].Value : "", parts[2].Contains("(running)")));
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var package = line[(eq + 1)..].Split('/')[0];
            if (!PackageNameRe.IsMatch(package)) continue;
            if (!voice.TryGetValue(package, out var list)) voice[package] = list = [];
            list.Add(line[..eq]);
        }
        return (voice, microphone);
    }

    /// <summary>Снимок экрана устройства (PNG).</summary>
    public Task<byte[]> ScreenshotAsync(string serial) => ExecOutAsync(serial, "screencap -p");

    /// <summary>Имена всех установленных для пользователя пакетов (<c>pm list packages</c>).</summary>
    public async Task<HashSet<string>> GetInstalledNamesAsync(string serial)
    {
        var r = await RunAsync(["-s", serial, "shell", "pm", "list", "packages"], quiet: true);
        return ParsePackages(r.Output, isSystem: false).Select(p => p.Name).ToHashSet();
    }

    /// <summary>
    /// Системные пакеты, удалённые для текущего пользователя (<c>pm uninstall --user 0</c>):
    /// они остались в прошивке и возвращаются через <c>cmd package install-existing</c>.
    /// </summary>
    public async Task<List<PackageInfo>> GetRemovedSystemPackagesAsync(string serial)
    {
        var installed = await GetInstalledNamesAsync(serial);
        var r = await RunAsync(["-s", serial, "shell", "pm", "list", "packages", "-u", "-f"], quiet: true);
        return ParsePackages(r.Output, isSystem: true)
            .Where(p => !installed.Contains(p.Name))
            // -u показывает и пользовательские приложения, удалённые с -k: их APK уже нет
            // (путь «null» или в /data) — вернуть нельзя.
            .Where(p => p.ApkPath.StartsWith('/') && !p.ApkPath.StartsWith("/data/", StringComparison.Ordinal))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AdbResult> InstallExistingAsync(string serial, string package)
    {
        var r = await RunOnDeviceAsync(serial, "shell", "cmd", "package", "install-existing", package);
        return r.Output.Contains("installed for user", StringComparison.OrdinalIgnoreCase)
            ? r
            : new AdbResult(r.ExitCode == 0 ? 1 : r.ExitCode, "", r.Text);
    }

    public async Task<string> GetSettingAsync(string serial, string ns, string key)
    {
        var r = await RunAsync(["-s", serial, "shell", "settings", "get", ns, key], quiet: true);
        var value = r.Output.Trim();
        return value == "null" ? "" : value;
    }

    public Task<AdbResult> PutSettingAsync(string serial, string ns, string key, string value) =>
        RunOnDeviceAsync(serial, "shell", "settings", "put", ns, key, value);

    /// <summary>Процессоры устройства (<c>ro.product.cpu.abilist</c>), основной — первым.</summary>
    public async Task<List<string>> GetAbiListAsync(string serial)
    {
        var r = await RunAsync(["-s", serial, "shell", "getprop", "ro.product.cpu.abilist"], quiet: true);
        return r.Output.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public Task<AdbResult> DeleteSettingAsync(string serial, string ns, string key) =>
        RunOnDeviceAsync(serial, "shell", "settings", "delete", ns, key);

    /// <summary>
    /// Работает ли на устройстве поиск адресов (идея ЧистогоТВ). Строку «PING host (ip)» ping печатает,
    /// только когда имя нашлось, — проверка не зависит от того, отвечает ли сервер на ping.
    /// </summary>
    public async Task<bool> CanResolveAsync(string serial)
    {
        var r = await RunAsync(["-s", serial, "shell", "ping -c 1 -W 2 connectivitycheck.gstatic.com 2>&1"], quiet: true);
        return System.Text.RegularExpressions.Regex.IsMatch(r.Output, @"PING \S+ \(\d");
    }

    // ----- Журнал на самом телевизоре (общий с ЧистымТВ) -----

    /// <summary>
    /// Список замороженных пакетов на устройстве, по строке на пакет. Тот же файл ведут скрипт и приложение
    /// ЧистыйТВ: заморозку можно откатить с другого компьютера или с телефона, а «Вернуть как было» здесь
    /// видит и то, что отключил ЧистыйТВ.
    /// </summary>
    public const string TvJournalPath = "/data/local/tmp/chistytv/disabled-by-chistytv.txt";

    public async Task<List<string>> ReadTvJournalAsync(string serial)
    {
        var r = await RunAsync(["-s", serial, "shell", $"cat {TvJournalPath} 2>/dev/null"], quiet: true);
        return r.Output.Split('\n').Select(l => l.Trim()).Where(l => PackageNameRe.IsMatch(l)).Distinct().ToList();
    }

    public async Task AddToTvJournalAsync(string serial, IEnumerable<string> packages)
    {
        // Имена — только проверенные: они попадают в командную строку shell.
        var names = packages.Where(p => PackageNameRe.IsMatch(p)).ToList();
        if (names.Count == 0) return;
        var dir = TvJournalPath[..TvJournalPath.LastIndexOf('/')];
        await RunAsync(["-s", serial, "shell",
            $"mkdir -p {dir} && " + string.Join(" && ", names.Select(n => $"echo {n} >> {TvJournalPath}"))], quiet: true);
    }

    public async Task RemoveFromTvJournalAsync(string serial, IEnumerable<string> packages)
    {
        var names = packages.Where(p => PackageNameRe.IsMatch(p)).ToList();
        if (names.Count == 0) return;
        var expr = string.Join(" ", names.Select(n => $"-e '/^{n.Replace(".", "\\.")}$/d'"));
        await RunAsync(["-s", serial, "shell", $"[ -f {TvJournalPath} ] && sed -i {expr} {TvJournalPath}"], quiet: true);
    }

    /// <summary>
    /// Устройства, которые adb видит по mDNS: беспроводная отладка Android 11+ (<c>_adb-tls-connect</c>)
    /// и отладка по сети на порту 5555 (<c>_adb</c>). Имя сервиса, адрес <c>IP:порт</c> и признак беспроводной отладки.
    /// </summary>
    public async Task<List<(string Name, string Address, bool Tls)>> GetMdnsDevicesAsync()
    {
        var result = new List<(string, string, bool)>();
        try
        {
            var r = await RunAsync(["mdns", "services"], quiet: true);
            foreach (var line in r.Output.Split('\n'))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !parts[^1].Contains(':')) continue;
                if (parts[1].StartsWith("_adb-tls-connect", StringComparison.Ordinal))
                    result.Add((parts[0], parts[^1], true));
                else if (parts[1].StartsWith("_adb._tcp", StringComparison.Ordinal))
                    result.Add((parts[0], parts[^1], false));
            }
        }
        catch (InvalidOperationException) { /* adb не найден */ }
        return result;
    }

    /// <summary>Системное свойство устройства (<c>getprop</c>), например ro.build.version.release.</summary>
    public async Task<string?> GetPropAsync(string serial, string property)
    {
        var r = await RunAsync(["-s", serial, "shell", "getprop", property], quiet: true);
        var value = r.Output.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Пути ко всем APK пакета (base + split), <c>pm path</c>.</summary>
    public async Task<IReadOnlyList<string>> GetApkPathsAsync(string serial, string package, CancellationToken ct = default)
    {
        var r = await RunAsync(["-s", serial, "shell", "pm", "path", package], quiet: true, ct);
        return r.Output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("package:"))
            .Select(l => l["package:".Length..])
            .ToList();
    }

    public async Task<string?> GetVersionAsync(string serial, string package)
    {
        var r = await RunOnDeviceAsync(serial, "shell", "dumpsys", "package", package);
        var match = System.Text.RegularExpressions.Regex.Match(r.Output, @"versionName=(\S+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Устанавливает APK. Несколько файлов одного приложения (split APK) ставятся через install-multiple.
    /// Перед установкой проверяет свободное место: не хватает — очищает кэш приложений (идея ЧистогоТВ:
    /// у недорогих ТВ 8 ГБ памяти) и при ошибке нехватки места пробует ещё раз.
    /// </summary>
    public async Task<AdbResult> InstallAsync(string serial, IReadOnlyList<string> apkPaths, bool allowDowngrade, bool grantPermissions)
    {
        var args = new List<string> { "-s", serial, apkPaths.Count > 1 ? "install-multiple" : "install", "-r" };
        if (allowDowngrade) args.Add("-d");
        if (grantPermissions) args.Add("-g");
        args.AddRange(apkPaths);

        var needMb = NeededMb(apkPaths);
        var trimmed = false;
        if (await GetFreeMbAsync(serial) is { } free && free < needMb)
        {
            Log?.Invoke(Res.Format("LowStorageTrimFormat", free, needMb));
            await TrimCachesAsync(serial);
            trimmed = true;
        }

        var r = await RunAsync(args);
        if (!trimmed && IsNoSpace(r.Text))
        {
            Log?.Invoke(Res.Get("NoSpaceRetry"));
            await TrimCachesAsync(serial);
            r = await RunAsync(args);
        }
        return r;
    }

    /// <summary>Сколько места нужно с запасом: APK копируется на устройство и распаковывается при установке.</summary>
    private static long NeededMb(IReadOnlyList<string> apkPaths) =>
        (long)Math.Ceiling(apkPaths.Sum(p => new FileInfo(p).Length) * 3 / 1048576.0) + 50;

    private static bool IsNoSpace(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, "INSUFFICIENT_STORAGE|not enough space|No space left");

    /// <summary>Свободно во внутренней памяти (/data), МБ; null — если устройство не ответило (stat -f есть в toybox с Android 6).</summary>
    public async Task<long?> GetFreeMbAsync(string serial)
    {
        var r = await RunAsync(["-s", serial, "shell", "stat -f -c '%a %S' /data"], quiet: true);
        var m = System.Text.RegularExpressions.Regex.Match(r.Output.Trim(), @"^(\d+) (\d+)$");
        return m.Success ? (long)(double.Parse(m.Groups[1].Value) * double.Parse(m.Groups[2].Value) / 1048576) : null;
    }

    /// <summary>Система освобождает место за счёт кэша приложений (<c>pm trim-caches</c>); данные и настройки не трогаются.</summary>
    public async Task TrimCachesAsync(string serial)
    {
        var before = await GetFreeMbAsync(serial);
        await RunOnDeviceAsync(serial, "shell", "pm", "trim-caches", "999G");
        if (before is not null && await GetFreeMbAsync(serial) is { } after)
            Log?.Invoke(Res.Format("CacheTrimmedFormat", after, before));
    }

    /// <summary>
    /// Ошибка установки одной строкой вместо стектрейса pm, с подсказкой для частых причин:
    /// нет места, нет сборки под процессор, прошивка запрещает установку по adb.
    /// </summary>
    public async Task<string> DescribeInstallErrorAsync(string serial, IReadOnlyList<string> apkPaths, string output)
    {
        var text = ShortError(output);
        if (IsNoSpace(output))
        {
            var need = NeededMb(apkPaths);
            text += Environment.NewLine + (await GetFreeMbAsync(serial) is { } free
                ? Res.Format("InstallNoSpaceFreeFormat", free, need)
                : Res.Format("InstallNoSpaceFormat", need));
        }
        else if (output.Contains("NO_MATCHING_ABIS", StringComparison.Ordinal))
        {
            var abi = (await RunAsync(["-s", serial, "shell", "getprop", "ro.product.cpu.abilist"], quiet: true)).Output.Trim();
            text += Environment.NewLine + Res.Format("InstallNoAbiFormat", abi);
        }
        else if (output.Contains("VERIFICATION_FAILURE", StringComparison.Ordinal))
        {
            text += Environment.NewLine + Res.Get("InstallVerificationFailure");
        }
        return text;
    }

    /// <summary>Суть ошибки pm: «Failure [...]» или текст последнего исключения, без строк стектрейса.</summary>
    private static string ShortError(string output)
    {
        var failure = System.Text.RegularExpressions.Regex.Match(output, @"Failure \[[^\]]+\]");
        if (failure.Success) return failure.Value;

        var lines = output.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(l, @"^(at |\.\.\. \d+ more|Caused by)"))
            .ToList();
        var reason = lines.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"Exception: (?!.*Exception: )(.+)$"))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value;
        var text = reason ?? lines.LastOrDefault() ?? "";
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>
    /// Удаляет приложение. Системные пакеты удаляются только для текущего пользователя (без root),
    /// их можно вернуть командой <c>cmd package install-existing</c>.
    /// </summary>
    public Task<AdbResult> UninstallAsync(string serial, PackageInfo package, bool keepData)
    {
        var args = new List<string> { "-s", serial, "shell", "pm", "uninstall" };
        if (keepData) args.Add("-k");
        if (package.IsSystem) args.AddRange(["--user", "0"]);
        args.Add(package.Name);
        return RunAsync(args);
    }

    /// <summary>Скачивает base.apk приложения с устройства.</summary>
    public Task<AdbResult> PullApkAsync(string serial, PackageInfo package, string destination) =>
        RunOnDeviceAsync(serial, "pull", package.ApkPath, destination);
}
