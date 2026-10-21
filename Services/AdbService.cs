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

/// <summary>Тонкая обёртка над adb.exe.</summary>
public sealed class AdbService
{
    private const string PlatformToolsUrl =
        "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

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

    /// <summary>Скачивает официальные platform-tools от Google в %LOCALAPPDATA%\AdbManager.</summary>
    public static async Task<string> DownloadPlatformToolsAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppDataDir);
        var zipPath = Path.Combine(AppDataDir, "platform-tools.zip");

        using (var http = new HttpClient())
        using (var response = await http.GetAsync(PlatformToolsUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
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

        if (Directory.Exists(LocalPlatformToolsDir))
            Directory.Delete(LocalPlatformToolsDir, recursive: true);

        // Архив содержит папку platform-tools в корне.
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, AppDataDir, overwriteFiles: true), ct);
        File.Delete(zipPath);

        var adb = Path.Combine(LocalPlatformToolsDir, "adb.exe");
        if (!File.Exists(adb))
            throw new FileNotFoundException("В архиве platform-tools не найден adb.exe", adb);
        return adb;
    }

    // ---------- Запуск команд ----------

    public async Task<AdbResult> RunAsync(IEnumerable<string> args, bool quiet = false, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(AdbPath) || !File.Exists(AdbPath))
            throw new InvalidOperationException("adb.exe не найден. Укажите путь к нему или скачайте platform-tools.");

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
            throw new InvalidOperationException("adb.exe не найден.");

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

        // Замороженные (отключённые) пакеты.
        var disabled = await RunOnDeviceAsync(serial, "shell", "pm", "list", "packages", "-d");
        var disabledNames = ParsePackages(disabled.Output, isSystem: false).Select(p => p.Name).ToHashSet();
        foreach (var p in list)
            p.IsDisabled = disabledNames.Contains(p.Name);

        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
        return r.Output.Contains("new state: " + expected, StringComparison.OrdinalIgnoreCase)
            ? r
            : r with { ExitCode = r.ExitCode == 0 ? 1 : r.ExitCode };
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
    /// </summary>
    public Task<AdbResult> InstallAsync(string serial, IReadOnlyList<string> apkPaths, bool allowDowngrade, bool grantPermissions)
    {
        var args = new List<string> { "-s", serial, apkPaths.Count > 1 ? "install-multiple" : "install", "-r" };
        if (allowDowngrade) args.Add("-d");
        if (grantPermissions) args.Add("-g");
        args.AddRange(apkPaths);
        return RunAsync(args);
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
