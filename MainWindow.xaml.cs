using System.Collections.ObjectModel;
using AdbManager.Models;
using AdbManager.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AdbManager;

public sealed partial class MainWindow : Window
{
    private readonly AdbService _adb = new();
    private readonly AppMetadataService _metadata;
    private readonly Settings _settings = Settings.Load();
    private CancellationTokenSource? _metadataCts;
    private readonly DispatcherQueueTimer _devicePollTimer;
    private List<PackageInfo> _allPackages = [];
    private int _busyCount;
    private bool _polling;

    public ObservableCollection<DeviceInfo> Devices { get; } = [];
    public ObservableCollection<PackageInfo> Packages { get; } = [];

    private DeviceInfo? SelectedDevice => DevicesList.SelectedItem as DeviceInfo;

    public MainWindow()
    {
        InitializeComponent();
        _metadata = new AppMetadataService(_adb);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1240, 820));

        _adb.Log += msg => DispatcherQueue.TryEnqueue(() => AppendLog(msg));

        ConnectAddressBox.Text = _settings.LastConnectAddress ?? "";

        _devicePollTimer = DispatcherQueue.CreateTimer();
        _devicePollTimer.Interval = TimeSpan.FromSeconds(3);
        _devicePollTimer.Tick += async (_, _) => await PollDevicesAsync();

        RootGrid.Loaded += async (_, _) => await InitializeAdbAsync();
        Closed += (_, _) =>
        {
            _devicePollTimer.Stop();
            _metadataCts?.Cancel();
        };
    }

    // ======================= Инициализация adb =======================

    private async Task InitializeAdbAsync()
    {
        SetAdbPath(AdbService.FindAdb(_settings.AdbPath));

        if (_adb.AdbPath is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "adb не найден",
                "Нажмите «Скачать», чтобы загрузить Android platform-tools, или укажите путь к adb.exe.");
            return;
        }

        await RunBusyAsync(async () =>
        {
            await _adb.StartServerAsync();
            await RefreshDevicesAsync();
        });
        _devicePollTimer.Start();
    }

    private void SetAdbPath(string? path)
    {
        _adb.AdbPath = path;
        AdbPathText.Text = path ?? "adb.exe не найден";
        if (path is not null && path != _settings.AdbPath)
        {
            _settings.AdbPath = path;
            _settings.Save();
        }
    }

    private async void BrowseAdb_Click(object sender, RoutedEventArgs e)
    {
        var picker = CreateOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        if (!file.Name.Equals("adb.exe", StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus(InfoBarSeverity.Warning, "Неверный файл", "Нужно выбрать adb.exe из папки platform-tools.");
            return;
        }

        SetAdbPath(file.Path);
        await InitializeAdbAsync();
    }

    private async void DownloadAdb_Click(object sender, RoutedEventArgs e)
    {
        DownloadAdbButton.IsEnabled = false;
        BusyBar.IsIndeterminate = false;
        BusyBar.Value = 0;
        BusyBar.Visibility = Visibility.Visible;
        ShowStatus(InfoBarSeverity.Informational, "Загрузка", "Скачивание platform-tools с dl.google.com…");

        try
        {
            var progress = new Progress<double>(p => BusyBar.Value = p * 100);
            var path = await AdbService.DownloadPlatformToolsAsync(progress);
            SetAdbPath(path);
            ShowStatus(InfoBarSeverity.Success, "Готово", "platform-tools установлены: " + path);
            await InitializeAdbAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Не удалось скачать platform-tools", ex.Message);
        }
        finally
        {
            BusyBar.IsIndeterminate = true;
            BusyBar.Visibility = _busyCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            DownloadAdbButton.IsEnabled = true;
        }
    }

    // ======================= Устройства =======================

    private async Task RefreshDevicesAsync(bool quiet = false)
    {
        var devices = await _adb.GetDevicesAsync(quiet);
        var selectedSerial = SelectedDevice?.Serial;

        static string Key(DeviceInfo d) => $"{d.Serial}|{d.State}|{d.Model}";
        if (!devices.Select(Key).SequenceEqual(Devices.Select(Key)))
        {
            Devices.Clear();
            foreach (var d in devices) Devices.Add(d);

            var toSelect = Devices.FirstOrDefault(d => d.Serial == selectedSerial)
                           ?? (Devices.Count == 1 ? Devices[0] : null);
            // Смена выделения вызовет SelectionChanged и перечитает пакеты
            // (в т.ч. когда устройство сменило состояние, например после разрешения отладки).
            DevicesList.SelectedItem = toSelect;
        }

        NoDevicesText.Visibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task PollDevicesAsync()
    {
        if (_polling || _adb.AdbPath is null) return;
        _polling = true;
        try { await RefreshDevicesAsync(quiet: true); }
        catch { /* фоновый опрос не должен мешать */ }
        finally { _polling = false; }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) =>
        await RunBusyAsync(() => RefreshDevicesAsync());

    private async void DevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await OnDeviceChangedAsync();

    private async Task OnDeviceChangedAsync()
    {
        var device = SelectedDevice;
        var online = device?.IsOnline == true;

        InstallButton.IsEnabled = online;
        RefreshPackagesButton.IsEnabled = online;
        EnableWifiButton.IsEnabled = online && !device!.IsWireless;
        DisconnectButton.IsEnabled = device?.IsWireless == true;
        PackagesHeader.Text = device is null ? "Приложения" : $"Приложения — {device.DisplayName}";

        if (online)
        {
            await LoadPackagesAsync();
        }
        else
        {
            CancelMetadataLoading();
            _allPackages = [];
            ApplyFilter();
            EmptyPackagesText.Text = device is null
                ? "Выберите устройство слева"
                : $"Устройство недоступно: {device.StateText}";
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void ConnectAddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        var address = ConnectAddressBox.Text.Trim();
        if (address.Length == 0) return;
        if (!address.Contains(':')) address += ":5555";

        await RunBusyAsync(async () =>
        {
            var r = await _adb.ConnectAsync(address);
            var ok = r.Output.Contains("connected to", StringComparison.OrdinalIgnoreCase);
            ShowStatus(ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                ok ? "Подключено" : "Не удалось подключиться", r.Text);
            if (ok)
            {
                _settings.LastConnectAddress = address;
                _settings.Save();
            }
            await RefreshDevicesAsync();
        });
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var addressBox = new TextBox { Header = "IP-адрес и порт сопряжения", PlaceholderText = "192.168.1.10:37123" };
        var codeBox = new TextBox { Header = "Код сопряжения (6 цифр)", PlaceholderText = "123456" };
        var connectBox = new TextBox
        {
            Header = "IP-адрес и порт для подключения (с главного экрана «Беспроводной отладки»)",
            PlaceholderText = "192.168.1.10:41234",
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Сопряжение по коду",
            PrimaryButtonText = "Подключить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "На телефоне: Настройки → Для разработчиков → Беспроводная отладка → " +
                               "«Подключить устройство с помощью кода». Введите показанные адрес и код.",
                    },
                    addressBox, codeBox, connectBox,
                },
            },
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var pairAddress = addressBox.Text.Trim();
        var code = codeBox.Text.Trim();
        var connectAddress = connectBox.Text.Trim();
        if (pairAddress.Length == 0 || code.Length == 0) return;

        await RunBusyAsync(async () =>
        {
            var r = await _adb.PairAsync(pairAddress, code);
            var ok = r.Output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase);
            if (!ok)
            {
                ShowStatus(InfoBarSeverity.Error, "Сопряжение не удалось", r.Text);
                return;
            }

            if (connectAddress.Length > 0)
            {
                ConnectAddressBox.Text = connectAddress;
                var c = await _adb.ConnectAsync(connectAddress);
                ShowStatus(InfoBarSeverity.Success, "Сопряжение выполнено", c.Text);
            }
            else
            {
                ShowStatus(InfoBarSeverity.Success, "Сопряжение выполнено",
                    "Устройство должно подключиться автоматически. Если нет — введите адрес и нажмите «Подключить».");
            }
            await Task.Delay(1000);
            await RefreshDevicesAsync();
        });
    }

    private async void EnableWifi_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device) return;

        await RunBusyAsync(async () =>
        {
            var ip = await _adb.GetWifiIpAsync(device.Serial);
            var r = await _adb.EnableTcpIpAsync(device.Serial);
            if (!r.Success)
            {
                ShowStatus(InfoBarSeverity.Error, "Не удалось включить режим TCP/IP", r.Text);
                return;
            }

            if (ip is null)
            {
                ShowStatus(InfoBarSeverity.Warning, "Режим TCP/IP включён",
                    "Не удалось определить IP телефона — убедитесь, что он подключён к Wi-Fi, и введите адрес вручную.");
                return;
            }

            ConnectAddressBox.Text = ip + ":5555";
            await Task.Delay(2000); // adbd перезапускается на телефоне
            var c = await _adb.ConnectAsync(ip + ":5555");
            ShowStatus(InfoBarSeverity.Success, "Wi-Fi режим",
                c.Text + Environment.NewLine + "Теперь USB-кабель можно отключить.");
            await RefreshDevicesAsync();
        });
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device) return;
        await RunBusyAsync(async () =>
        {
            await _adb.DisconnectAsync(device.Serial);
            await RefreshDevicesAsync();
        });
    }

    // ======================= Приложения =======================

    private async Task LoadPackagesAsync()
    {
        if (SelectedDevice is not { IsOnline: true } device) return;

        await RunBusyAsync(async () =>
        {
            EmptyPackagesText.Text = "Загрузка…";
            await ReloadPackagesCoreAsync(device);
            EmptyPackagesText.Text = "Приложения не найдены";
        });
    }

    /// <summary>
    /// Перечитывает список пакетов: названия и иконки сразу берутся из кэша,
    /// недостающие загружаются с устройства в фоне.
    /// </summary>
    private async Task ReloadPackagesCoreAsync(DeviceInfo device)
    {
        CancelMetadataLoading();
        var cts = _metadataCts = new CancellationTokenSource();

        var packages = await _adb.GetPackagesAsync(device.Serial, ShowSystemToggle.IsOn);
        var cached = await Task.Run(() => packages.Select(p => _metadata.LoadCached(device.Serial, p)).ToList());

        var missing = new List<PackageInfo>();
        for (int i = 0; i < packages.Count; i++)
        {
            if (cached[i] is { } meta) packages[i].Apply(meta);
            else missing.Add(packages[i]);
        }

        _allPackages = packages.ToList();
        ApplyFilter();

        _ = LoadMetadataAsync(device.Serial, missing, cts);
    }

    private void CancelMetadataLoading()
    {
        _metadataCts?.Cancel();
        _metadataCts = null;
        MetadataProgressPanel.Visibility = Visibility.Collapsed;
    }

    private async Task LoadMetadataAsync(string serial, List<PackageInfo> packages, CancellationTokenSource cts)
    {
        if (packages.Count == 0) return;

        var ct = cts.Token;
        int done = 0;
        void Report() => MetadataProgressText.Text = $"Загрузка иконок: {done} из {packages.Count}";
        Report();
        MetadataProgressPanel.Visibility = Visibility.Visible;

        using var throttle = new SemaphoreSlim(4);
        var tasks = packages.Select(async p =>
        {
            try
            {
                await throttle.WaitAsync(ct);
                try
                {
                    var meta = await Task.Run(() => _metadata.FetchAsync(serial, p, ct), ct);
                    if (meta is not null && !ct.IsCancellationRequested) p.Apply(meta);
                }
                finally
                {
                    throttle.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppendLog($"! {p.Name}: {ex.Message}"); }
            finally
            {
                done++;
                if (!ct.IsCancellationRequested) Report();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        if (ct.IsCancellationRequested) return;

        MetadataProgressPanel.Visibility = Visibility.Collapsed;
        ApplyFilter(); // пересортировать по загруженным названиям
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var filtered = _allPackages
            .Where(p => query.Length == 0 ||
                        p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Label?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true)
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var selected = PackagesList.SelectedItems.OfType<PackageInfo>().ToHashSet();
        Packages.Clear();
        foreach (var p in filtered) Packages.Add(p);
        foreach (var p in filtered.Where(selected.Contains)) PackagesList.SelectedItems.Add(p);

        EmptyPackagesPanel.Visibility = Packages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PackagesCountText.Text = _allPackages.Count == 0
            ? ""
            : query.Length == 0
                ? $"Всего: {_allPackages.Count}"
                : $"Найдено: {Packages.Count} из {_allPackages.Count}";
        UpdatePackageButtons();
    }

    private void UpdatePackageButtons()
    {
        var count = PackagesList.SelectedItems.Count;
        var online = SelectedDevice?.IsOnline == true;
        UninstallButton.IsEnabled = online && count > 0;
        PullApkButton.IsEnabled = online && count == 1;
        CopyNameButton.IsEnabled = count > 0;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private async void ShowSystemToggle_Toggled(object sender, RoutedEventArgs e) => await LoadPackagesAsync();

    private async void RefreshPackages_Click(object sender, RoutedEventArgs e) => await LoadPackagesAsync();

    private async void PackagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePackageButtons();

        // Для одного выбранного приложения показываем версию.
        if (PackagesList.SelectedItems.Count == 1 &&
            PackagesList.SelectedItem is PackageInfo pkg &&
            SelectedDevice is { IsOnline: true } device)
        {
            try
            {
                var version = await _adb.GetVersionAsync(device.Serial, pkg.Name);
                if (ReferenceEquals(PackagesList.SelectedItem, pkg))
                    PackagesCountText.Text = $"{pkg.Name} · версия {version ?? "?"}";
            }
            catch { /* необязательная информация */ }
        }
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        var names = PackagesList.SelectedItems.OfType<PackageInfo>().Select(p => p.Name);
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, names));
        Clipboard.SetContent(package);
    }

    // ----- Установка -----

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var picker = CreateOpenPicker();
        picker.FileTypeFilter.Add(".apk");
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
            await InstallFilesAsync(files.Select(f => f.Path).ToList());
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) && SelectedDevice?.IsOnline == true)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Установить на " + SelectedDevice.DisplayName;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        var apks = items.OfType<StorageFile>()
            .Where(f => f.FileType.Equals(".apk", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path)
            .ToList();

        if (apks.Count == 0)
            ShowStatus(InfoBarSeverity.Warning, "Нет APK", "Перетащите файлы с расширением .apk.");
        else
            await InstallFilesAsync(apks);
    }

    private async Task InstallFilesAsync(List<string> paths)
    {
        if (SelectedDevice is not { IsOnline: true } device)
        {
            ShowStatus(InfoBarSeverity.Warning, "Нет устройства", "Сначала выберите подключённое устройство.");
            return;
        }

        var downgrade = new CheckBox { Content = "Разрешить установку более старой версии (-d)" };
        var grant = new CheckBox { Content = "Сразу выдать все разрешения (-g)" };
        var split = new CheckBox
        {
            Content = "Это части одного приложения (split APK, install-multiple)",
            Visibility = paths.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Установка на {device.DisplayName}",
            PrimaryButtonText = "Установить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = string.Join(Environment.NewLine, paths.Select(Path.GetFileName)),
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    },
                    downgrade, grant, split,
                },
            },
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunBusyAsync(async () =>
        {
            List<List<string>> groups = split.IsChecked == true
                ? [paths]
                : paths.Select(p => new List<string> { p }).ToList();

            var errors = new List<string>();
            foreach (var group in groups)
            {
                var name = string.Join(", ", group.Select(Path.GetFileName));
                ShowStatus(InfoBarSeverity.Informational, "Установка…", name);

                var r = await _adb.InstallAsync(device.Serial, group, downgrade.IsChecked == true, grant.IsChecked == true);
                if (!r.Success || !r.Text.Contains("Success"))
                    errors.Add($"{name}: {LastLine(r.Text)}");
            }

            if (errors.Count == 0)
                ShowStatus(InfoBarSeverity.Success, "Установлено",
                    groups.Count == 1 ? Path.GetFileName(paths[0]) : $"Установлено пакетов: {groups.Count}");
            else
                ShowStatus(InfoBarSeverity.Error, "Ошибка установки", string.Join(Environment.NewLine, errors));

            await ReloadPackagesCoreAsync(device);
        });
    }

    // ----- Удаление -----

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;
        var selected = PackagesList.SelectedItems.OfType<PackageInfo>().ToList();
        if (selected.Count == 0) return;

        var keepData = new CheckBox { Content = "Сохранить данные и кэш (-k)" };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = selected.Count == 1 ? "Удалить приложение?" : $"Удалить приложения ({selected.Count})?",
        });
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 220,
            Content = new TextBlock
            {
                Text = string.Join(Environment.NewLine, selected.Select(p => p.Name)),
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                TextWrapping = TextWrapping.Wrap,
            },
        });
        if (selected.Any(p => p.IsSystem))
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = "Системные приложения",
                Message = "Будут удалены только для текущего пользователя (pm uninstall --user 0). " +
                          "Удаление важных системных компонентов может нарушить работу телефона. " +
                          "Вернуть: adb shell cmd package install-existing <пакет>.",
            });
        }
        content.Children.Add(keepData);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Удаление",
            PrimaryButtonText = "Удалить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            Content = content,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunBusyAsync(async () =>
        {
            var errors = new List<string>();
            var removed = new List<PackageInfo>();
            foreach (var pkg in selected)
            {
                ShowStatus(InfoBarSeverity.Informational, "Удаление…", pkg.Name);
                var r = await _adb.UninstallAsync(device.Serial, pkg, keepData.IsChecked == true);
                if (r.Success && r.Text.Contains("Success"))
                    removed.Add(pkg);
                else
                    errors.Add($"{pkg.Name}: {LastLine(r.Text)}");
            }

            _allPackages = _allPackages.Except(removed).ToList();
            ApplyFilter();

            if (errors.Count == 0)
                ShowStatus(InfoBarSeverity.Success, "Удалено", string.Join(", ", removed.Select(p => p.Name)));
            else
                ShowStatus(InfoBarSeverity.Error, $"Удалено: {removed.Count}, ошибок: {errors.Count}",
                    string.Join(Environment.NewLine, errors));
        });
    }

    // ----- Скачивание APK -----

    private async void PullApk_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device || PackagesList.SelectedItem is not PackageInfo pkg) return;
        if (string.IsNullOrEmpty(pkg.ApkPath)) return;

        var picker = new FileSavePicker { SuggestedFileName = pkg.Name };
        picker.FileTypeChoices.Add("Android package", [".apk"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        await RunBusyAsync(async () =>
        {
            var r = await _adb.PullApkAsync(device.Serial, pkg, file.Path);
            ShowStatus(r.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                r.Success ? "APK сохранён" : "Ошибка", r.Success ? file.Path : r.Text);
        });
    }

    // ======================= Вспомогательное =======================

    private FileOpenPicker CreateOpenPicker()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        // В неупакованном приложении пикеру нужно явно передать окно-владельца.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return picker;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _busyCount++;
        BusyBar.Visibility = Visibility.Visible;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Ошибка", ex.Message);
            AppendLog("! " + ex.Message);
        }
        finally
        {
            if (--_busyCount == 0) BusyBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void AppendLog(string text)
    {
        LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {text.TrimEnd()}{Environment.NewLine}";
        LogBox.SelectionStart = LogBox.Text.Length;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Text = "";

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "неизвестная ошибка";
}
