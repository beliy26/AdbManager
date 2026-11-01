using System.Globalization;
using AdbManager.Models;
using AdbManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AdbManager;

/// <summary>
/// Окно «Состояние устройства»: параметры, загрузка процессора, память, место, батарея, температуры
/// (те же данные, что у инструмента MCP get_device_status). Можно обновлять автоматически.
/// </summary>
public sealed partial class MainWindow
{
    private const int DeviceStatusAutoRefreshMs = 3000;

    private async void DeviceStatus_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { IsOnline: true } device) return;

        var body = new StackPanel { Spacing = 16 };
        var progress = new ProgressRing { IsActive = true, Width = 16, Height = 16 };
        var updated = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var auto = new CheckBox { Content = Res.Get("DeviceStatusAutoRefresh") };
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(progress);
        Grid.SetColumn(updated, 1);
        top.Children.Add(updated);
        Grid.SetColumn(auto, 2);
        top.Children.Add(auto);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Res.Format("DeviceStatusTitleFormat", device.DisplayName),
            PrimaryButtonText = Res.Get("Refresh"),
            CloseButtonText = Res.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
            Content = new Grid
            {
                RowSpacing = 12,
                RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() },
                Children = { top, WithRow(new ScrollViewer { Content = body, Padding = new Thickness(0, 0, 16, 0) }, 1) },
            },
        };
        // Таблица процессов из top широкая — окно шире стандартных 548 пикселей.
        dialog.Resources["ContentDialogMaxWidth"] = 820.0;

        var open = true;
        var loading = false;
        async Task LoadAsync()
        {
            if (loading || !open) return;
            loading = true;
            progress.Visibility = Visibility.Visible;
            try
            {
                var status = await DeviceStatus.ReadAsync(_adb, device.Serial);
                if (!open) return;
                FillDeviceStatus(body, status);
                updated.Text = Res.Format("DeviceStatusUpdatedFormat", DateTime.Now.ToString("T", CultureInfo.CurrentCulture));
            }
            catch (Exception ex)
            {
                updated.Text = Res.Get("Error") + ": " + ex.Message;
            }
            finally
            {
                loading = false;
                progress.Visibility = Visibility.Collapsed;
            }
        }

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; // «Обновить» не закрывает окно
            await LoadAsync();
        };
        dialog.Closed += (_, _) => open = false;
        dialog.Opened += async (_, _) =>
        {
            await LoadAsync();
            // Автообновление: следующий замер — через паузу после окончания предыдущего (замер сам идёт ~1,5 с).
            while (open)
            {
                await Task.Delay(DeviceStatusAutoRefreshMs);
                if (open && auto.IsChecked == true) await LoadAsync();
            }
        };

        await dialog.ShowAsync();
    }

    private static FrameworkElement WithRow(FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        return element;
    }

    private static void FillDeviceStatus(StackPanel body, DeviceStatusInfo s)
    {
        body.Children.Clear();
        static string Size(long bytes) => PackageInfo.FormatSize(bytes);
        static string Percent(double value) => Res.Format("PercentFormat", value.ToString("0.#", CultureInfo.CurrentCulture));

        // ----- Устройство -----
        var d = s.Device;
        var rows = new List<(string, string)>
        {
            (Res.Get("DeviceStatusModel"), string.Join(" ", new[] { d.Manufacturer, d.Model }.Where(x => x.Length > 0).Distinct())),
            (Res.Get("DeviceStatusCodename"), d.Codename),
            (Res.Get("DeviceStatusAndroid"), d.Sdk is { } sdk ? Res.Format("DeviceStatusAndroidFormat", d.Android, sdk) : d.Android),
            (Res.Get("DeviceStatusSecurityPatch"), d.SecurityPatch),
            (Res.Get("DeviceStatusBuild"), d.Build),
            (Res.Get("DeviceStatusSoc"), d.Hardware.Length > 0 && !d.Soc.Contains(d.Hardware, StringComparison.OrdinalIgnoreCase)
                ? $"{d.Soc} ({d.Hardware})" : d.Soc),
            (Res.Get("DeviceStatusAbis"), string.Join(", ", d.Abis)),
            (Res.Get("DeviceStatusScreen"), d.DensityDpi is { } dpi ? Res.Format("DeviceStatusScreenFormat", d.Screen, dpi) : d.Screen),
            (Res.Get("DeviceStatusUptime"), d.Uptime is { } up ? FormatUptime(up) : ""),
        };
        body.Children.Add(Section(Res.Get("DeviceStatusDeviceSection"), KeyValues(rows)));

        // ----- Процессор -----
        var cpu = new StackPanel { Spacing = 8 };
        if (s.Cpu.UsagePercent is { } usage)
            cpu.Children.Add(Meter(Res.Get("DeviceStatusCpuLoad"), usage, Percent(usage)));
        var cpuRows = new List<(string, string)>();
        if (s.Cpu.Cores > 0) cpuRows.Add((Res.Get("DeviceStatusCores"), s.Cpu.Cores.ToString(CultureInfo.CurrentCulture)));
        if (s.Cpu.Frequencies.Count > 0)
        {
            var max = s.Cpu.Frequencies.Select(f => f.MaxMhz).Where(m => m is not null).Distinct().ToList();
            var text = Res.Format("MhzFormat", string.Join(", ", s.Cpu.Frequencies.Select(f => f.Mhz)));
            if (max.Count > 0) text += " " + Res.Format("DeviceStatusMaxFormat", Res.Format("MhzFormat", string.Join(", ", max)));
            cpuRows.Add((Res.Get("DeviceStatusFrequencies"), text));
        }
        if (s.Cpu.LoadAverage.Length > 0) cpuRows.Add((Res.Get("DeviceStatusLoadAverage"), string.Join("  ", s.Cpu.LoadAverage)));
        if (cpuRows.Count > 0) cpu.Children.Add(KeyValues(cpuRows));
        if (s.Cpu.TopProcesses.Count > 1)
        {
            cpu.Children.Add(new TextBlock { Text = Res.Get("DeviceStatusTopProcesses"), Margin = new Thickness(0, 4, 0, 0) });
            cpu.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = new TextBlock
                {
                    Text = string.Join("\n", s.Cpu.TopProcesses),
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 11,
                    IsTextSelectionEnabled = true,
                    Padding = new Thickness(0, 0, 0, 12),
                },
            });
        }
        body.Children.Add(Section(Res.Get("DeviceStatusCpuSection"), cpu));

        // ----- Память -----
        if (s.Memory is { } m)
        {
            var mem = new StackPanel { Spacing = 8 };
            mem.Children.Add(Meter(Res.Get("DeviceStatusRam"), m.UsedPercent,
                Res.Format("DeviceStatusUsedOfFormat", Size(m.TotalBytes - m.AvailableBytes), Size(m.TotalBytes), Percent(m.UsedPercent))));
            if (m.SwapTotalBytes > 0)
            {
                var swap = Math.Round(100.0 * m.SwapUsedBytes / m.SwapTotalBytes, 1);
                mem.Children.Add(Meter(Res.Get("DeviceStatusSwap"), swap,
                    Res.Format("DeviceStatusUsedOfFormat", Size(m.SwapUsedBytes), Size(m.SwapTotalBytes), Percent(swap))));
            }
            body.Children.Add(Section(Res.Get("DeviceStatusMemorySection"), mem));
        }

        // ----- Место -----
        if (s.Storage.Count > 0)
        {
            var storage = new StackPanel { Spacing = 8 };
            foreach (var v in s.Storage)
            {
                var label = v.Kind switch
                {
                    StorageKind.Internal => Res.Get("DeviceStatusStorageInternal"),
                    StorageKind.System => Res.Get("DeviceStatusStorageSystem"),
                    _ => Res.Format("DeviceStatusStorageExternalFormat", v.Path),
                };
                // Системный раздел только для чтения и всегда почти заполнен — без предупреждающего цвета.
                storage.Children.Add(Meter(label, v.UsedPercent,
                    Res.Format("DeviceStatusFreeOfFormat", Size(v.FreeBytes), Size(v.TotalBytes), Percent(v.UsedPercent)),
                    warn: v.Kind != StorageKind.System));
            }
            body.Children.Add(Section(Res.Get("DeviceStatusStorageSection"), storage));
        }

        // ----- Батарея -----
        if (s.Battery is { } b)
        {
            var battery = new StackPanel { Spacing = 8 };
            battery.Children.Add(Meter(Res.Get("DeviceStatusCharge"), b.LevelPercent, Percent(b.LevelPercent), lowIsBad: true));
            var status = b.Status switch
            {
                "charging" => Res.Get("BatteryCharging"),
                "discharging" => Res.Get("BatteryDischarging"),
                "not charging" => Res.Get("BatteryNotCharging"),
                "full" => Res.Get("BatteryFull"),
                _ => Res.Get("BatteryUnknown"),
            };
            if (b.PoweredBy.Length > 0)
                status += " (" + string.Join(", ", b.PoweredBy.Select(p => p switch
                {
                    "ac" => Res.Get("BatterySourceAc"),
                    "usb" => "USB",
                    _ => Res.Get("BatterySourceWireless"),
                })) + ")";
            var health = b.Health switch
            {
                "good" => Res.Get("BatteryHealthGood"),
                "overheat" => Res.Get("BatteryHealthOverheat"),
                "dead" => Res.Get("BatteryHealthDead"),
                "over voltage" => Res.Get("BatteryHealthOverVoltage"),
                "failure" => Res.Get("BatteryHealthFailure"),
                "cold" => Res.Get("BatteryHealthCold"),
                _ => Res.Get("BatteryUnknown"),
            };
            battery.Children.Add(KeyValues(
            [
                (Res.Get("DeviceStatusBatteryState"), status),
                (Res.Get("DeviceStatusBatteryHealth"), health),
                (Res.Get("DeviceStatusTemperature"), b.TemperatureC is { } t ? Celsius(t) : ""),
                (Res.Get("DeviceStatusVoltage"), b.VoltageMv is { } mv ? Res.Format("VoltsFormat", (mv / 1000.0).ToString("0.00", CultureInfo.CurrentCulture)) : ""),
            ]));
            body.Children.Add(Section(Res.Get("DeviceStatusBatterySection"), battery));
        }

        // ----- Температуры -----
        if (s.Temperatures.Count > 0)
            body.Children.Add(Section(Res.Get("DeviceStatusTemperaturesSection"),
                KeyValues(s.Temperatures.Select(t => (t.Name, Celsius(t.Celsius))).ToList())));
    }

    private static string Celsius(double value) => Res.Format("CelsiusFormat", value.ToString("0.#", CultureInfo.CurrentCulture));

    private static string FormatUptime(TimeSpan t) => t.TotalDays >= 1
        ? Res.Format("UptimeDaysFormat", (int)t.TotalDays, t.Hours, t.Minutes)
        : Res.Format("UptimeHoursFormat", t.Hours, t.Minutes);

    private static StackPanel Section(string title, UIElement content) => new()
    {
        Spacing = 6,
        Children =
        {
            new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
            content,
        },
    };

    /// <summary>Таблица «название — значение»; пустые значения пропускаются, значения можно выделить и скопировать.</summary>
    private static Grid KeyValues(List<(string Key, string Value)> rows)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        // Одинаковая ширина подписей во всех разделах — значения стоят в одну колонку.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 200 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (key, value) in rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)))
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = key, Foreground = ThemeBrush("TextFillColorSecondaryBrush") };
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(k, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            grid.Children.Add(k);
            grid.Children.Add(v);
        }
        return grid;
    }

    /// <summary>
    /// Шкала: подпись слева, значение справа, под ними полоса — жёлтая от 75 %, красная от 90 %
    /// (для заряда батареи наоборот: жёлтая до 25 %, красная до 10 %).
    /// </summary>
    private static StackPanel Meter(string label, double percent, string value, bool lowIsBad = false, bool warn = true)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = label, Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        var v = new TextBlock { Text = value, IsTextSelectionEnabled = true };
        Grid.SetColumn(v, 1);
        header.Children.Add(v);

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(percent, 0, 100), Height = 4 };
        var level = lowIsBad ? 100 - percent : percent;
        if (warn && level >= 90) bar.Foreground = ThemeBrush("SystemFillColorCriticalBrush");
        else if (warn && level >= 75) bar.Foreground = ThemeBrush("SystemFillColorCautionBrush");
        return new StackPanel { Spacing = 4, Children = { header, bar } };
    }
}
