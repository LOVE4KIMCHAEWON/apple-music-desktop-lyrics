namespace AppleMusicLyrics.Core;

/// <summary>Independent display flags; supplemental text is joined exclusively by original lyric key.</summary>
public static class LyricPresentation
{
    public static LyricPresentationResult Select(NativeLyricsDocument document, TimeSpan position,
        bool showTranslation, bool showPronunciation, string? preferredTranslationLanguage = null,
        string? preferredPronunciationLanguage = null, TimeSpan offset = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var target = position.TotalSeconds + offset.TotalSeconds;
        var low = 0;
        var high = document.Lines.Count - 1;
        var activeIndex = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (document.Lines[middle].Begin.TotalSeconds <= target)
            {
                activeIndex = middle;
                low = middle + 1;
            }
            else high = middle - 1;
        }

        var current = activeIndex >= 0 ? document.Lines[activeIndex] : null;
        var upcoming = activeIndex + 1 < document.Lines.Count ? document.Lines[activeIndex + 1] : null;
        var original = new PresentedLyricLine(current?.Text ?? "", PresentedLyricKind.Original, current?.Language);
        LyricPresentationResult OriginalPair(string? limitation = null) => new(
            [original, new(upcoming?.Text ?? "", PresentedLyricKind.Upcoming, upcoming?.Language)], limitation, activeIndex);
        if (current is null || (!showTranslation && !showPronunciation)) return OriginalPair();

        var translation = showTranslation ? SelectLanguage(document.Translations, preferredTranslationLanguage, false) : null;
        var pronunciation = showPronunciation ? SelectLanguage(document.Pronunciations, preferredPronunciationLanguage, true) : null;
        var translatedLine = translation?.Lines.SingleOrDefault(line => line.OriginalKey == current.Key);
        var pronunciationLine = pronunciation?.Lines.SingleOrDefault(line => line.OriginalKey == current.Key);
        var missing = new List<string>();
        if (showTranslation && string.IsNullOrWhiteSpace(translatedLine?.Text)) missing.Add("translation");
        if (showPronunciation && string.IsNullOrWhiteSpace(pronunciationLine?.Text)) missing.Add("pronunciation");
        if (missing.Count > 0)
            return OriginalPair($"Native {string.Join(" and ", missing)} unavailable for this line or selected language; showing original lyrics.");

        var lines = new List<PresentedLyricLine> { original };
        if (showPronunciation) lines.Add(new(pronunciationLine!.Text, PresentedLyricKind.Pronunciation, pronunciation!.Language));
        if (showTranslation) lines.Add(new(translatedLine!.Text, PresentedLyricKind.Translation, translation!.Language));
        return new(lines, null, activeIndex, translation?.Language, pronunciation?.Language);
    }

    /// <summary>
    /// An explicit language preference never silently switches to an unrelated language.
    /// Without a preference, English translations and Latin-script pronunciation are preferred,
    /// followed by a stable language/type ordering. All unselected tracks remain in the model.
    /// </summary>
    public static NativeLocalizedLyrics? SelectLanguage(IReadOnlyList<NativeLocalizedLyrics> groups,
        string? preferredLanguage, bool pronunciation)
    {
        var preference = preferredLanguage?.Trim();
        var candidates = groups.Where(group => group.Lines.Count > 0);
        if (!string.IsNullOrEmpty(preference))
        {
            candidates = candidates.Where(group => LanguageRank(group.Language, preference) < 2);
            return candidates.OrderBy(group => LanguageRank(group.Language, preference))
                .ThenBy(group => group.Language, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Type, StringComparer.Ordinal).FirstOrDefault();
        }
        return candidates.OrderBy(group => pronunciation
                ? (group.Language?.Contains("-Latn", StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
                : (LanguageRank(group.Language, "en") < 2 ? 0 : 1))
            .ThenBy(group => group.Language, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Type, StringComparer.Ordinal).FirstOrDefault();
    }

    private static int LanguageRank(string? available, string requested)
    {
        if (string.Equals(available, requested, StringComparison.OrdinalIgnoreCase)) return 0;
        if (available is null) return 2;
        // A neutral tag can select a specific variant, and a neutral available track can
        // satisfy its variant. Different explicit variants/scripts are not interchangeable.
        if (!requested.Contains('-') && available.StartsWith(requested + "-", StringComparison.OrdinalIgnoreCase)) return 1;
        if (!available.Contains('-') && requested.StartsWith(available + "-", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }
}
