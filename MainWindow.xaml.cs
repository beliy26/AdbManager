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
    private readonly PackageAdvisor _advisor = PackageAdvisor.Load();
    private readonly Dictionary<string, DeviceProps> _deviceProps = [];
    private readonly HashSet<string> _insecureWarned = [];
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
        SetInitialWindowSize();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        _adb.Log += msg => DispatcherQueue.TryEnqueue(() => AppendLog(msg));

        ConnectAddressBox.Text = _settings.LastConnectAddress ?? "";
        if (Enum.TryParse<SortColumn>(_settings.SortColumn, out var sortColumn)) _sortColumn = sortColumn;
        _sortDescending = _settings.SortDescending;

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

    // ======================= Размер окна =======================

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Стартовый размер окна в логических пикселях — AppWindow работает в физических,
    /// поэтому учитываем масштаб экрана и не выходим за рабочую область монитора.
    /// </summary>
    private void SetInitialWindowSize()
    {
        const int width = 1480, height = 920;          // желаемый размер, DIP
        const int minWidth = 1100, minHeight = 640;    // меньше — таблица не помещается

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;

        int w = Math.Min((int)(width * scale), (int)(area.Width * 0.95));
        int h = Math.Min((int)(height * scale), (int)(area.Height * 0.92));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));

        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(minWidth * scale), area.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(minHeight * scale), area.Height);
        }
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
    //
    // В списке: подключённые по USB и по сети (adb devices), найденные кнопкой «Найти в сети»
    // и сопряжённые по коду. Работаем с одним устройством: при выборе устройства остальные
    // сетевые подключения разрываются (USB через adb не отключить — такое просто не выбрано).

    /// <summary>Найденное в сети за этот сеанс: адрес и серийный номер из mDNS (если есть).</summary>
    private sealed record NetworkDevice(string Address, string? Serial, bool Tls);

    private readonly List<NetworkDevice> _found = [];
    private bool _rebuildingDevices;
    private string? _shownDeviceKey;

    private async Task RefreshDevicesAsync(bool quiet = false, string? select = null)
    {
        var adbDevices = await _adb.GetDevicesAsync(quiet);
        var mdns = _settings.PairedDevices.Count > 0 ? await _adb.GetMdnsDevicesAsync() : null;
        UpdatePairedAddresses(mdns);

        var selectedSerial = select ?? SelectedDevice?.Serial;
        var list = BuildDeviceList(adbDevices, mdns);

        // Активное устройство — выбранное; если выбранного нет, первое подключённое.
        var active = list.FirstOrDefault(d => d.Serial.Equals(selectedSerial, StringComparison.OrdinalIgnoreCase))
                     ?? list.FirstOrDefault(d => d.IsOnline);

        // Одно устройство за раз: остальные сетевые подключения разрываем.
        if (active is { IsOnline: true })
        {
            var others = list.Where(d => d.IsConnected && d.IsWireless && d != active).ToList();
            if (others.Count > 0)
            {
                foreach (var d in others) await _adb.DisconnectAsync(d.Serial);
                list = BuildDeviceList(await _adb.GetDevicesAsync(quiet: true), mdns);
                active = list.FirstOrDefault(d => d.Serial.Equals(active.Serial, StringComparison.OrdinalIgnoreCase));
            }
        }

        static string Key(DeviceInfo d) => $"{d.Serial}|{d.State}|{d.Model}|{d.Kind}|{d.Type}";
        if (!list.Select(Key).SequenceEqual(Devices.Select(Key)))
        {
            _rebuildingDevices = true;
            try
            {
                Devices.Clear();
                foreach (var d in list) Devices.Add(d);
            }
            finally
            {
                _rebuildingDevices = false;
            }
            DevicesList.SelectedItem = Devices.FirstOrDefault(d => d.Serial == active?.Serial);
            await OnDeviceChangedAsync();
        }
        else if (SelectedDevice?.Serial != active?.Serial)
        {
            DevicesList.SelectedItem = Devices.FirstOrDefault(d => d.Serial == active?.Serial);
        }

        NoDevicesText.Visibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Подключённые устройства, затем сопряжённые и найденные в сети, которые сейчас не подключены.</summary>
    private List<DeviceInfo> BuildDeviceList(IReadOnlyList<DeviceInfo> adbDevices, List<(string Name, string Address, bool Tls)>? mdns)
    {
        var list = new List<DeviceInfo>();
        var connectedAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectedSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in adbDevices)
        {
            var tlsSerial = DeviceInfo.SerialFromServiceName(d.Serial);
            var paired = tlsSerial is not null || _settings.PairedDevices.Any(p => p.Host == d.Host && p.Address == d.Serial);
            var key = d.Kind == DeviceKind.Usb ? d.Serial : d.Host ?? tlsSerial;
            list.Add(d.WithKindAndType(paired ? DeviceKind.Paired : d.Kind, TypeFor(tlsSerial ?? d.Serial, key)));

            if (d.Host is { } host) connectedAddresses.Add(d.Serial);
            if (tlsSerial is not null) connectedSerials.Add(tlsSerial);
            RememberName(key, d.Model);

            // Подключённое по адресу остаётся в списке и после отключения.
            if (d.Host is not null && !paired && _found.All(f => f.Address != d.Serial))
                _found.Add(new NetworkDevice(d.Serial, null, false));
        }

        foreach (var p in _settings.PairedDevices)
        {
            if (p.Serial is not null && connectedSerials.Contains(p.Serial)) continue;
            if (p.Address is not null && connectedAddresses.Contains(p.Address)) continue;
            var seen = mdns is null || mdns.Any(m => m.Tls && (m.Address.StartsWith(p.Host + ":", StringComparison.Ordinal) ||
                                                               DeviceInfo.SerialFromServiceName(m.Name) == p.Serial));
            list.Add(DeviceInfo.Available(p.Address ?? p.Host, NameFor(p.Serial, p.Host), DeviceKind.Paired,
                TypeFor(p.Serial, p.Host), missing: !seen));
        }

        foreach (var f in _found)
        {
            if (connectedAddresses.Contains(f.Address) || (f.Serial is not null && connectedSerials.Contains(f.Serial))) continue;
            if (list.Any(d => d.Serial == f.Address)) continue;
            var host = f.Address.Split(':')[0];
            list.Add(DeviceInfo.Available(f.Address, NameFor(f.Serial, host), f.Tls ? DeviceKind.Paired : DeviceKind.Network,
                TypeFor(f.Serial, host)));
        }
        return list;
    }

    private DeviceType TypeFor(string? serial, string? host) =>
        serial is not null && _settings.DeviceTypes.TryGetValue(serial, out var t) ? t
        : host is not null && _settings.DeviceTypes.TryGetValue(host, out t) ? t
        : DeviceType.Unknown;

    /// <summary>Запоминает тип по ключам устройства; true — если что-то изменилось.</summary>
    private bool RememberType(DeviceType type, params string?[] keys)
    {
        var changed = false;
        foreach (var key in keys)
        {
            if (string.IsNullOrEmpty(key) || _settings.DeviceTypes.GetValueOrDefault(key) == type) continue;
            _settings.DeviceTypes[key] = type;
            changed = true;
        }
        if (changed) _settings.Save();
        return changed;
    }

    private string? NameFor(string? serial, string? host) =>
        (serial is not null ? _settings.DeviceNames.GetValueOrDefault(serial) : null)
        ?? (host is not null ? _settings.DeviceNames.GetValueOrDefault(host) : null)
        ?? serial;

    private void RememberName(string? key, string? model)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(model)) return;
        if (_settings.DeviceNames.GetValueOrDefault(key) == model) return;
        _settings.DeviceNames[key] = model;
        _settings.Save();
    }

    /// <summary>Порт беспроводной отладки меняется при каждом включении — берём свежий из mDNS.</summary>
    private void UpdatePairedAddresses(List<(string Name, string Address, bool Tls)>? mdns)
    {
        if (mdns is null) return;
        var changed = false;
        foreach (var p in _settings.PairedDevices)
        {
            var m = mdns.FirstOrDefault(x => x.Tls && (DeviceInfo.SerialFromServiceName(x.Name) is { } s && s == p.Serial ||
                                                       p.Serial is null && x.Address.StartsWith(p.Host + ":", StringComparison.Ordinal)));
            if (m.Address is null) continue;
            if (p.Address != m.Address || p.Serial is null)
            {
                p.Address = m.Address;
                p.Serial ??= DeviceInfo.SerialFromServiceName(m.Name);
                p.Host = m.Address.Split(':')[0];
                changed = true;
            }
        }
        if (changed) _settings.Save();
    }

    private async Task PollDevicesAsync()
    {
        // Во время подключения и других операций список обновит сама операция.
        if (_polling || _busyCount > 0 || _adb.AdbPath is null) return;
        _polling = true;
        try { await RefreshDevicesAsync(quiet: true); }
        catch { /* фоновый опрос не должен мешать */ }
        finally { _polling = false; }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) =>
        await RunBusyAsync(() => RefreshDevicesAsync());

    private async void DevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_rebuildingDevices) await OnDeviceChangedAsync();
    }

    /// <summary>Клик: неподключённое устройство — подключаемся, подключённое — делаем активным.</summary>
    private async void DevicesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DeviceInfo device) return;
        // С IsItemClickEnabled клик сам не выделяет элемент.
        DevicesList.SelectedItem = device;

        if (!device.IsConnected)
        {
            await ConnectToAsync(device.Serial, device.Kind == DeviceKind.Paired);
            return;
        }
        if (Devices.Any(d => d.IsConnected && d.IsWireless && d != device))
            await RunBusyAsync(() => RefreshDevicesAsync(select: device.Serial));
    }

    private async Task OnDeviceChangedAsync()
    {
        var device = SelectedDevice;
        var online = device?.IsOnline == true;

        InstallButton.IsEnabled = online;
        RefreshPackagesButton.IsEnabled = online;
        RestoreButton.IsEnabled = online;
        RestoreRemovedButton.IsEnabled = online;
        PrivateDnsButton.IsEnabled = online;
        OpenBackupsButton.IsEnabled = online;
        CheckDriftButton.IsEnabled = online;
        PrivacyAuditButton.IsEnabled = online;
        UpdateLauncherButton();
        ScreenshotButton.IsEnabled = online;
        EnableWifiButton.IsEnabled = online && !device!.IsWireless;
        var forget = device is { IsConnected: false, Kind: DeviceKind.Paired };
        DisconnectButton.IsEnabled = device is { IsConnected: true, IsWireless: true } || forget;
        DisconnectButtonText.Text = forget ? "Забыть" : "Отключить";

        // Тот же выбор после обновления списка — приложения не перечитываем.
        var key = device is null ? null : $"{device.Serial}|{device.State}";
        if (key == _shownDeviceKey) return;
        _shownDeviceKey = key;

        // Другое устройство — сразу очищаем список приложений прежнего.
        CancelMetadataLoading();
        _allPackages = [];
        ApplyFilter();
        PackagesHeader.Text = device is null ? "Приложения" : $"Приложения — {device.DisplayName}";

        if (online)
        {
            await LoadPackagesAsync();
        }
        else
        {
            EmptyPackagesText.Text = device switch
            {
                null => "Выберите устройство слева",
                { IsConnected: false } => "Устройство не подключено — нажмите на него в списке слева",
                _ => $"Устройство недоступно: {device.StateText}",
            };
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
        await ConnectToAsync(address, paired: false);
    }

    /// <summary>
    /// Подключение по адресу. Список приложений прежнего устройства очищается, остальные
    /// сетевые подключения разрываются (см. <see cref="RefreshDevicesAsync"/>).
    /// </summary>
    private async Task ConnectToAsync(string address, bool paired)
    {
        await RunBusyAsync(async () =>
        {
            ShowStatus(InfoBarSeverity.Informational, "Подключение…", address);
            var r = await _adb.ConnectAsync(address);
            var ok = r.Output.Contains("connected to", StringComparison.OrdinalIgnoreCase);
            var message = r.Text;
            if (!ok && NetworkScanner.GetVirtualRouteAdapter(address) is { } vpn)
            {
                message += Environment.NewLine + $"Трафик к устройству идёт через «{vpn}» — VPN на компьютере закрывает доступ " +
                           "к домашней сети. Выключите его или разрешите в нём доступ к локальной сети.";
            }
            else if (!ok && paired)
            {
                message += Environment.NewLine + "Включите на телефоне «Беспроводную отладку». Если не поможет — выполните сопряжение по коду заново.";
            }
            ShowStatus(ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                ok ? "Подключено" : "Не удалось подключиться", message);

            if (ok && !paired)
            {
                _settings.LastConnectAddress = address;
                _settings.Save();
            }
            await RefreshDevicesAsync(select: ok ? address : null);
        });
    }

    /// <summary>
    /// Поиск устройств с отладкой по сети: скан порта 5555 в домашних подсетях (как в ЧистомТВ)
    /// и устройства с беспроводной отладкой Android 11+, которые adb видит по mDNS. Найденные
    /// добавляются в список устройств.
    /// </summary>
    private async void FindInNetwork_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            ShowStatus(InfoBarSeverity.Informational, "Поиск в сети…", "Проверяю порт 5555 в домашней сети и беспроводную отладку (mDNS)");
            var scanTask = NetworkScanner.ScanAsync();
            var mdnsTask = _adb.GetMdnsDevicesAsync();
            await Task.WhenAll(scanTask, mdnsTask);

            var found = new List<NetworkDevice>();
            // Имя mDNS-сервиса вида adb-<серийный номер>-xxxx помогает узнать устройство.
            foreach (var (name, address, tls) in mdnsTask.Result.DistinctBy(m => m.Address))
                found.Add(new NetworkDevice(address, DeviceInfo.SerialFromServiceName(name), tls));
            foreach (var ip in scanTask.Result.Where(ip => found.All(f => !f.Address.StartsWith(ip + ":", StringComparison.Ordinal))))
                found.Add(new NetworkDevice($"{ip}:5555", null, false));

            _found.Clear();
            _found.AddRange(found);
            await RefreshDevicesAsync(quiet: true);

            if (found.Count == 0)
            {
                ShowStatus(InfoBarSeverity.Warning, "Устройства не найдены",
                    "Проверьте, что устройство в той же Wi-Fi сети и на нём включена отладка по сети (или «Беспроводная отладка» на Android 11+). " +
                    "Для беспроводной отладки без сопряжения используйте «Сопряжение по коду». VPN на компьютере может закрывать доступ к домашней сети.");
            }
            else
            {
                ShowStatus(InfoBarSeverity.Success, $"Найдено устройств: {found.Count}",
                    "Они добавлены в список слева — нажмите на устройство, чтобы подключиться.");
            }
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

        string? address = null;
        await RunBusyAsync(async () =>
        {
            var r = await _adb.PairAsync(pairAddress, code);
            if (!r.Output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            {
                ShowStatus(InfoBarSeverity.Error, "Сопряжение не удалось", r.Text);
                return;
            }

            // Запоминаем устройство; адрес подключения и серийный номер уточняем по mDNS.
            var host = pairAddress.Split(':')[0];
            await Task.Delay(1000);
            var m = (await _adb.GetMdnsDevicesAsync()).FirstOrDefault(x => x.Tls && x.Address.StartsWith(host + ":", StringComparison.Ordinal));
            address = connectAddress.Length > 0 ? connectAddress : m.Address;

            var paired = _settings.PairedDevices.FirstOrDefault(p => p.Host == host);
            if (paired is null) _settings.PairedDevices.Add(paired = new PairedDevice { Host = host });
            paired.Address = address ?? paired.Address;
            paired.Serial = DeviceInfo.SerialFromServiceName(m.Name) ?? paired.Serial;
            _settings.Save();

            if (address is null)
            {
                ShowStatus(InfoBarSeverity.Success, "Сопряжение выполнено",
                    "Устройство появится в списке, когда телефон объявит адрес подключения. Если нет — введите адрес с экрана «Беспроводной отладки».");
                await RefreshDevicesAsync();
            }
        });

        if (address is not null) await ConnectToAsync(address, paired: true);
    }

    private async void EnableWifi_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device) return;

        string? address = null;
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

            address = ip + ":5555";
            ConnectAddressBox.Text = address;
            await Task.Delay(2000); // adbd перезапускается на телефоне
        });

        if (address is null) return;
        await ConnectToAsync(address, paired: false);
        if (SelectedDevice?.Serial == address)
            StatusBar.Message += Environment.NewLine + "Теперь USB-кабель можно отключить.";
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device) return;
        await RunBusyAsync(async () =>
        {
            if (device.IsConnected)
            {
                await _adb.DisconnectAsync(device.Serial);
            }
            else if (device.Kind == DeviceKind.Paired)
            {
                // Забыть: убираем из списка. Ключ сопряжения на телефоне удаляется там же,
                // в «Беспроводной отладке» → «Сопряжённые устройства».
                _settings.PairedDevices.RemoveAll(p => p.Address == device.Serial || p.Host == device.Serial);
                _settings.Save();
                _found.RemoveAll(f => f.Address == device.Serial);
            }
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
        await ApplyAdviceAsync(device, packages);
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

    /// <summary>Свойства устройства (кэшируются на время работы программы).</summary>
    private async Task<DeviceProps> GetPropsAsync(DeviceInfo device)
    {
        if (!_deviceProps.TryGetValue(device.Serial, out var props))
        {
            _deviceProps[device.Serial] = props = await _adb.GetDevicePropsAsync(device.Serial);
            RememberName(props.SerialNo, device.Model); // чтобы узнать устройство среди найденных по mDNS
            var key = device.Kind == DeviceKind.Usb ? device.Serial : device.Host ?? DeviceInfo.SerialFromServiceName(device.Serial);
            RememberType(props.Type, props.SerialNo, key);
            if (device.Type != props.Type) ReplaceDeviceType(device, props.Type);
        }
        return props;
    }

    /// <summary>Тип стал известен после подключения — обновляем иконку в списке, не перечитывая приложения.</summary>
    private void ReplaceDeviceType(DeviceInfo device, DeviceType type)
    {
        var i = Devices.IndexOf(device);
        if (i < 0) return;
        var wasSelected = SelectedDevice == device;
        var updated = device.WithKindAndType(device.Kind, type);
        _rebuildingDevices = true;
        try
        {
            Devices[i] = updated;
            if (wasSelected) DevicesList.SelectedItem = updated;
        }
        finally
        {
            _rebuildingDevices = false;
        }
        UpdateLauncherButton();
    }

    /// <summary>Projectivy — лаунчер для Android TV: на телефонах и планшетах пункт неактивен.</summary>
    private void UpdateLauncherButton()
    {
        var device = SelectedDevice;
        var phone = device?.Type is DeviceType.Phone or DeviceType.Tablet;
        InstallLauncherButton.IsEnabled = device?.IsOnline == true && !phone;
        ToolTipService.SetToolTip(InstallLauncherButton, phone
            ? "Projectivy Launcher — главный экран для Android TV; на телефонах и планшетах не устанавливается"
            : "Скачать с GitHub и установить Projectivy Launcher — главный экран для Android TV без рекламы");
    }

    /// <summary>Журнал изменений устройства — по стабильному ключу (модель + серийный номер).</summary>
    private async Task<ChangeJournal> GetJournalAsync(DeviceInfo device)
    {
        var props = await GetPropsAsync(device);
        return new ChangeJournal(string.IsNullOrEmpty(props.SerialNo) ? device.Serial : props.Key);
    }

    /// <summary>Советы из базы ЧистогоТВ и защита критичных пакетов; платформа определяется по всем пакетам.</summary>
    private async Task ApplyAdviceAsync(DeviceInfo device, IReadOnlyList<PackageInfo> packages)
    {
        var props = await GetPropsAsync(device);
        var installed = await _adb.GetInstalledNamesAsync(device.Serial);
        var platforms = _advisor.DetectPlatforms(installed, props.Manufacturer, props.Brand);
        var ids = platforms.Select(p => p.Id).ToList();
        foreach (var p in packages) _advisor.Apply(p, ids);

        // Защита по состоянию устройства (идеи CleanYourTV): единственный главный экран и входы ТВ.
        var (home, homeHasAlternative, tvInputs) = await _adb.GetGuardsAsync(device.Serial);
        foreach (var p in packages)
        {
            if (p.Name == home && !homeHasAlternative)
            {
                p.SetAdvice(PackageAdvice.Protected, "Главный экран",
                    "Единственное приложение главного экрана: без него устройству нечего показать по кнопке Home. " +
                    "Установите и выберите другой лаунчер — тогда защита снимется.");
            }
            else if (p.IsSystem && tvInputs.Contains(p.Name))
            {
                p.SetAdvice(PackageAdvice.Protected, "Входы ТВ (HDMI / AV / тюнер)",
                    "Обслуживает входы телевизора. Если отключить, вход видит кабель, но показывает «Нет сигнала».");
            }
        }

        if (props.AdbSecure == "0" && _insecureWarned.Add(device.Serial))
        {
            ShowStatus(InfoBarSeverity.Warning, "Отладка по сети без подтверждения",
                $"{device.DisplayName} сообщает ro.adb.secure=0: порт adb 5555 даёт полный доступ к устройству любому в сети, " +
                "без запроса «Разрешить отладку?». Закончив, выключите на устройстве отладку по USB и по сети и перезагрузите его.");
        }

        var title = $"Приложения — {device.DisplayName}";
        if (platforms.Count > 0) title += " · " + string.Join(", ", platforms.Select(p => p.Name));
        PackagesHeader.Text = title;
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
            .ToList();
        filtered = SortPackages(filtered);

        // Отметки сохраняются при фильтрации и пересортировке (у скрытых фильтром — снимаются).
        var selected = PackagesList.SelectedItems.OfType<PackageInfo>().ToHashSet();
        _bulkSelecting = true;
        try
        {
            Packages.Clear();
            foreach (var p in filtered) Packages.Add(p);
            foreach (var p in filtered.Where(selected.Contains)) PackagesList.SelectedItems.Add(p);
        }
        finally
        {
            _bulkSelecting = false;
        }

        EmptyPackagesPanel.Visibility = Packages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PackagesHeaderRow.Visibility = Packages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSortArrows();
        OnMarkedChanged();
    }

    // ----- Сортировка -----

    private enum SortColumn { Name, Package, Status, Advice, Kind, Size }

    private SortColumn _sortColumn = SortColumn.Name;
    private bool _sortDescending;

    private List<PackageInfo> SortPackages(List<PackageInfo> items)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<PackageInfo> sorted = _sortColumn switch
        {
            SortColumn.Package => Order(items, p => p.Name, StringComparer.OrdinalIgnoreCase),
            SortColumn.Status => Order(items, p => p.State, Comparer<PackageState>.Create((a, b) => StatusRank(a).CompareTo(StatusRank(b)))),
            SortColumn.Advice => Order(items, p => AdviceRank(p.Advice), Comparer<int>.Default),
            SortColumn.Kind => Order(items, p => p.IsSystem, Comparer<bool>.Default),
            // Приложения без данных о размере — всегда в конце.
            SortColumn.Size => items.OrderBy(p => p.TotalSize is null)
                .ThenBy(p => p.TotalSize ?? 0, _sortDescending ? Comparer<long>.Create((a, b) => b.CompareTo(a)) : Comparer<long>.Default),
            _ => Order(items, p => p.DisplayName, byName),
        };
        // Вторичная сортировка — по названию.
        return sorted.ThenBy(p => p.DisplayName, byName).ToList();

        IOrderedEnumerable<PackageInfo> Order<TKey>(IEnumerable<PackageInfo> source, Func<PackageInfo, TKey> key, IComparer<TKey> comparer) =>
            _sortDescending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);

        static int StatusRank(PackageState s) => s switch
        {
            PackageState.Enabled => 0,
            PackageState.Unused => 1,
            _ => 2,
        };

        // Сначала то, что советуют отключить, в конце — то, что трогать нельзя.
        static int AdviceRank(PackageAdvice a) => a switch
        {
            PackageAdvice.Disable => 0,
            PackageAdvice.Optional => 1,
            PackageAdvice.None => 2,
            PackageAdvice.Keep => 3,
            _ => 4,
        };
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<SortColumn>(tag, out var column)) return;

        if (column == _sortColumn)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            // Размер удобнее сразу смотреть от больших к маленьким.
            _sortDescending = column == SortColumn.Size;
        }

        _settings.SortColumn = _sortColumn.ToString();
        _settings.SortDescending = _sortDescending;
        _settings.Save();
        ApplyFilter();
    }

    private void UpdateSortArrows()
    {
        var arrows = new Dictionary<SortColumn, FontIcon>
        {
            [SortColumn.Name] = SortByNameArrow,
            [SortColumn.Package] = SortByPackageArrow,
            [SortColumn.Status] = SortByStatusArrow,
            [SortColumn.Advice] = SortByAdviceArrow,
            [SortColumn.Kind] = SortByKindArrow,
            [SortColumn.Size] = SortBySizeArrow,
        };
        foreach (var (column, arrow) in arrows)
            arrow.Glyph = column != _sortColumn ? "" : _sortDescending ? "\uE70D" : "\uE70E"; // вниз / вверх
    }

    private void UpdatePackagesCountText()
    {
        if (_allPackages.Count == 0)
        {
            PackagesCountText.Text = "";
            return;
        }

        var text = Packages.Count == _allPackages.Count
            ? $"Всего: {_allPackages.Count}"
            : $"Найдено: {Packages.Count} из {_allPackages.Count}";
        var frozen = _allPackages.Count(p => p.IsFrozen);
        var unused = _allPackages.Count(p => p.IsUnused);
        if (frozen > 0) text += $" · заморожено: {frozen}";
        if (unused > 0) text += $" · не используется: {unused}";
        var marked = PackagesList.SelectedItems.Count;
        if (marked > 0) text += $" · отмечено: {marked}";
        PackagesCountText.Text = text;
    }

    // ----- Отметка (чекбоксы) -----

    // Массовое изменение отметки: SelectionChanged срабатывает на каждый элемент — обновляем UI один раз в конце.
    private bool _bulkSelecting;

    private void SetMarked(IEnumerable<PackageInfo> toMark)
    {
        var set = toMark.ToHashSet();
        _bulkSelecting = true;
        try
        {
            foreach (var p in PackagesList.SelectedItems.OfType<PackageInfo>().Where(p => !set.Contains(p)).ToList())
                PackagesList.SelectedItems.Remove(p);
            var already = PackagesList.SelectedItems.OfType<PackageInfo>().ToHashSet();
            foreach (var p in Packages.Where(p => set.Contains(p) && !already.Contains(p)))
                PackagesList.SelectedItems.Add(p);
        }
        finally
        {
            _bulkSelecting = false;
        }
        OnMarkedChanged();
    }

    private void OnMarkedChanged()
    {
        UpdatePackageButtons();
        UpdatePackagesCountText();

        var marked = PackagesList.SelectedItems.Count;
        SelectAllCheckBox.IsChecked = marked == 0 ? false : marked == Packages.Count ? true : null;
    }

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        // Если отмечены не все — отмечаем все показанные, иначе снимаем отметку.
        var allMarked = Packages.Count > 0 && PackagesList.SelectedItems.Count == Packages.Count;
        SetMarked(allMarked ? [] : Packages);
    }

    private void Mark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        var marked = PackagesList.SelectedItems.OfType<PackageInfo>().ToHashSet();

        IEnumerable<PackageInfo> target = tag switch
        {
            "All" => Packages,
            "Enabled" => Packages.Where(p => p.State == PackageState.Enabled),
            "Frozen" => Packages.Where(p => p.IsFrozen),
            "Unused" => Packages.Where(p => p.IsUnused),
            "User" => Packages.Where(p => !p.IsSystem),
            "System" => Packages.Where(p => p.IsSystem),
            "Recommended" => Packages.Where(p => p.IsAdviceDisable && p.State == PackageState.Enabled),
            "Invert" => Packages.Where(p => !marked.Contains(p)),
            _ => [],
        };
        SetMarked(target);

        if (tag == "Recommended")
        {
            var count = PackagesList.SelectedItems.Count;
            ShowStatus(count > 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success,
                count > 0 ? $"Рекомендуемых к отключению: {count}" : "Рекомендуемых к отключению нет",
                count > 0
                    ? "Проверьте список и нажмите «Заморозить». Пояснения — в подсказке колонки «Совет»."
                    : "Среди показанных включённых приложений не найдено известной рекламы и мусора. " +
                      "Включите «Системные», если они скрыты.");
        }
    }

    // ----- Пресеты отметки -----

    /// <summary>Пресет, который применится после перезагрузки списка с системными приложениями.</summary>
    private SelectionPreset? _pendingPreset;

    private static Microsoft.UI.Xaml.Media.Brush ThemeBrush(string key) =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    private SelectionPreset? FindPreset(string name) =>
        _settings.Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));

    private List<SelectionPreset> SortedPresets() =>
        _settings.Presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private void MarkFlyout_Opening(object sender, object e)
    {
        // Меню пресетов собирается при каждом открытии — список меняется в диалогах.
        PresetsMenu.Items.Clear();
        foreach (var preset in SortedPresets())
        {
            var item = new MenuFlyoutItem { Text = preset.Name, Tag = preset };
            ToolTipService.SetToolTip(item,
                (preset.Description.Length > 0 ? preset.Description : "Без описания") +
                Environment.NewLine + $"Приложений: {preset.Packages.Count}");
            item.Click += PresetItem_Click;
            PresetsMenu.Items.Add(item);
        }
        if (_settings.Presets.Count > 0) PresetsMenu.Items.Add(new MenuFlyoutSeparator());

        var save = new MenuFlyoutItem
        {
            Text = "Сохранить отмеченные как пресет…",
            Icon = new SymbolIcon(Symbol.Save),
            IsEnabled = PackagesList.SelectedItems.Count > 0,
        };
        save.Click += SavePreset_Click;
        PresetsMenu.Items.Add(save);

        var manage = new MenuFlyoutItem
        {
            Text = "Управление пресетами…",
            Icon = new SymbolIcon(Symbol.Edit),
            IsEnabled = _settings.Presets.Count > 0,
        };
        manage.Click += ManagePresets_Click;
        PresetsMenu.Items.Add(manage);

        PresetsMenu.Items.Add(new MenuFlyoutSeparator());
        var import = new MenuFlyoutItem { Text = "Импорт из файла…", Icon = new SymbolIcon(Symbol.OpenFile) };
        ToolTipService.SetToolTip(import, "Пресеты ADB Manager (*.json) или список имён пакетов (*.txt, по одному в строке)");
        import.Click += ImportPresets_Click;
        PresetsMenu.Items.Add(import);
    }

    private async Task ExportPresetsAsync(List<SelectionPreset> presets, string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = PresetFile.SafeFileName(suggestedName),
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        picker.FileTypeChoices.Add("Пресеты ADB Manager", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            await PresetFile.ExportAsync(file.Path, presets);
            ShowStatus(InfoBarSeverity.Success, $"Экспортировано пресетов: {presets.Count}", file.Path);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Не удалось сохранить файл", ex.Message);
        }
    }

    private async void ImportPresets_Click(object sender, RoutedEventArgs e)
    {
        var picker = CreateOpenPicker();
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".txt");
        var files = await picker.PickMultipleFilesAsync();
        if (files is not { Count: > 0 }) return;

        var added = new List<string>();
        var skipped = 0;
        var errors = new List<string>();
        foreach (var file in files)
        {
            List<SelectionPreset> imported;
            try
            {
                imported = await PresetFile.ImportAsync(file.Path);
            }
            catch (Exception ex)
            {
                errors.Add($"{file.Name}: {ex.Message}");
                continue;
            }
            if (imported.Count == 0) errors.Add($"{file.Name}: нет имён пакетов");

            foreach (var preset in imported)
            {
                if (preset.Name.Length == 0) preset.Name = Path.GetFileNameWithoutExtension(file.Name);
                // Тот же пресет уже есть — пропускаем; другой с тем же названием — добавляем с номером.
                if (FindPreset(preset.Name) is { } same && same.Packages.Order(StringComparer.Ordinal).SequenceEqual(preset.Packages))
                {
                    skipped++;
                    continue;
                }
                var name = preset.Name;
                for (var n = 2; FindPreset(name) is not null; n++) name = $"{preset.Name} ({n})";
                preset.Name = name;
                _settings.Presets.Add(preset);
                added.Add(name);
            }
        }
        if (added.Count > 0) _settings.Save();

        var lines = new List<string>();
        if (added.Count > 0) lines.Add("Добавлены: " + string.Join(", ", added.Select(n => $"«{n}»")));
        if (skipped > 0) lines.Add($"Уже были: {skipped}");
        lines.AddRange(errors);
        ShowStatus(errors.Count > 0 ? InfoBarSeverity.Warning : added.Count > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
            $"Импортировано пресетов: {added.Count}", string.Join(Environment.NewLine, lines));
    }

    private void PresetItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SelectionPreset preset }) ApplyPreset(preset);
    }

    private void ApplyPreset(SelectionPreset preset)
    {
        if (_allPackages.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "Список приложений пуст", "Сначала подключите устройство и дождитесь загрузки списка.");
            return;
        }

        var names = preset.Packages.ToHashSet(StringComparer.Ordinal);
        var loaded = _allPackages.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        // Пакетов нет в списке — возможно, это скрытые системные: показываем их и применяем пресет после загрузки.
        if (!ShowSystemToggle.IsOn && names.Any(n => !loaded.Contains(n)))
        {
            _pendingPreset = preset;
            ShowSystemToggle.IsOn = true;
            return;
        }

        // Поиск скрыл бы часть пакетов пресета.
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            ApplyFilter();
        }

        SetMarked(Packages.Where(p => names.Contains(p.Name)));

        var found = PackagesList.SelectedItems.Count;
        var missing = names.Where(n => !loaded.Contains(n)).Order(StringComparer.Ordinal).ToList();
        var message = preset.Description;
        if (missing.Count > 0)
        {
            message += (message.Length > 0 ? Environment.NewLine : "") +
                       $"Нет на устройстве ({missing.Count}): " + string.Join(", ", missing.Take(5)) +
                       (missing.Count > 5 ? $" и ещё {missing.Count - 5}" : "");
        }
        ShowStatus(found > 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Warning,
            $"Пресет «{preset.Name}»: отмечено {found} из {names.Count}", message);
    }

    private async void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var packages = PackagesList.SelectedItems.OfType<PackageInfo>()
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        if (packages.Count == 0) return;

        var nameBox = new TextBox { Header = "Название", PlaceholderText = "Например: Реклама Xiaomi" };
        var descriptionBox = new TextBox
        {
            Header = "Описание",
            PlaceholderText = "Что входит в пресет и для чего он нужен",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            MaxHeight = 160,
        };
        var replaceBar = new InfoBar
        {
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = "Пресет с таким названием уже есть",
            Message = "Его список приложений и описание будут заменены.",
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 400 };
        content.Children.Add(new TextBlock
        {
            Text = $"Отмеченных приложений: {packages.Count}. Пресет можно загрузить на любом устройстве: «Отметить → Пресеты».",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(nameBox);
        content.Children.Add(descriptionBox);
        content.Children.Add(replaceBar);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Новый пресет",
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            Content = content,
        };

        // При совпадении названия подставляем описание существующего пресета, если своё ещё не введено.
        var autoDescription = "";
        nameBox.TextChanged += (_, _) =>
        {
            var existing = FindPreset(nameBox.Text.Trim());
            dialog.IsPrimaryButtonEnabled = nameBox.Text.Trim().Length > 0;
            dialog.PrimaryButtonText = existing is null ? "Сохранить" : "Заменить";
            replaceBar.IsOpen = existing is not null;
            if (descriptionBox.Text == autoDescription)
                descriptionBox.Text = autoDescription = existing?.Description ?? "";
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        _settings.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        _settings.Presets.Add(new SelectionPreset
        {
            Name = name,
            Description = descriptionBox.Text.Trim(),
            Packages = packages,
            Created = DateTime.Now,
        });
        _settings.Save();
        ShowStatus(InfoBarSeverity.Success, "Пресет сохранён",
            $"«{name}» — приложений: {packages.Count}. Загрузить: «Отметить → Пресеты».");
    }

    private async void ManagePresets_Click(object sender, RoutedEventArgs e)
    {
        var rows = new List<(SelectionPreset Preset, TextBox Name, TextBox Description)>();
        var list = new StackPanel { Spacing = 8 };
        foreach (var preset in SortedPresets())
        {
            var nameBox = new TextBox { Text = preset.Name, PlaceholderText = "Название" };
            var descriptionBox = new TextBox
            {
                Text = preset.Description,
                PlaceholderText = "Описание",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 120,
            };
            var fields = new StackPanel { Spacing = 6 };
            fields.Children.Add(nameBox);
            fields.Children.Add(descriptionBox);
            fields.Children.Add(new TextBlock
            {
                Text = $"Приложений: {preset.Packages.Count} · создан {preset.Created:dd.MM.yyyy HH:mm}",
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                FontSize = 12,
            });

            var delete = new Button { Content = new SymbolIcon(Symbol.Delete) };
            ToolTipService.SetToolTip(delete, "Удалить пресет");
            var export = new Button { Content = new SymbolIcon(Symbol.Share) };
            ToolTipService.SetToolTip(export, "Экспорт в файл");
            export.Click += async (_, _) =>
            {
                // В файл — с правками из полей, даже если диалог ещё не сохранён.
                var copy = new SelectionPreset
                {
                    Name = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : preset.Name,
                    Description = descriptionBox.Text.Trim(),
                    Packages = preset.Packages,
                    Created = preset.Created,
                };
                await ExportPresetsAsync([copy], copy.Name);
            };
            var buttons = new StackPanel { Spacing = 6 };
            buttons.Children.Add(export);
            buttons.Children.Add(delete);

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(fields);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            var card = new Border
            {
                Child = grid,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            };
            var row = (preset, nameBox, descriptionBox);
            delete.Click += (_, _) =>
            {
                rows.Remove(row);
                list.Children.Remove(card);
            };
            rows.Add(row);
            list.Children.Add(card);
        }

        var errorBar = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error, Title = "Проверьте названия" };
        var content = new StackPanel { Spacing = 12, MinWidth = 440 };
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420 });
        content.Children.Add(errorBar);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Пресеты",
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var names = rows.Select(r => r.Name.Text.Trim()).ToList();
            string? error = names.Any(n => n.Length == 0) ? "Название пресета не может быть пустым."
                : names.GroupBy(n => n, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup
                    ? $"Название «{dup.Key}» повторяется."
                    : null;
            errorBar.Message = error ?? "";
            errorBar.IsOpen = error is not null;
            args.Cancel = error is not null;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var removed = _settings.Presets.Count - rows.Count;
        foreach (var (preset, nameBox, descriptionBox) in rows)
        {
            preset.Name = nameBox.Text.Trim();
            preset.Description = descriptionBox.Text.Trim();
        }
        _settings.Presets = rows.Select(r => r.Preset).ToList();
        _settings.Save();
        ShowStatus(InfoBarSeverity.Success, "Пресеты сохранены",
            $"Пресетов: {rows.Count}" + (removed > 0 ? $", удалено: {removed}" : "") + ".");
    }

    private void UpdatePackageButtons()
    {
        var selected = PackagesList.SelectedItems.OfType<PackageInfo>().ToList();
        var count = selected.Count;
        var online = SelectedDevice?.IsOnline == true;
        UninstallButton.IsEnabled = online && selected.Any(p => !IsLocked(p));
        FreezeButton.IsEnabled = online && selected.Any(p => p.State == PackageState.Enabled && !IsLocked(p));
        UnfreezeButton.IsEnabled = online && selected.Any(p => p.State != PackageState.Enabled);
        PullApkButton.IsEnabled = online && count > 0;
        CopyNameButton.IsEnabled = count > 0;
        CopyListButton.IsEnabled = Packages.Count > 0;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private async void ShowSystemToggle_Toggled(object sender, RoutedEventArgs e)
    {
        await LoadPackagesAsync();
        if (_pendingPreset is { } preset)
        {
            _pendingPreset = null;
            if (ShowSystemToggle.IsOn) ApplyPreset(preset);
        }
    }

    private async void RefreshPackages_Click(object sender, RoutedEventArgs e) => await LoadPackagesAsync();

    private async void PackagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bulkSelecting) return;
        OnMarkedChanged();

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

    /// <summary>
    /// Копирует показанный список приложений в виде Markdown-таблицы с запросом —
    /// чтобы вставить в чат с LLM и спросить, что можно удалить или заморозить.
    /// </summary>
    private async void CopyListForLlm_Click(object sender, RoutedEventArgs e)
    {
        var items = Packages.ToList();
        if (items.Count == 0) return;

        var device = SelectedDevice;
        string? android = null, manufacturer = null;
        if (device is { IsOnline: true })
        {
            try
            {
                android = await _adb.GetPropAsync(device.Serial, "ro.build.version.release");
                manufacturer = await _adb.GetPropAsync(device.Serial, "ro.product.manufacturer");
            }
            catch { /* без версии Android */ }
        }

        var phone = string.Join(" ", new[] { manufacturer, device?.DisplayName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Ниже список приложений на моём Android-смартфоне{(phone.Length > 0 ? " " + phone : "")}" +
                      $"{(android is null ? "" : $" (Android {android})")}.");
        sb.AppendLine("Подскажи, какие из них можно безопасно удалить или заморозить, а какие трогать нельзя. " +
                      "Для каждого рекомендуемого приложения кратко объясни, что оно делает и чем грозит его отключение. " +
                      "Сгруппируй ответ: «можно удалить», «лучше заморозить», «не трогать».");
        sb.AppendLine();
        sb.AppendLine("Пояснения к таблице:");
        sb.AppendLine("- Заморозка — `pm disable-user`: приложение не запускается, данные сохраняются, можно включить обратно.");
        sb.AppendLine("- Удаление системных приложений — только для текущего пользователя (`pm uninstall --user 0`), " +
                      "восстанавливается командой `cmd package install-existing`.");
        sb.AppendLine("- Статус «Заморожено» — отключено вручную; «Не используется» — Android сам остановил давно не используемое приложение.");
        sb.AppendLine("- Размер — приложение вместе с данными.");
        sb.AppendLine("- Совет — из базы проекта ЧистыйТВ (в основном Android TV), списка рекламных предустановок телефонов и встроенной защиты " +
                      "критичных системных пакетов; «Защищено» и «Не трогать» программа отключать не даёт.");
        sb.AppendLine();
        sb.AppendLine($"Приложений в списке: {items.Count}" +
                      (ShowSystemToggle.IsOn ? " (включая системные)" : " (только пользовательские)") +
                      (SearchBox.Text.Trim().Length > 0 ? $", фильтр: «{SearchBox.Text.Trim()}»" : "") + ".");
        sb.AppendLine();
        sb.AppendLine("| Название | Пакет | Тип | Статус | Совет | Размер | Расположение |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var p in items)
        {
            var advice = p.Advice == PackageAdvice.None ? "" : p.AdviceText +
                (string.IsNullOrWhiteSpace(p.AdviceNote) ? "" : ": " + p.AdviceNote);
            sb.AppendLine($"| {Md(p.DisplayName)} | `{p.Name}` | {p.KindText} | {(p.StatusText.Length > 0 ? p.StatusText : "Включено")} " +
                          $"| {Md(advice)} | {p.SizeText} | {Md(ApkLocation(p.ApkPath))} |");
        }

        var package = new DataPackage();
        package.SetText(sb.ToString());
        Clipboard.SetContent(package);
        ShowStatus(InfoBarSeverity.Success, "Список скопирован",
            $"Приложений: {items.Count}. Вставьте его в чат с ИИ (Ctrl+V).");

        static string Md(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

        // Каталог установки: /system/priv-app, /product/app, /data/app… — подсказка, насколько приложение системное.
        static string ApkLocation(string apkPath)
        {
            if (string.IsNullOrEmpty(apkPath)) return "";
            if (apkPath.StartsWith("/data/", StringComparison.Ordinal)) return "/data/app";
            var parts = apkPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"/{parts[0]}/{parts[1]}" : apkPath;
        }
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
        var marked = PackagesList.SelectedItems.OfType<PackageInfo>().ToList();
        var drivers = await FindTvInputDriversAsync(device, marked.Where(p => !IsLocked(p)).ToList());
        var selected = marked.Where(p => !IsLocked(p) && (_expertMode || !drivers.Contains(p))).ToList();
        if (selected.Count == 0)
        {
            ShowTvInputDriversSkipped(drivers);
            return;
        }

        var keepData = new CheckBox { Content = "Сохранить данные и кэш (-k)" };
        var content = new StackPanel { Spacing = 8 };
        AddSkippedProtectedInfo(content, marked.Where(IsLocked));
        if (!_expertMode) AddSkippedTvInputInfo(content, drivers);
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
                          "Вернуть можно через «… → Вернуть как было» или «Восстановить удалённые системные».",
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
        AddExpertRiskConfirmation(dialog, content, selected.Where(p => p.IsBlocked || drivers.Contains(p)).ToList());
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunBusyAsync(async () =>
        {
            var journal = await GetJournalAsync(device);
            ShowStatus(InfoBarSeverity.Informational, "Бэкап…", "Сохраняю состояние устройства перед изменениями");
            await journal.EnsureBackupAsync(_adb, device.Serial);

            var errors = new List<string>();
            var removed = new List<PackageInfo>();
            foreach (var pkg in selected)
            {
                ShowStatus(InfoBarSeverity.Informational, "Удаление…", pkg.Name);
                var r = await _adb.UninstallAsync(device.Serial, pkg, keepData.IsChecked == true);
                if (r.Success && r.Text.Contains("Success"))
                {
                    removed.Add(pkg);
                    journal.Add(new JournalEntry(DateTime.Now, "uninstall", pkg.Name, pkg.DisplayName, pkg.IsSystem));
                }
                else
                {
                    errors.Add($"{pkg.Name}: {LastLine(r.Text)}");
                }
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

    // ----- Заморозка -----

    private async void Freeze_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;
        var candidates = PackagesList.SelectedItems.OfType<PackageInfo>().Where(p => p.State == PackageState.Enabled).ToList();
        var drivers = await FindTvInputDriversAsync(device, candidates.Where(p => !IsLocked(p)).ToList());
        var selected = candidates.Where(p => !IsLocked(p) && (_expertMode || !drivers.Contains(p))).ToList();
        if (selected.Count == 0)
        {
            ShowTvInputDriversSkipped(drivers);
            return;
        }

        var content = new StackPanel { Spacing = 8 };
        AddSkippedProtectedInfo(content, candidates.Where(IsLocked));
        if (!_expertMode) AddSkippedTvInputInfo(content, drivers);
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Замороженное приложение не запускается, не получает уведомлений и пропадает из меню. " +
                   "Данные сохраняются — его можно разморозить в любой момент.",
        });
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 220,
            Content = new TextBlock
            {
                Text = string.Join(Environment.NewLine, selected.Select(p => $"{p.DisplayName}  ({p.Name})")),
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
                Message = "Заморозка важных системных компонентов может нарушить работу телефона " +
                          "(вплоть до циклической перезагрузки). Замораживайте только то, назначение чего знаете.",
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = selected.Count == 1 ? "Заморозить приложение?" : $"Заморозить приложения ({selected.Count})?",
            PrimaryButtonText = "Заморозить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            Content = content,
        };
        AddExpertRiskConfirmation(dialog, content, selected.Where(p => p.IsBlocked || drivers.Contains(p)).ToList());
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await SetFrozenAsync(selected, frozen: true);
    }

    private async void Unfreeze_Click(object sender, RoutedEventArgs e)
    {
        var selected = PackagesList.SelectedItems.OfType<PackageInfo>().Where(p => p.State != PackageState.Enabled).ToList();
        if (selected.Count > 0) await SetFrozenAsync(selected, frozen: false);
    }

    private async Task SetFrozenAsync(List<PackageInfo> packages, bool frozen)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;

        await RunBusyAsync(async () =>
        {
            var journal = await GetJournalAsync(device);
            if (frozen)
            {
                ShowStatus(InfoBarSeverity.Informational, "Бэкап…", "Сохраняю состояние устройства перед изменениями");
                await journal.EnsureBackupAsync(_adb, device.Serial);
            }

            var errors = new List<string>();
            var changed = new List<PackageInfo>();
            foreach (var pkg in packages)
            {
                AdbResult r;
                if (!frozen && pkg.IsUnused)
                {
                    // Остановленное системой приложение включается только запуском.
                    ShowStatus(InfoBarSeverity.Informational, "Включение (запуск на телефоне)…", pkg.DisplayName);
                    r = await _adb.WakeAsync(device.Serial, pkg.Name);
                }
                else
                {
                    ShowStatus(InfoBarSeverity.Informational, frozen ? "Заморозка…" : "Разморозка…", pkg.DisplayName);
                    r = await _adb.SetFrozenAsync(device.Serial, pkg.Name, frozen);
                }

                if (r.Success)
                {
                    pkg.State = frozen ? PackageState.Frozen : PackageState.Enabled;
                    changed.Add(pkg);
                    journal.Add(new JournalEntry(DateTime.Now, frozen ? "freeze" : "enable", pkg.Name, pkg.DisplayName, pkg.IsSystem));
                }
                else
                {
                    errors.Add($"{pkg.Name}: {LastLine(r.Text)}");
                }
            }

            UpdatePackagesCountText();
            UpdatePackageButtons();

            var verb = frozen ? "Заморожено" : "Включено";
            if (errors.Count == 0)
                ShowStatus(InfoBarSeverity.Success, verb, string.Join(", ", changed.Select(p => p.DisplayName)));
            else
                ShowStatus(InfoBarSeverity.Error, $"{verb}: {changed.Count}, ошибок: {errors.Count}",
                    string.Join(Environment.NewLine, errors));
        });
    }

    /// <summary>Сообщение в диалоге о пакетах, которые пропущены из-за защиты.</summary>
    private static void AddSkippedProtectedInfo(StackPanel content, IEnumerable<PackageInfo> skipped)
    {
        var list = skipped.ToList();
        if (list.Count == 0) return;
        content.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = $"Пропущено защищённых: {list.Count}",
            Message = "Эти пакеты не будут затронуты — без них устройство может перестать работать: " +
                      string.Join(", ", list.Select(p => p.DisplayName)),
        });
    }

    // ----- Экспертный режим -----

    private bool _expertMode;

    /// <summary>Пакет заблокирован защитой (в экспертном режиме — ничего).</summary>
    private bool IsLocked(PackageInfo p) => p.IsBlocked && !_expertMode;

    private async void ExpertMode_Click(object sender, RoutedEventArgs e)
    {
        if (ExpertModeButton.IsChecked != true)
        {
            SetExpertMode(false);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Включить экспертный режим?",
            PrimaryButtonText = "Включить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                Text = "Защита будет снята: можно заморозить и удалить любые системные приложения — системный интерфейс, " +
                       "телефонию, Wi-Fi, установщик, Google Play, главный экран, входы ТВ." + Environment.NewLine + Environment.NewLine +
                       "Удаление системного приложения выполняется только для текущего пользователя (pm uninstall --user 0): " +
                       "оно остаётся в прошивке, и его можно вернуть через «Вернуть как было» или «Восстановить удалённые системные». " +
                       "Но без критичного компонента устройство может зависнуть на загрузке, потерять сеть или экран — тогда " +
                       "вернуть пакет можно по adb (он обычно работает и при зависшем интерфейсе), в крайнем случае поможет только сброс к заводским настройкам." +
                       Environment.NewLine + Environment.NewLine +
                       "Режим действует до закрытия программы.",
            },
        };
        SetExpertMode(await dialog.ShowAsync() == ContentDialogResult.Primary);
    }

    private void DisableExpertMode_Click(object sender, RoutedEventArgs e) => SetExpertMode(false);

    private void SetExpertMode(bool on)
    {
        _expertMode = on;
        ExpertModeButton.IsChecked = on;
        ExpertModeBar.IsOpen = on;
        UpdatePackageButtons();
    }

    /// <summary>
    /// Экспертный режим: в списке есть защищённые пакеты — предупреждение и галочка подтверждения,
    /// без которой кнопка действия неактивна.
    /// </summary>
    private static void AddExpertRiskConfirmation(ContentDialog dialog, StackPanel content, IReadOnlyCollection<PackageInfo> risky)
    {
        if (risky.Count == 0) return;
        content.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
            Title = $"Экспертный режим: защищённых пакетов — {risky.Count}",
            Message = string.Join(", ", risky.Select(p => p.DisplayName)) + ". Без них устройство может перестать загружаться, " +
                      "потерять сеть, экран, звук или входы ТВ. Вернуть: «… → Вернуть как было» или «Восстановить удалённые системные».",
        });
        var confirm = new CheckBox { Content = "Я понимаю риск и знаю, что делаю" };
        confirm.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        confirm.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        content.Children.Add(confirm);
        dialog.IsPrimaryButtonEnabled = false;
    }

    private const string TvInputDriversMessage =
        "Запрашивают android.permission.TV_INPUT_HARDWARE — это программная часть входов HDMI/AV/тюнера. " +
        "Если их отключить, вход видит кабель, но показывает «Нет сигнала», и картинку возвращает только " +
        "полное восстановление и холодная перезагрузка.";

    /// <summary>Системные пакеты из списка, которые обслуживают входы ТВ (идея CleanYourTV), — их не трогаем.</summary>
    private async Task<List<PackageInfo>> FindTvInputDriversAsync(DeviceInfo device, IReadOnlyCollection<PackageInfo> packages)
    {
        var system = packages.Where(p => p.IsSystem).Select(p => p.Name).ToList();
        if (system.Count == 0) return [];

        HashSet<string> found = [];
        await RunBusyAsync(async () =>
        {
            ShowStatus(InfoBarSeverity.Informational, "Проверка…", "Проверяю, не обслуживают ли пакеты входы ТВ (HDMI/AV)");
            found = await _adb.GetTvInputHardwarePackagesAsync(device.Serial, system);
            StatusBar.IsOpen = false;
        });
        return packages.Where(p => found.Contains(p.Name)).ToList();
    }

    private static void AddSkippedTvInputInfo(StackPanel content, IReadOnlyCollection<PackageInfo> skipped)
    {
        if (skipped.Count == 0) return;
        content.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = $"Пропущено — входы ТВ: {skipped.Count}",
            Message = string.Join(", ", skipped.Select(p => p.DisplayName)) + ". " + TvInputDriversMessage,
        });
    }

    private void ShowTvInputDriversSkipped(IReadOnlyCollection<PackageInfo> skipped)
    {
        if (skipped.Count > 0)
            ShowStatus(InfoBarSeverity.Warning, "Пропущено — входы ТВ",
                string.Join(", ", skipped.Select(p => p.DisplayName)) + ". " + TvInputDriversMessage);
    }

    // ----- Откат изменений (идея ЧистогоТВ) -----

    /// <summary>Диалог со списком-чекбоксами (всё отмечено). Возвращает выбранное или null при отмене.</summary>
    private async Task<List<T>?> ShowChecklistAsync<T>(string title, string intro, IReadOnlyList<(string Text, T Value)> items, string primary)
    {
        var texts = items.Select(i => i.Text).ToList();
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, ItemsSource = texts, MaxHeight = 360 };
        var content = new StackPanel { Spacing = 8, MinWidth = 520 };
        content.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(list);
        list.Loaded += (_, _) => list.SelectAll();

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            PrimaryButtonText = primary,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var chosen = list.SelectedItems.OfType<string>().ToHashSet();
        return items.Where(i => chosen.Contains(i.Text)).Select(i => i.Value).ToList();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;

        ChangeJournal journal;
        List<JournalEntry> restorable;
        try
        {
            journal = await GetJournalAsync(device);
            restorable = journal.GetRestorable();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Ошибка", ex.Message);
            return;
        }

        if (restorable.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "Нечего возвращать",
                "В журнале нет изменений этой программы, которые можно отменить.");
            return;
        }

        static string Describe(JournalEntry e) => e.Action switch
        {
            "freeze" => $"Разморозить: {e.Label} ({e.Package}) — заморожено {e.Time:dd.MM HH:mm}",
            "uninstall" => $"Вернуть: {e.Label} ({e.Package}) — удалено {e.Time:dd.MM HH:mm}",
            "dns" => $"Частный DNS: вернуть прежнюю настройку ({DescribeDns(e.OldValue)}) — изменено {e.Time:dd.MM HH:mm}",
            "home" => $"Главный экран: вернуть {e.OldValue?.Split('/')[0] ?? "прежний"} — изменено {e.Time:dd.MM HH:mm}",
            "install" => $"Удалить установленное программой: {e.Label} ({e.Package}) — установлено {e.Time:dd.MM HH:mm}",
            _ => e.Package,
        };

        var chosen = await ShowChecklistAsync("Вернуть как было",
            "Изменения, сделанные этой программой на устройстве. Снимите отметку с того, что возвращать не нужно.",
            restorable.Select(r => (Describe(r), r)).ToList(), "Вернуть");
        if (chosen is not { Count: > 0 }) return;

        await RunBusyAsync(async () =>
        {
            var errors = new List<string>();
            int ok = 0;
            // Сначала главный экран — потом можно удалять лаунчер, который им был.
            foreach (var entry in chosen.OrderBy(c => c.Action == "home" ? 0 : 1))
            {
                ShowStatus(InfoBarSeverity.Informational, "Возвращаю…", entry.Label ?? entry.Package);
                AdbResult r = entry.Action switch
                {
                    "freeze" => await _adb.SetFrozenAsync(device.Serial, entry.Package, frozen: false),
                    "uninstall" => await _adb.InstallExistingAsync(device.Serial, entry.Package),
                    "dns" => await RestoreDnsAsync(device, entry.OldValue),
                    "home" => string.IsNullOrEmpty(entry.OldValue)
                        ? new AdbResult(1, "", "прежний главный экран неизвестен — выберите его на устройстве")
                        : await _adb.SetHomeActivityAsync(device.Serial, entry.OldValue),
                    "install" => await _adb.UninstallAsync(device.Serial, new PackageInfo(entry.Package, false, ""), keepData: false),
                    _ => new AdbResult(1, "", "неизвестное действие"),
                };
                if (r.Success)
                {
                    ok++;
                    journal.Add(new JournalEntry(DateTime.Now, "restore", entry.Package, entry.Label, entry.System));
                }
                else
                {
                    errors.Add($"{entry.Package}: {LastLine(r.Text)}");
                }
            }

            await ReloadPackagesCoreAsync(device);
            if (errors.Count == 0)
                ShowStatus(InfoBarSeverity.Success, "Возвращено", $"Изменений отменено: {ok}. Перезагрузите устройство, если что-то работало неправильно.");
            else
                ShowStatus(InfoBarSeverity.Error, $"Возвращено: {ok}, ошибок: {errors.Count}", string.Join(Environment.NewLine, errors));
        });
    }

    private async void RestoreRemoved_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;

        List<PackageInfo> removed = [];
        await RunBusyAsync(async () => removed = await _adb.GetRemovedSystemPackagesAsync(device.Serial));
        if (removed.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "Удалённых системных приложений нет",
                "Все системные приложения установлены для текущего пользователя.");
            return;
        }

        // Названия — из кэша иконок, если приложение уже встречалось.
        foreach (var p in removed)
            if (_metadata.LoadCached(device.Serial, p) is { } meta) p.Apply(meta);

        var chosen = await ShowChecklistAsync("Восстановить удалённые системные приложения",
            "Эти системные приложения удалены для текущего пользователя (pm uninstall --user 0), но остались в прошивке. " +
            "Отмеченные будут возвращены командой cmd package install-existing.",
            removed.Select(p => (p.Label is null ? p.Name : $"{p.Label} ({p.Name})", p)).ToList(), "Восстановить");
        if (chosen is not { Count: > 0 }) return;

        await RunBusyAsync(async () =>
        {
            var journal = await GetJournalAsync(device);
            var errors = new List<string>();
            int ok = 0;
            foreach (var p in chosen)
            {
                ShowStatus(InfoBarSeverity.Informational, "Восстановление…", p.DisplayName);
                var r = await _adb.InstallExistingAsync(device.Serial, p.Name);
                if (r.Success)
                {
                    ok++;
                    journal.Add(new JournalEntry(DateTime.Now, "restore", p.Name, p.DisplayName, true));
                }
                else
                {
                    errors.Add($"{p.Name}: {LastLine(r.Text)}");
                }
            }

            await ReloadPackagesCoreAsync(device);
            if (errors.Count == 0)
                ShowStatus(InfoBarSeverity.Success, "Восстановлено", $"Приложений: {ok}");
            else
                ShowStatus(InfoBarSeverity.Error, $"Восстановлено: {ok}, ошибок: {errors.Count}", string.Join(Environment.NewLine, errors));
        });
    }

    private async void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device) return;
        try
        {
            var journal = await GetJournalAsync(device);
            Directory.CreateDirectory(journal.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(journal.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Не удалось открыть папку", ex.Message);
        }
    }

    // ----- После обновления прошивки (идея CleanYourTV) -----

    /// <summary>
    /// Обновление прошивки ставится поверх: замороженные и удалённые системные приложения возвращаются.
    /// Сравниваем журнал с устройством и предлагаем повторить то, что откатилось.
    /// </summary>
    private async void CheckDrift_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;

        ChangeJournal? journal = null;
        List<JournalEntry> drifted = [];
        var appliedCount = 0;
        await RunBusyAsync(async () =>
        {
            ShowStatus(InfoBarSeverity.Informational, "Проверка…", "Сравниваю журнал изменений с устройством");
            journal = await GetJournalAsync(device);
            var applied = journal.GetRestorable().Where(j => j.Action is "freeze" or "uninstall").ToList();
            appliedCount = applied.Count;
            if (applied.Count == 0) return;

            var installed = await _adb.GetInstalledNamesAsync(device.Serial);
            var disabled = await _adb.GetDisabledStatesAsync(device.Serial);
            drifted = applied.Where(j => installed.Contains(j.Package) && (j.Action == "uninstall" || !disabled.ContainsKey(j.Package))).ToList();
        });
        if (journal is null) return;

        if (drifted.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Success, appliedCount == 0 ? "Нечего проверять" : "Всё на месте",
                appliedCount == 0
                    ? "Программа ещё ничего не замораживала и не удаляла на этом устройстве."
                    : $"Всё, что программа замораживала и удаляла на этом устройстве ({appliedCount}), по-прежнему заморожено и удалено.");
            return;
        }

        static string Describe(JournalEntry j) => j.Action == "freeze"
            ? $"Заморозить снова: {j.Label} ({j.Package}) — снова включено"
            : $"Удалить снова: {j.Label} ({j.Package}) — снова установлено";
        var chosen = await ShowChecklistAsync("После обновления прошивки",
            "Эти приложения программа замораживала или удаляла, но сейчас они снова работают — обычно так бывает после обновления прошивки. " +
            "Отмеченные будут заморожены или удалены заново.",
            drifted.Select(j => (Describe(j), j)).ToList(), "Повторить");
        if (chosen is not { Count: > 0 }) return;

        await RunBusyAsync(async () =>
        {
            await journal.EnsureBackupAsync(_adb, device.Serial);
            var errors = new List<string>();
            int ok = 0;
            foreach (var j in chosen)
            {
                ShowStatus(InfoBarSeverity.Informational, "Повторяю…", j.Label ?? j.Package);
                var r = j.Action == "freeze"
                    ? await _adb.SetFrozenAsync(device.Serial, j.Package, frozen: true)
                    : await _adb.UninstallAsync(device.Serial, new PackageInfo(j.Package, j.System, ""), keepData: false);
                if (r.Success && (j.Action == "freeze" || r.Text.Contains("Success")))
                {
                    ok++;
                    journal.Add(j with { Time = DateTime.Now });
                }
                else
                {
                    errors.Add($"{j.Package}: {LastLine(r.Text)}");
                }
            }

            await ReloadPackagesCoreAsync(device);
            if (errors.Count == 0)
                ShowStatus(InfoBarSeverity.Success, "Повторено", $"Приложений: {ok}");
            else
                ShowStatus(InfoBarSeverity.Error, $"Повторено: {ok}, ошибок: {errors.Count}", string.Join(Environment.NewLine, errors));
        });
    }

    // ----- Лаунчер Projectivy -----

    private async void InstallLauncher_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;

        LauncherRelease? release = null;
        string? installedVersion = null;
        var isTv = true;
        string? currentHome = null;
        await RunBusyAsync(async () =>
        {
            ShowStatus(InfoBarSeverity.Informational, ProjectivyLauncher.Name, "Узнаю последнюю версию на GitHub…");
            release = await ProjectivyLauncher.GetLatestAsync();
            if ((await _adb.GetInstalledNamesAsync(device.Serial)).Contains(ProjectivyLauncher.Package))
                installedVersion = await _adb.GetVersionAsync(device.Serial, ProjectivyLauncher.Package) ?? "?";
            isTv = (await GetPropsAsync(device)).Type == DeviceType.Tv;
            (currentHome, _) = await _adb.GetHomeActivitiesAsync(device.Serial);
            StatusBar.IsOpen = false;
        });
        if (release is null) return;

        var alreadyHome = currentHome?.StartsWith(ProjectivyLauncher.Package + "/", StringComparison.Ordinal) == true;
        var makeHome = new CheckBox { Content = "Сделать главным экраном (кнопка Home)", IsChecked = !alreadyHome, IsEnabled = !alreadyHome };
        var content = new StackPanel { Spacing = 10, MaxWidth = 560 };
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Бесплатный лаунчер для Android TV без рекламы и рекомендаций: свои категории, обои, " +
                   "скрытие приложений. Сторонний проект, APK скачивается с GitHub автора.",
        });
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Последняя версия: {release.Version} ({PackageInfo.FormatSize(release.Size)})" + Environment.NewLine +
                   $"На устройстве: {(installedVersion is null ? "не установлен" : installedVersion)}" +
                   (alreadyHome ? " — уже главный экран" : ""),
        });
        content.Children.Add(new HyperlinkButton { Content = "Страница выпуска на GitHub", NavigateUri = new Uri(release.PageUrl), Padding = new Thickness(0) });
        if (!isTv)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                Title = "Это не Android TV",
                Message = "Projectivy рассчитан на телевизоры и приставки; на телефоне им неудобно пользоваться.",
            });
        }
        content.Children.Add(makeHome);
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Text = "Стандартный лаунчер останется. Если кнопка Home всё равно открывает его (так бывает на Google TV), " +
                   "заморозьте его в таблице — защита снимется, когда появится второй лаунчер. " +
                   "Отменить установку и смену главного экрана — «… → Вернуть как было».",
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = ProjectivyLauncher.Name,
            PrimaryButtonText = installedVersion is null ? "Скачать и установить"
                : installedVersion == release.Version ? "Переустановить" : "Обновить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var setHome = makeHome.IsChecked == true && !alreadyHome;

        await RunBusyAsync(async () =>
        {
            ShowStatus(InfoBarSeverity.Informational, "Загрузка…", release.ApkName);
            BusyBar.IsIndeterminate = false;
            string apk;
            try
            {
                apk = await ProjectivyLauncher.DownloadAsync(release, new Progress<double>(p => BusyBar.Value = p * 100));
            }
            finally
            {
                BusyBar.IsIndeterminate = true;
            }

            var journal = await GetJournalAsync(device);
            await journal.EnsureBackupAsync(_adb, device.Serial);

            ShowStatus(InfoBarSeverity.Informational, "Установка…", release.ApkName);
            var r = await _adb.InstallAsync(device.Serial, [apk], allowDowngrade: false, grantPermissions: false);
            if (!r.Success || !r.Text.Contains("Success"))
            {
                ShowStatus(InfoBarSeverity.Error, "Не удалось установить", LastLine(r.Text));
                return;
            }
            // В журнал — только новая установка: «вернуть» обновление значило бы удалить лаунчер целиком.
            if (installedVersion is null)
                journal.Add(new JournalEntry(DateTime.Now, "install", ProjectivyLauncher.Package, ProjectivyLauncher.Name, false));

            var message = $"Версия {release.Version} установлена.";
            if (setHome)
            {
                var (before, candidates) = await _adb.GetHomeActivitiesAsync(device.Serial);
                var component = candidates.FirstOrDefault(c => c.StartsWith(ProjectivyLauncher.Package + "/", StringComparison.Ordinal));
                var h = component is null
                    ? new AdbResult(1, "", "лаунчер не объявил activity главного экрана")
                    : await _adb.SetHomeActivityAsync(device.Serial, component);
                if (h.Success)
                {
                    journal.Add(new JournalEntry(DateTime.Now, "home", ChangeJournal.HomeKey, "Главный экран", false, before));
                    message += " Теперь это главный экран — нажмите Home на пульте.";
                }
                else
                {
                    message += " Сделать главным экраном не удалось: " + LastLine(h.Text) +
                               ". Выберите его на устройстве: Настройки → Приложения → Приложения по умолчанию → Главный экран.";
                }
            }

            await ReloadPackagesCoreAsync(device);
            ShowStatus(InfoBarSeverity.Success, ProjectivyLauncher.Name, message);
        });
    }

    // ----- Аудит приватности (идея CleanYourTV) -----

    /// <summary>Известные пакеты с понятной ролью (из отчётов CleanYourTV), а не догадки по имени.</summary>
    private static readonly Dictionary<string, (string Category, string Note)> PrivacyCatalog = new()
    {
        ["com.google.android.katniss"] = ("voice", "Google Ассистент и голосовой поиск; без него перестанут работать кнопка микрофона на пульте и голосовой поиск в приложениях"),
        ["com.google.android.speech.pumpkin"] = ("voice", "офлайн-модель для фразы-активации, не нужна без ассистента"),
        ["org.droidtv.amazonalexa"] = ("voice", "Amazon Alexa на ТВ Philips; микрофон — в пульте"),
        ["tv.inscape.tvclient.inscapeacr"] = ("acr", "Inscape ACR: распознаёт, что на экране (включая HDMI), по картинке — микрофон не нужен"),
        ["com.skyworth.aisense"] = ("acr", "ACR Coocaa/Skyworth для таргетинга рекламы"),
        ["tv.alphonso.alphonso_eula"] = ("acr", "Alphonso: рекламный трекинг, читает статистику использования приложений"),
        ["org.droidtv.candeebug"] = ("telemetry", "аналитика «DAC» на ТВ Philips"),
        ["org.droidtv.gel"] = ("telemetry", "аналитика «GEL» на ТВ Philips (работает в процессе candeebug)"),
        ["org.droidtv.usagelogger"] = ("telemetry", "статистика загрузок, нажатий пульта и телеметрия панели"),
        ["org.droidtv.tv.frameworklogger"] = ("telemetry", "журналирование фреймворка и перезагрузок"),
        ["com.google.android.feedback"] = ("telemetry", "отправка отзывов и отчётов в Google"),
        ["com.miui.tv.analytics"] = ("telemetry", "аналитика Xiaomi: журналы и статистика использования"),
        ["com.xiaomi.statistic"] = ("telemetry", "статистика Xiaomi"),
        ["com.tcl.bi"] = ("telemetry", "аналитика TCL"),
        ["com.tcl.logkit"] = ("telemetry", "сбор журналов TCL"),
    };

    private async void PrivacyAudit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;

        Dictionary<string, List<string>> voice = [];
        List<(string Package, string Ago, bool Running)> mic = [];
        HashSet<string> installed = [];
        var failed = true;
        await RunBusyAsync(async () =>
        {
            ShowStatus(InfoBarSeverity.Informational, "Аудит приватности…", "Читаю настройки ассистента и историю доступа к микрофону (AppOps)");
            (voice, mic) = await _adb.GetPrivacyAuditAsync(device.Serial);
            installed = await _adb.GetInstalledNamesAsync(device.Serial);
            StatusBar.IsOpen = false;
            failed = false;
        });
        if (failed) return;

        string Name(string package) =>
            _allPackages.FirstOrDefault(p => p.Name == package) is { Label: { Length: > 0 } label } ? $"{label} ({package})" : package;

        var found = new HashSet<string>();
        var text = new System.Text.StringBuilder();
        void Section(string title, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            text.AppendLine(title);
            text.AppendLine(list.Count == 0 ? "  — не найдено" : string.Join(Environment.NewLine, list.Select(l => "  • " + l)));
            text.AppendLine();
        }

        Section("Голосовой ассистент (назначен в настройках системы)", voice.Select(v =>
        {
            found.Add(v.Key);
            var note = PrivacyCatalog.TryGetValue(v.Key, out var c) ? " — " + c.Note : "";
            return $"{Name(v.Key)}{note}";
        }));

        Section("Обращались к микрофону (история AppOps)", mic.OrderByDescending(m => m.Running).Select(m =>
        {
            found.Add(m.Package);
            return $"{Name(m.Package)} — {(m.Running ? "использует сейчас" : FormatAgo(m.Ago))}";
        }));

        foreach (var (category, title) in new[] { ("acr", "Распознавание контента на экране (ACR) — микрофон не нужен"), ("telemetry", "Телеметрия и аналитика") })
        {
            Section(title, PrivacyCatalog.Where(c => c.Value.Category == category && installed.Contains(c.Key)).Select(c =>
            {
                found.Add(c.Key);
                return $"{Name(c.Key)} — {c.Value.Note}";
            }));
        }
        text.Append("Аудит не доказывает, что звук или данные уходят с устройства: AppOps показывает только факт обращения. " +
                    "Отключая ассистент, вы теряете кнопку микрофона и голосовой поиск.");

        var markable = Packages.Where(p => found.Contains(p.Name) && !p.IsBlocked).ToList();
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Аудит приватности — " + device.DisplayName,
            CloseButtonText = "Закрыть",
            PrimaryButtonText = markable.Count > 0 ? $"Отметить в таблице ({markable.Count})" : "",
            DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock { Text = text.ToString(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            SetMarked(markable);
            ShowStatus(InfoBarSeverity.Informational, $"Отмечено: {markable.Count}",
                "Проверьте список и нажмите «Заморозить» — заморозку можно отменить в любой момент.");
        }
    }

    /// <summary>«2d18h40m39s429ms» из AppOps → «2 д 18 ч назад» (две старшие единицы).</summary>
    private static string FormatAgo(string ago)
    {
        var parts = System.Text.RegularExpressions.Regex.Matches(ago, @"(\d+)(d|h|ms|m|s)")
            .Where(m => m.Groups[2].Value != "ms")
            .Select(m => m.Groups[1].Value + " " + m.Groups[2].Value switch { "d" => "д", "h" => "ч", "m" => "мин", _ => "с" })
            .Take(2)
            .ToList();
        return parts.Count == 0 ? "недавно" : string.Join(" ", parts) + " назад";
    }

    // ----- Снимок экрана -----

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;

        byte[] png = [];
        await RunBusyAsync(async () => png = await _adb.ScreenshotAsync(device.Serial));
        // Сигнатура PNG: иначе на экране защищённое содержимое или команда не поддерживается.
        if (png.Length < 8 || png[0] != 0x89 || png[1] != (byte)'P')
        {
            ShowStatus(InfoBarSeverity.Error, "Не удалось сделать снимок экрана",
                "Устройство не вернуло изображение. На некоторых экранах (видео с защитой, банковские приложения) снимок запрещён.");
            return;
        }

        var picker = new FileSavePicker { SuggestedFileName = $"{device.DisplayName.Replace(' ', '-')}-{DateTime.Now:yyyyMMdd-HHmmss}" };
        picker.FileTypeChoices.Add("PNG", [".png"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        await File.WriteAllBytesAsync(file.Path, png);
        ShowStatus(InfoBarSeverity.Success, "Снимок экрана сохранён", file.Path);
    }

    // ----- Частный DNS (идея ЧистогоТВ) -----

    private static readonly (string Title, string? Host, string Mode)[] DnsPresets =
    [
        ("Автоматически (по умолчанию Android)", null, "opportunistic"),
        ("Выключен", null, "off"),
        ("AdGuard — блокировка рекламы и трекеров", "dns.adguard-dns.com", "hostname"),
        ("AdGuard Семейный — плюс взрослый контент", "family.adguard-dns.com", "hostname"),
    ];

    private static string DescribeDns(string? value)
    {
        var parts = (value ?? "").Split('|');
        var mode = parts.ElementAtOrDefault(0) ?? "";
        var host = parts.ElementAtOrDefault(1) ?? "";
        return mode switch
        {
            "hostname" => host,
            "off" => "выключен",
            _ => "автоматически",
        };
    }

    private async Task<AdbResult> RestoreDnsAsync(DeviceInfo device, string? oldValue)
    {
        var parts = (oldValue ?? "").Split('|');
        var mode = string.IsNullOrEmpty(parts.ElementAtOrDefault(0)) ? "opportunistic" : parts[0];
        var host = parts.ElementAtOrDefault(1) ?? "";
        if (host.Length > 0) await _adb.PutSettingAsync(device.Serial, "global", "private_dns_specifier", host);
        return await _adb.PutSettingAsync(device.Serial, "global", "private_dns_mode", mode);
    }

    private async void PrivateDns_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;

        DeviceProps props;
        string mode, host;
        try
        {
            props = await GetPropsAsync(device);
            mode = await _adb.GetSettingAsync(device.Serial, "global", "private_dns_mode");
            host = await _adb.GetSettingAsync(device.Serial, "global", "private_dns_specifier");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Ошибка", ex.Message);
            return;
        }

        if (props.Sdk is > 0 and < 28)
        {
            ShowStatus(InfoBarSeverity.Warning, "Частный DNS недоступен", "Нужен Android 9 или новее.");
            return;
        }

        var radios = new RadioButtons();
        foreach (var preset in DnsPresets) radios.Items.Add(preset.Title);
        radios.Items.Add("Свой сервер");
        var customBox = new TextBox { PlaceholderText = "dns.example.com", IsEnabled = false };

        var currentIndex = Array.FindIndex(DnsPresets, p => p.Mode == (mode.Length == 0 ? "opportunistic" : mode) &&
                                                            (p.Host is null || p.Host.Equals(host, StringComparison.OrdinalIgnoreCase)));
        if (currentIndex < 0 && mode == "hostname")
        {
            currentIndex = DnsPresets.Length;
            customBox.Text = host;
        }
        radios.SelectedIndex = Math.Max(0, currentIndex);
        customBox.IsEnabled = radios.SelectedIndex == DnsPresets.Length;
        radios.SelectionChanged += (_, _) => customBox.IsEnabled = radios.SelectedIndex == DnsPresets.Length;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Частный DNS",
            PrimaryButtonText = "Применить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = $"Сейчас: {DescribeDns($"{mode}|{host}")}. Блокирующий DNS убирает рекламу и трекеры во всех приложениях " +
                               "и браузере, но не рекламу внутри YouTube. Если какой-то сайт или приложение перестанет работать — " +
                               "верните «Автоматически».",
                    },
                    radios,
                    customBox,
                },
            },
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string newMode, newHost;
        if (radios.SelectedIndex == DnsPresets.Length)
        {
            newHost = customBox.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(newHost, @"^[A-Za-z0-9]([A-Za-z0-9\-\.]*[A-Za-z0-9])?$"))
            {
                ShowStatus(InfoBarSeverity.Warning, "Неверный адрес", "Укажите имя DNS-over-TLS сервера, например dns.adguard-dns.com.");
                return;
            }
            newMode = "hostname";
        }
        else
        {
            var preset = DnsPresets[Math.Max(0, radios.SelectedIndex)];
            newMode = preset.Mode;
            newHost = preset.Host ?? "";
        }

        await RunBusyAsync(async () =>
        {
            var journal = await GetJournalAsync(device);
            await journal.EnsureBackupAsync(_adb, device.Serial);

            if (newHost.Length > 0) await _adb.PutSettingAsync(device.Serial, "global", "private_dns_specifier", newHost);
            var r = await _adb.PutSettingAsync(device.Serial, "global", "private_dns_mode", newMode);
            var applied = await _adb.GetSettingAsync(device.Serial, "global", "private_dns_mode");
            if (!r.Success || applied != newMode)
            {
                ShowStatus(InfoBarSeverity.Error, "Не удалось изменить DNS", r.Text);
                return;
            }

            journal.Add(new JournalEntry(DateTime.Now, "dns", ChangeJournal.DnsKey, "Частный DNS", true, $"{mode}|{host}"));
            ShowStatus(InfoBarSeverity.Success, "Частный DNS", $"Установлено: {DescribeDns($"{newMode}|{newHost}")}.");
        });
    }

    // ----- Скачивание APK -----

    private async void PullApk_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;
        var selected = PackagesList.SelectedItems.OfType<PackageInfo>().Where(p => !string.IsNullOrEmpty(p.ApkPath)).ToList();
        if (selected.Count == 0) return;
        if (selected.Count > 1)
        {
            await PullApksToFolderAsync(device, selected);
            return;
        }

        var pkg = selected[0];
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

    /// <summary>Несколько отмеченных приложений — в выбранную папку, файлы называются по имени пакета.</summary>
    private async Task PullApksToFolderAsync(DeviceInfo device, List<PackageInfo> packages)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        await RunBusyAsync(async () =>
        {
            var failed = new List<string>();
            for (int i = 0; i < packages.Count; i++)
            {
                var pkg = packages[i];
                ShowStatus(InfoBarSeverity.Informational, $"Сохранение APK: {i + 1} из {packages.Count}", pkg.DisplayName);
                var r = await _adb.PullApkAsync(device.Serial, pkg, Path.Combine(folder.Path, pkg.Name + ".apk"));
                if (!r.Success) failed.Add($"{pkg.DisplayName}: {r.Text}");
            }

            var saved = packages.Count - failed.Count;
            ShowStatus(failed.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                $"Сохранено APK: {saved} из {packages.Count}",
                failed.Count == 0 ? folder.Path : folder.Path + Environment.NewLine + "Не удалось:" + Environment.NewLine + string.Join(Environment.NewLine, failed));
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
