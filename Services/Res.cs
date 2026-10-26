using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace AdbManager.Services;

/// <summary>
/// Строки интерфейса из ресурсов <c>Strings/&lt;язык&gt;/Resources.resw</c> (MRT Core).
/// В XAML строки подставляются через <c>x:Uid</c>, в коде — <see cref="Get"/> и <see cref="Format"/>.
/// </summary>
public static class Res
{
    /// <summary>Поддерживаемые языки интерфейса (папки в <c>Strings</c>).</summary>
    public const string Russian = "ru-RU";
    public const string English = "en-US";

    private static ResourceLoader? _loader;
    private static ResourceLoader Loader => _loader ??= new ResourceLoader();

    /// <summary>
    /// Выбирает язык до создания окна: <paramref name="language"/> — «ru-RU», «en-US» или null (по языкам Windows).
    /// Смена языка во время работы требует перезапуска: XAML читает строки при создании элементов.
    /// </summary>
    public static void Init(string? language)
    {
        // Без выбора — язык интерфейса Windows, а не первый из «предпочитаемых языков», который взял бы MRT:
        // у Windows с русским интерфейсом английский часто стоит первым в списке. CurrentUICulture
        // при запуске .NET берёт из GetUserDefaultUILanguage — это и есть язык интерфейса Windows.
        // (Пустую строку в PrimaryLanguageOverride передавать нельзя — у неупакованного приложения это E_FAIL.)
        if (language is not (Russian or English))
            language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? Russian : English;
        ApplicationLanguages.PrimaryLanguageOverride = language;

        // Язык, который выбрал MRT (ресурс есть в каждом языке). Названия приложений из APK (ApkResolver) — на нём же.
        Language = Get("LanguageTag");
        var culture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>Язык, выбранный ресурсами: «ru-RU» или «en-US».</summary>
    public static string Language { get; private set; } = English;

    public static string Get(string key)
    {
        try
        {
            return Loader.GetString(key) is { Length: > 0 } s ? s : key;
        }
        catch (Exception)
        {
            return key; // нет ресурса — видно по ключу, а не падение
        }
    }

    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
