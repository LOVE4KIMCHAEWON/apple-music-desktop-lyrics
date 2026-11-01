using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AppleMusicLyrics.Core;

public static partial class MetadataNormalizer
{
    /// <summary>Fix Apple's observed artist + em-dash + album representation only when album is absent.</summary>
    public static TrackMetadata Prepare(TrackMetadata track)
    {
        var title = Clean(track.Title);
        var artist = Clean(track.Artist);
        var album = Clean(track.Album);
        if (album.Length == 0)
        {
            // A hyphen can belong to an artist's name. Only the observed spaced em dash is accepted.
            var separator = artist.IndexOf(" — ", StringComparison.Ordinal);
            if (separator > 0 && separator == artist.LastIndexOf(" — ", StringComparison.Ordinal))
            {
                album = artist[(separator + 3)..].Trim();
                artist = artist[..separator].Trim();
            }
        }
        return new TrackMetadata(title, artist, album, track.DurationSeconds);
    }

    public static string Normalize(string? value)
    {
        var builder = new StringBuilder();
        foreach (var rune in Clean(value).Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetterOrDigit(rune) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
                builder.Append(Rune.ToLowerInvariant(rune));
            else if (rune.Value is '\'' or '’' or '‘' or '`')
                continue;
            else
                builder.Append(' ');
        }
        return Whitespace().Replace(builder.ToString(), " ").Trim();
    }

    public static string Clean(string? value) => Whitespace().Replace((value ?? "").Trim().Trim('\uFEFF'), " ");

    /// <summary>Preserve version qualifiers. A studio recording must not silently receive a live/remix sheet.</summary>
    public static string VersionSignature(string? title, string? album)
    {
        var fragments = Qualifier().Matches(title ?? "").Select(match => match.Value).ToList();
        if (!string.IsNullOrWhiteSpace(album)) fragments.Add(album);
        var text = Normalize(string.Join(' ', fragments));
        var markers = VersionWord().Matches(text).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        if (CjkLive().IsMatch(text)) markers.Add("live");
        if (CjkRemix().IsMatch(text)) markers.Add("remix");
        if (CjkCover().IsMatch(text)) markers.Add("cover");
        if (CjkInstrumental().IsMatch(text)) markers.Add("instrumental");
        return string.Join('|', markers.Order(StringComparer.Ordinal));
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\s[-–—:]\s.*$", RegexOptions.CultureInvariant)]
    private static partial Regex Qualifier();
    [GeneratedRegex(@"\b(live|remix|mix|cover|acoustic|instrumental|karaoke|demo|remaster(?:ed)?|radio|edit|extended|sped|slowed|revisited|rerecorded|version|mono|stereo)\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionWord();
    [GeneratedRegex("现场|現場|ライブ|라이브", RegexOptions.CultureInvariant)]
    private static partial Regex CjkLive();
    [GeneratedRegex("混音|リミックス|리믹스", RegexOptions.CultureInvariant)]
    private static partial Regex CjkRemix();
    [GeneratedRegex("翻唱|カバー|커버", RegexOptions.CultureInvariant)]
    private static partial Regex CjkCover();
    [GeneratedRegex("伴奏|纯音乐|純音樂|インストゥルメンタル|연주곡", RegexOptions.CultureInvariant)]
    private static partial Regex CjkInstrumental();
}
