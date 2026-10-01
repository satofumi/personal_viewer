using System.Globalization;
using System.Text.Json;
using System.Windows.Markup;

namespace PersonalViewer.Localization;

public static class LocalizationService
{
    private static IReadOnlyDictionary<string, string> _strings = new Dictionary<string, string>(StringComparer.Ordinal);

    public static void Configure(string? language)
    {
        var requestedLanguage = language?.Trim().ToLowerInvariant();
        var selectedLanguage = requestedLanguage switch
        {
            "ja" => "ja",
            "en" => "en",
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en" : "ja"
        };

        var culture = CultureInfo.GetCultureInfo(selectedLanguage);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        var resourceName = $"PersonalViewer.Localization.Strings.{selectedLanguage}.json";
        using var stream = typeof(LocalizationService).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Localization resource was not embedded: {resourceName}");
        _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Localization resource is empty: {resourceName}");
    }

    public static string GetString(string key)
    {
        return _strings.TryGetValue(key, out var value) ? value : key;
    }

    public static string Format(string key, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, GetString(key), arguments);
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return LocalizationService.GetString(key);
    }
}