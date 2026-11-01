namespace AppleMusicLyrics.Core;

/// <summary>Apple-supplied data only. Raw TTML and attributes retain unrecognized metadata.</summary>
public sealed record NativeLyricsDocument(
    string? CatalogId,
    string? ResourceId,
    string? ResourceType,
    IReadOnlyDictionary<string, string> PlayParameters,
    string RawTtml,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<NativeLyricLine> Lines,
    IReadOnlyList<NativeLocalizedLyrics> Translations,
    IReadOnlyList<NativeLocalizedLyrics> Pronunciations,
    TimeSpan? Duration)
{
    public LyricsResult ToLyricsResult() => new(
        Lines.Select(line => new LyricLine(line.Begin, line.Text)).ToArray(), null, false,
        "Apple Music native cache");
}

public sealed record NativeLyricLine(
    string Key, TimeSpan Begin, TimeSpan? End, string Text, string? Language,
    IReadOnlyList<NativeLyricSpan> Spans, IReadOnlyDictionary<string, string> Metadata);

public sealed record NativeLyricSpan(
    string Text, TimeSpan? Begin, TimeSpan? End, string? Language,
    IReadOnlyList<NativeLyricSpan> Children, IReadOnlyDictionary<string, string> Metadata);

public sealed record NativeLocalizedLyrics(
    string? Language, string? Type, bool AutomaticallyCreated,
    IReadOnlyList<NativeLocalizedLine> Lines, IReadOnlyDictionary<string, string> Metadata);

public sealed record NativeLocalizedLine(
    string OriginalKey, string Text, TimeSpan? Begin, TimeSpan? End,
    IReadOnlyList<NativeLyricSpan> Spans, IReadOnlyDictionary<string, string> Metadata);

public enum PresentedLyricKind { Original, Upcoming, Pronunciation, Translation }

public sealed record PresentedLyricLine(string Text, PresentedLyricKind Kind, string? Language = null);

public sealed record LyricPresentationResult(
    IReadOnlyList<PresentedLyricLine> Lines, string? Limitation, int ActiveIndex,
    string? TranslationLanguage = null, string? PronunciationLanguage = null);
