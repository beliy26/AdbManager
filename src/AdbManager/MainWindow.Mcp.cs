using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdbManager.Models;
using AdbManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdbManager;

/// <summary>
/// MCP-сервер для агентов (Claude Code, Cursor, VS Code…): работает, пока открыто окно, и действует
/// на выбранное в нём устройство теми же методами, что и кнопки, — с защитой, журналом и бэкапом,
/// но без диалогов (подтверждение вызова инструмента — на стороне агента). Защита действует всегда,
/// экспертный режим на агента не распространяется. Тексты для агента — по-английски.
/// </summary>
public sealed partial class MainWindow
{
    private const string McpInstructions =
        "ADB Manager works with the Android device (phone, tablet or Android TV) selected in its window, over adb. " +
        "Start with list_devices, then list_packages. Every package has advice from bloatware databases: " +
        "disable = ads/telemetry, safe to disable; optional = up to the user; keep and protected = must not be touched " +
        "(such packages, the only home screen and TV input services are refused). " +
        "Prefer freeze_packages: freezing is fully reversible. uninstall_packages removes system apps for the current user only " +
        "(restorable), but removing a user app deletes it with its data. " +
        "Every change is written to the device journal (get_change_journal) and can be undone in the app: … → Undo changes. " +
        "Package labels are in the app's interface language.";

    private McpServer? _mcp;
    private readonly SemaphoreSlim _mcpLock = new(1, 1);

    private void InitializeMcp()
    {
        RootGrid.Loaded += (_, _) =>
        {
            if (_settings.McpEnabled && StartMcpServer() is { } error)
                ShowStatus(InfoBarSeverity.Warning, Res.Get("McpDialogTitle"), Res.Format("McpStartFailedFormat", _settings.McpPort, error));
        };
        Closed += (_, _) => StopMcpServer();
    }

    private string McpUrl => $"http://localhost:{_settings.McpPort}{McpServer.EndpointPath}";

