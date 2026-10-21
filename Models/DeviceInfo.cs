namespace AdbManager.Models;

/// <summary>Устройство из вывода <c>adb devices -l</c>.</summary>
/// <remarks>Свойства только для чтения (без init): генератор XAML не умеет работать с init-аксессорами.</remarks>
public sealed class DeviceInfo(string serial, string state, string model, string product)
{
    public string Serial { get; } = serial;
    public string State { get; } = state;
    public string Model { get; } = model;
    public string Product { get; } = product;

    public bool IsOnline => State == "device";

    public bool IsWireless => Serial.Contains(':') || Serial.Contains("._adb-tls-connect.");

    public string DisplayName => string.IsNullOrEmpty(Model) ? Serial : Model.Replace('_', ' ');

    public string StateText => State switch
    {
        "device" => "Подключено",
        "unauthorized" => "Не авторизовано — подтвердите отладку на телефоне",
        "offline" => "Офлайн",
        "recovery" => "Recovery",
        "sideload" => "Sideload",
        "bootloader" => "Bootloader",
        _ => State,
    };

    public string Glyph => IsWireless ? "\uE701" : "\uE8EA"; // Wi-Fi / телефон
}
