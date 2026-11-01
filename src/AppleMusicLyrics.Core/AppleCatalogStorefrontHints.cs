namespace AppleMusicLyrics.Core;

/// <summary>
/// Language tags can prioritize a bounded public catalog lookup. They are never evidence
/// of track identity, the user's account region, or availability in a particular storefront.
/// </summary>
public static class AppleCatalogStorefrontHints
{
    private const int MaximumHintsPerIdentity = 4;
    private static readonly HashSet<string> ExplicitRegions = new(StringComparer.Ordinal)
    {
        "AU", "US", "GB", "CA", "KR", "JP", "CN", "TW", "HK", "NZ", "SG", "DE", "FR", "ES", "IT", "IN", "BR", "MX"
    };

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Build(
        IEnumerable<NativeLyricsDocument> documents, IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.Where(NativeTrackMatcher.IsCatalogId).ToHashSet(StringComparer.Ordinal);
        var grouped = documents.Where(document => NativeTrackMatcher.IsCatalogId(document.CatalogId) &&
                document.ResourceType == "syllable-lyrics" && document.ResourceId == document.CatalogId &&
                wanted.Contains(document.CatalogId!))
            .GroupBy(document => document.CatalogId!, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var group in grouped)
        {
            var sources = group.ToArray();
            // Original/pronunciation languages describe the recording more directly
            // than the target language selected for a translation.
            var languages = sources.SelectMany(document => document.Lines.Select(line => line.Language))
                .Concat(sources.SelectMany(document => document.Pronunciations.Select(track => track.Language)))
                .Concat(sources.SelectMany(document => document.Translations.Select(track => track.Language)));
            var hints = languages.Select(StorefrontForLanguage).Where(country => country is not null)
                .Select(country => country!).Distinct(StringComparer.Ordinal).Take(MaximumHintsPerIdentity).ToArray();
            if (hints.Length > 0) result[group.Key] = hints;
        }
        return result;
    }

    private static string? StorefrontForLanguage(string? language)
    {
        if (language is null || language.Length is 0 or > 64 ||
            language.Any(character => character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')))
            return null;
        var parts = language.Split('-');
        if (parts.Any(part => part.Length == 0) || parts[0].Length is < 2 or > 3 ||
            !parts[0].All(char.IsAsciiLetter)) return null;
        // An explicit valid region takes precedence over a language/script suggestion.
        var region = parts.Skip(1).FirstOrDefault(part => part.Length == 2 && part.All(char.IsAsciiLetter))?.ToUpperInvariant();
        if (region is not null) return ExplicitRegions.Contains(region) ? region : null;
        return parts[0].ToLowerInvariant() switch
        {
            "ko" => "KR",
            "ja" => "JP",
            "zh" when parts.Contains("Hant", StringComparer.OrdinalIgnoreCase) => "TW",
            "zh" when parts.Contains("Hans", StringComparer.OrdinalIgnoreCase) => "CN",
            _ => null // Neutral English/Chinese cannot establish a region.
        };
    }
}