    /// <summary>Запуск сервера; null — запущен, иначе текст ошибки (например, порт занят).</summary>
    private string? StartMcpServer()
    {
        if (_mcp?.IsRunning == true) return null;
        var server = new McpServer(_settings.McpPort, CreateMcpTools(), McpInstructions);
        server.Log += msg => DispatcherQueue.TryEnqueue(() => AppendLog(msg));
        try
        {
            server.Start();
        }
        catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException)
        {
            return ex.Message;
        }
        _mcp = server;
        AppendLog("MCP: " + server.Url);
        return null;
    }

    private void StopMcpServer()
    {
        _mcp?.Dispose();
        _mcp = null;
    }

    // ======================= Диалог подключения =======================

    private async void Mcp_Click(object sender, RoutedEventArgs e)
    {
        var url = McpUrl;
        var mono = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas");
        // Заголовок — отдельным TextBlock: Header у TextBox унаследовал бы моноширинный шрифт.
        StackPanel Snippet(string header, string text) => new()
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap },
                new TextBox
                {
                    Text = text,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = mono,
                    FontSize = 12,
                },
            },
        };

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var toggle = new ToggleSwitch { Header = Res.Get("McpServerToggle"), IsOn = _mcp?.IsRunning == true };
        void UpdateStatus(string? error = null) => status.Text = error is not null
            ? Res.Format("McpStartFailedFormat", _settings.McpPort, error)
            : _mcp?.IsRunning == true ? Res.Format("McpRunningFormat", url) : Res.Get("McpStopped");
        UpdateStatus();

        toggle.Toggled += (_, _) =>
        {
            string? error = null;
            if (toggle.IsOn) error = StartMcpServer();
            else StopMcpServer();
            _settings.McpEnabled = _mcp?.IsRunning == true;
            _settings.Save();
            if (error is not null) toggle.IsOn = false;
            UpdateStatus(error);
        };

        var json = new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["adb-manager"] = new JsonObject { ["type"] = "http", ["url"] = url } },
        };
        var desktop = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["adb-manager"] = new JsonObject { ["command"] = "npx", ["args"] = new JsonArray("mcp-remote", url) },
            },
        };
        var content = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 560,
            Children =
            {
                new TextBlock { Text = Res.Get("McpIntro"), TextWrapping = TextWrapping.Wrap },
                toggle,
                status,
                Snippet(Res.Get("McpClaudeCodeHeader"), $"claude mcp add --transport http adb-manager {url}"),
                Snippet(Res.Get("McpJsonHeader"), json.ToJsonString()),
                Snippet(Res.Get("McpClaudeDesktopHeader"), desktop.ToJsonString()),
            },
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Res.Get("McpDialogTitle"),
            CloseButtonText = Res.Get("Close"),
            Content = new ScrollViewer { Content = content },
        };
        await dialog.ShowAsync();
    }

    // ======================= Инструменты =======================

    private List<McpTool> CreateMcpTools() =>
    [
        Tool("list_devices", "List devices",
            "Devices known to ADB Manager: connected over USB or Wi-Fi, paired, or found on the network. " +
            "The selected one is what all other tools act on; for it, manufacturer and Android version are included.",
            Schema(), readOnly: true, destructive: false, ListDevicesAsync),

        Tool("select_device", "Select device",
            "Make a device active in ADB Manager (connecting if needed). Pass a serial from list_devices, " +
            "or an address like 192.168.1.10:5555 to connect over the network. Other network connections are closed.",
            Schema(new JsonObject { ["serial"] = Prop("string", "Serial or address from list_devices, or IP:port to connect.") }, "serial"),
            readOnly: false, destructive: false, SelectDeviceAsync),

        Tool("list_packages", "List apps",
            "Installed packages of the selected device with label, state (enabled, frozen, unused = stopped by Android), " +
            "advice (disable, optional, keep, protected, none) with its explanation, and locked = refused by protection.",
            Schema(new JsonObject
            {
                ["include_system"] = Prop("boolean", "Include system packages (default true)."),
                ["search"] = Prop("string", "Case-insensitive substring of the package name or label."),
                ["advice"] = EnumProp("Only packages with this advice.", "disable", "optional", "keep", "protected", "none"),
                ["state"] = EnumProp("Only packages in this state.", "enabled", "frozen", "unused"),
            }),
            readOnly: true, destructive: false, ListPackagesAsync),

        Tool("freeze_packages", "Freeze apps",
            "Disable packages (pm disable-user): the app stops running but stays installed and can be unfrozen any time. " +
            "Protected packages, the only home screen and TV input services are skipped. A backup is made before the first change.",
            Schema(new JsonObject { ["packages"] = StringArray("Package names, e.g. com.example.ads.") }, "packages"),
            readOnly: false, destructive: false, args => FreezeAsync(args, frozen: true)),

        Tool("unfreeze_packages", "Unfreeze apps",
            "Enable frozen packages again, or wake packages that Android stopped as unused.",
            Schema(new JsonObject { ["packages"] = StringArray("Package names.") }, "packages"),
            readOnly: false, destructive: false, args => FreezeAsync(args, frozen: false)),

        Tool("uninstall_packages", "Uninstall apps",
            "Uninstall packages. System apps are removed for the current user only and can be restored in the app; " +
            "user apps are deleted together with their data unless keep_data is true. Protected packages are skipped. " +
            "Prefer freeze_packages when unsure.",
            Schema(new JsonObject
            {
                ["packages"] = StringArray("Package names."),
                ["keep_data"] = Prop("boolean", "Keep app data and cache (uninstall -k). Default false."),
            }, "packages"),
            readOnly: false, destructive: true, UninstallAsync),

        Tool("install_apk", "Install APK",
            "Install APK files from this computer onto the selected device. Free space is checked and the app cache is " +
            "trimmed if needed.",
            Schema(new JsonObject
            {
                ["paths"] = StringArray("Absolute paths to .apk files on this computer."),
                ["split"] = Prop("boolean", "The files are parts of one split APK (install-multiple). Default false."),
                ["allow_downgrade"] = Prop("boolean", "Allow installing an older version (-d). Default false."),
                ["grant_permissions"] = Prop("boolean", "Grant all runtime permissions (-g). Default false."),
            }, "paths"),
            readOnly: false, destructive: false, InstallApkAsync),

        Tool("get_change_journal", "Change journal",
            "Changes made on the selected device by ADB Manager (by the user or agents): freeze, enable, uninstall, " +
            "install, dns, home, restore — newest last.",
            Schema(new JsonObject { ["limit"] = Prop("integer", "How many latest entries to return (default 50).") }),
            readOnly: true, destructive: false, GetJournalEntriesAsync),

        Tool("take_screenshot", "Screenshot",
            "Screenshot of the selected device's screen as PNG.",
            Schema(), readOnly: true, destructive: false, TakeScreenshotAsync),
    ];

    /// <summary>
    /// Обёртка инструмента: вызовы по одному (как действия в окне), в потоке интерфейса —
    /// методы окна работают с элементами XAML.
    /// </summary>
    private McpTool Tool(string name, string title, string description, JsonObject schema, bool readOnly, bool destructive,
        Func<JsonElement, Task<McpToolResult>> handler) =>
        new(name, title, description, schema, readOnly, destructive, async args =>
        {
            await _mcpLock.WaitAsync();
            try
            {
                return await OnUiAsync(async () =>
                {
                    var text = args.ValueKind == JsonValueKind.Object && args.EnumerateObject().Any() ? " " + args.GetRawText() : "";
                    AppendLog($"MCP: {name}{(text.Length > 300 ? text[..300] + "…" : text)}");
                    return await handler(args);
                });
            }
            finally
            {
                _mcpLock.Release();
            }
        });

    private Task<T> OnUiAsync<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>();
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try { tcs.SetResult(await func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }))
            tcs.SetException(new McpToolException("ADB Manager window is closing."));
        return tcs.Task;
    }

    private async Task<McpToolResult> ListDevicesAsync(JsonElement args)
    {
        if (_adb.AdbPath is null) throw new McpToolException("adb.exe is not found. Set it up in the ADB Manager window.");

        var list = new JsonArray();
        foreach (var d in Devices)
        {
            var item = new JsonObject
            {
                ["serial"] = d.Serial,
                ["name"] = d.DisplayName,
                ["state"] = d.State,
                ["connection"] = d.Kind.ToString().ToLowerInvariant(),
                ["type"] = d.Type.ToString().ToLowerInvariant(),
                ["selected"] = d == SelectedDevice,
            };
            if (d == SelectedDevice && d.IsOnline)
            {
                var props = await GetPropsAsync(d);
                item["type"] = props.Type.ToString().ToLowerInvariant();
                item["manufacturer"] = props.Manufacturer;
                item["model"] = props.Model;
                item["android"] = props.Android;
                item["sdk"] = props.Sdk;
            }
            list.Add(item);
        }
        return McpToolResult.Text(list.Count == 0
            ? "No devices. Connect a device over USB with USB debugging enabled, or pass IP:port to select_device."
            : list.ToJsonString(McpServer.JsonOptions));
    }

    private async Task<McpToolResult> SelectDeviceAsync(JsonElement args)
    {
        var serial = Str(args, "serial")?.Trim();
        if (string.IsNullOrEmpty(serial)) throw new McpToolException("'serial' is required.");

        var device = Devices.FirstOrDefault(d => d.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            if (!serial.Contains(':'))
                throw new McpToolException($"No device '{serial}'. Call list_devices, or pass an address like 192.168.1.10:5555.");
            await ConnectToAsync(serial, paired: false);
        }
        else if (!device.IsConnected)
        {
            await ConnectToAsync(device.Serial, device.Kind == DeviceKind.Paired);
        }
        else if (device != SelectedDevice)
        {
            await RunBusyAsync(() => RefreshDevicesAsync(select: device.Serial));
        }

        if (SelectedDevice is { } now && now.Serial.Equals(device?.Serial ?? serial, StringComparison.OrdinalIgnoreCase))
        {
            return now.IsOnline
                ? await ListDevicesAsync(args)
                : McpToolResult.Text($"{now.DisplayName} is selected but not ready: {now.State}. " +
                                     "If it is 'unauthorized', accept the debugging prompt on the device screen.", isError: true);
        }
        return McpToolResult.Text($"Could not select {serial}. {StatusBar.Title}: {StatusBar.Message}", isError: true);
    }

    private async Task<McpToolResult> ListPackagesAsync(JsonElement args)
    {
        var device = RequireDevice();
        var includeSystem = Bool(args, "include_system", true);
        var search = Str(args, "search");
        var advice = Str(args, "advice");
        var state = Str(args, "state");

        var (all, _) = await GetAgentPackagesAsync(device, includeSystem);
        var list = all.Where(p => includeSystem || !p.IsSystem)
            .Where(p => string.IsNullOrEmpty(search) ||
                        p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        p.Label?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
            .Where(p => string.IsNullOrEmpty(advice) || p.Advice.ToString().Equals(advice, StringComparison.OrdinalIgnoreCase))
            .Where(p => string.IsNullOrEmpty(state) || p.State.ToString().Equals(state, StringComparison.OrdinalIgnoreCase))
            .Select(PackageJson)
            .ToArray();
        return McpToolResult.Text(new JsonObject
        {
            ["device"] = device.DisplayName,
            ["count"] = list.Length,
            ["packages"] = new JsonArray(list),
        }.ToJsonString(McpServer.JsonOptions));
    }

    private async Task<McpToolResult> FreezeAsync(JsonElement args, bool frozen)
    {
        var device = RequireDevice();
        var names = StrList(args, "packages");
        var (all, fromUi) = await GetAgentPackagesAsync(device, includeSystem: true);
        var (found, missing) = Resolve(all, names);

        if (!frozen)
        {
            var toEnable = found.Where(p => p.State != PackageState.Enabled).ToList();
            var enabled = toEnable.Count > 0 ? await SetFrozenAsync(toEnable, frozen: false) : new PackagesActionResult([], []);
            if (!fromUi && enabled.Done.Count > 0) await RefreshShownPackagesAsync(device);
            return Summary(enabled, "Enabled",
                ("Already enabled", Names(found.Except(toEnable))),
                ("Not installed", missing));
        }

        var locked = found.Where(p => p.IsBlocked).ToList();
        var candidates = found.Where(p => !p.IsBlocked && p.State == PackageState.Enabled).ToList();
        var drivers = await FindTvInputDriversAsync(device, candidates);
        var selected = candidates.Except(drivers).ToList();
        var result = selected.Count > 0 ? await SetFrozenAsync(selected, frozen: true) : new PackagesActionResult([], []);
        if (!fromUi && result.Done.Count > 0) await RefreshShownPackagesAsync(device);
        return Summary(result, "Frozen",
            ("Skipped, protected", Names(locked)),
            ("Skipped, serves TV inputs (HDMI/tuner)", Names(drivers)),
            ("Already frozen or stopped", Names(found.Where(p => !p.IsBlocked && p.State != PackageState.Enabled))),
            ("Not installed", missing));
    }

    private async Task<McpToolResult> UninstallAsync(JsonElement args)
    {
        var device = RequireDevice();
        var names = StrList(args, "packages");
        var (all, _) = await GetAgentPackagesAsync(device, includeSystem: true);
        var (found, missing) = Resolve(all, names);

        var locked = found.Where(p => p.IsBlocked).ToList();
        var candidates = found.Where(p => !p.IsBlocked).ToList();
        var drivers = await FindTvInputDriversAsync(device, candidates);
        var selected = candidates.Except(drivers).ToList();
        var result = selected.Count > 0
            ? await UninstallCoreAsync(device, selected, Bool(args, "keep_data", false))
            : new PackagesActionResult([], []);
        return Summary(result, "Uninstalled",
            ("Skipped, protected", Names(locked)),
            ("Skipped, serves TV inputs (HDMI/tuner)", Names(drivers)),
            ("Not installed", missing));
    }

    private async Task<McpToolResult> InstallApkAsync(JsonElement args)
    {
        var device = RequireDevice();
        var paths = StrList(args, "paths", "file paths");
        foreach (var path in paths)
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new McpToolException($"File not found: {path}. Pass absolute paths on this computer.");
            if (!path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException($"Not an .apk file: {path}");
        }

        var errors = await InstallCoreAsync(device, paths, Bool(args, "split", false),
            Bool(args, "allow_downgrade", false), Bool(args, "grant_permissions", false));
        return errors.Count == 0
            ? McpToolResult.Text("Installed: " + string.Join(", ", paths.Select(Path.GetFileName)))
            : McpToolResult.Text("Errors:\n" + string.Join("\n", errors), isError: true);
    }

    private async Task<McpToolResult> GetJournalEntriesAsync(JsonElement args)
    {
        var device = RequireDevice();
        var limit = Math.Clamp(Int(args, "limit", 50), 1, 1000);
        var journal = await GetJournalAsync(device);
        var entries = journal.ReadAll().TakeLast(limit).ToList();
        return McpToolResult.Text(entries.Count == 0
            ? "The journal is empty: nothing was changed on this device by ADB Manager."
            : JsonSerializer.Serialize(entries, McpServer.JsonOptions));
    }

    private async Task<McpToolResult> TakeScreenshotAsync(JsonElement args)
    {
        var device = RequireDevice();
        var png = await _adb.ScreenshotAsync(device.Serial);
        if (png.Length < 8 || png[0] != 0x89 || png[1] != (byte)'P')
            throw new McpToolException("The device did not return a screenshot.");
        return McpToolResult.Image(png, "image/png");
    }

    // ----- Вспомогательное -----

    private DeviceInfo RequireDevice() =>
        SelectedDevice is { IsOnline: true } device
            ? device
            : throw new McpToolException(SelectedDevice is { } d
                ? $"The selected device {d.DisplayName} is not ready ({d.State}). Call list_devices or select_device."
                : "No device is selected in ADB Manager. Call list_devices and select_device.");

    /// <summary>
    /// Пакеты для агента: список окна, если он загружен для этого устройства и в нём есть нужное
    /// (тогда изменения сразу видны в таблице), иначе — отдельное чтение с теми же советами и защитой.
    /// </summary>
    private async Task<(IReadOnlyList<PackageInfo> Packages, bool FromUi)> GetAgentPackagesAsync(DeviceInfo device, bool includeSystem)
    {
        if (_allPackages.Count > 0 && _shownDeviceKey == $"{device.Serial}|{device.State}" && (ShowSystemToggle.IsOn || !includeSystem))
            return (_allPackages, true);

        var packages = await _adb.GetPackagesAsync(device.Serial, includeSystem);
        await ApplyAdviceAsync(device, packages);
        var cached = await Task.Run(() => packages.Select(p => _metadata.LoadCached(device.Serial, p)).ToList());
        for (int i = 0; i < packages.Count; i++)
            if (cached[i] is { } meta) packages[i].Apply(meta);
        return (packages, false);
    }

    /// <summary>Агент менял пакеты не из таблицы окна — перечитываем её, чтобы было видно изменения.</summary>
    private async Task RefreshShownPackagesAsync(DeviceInfo device)
    {
        if (SelectedDevice == device) await LoadPackagesAsync();
    }

    private static (List<PackageInfo> Found, List<string> Missing) Resolve(IReadOnlyList<PackageInfo> all, List<string> names)
    {
        var byName = all.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var found = new List<PackageInfo>();
        var missing = new List<string>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (byName.TryGetValue(name, out var p)) found.Add(p);
            else missing.Add(name);
        }
        return (found, missing);
    }

    private static IEnumerable<string> Names(IEnumerable<PackageInfo> packages) => packages.Select(p => p.Name);

    private static McpToolResult Summary(PackagesActionResult result, string doneLabel, params (string Label, IEnumerable<string> Items)[] skipped)
    {
        var lines = new List<string> { $"{doneLabel}: " + (result.Done.Count == 0 ? "none" : string.Join(", ", result.Done.Select(p => p.Name))) };
        lines.AddRange(skipped.Select(s => (s.Label, Items: s.Items.ToList()))
            .Where(s => s.Items.Count > 0)
            .Select(s => $"{s.Label}: {string.Join(", ", s.Items)}"));
        if (result.Errors.Count > 0) lines.Add("Errors:\n" + string.Join("\n", result.Errors));
        return McpToolResult.Text(string.Join("\n", lines), isError: result.Done.Count == 0 && result.Errors.Count > 0);
    }

    private static JsonObject PackageJson(PackageInfo p)
    {
        var o = new JsonObject
        {
            ["package"] = p.Name,
            ["system"] = p.IsSystem,
            ["state"] = p.State.ToString().ToLowerInvariant(),
            ["advice"] = p.Advice.ToString().ToLowerInvariant(),
        };
        if (!string.IsNullOrWhiteSpace(p.Label)) o["label"] = p.Label;
        if (!string.IsNullOrWhiteSpace(p.AdviceTitle)) o["adviceTitle"] = p.AdviceTitle;
        if (!string.IsNullOrWhiteSpace(p.AdviceNote)) o["adviceNote"] = p.AdviceNote;
        if (p.IsBlocked) o["locked"] = true;
        if (p.TotalSize is { } size) o["sizeMb"] = Math.Round(size / 1048576.0, 1);
        return o;
    }

    private static JsonObject Schema(JsonObject? properties = null, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties ?? new JsonObject() };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return schema;
    }

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonObject StringArray(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    };

    private static JsonObject EnumProp(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()),
        ["description"] = description,
    };

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool Bool(JsonElement args, string name, bool fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    private static int Int(JsonElement args, string name, int fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    /// <summary>Непустой список строк; одна строка вместо массива тоже принимается.</summary>
    private static List<string> StrList(JsonElement args, string name, string? what = null)
    {
        var list = new List<string>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.String) list.Add(v.GetString()!);
            else if (v.ValueKind == JsonValueKind.Array)
                list.AddRange(v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        }
        list = list.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return list.Count > 0 ? list : throw new McpToolException($"'{name}' must be a non-empty array of {what ?? "package names"}.");
    }
}
