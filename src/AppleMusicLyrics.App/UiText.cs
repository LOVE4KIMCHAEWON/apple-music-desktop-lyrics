using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace AppleMusicLyrics.App;

/// <summary>Interface text only. Song metadata and lyric content never pass through this catalog.</summary>
public static partial class UiText
{
    private static readonly IReadOnlyDictionary<string, string> Chinese = LoadCatalog();
    private static string _language = "en-US";
    public static string Language
    {
        get => _language;
        set
        {
            _language = NormalizeLanguage(value) ?? "en-US";
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(_language);
        }
    }
    public static bool IsChinese => _language == "zh-CN";
    public static string? NormalizeLanguage(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "zh-hans" => "zh-CN",
        "en" or "en-us" => "en-US",
        _ => null
    };
    public static string ResolveLanguage(string? requested, string? saved, string? portableDefault) =>
        NormalizeLanguage(requested) ?? NormalizeLanguage(saved) ?? NormalizeLanguage(portableDefault) ?? "en-US";
    public static string T(string text) => IsChinese && Chinese.TryGetValue(text, out var translated) ? translated : text;
    public static string Format(string template, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, T(template), arguments);

    private static IReadOnlyDictionary<string, string> LoadCatalog()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains("UiText.", StringComparison.Ordinal) && name.EndsWith(".zh-CN.json", StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("Missing UI resource: " + name);
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? throw new InvalidDataException("Empty UI resource: " + name);
            foreach (var entry in entries) result[entry.Key] = entry.Value;
        }
        return result;
    }
}
