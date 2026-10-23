namespace AdbManager.Models;

/// <summary>Как устройство попало в список.</summary>
public enum DeviceKind
{
    Usb,
    /// <summary>Отладка по сети на порту 5555 (adb tcpip) или найденное поиском в сети.</summary>
    Network,
    /// <summary>Беспроводная отладка Android 11+, сопряжённое по коду.</summary>
    Paired,
}

/// <summary>Тип устройства по <c>ro.build.characteristics</c> и <c>android.software.leanback</c>.</summary>
public enum DeviceType
{
    /// <summary>Ещё не подключались или тип не распознан (часы, автомобиль).</summary>
    Unknown,
    Phone,
    Tablet,
    /// <summary>Android TV / Google TV: телевизор или приставка.</summary>
    Tv,
}

/// <summary>
/// Устройство в списке: подключённое (из <c>adb devices -l</c>) или доступное для подключения —
/// найденное в сети или сопряжённое ранее. У доступных <see cref="Serial"/> — адрес для <c>adb connect</c>.
/// </summary>
/// <remarks>Свойства только для чтения (без init): генератор XAML не умеет работать с init-аксессорами.</remarks>
public sealed class DeviceInfo(string serial, string state, string model, string product, DeviceKind? kind = null,
    DeviceType type = DeviceType.Unknown)
{
    /// <summary>Не подключено, но доступно в сети.</summary>
    public const string AvailableState = "available";
    /// <summary>Сопряжено ранее, сейчас в сети не видно.</summary>
    public const string MissingState = "missing";

    public string Serial { get; } = serial;
    public string State { get; } = state;
    public string Model { get; } = model;
    public string Product { get; } = product;

    public DeviceKind Kind { get; } = kind ?? (!IsWirelessSerial(serial) ? DeviceKind.Usb
        : serial.Contains("._adb-tls-connect.") ? DeviceKind.Paired : DeviceKind.Network);

    public DeviceType Type { get; } = type;

    public static DeviceInfo Available(string address, string? name, DeviceKind kind, DeviceType type, bool missing = false) =>
        new(address, missing ? MissingState : AvailableState, name ?? "", "", kind, type);

    public DeviceInfo WithKindAndType(DeviceKind kind, DeviceType type) => new(Serial, State, Model, Product, kind, type);

    public bool IsConnected => State is not (AvailableState or MissingState);

    public bool IsOnline => State == "device";

    public bool IsWireless => Kind != DeviceKind.Usb;

    public string DisplayName => string.IsNullOrEmpty(Model) ? Serial : Model.Replace('_', ' ');

    public string KindText => Kind switch
    {
        DeviceKind.Usb => "USB",
        DeviceKind.Paired => "Беспроводная отладка",
        _ => "Wi-Fi",
    };

    public string SubtitleText => $"{KindText} · {Serial}";

    public string StateText => State switch
    {
        "device" => "Подключено",
        AvailableState => "Не подключено — нажмите, чтобы подключить",
        MissingState => "Сопряжено, сейчас не в сети",
        "unauthorized" => "Не авторизовано — подтвердите отладку на телефоне",
        "offline" => "Офлайн",
        "recovery" => "Recovery",
        "sideload" => "Sideload",
        "bootloader" => "Bootloader",
        _ => State,
    };

    public double ItemOpacity => IsConnected ? 1 : 0.7;

    public string TypeText => Type switch
    {
        DeviceType.Phone => "Телефон",
        DeviceType.Tablet => "Планшет",
        DeviceType.Tv => "Android TV (телевизор или приставка)",
        _ => "Тип устройства станет известен после подключения",
    };

    /// <summary>Иконка по типу устройства; пока тип неизвестен — по способу подключения.</summary>
    public string Glyph => Type switch
    {
        DeviceType.Phone => "\uE8EA",   // телефон
        DeviceType.Tablet => "\uE70A",  // планшет
        DeviceType.Tv => "\uE7F4",      // телевизор
        _ => Kind switch
        {
            DeviceKind.Usb => "\uE88E",     // USB
            DeviceKind.Paired => "\uE71B",  // связь (сопряжение)
            _ => "\uE701",                  // Wi-Fi
        },
    };

    /// <summary>Хост из адреса «ip:порт», для остальных серийных номеров — null.</summary>
    public string? Host => Serial.LastIndexOf(':') is var i and > 0 && !Serial.Contains("._adb-tls-connect.") ? Serial[..i] : null;

    /// <summary>
    /// Серийный номер из имени mDNS-сервиса adb: «adb-R5CY14ADFFY-pQHpll» или
    /// «adb-R5CY14ADFFY-pQHpll._adb-tls-connect._tcp» → R5CY14ADFFY.
    /// </summary>
    public static string? SerialFromServiceName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("adb-", StringComparison.Ordinal)) return null;
        var s = name[4..];
        var dot = s.IndexOf('.');
        if (dot >= 0) s = s[..dot];
        var dash = s.LastIndexOf('-');
        if (dash > 0) s = s[..dash];
        return s.Length > 0 ? s : null;
    }

    private static bool IsWirelessSerial(string serial) => serial.Contains(':') || serial.Contains("._adb-tls-connect.");
}
