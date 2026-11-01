using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace AppleMusicLyrics.Core;

/// <summary>Parses the observed Apple Windows syllable-lyrics payload without network or file access.</summary>
public static class AppleNativeLyricsParser
{
    public const int MaximumPayloadCharacters = 4 * 1024 * 1024;
    private const int MaximumXmlCharacters = 2 * 1024 * 1024;
    private static readonly XNamespace Xml = XNamespace.Xml;

    public static bool TryParse(string json, out IReadOnlyList<NativeLyricsDocument> documents,
        out string? error)
    {
        try
        {
            documents = Parse(json);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or XmlException or FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            documents = [];
            // Do not include payload text, lyric text, or parser exception messages in logs.
            error = "The native lyric payload is incomplete, unsupported, or has ambiguous alignment.";
            return false;
        }
    }

    public static IReadOnlyList<NativeLyricsDocument> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumPayloadCharacters) throw new FormatException("Native payload is too large.");
        using var payload = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (payload.RootElement.ValueKind != JsonValueKind.Object ||
            !payload.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("Missing native lyric resources.");
        if (data.GetArrayLength() is 0 or > 64) throw new FormatException("Unsupported native lyric resource count.");

        var result = new List<NativeLyricsDocument>();
        foreach (var resource in data.EnumerateArray())
        {
            if (resource.ValueKind != JsonValueKind.Object ||
                !resource.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object ||
                !attributes.TryGetProperty("ttmlLocalizations", out var ttml) || ttml.ValueKind != JsonValueKind.String)
                throw new FormatException("Missing localized TTML.");
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            if (attributes.TryGetProperty("playParams", out var playParams) && playParams.ValueKind == JsonValueKind.Object)
                foreach (var parameter in playParams.EnumerateObject())
                    if (parameter.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        parameters.Add(parameter.Name, parameter.Value.ToString());
            var rawTtml = ttml.GetString()!;
            result.Add(ParseTtml(rawTtml, parameters.GetValueOrDefault("catalogId"),
                StringProperty(resource, "id"), StringProperty(resource, "type"), parameters));
        }
        return result;
    }

    private static NativeLyricsDocument ParseTtml(string rawTtml, string? catalogId,
        string? resourceId, string? resourceType, IReadOnlyDictionary<string, string> parameters)
    {
        if (rawTtml.Length > MaximumXmlCharacters) throw new FormatException("Native TTML is too large.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlCharacters,
            IgnoreWhitespace = false
        };
        using var stringReader = new StringReader(rawTtml);
        using var reader = XmlReader.Create(stringReader, settings);
        var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = xml.Root ?? throw new FormatException("Missing TTML root.");
        if (root.Name.LocalName != "tt") throw new FormatException("Unknown TTML root.");
        var elements = root.DescendantsAndSelf().Take(50001).ToArray();
        if (elements.Length > 50000 || elements.Any(element => element.Ancestors().Take(65).Count() > 64))
            throw new FormatException("Native TTML is too complex.");
        // Sequential timing requires a different interpretation; never silently treat it as absolute.
        if (elements.Any(element => Attribute(element, "timeContainer") == "seq"))
            throw new FormatException("Sequential TTML timing is unsupported.");

        var timing = TimingContext.Create(root);
        var bodies = root.Elements().Where(element => element.Name.LocalName == "body").Take(2).ToArray();
        if (bodies.Length != 1) throw new FormatException("Missing or ambiguous lyric body.");
        var body = bodies[0];
        var metadata = Attributes(root).ToDictionary(entry => "tt/" + entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (var attribute in Attributes(body)) metadata.Add("body/" + attribute.Key, attribute.Value);
        var originals = new List<NativeLyricLine>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paragraph in body.Descendants().Where(element => element.Name.LocalName == "p"))
        {
            var begin = ReadTime(paragraph, "begin", timing) ?? throw new FormatException("Missing lyric begin.");
            var end = ReadTime(paragraph, "end", timing);
            if (end < begin) throw new FormatException("Invalid lyric interval.");
            var key = Attribute(paragraph, "key") ?? "";
            if (key.Length > 0 && !keys.Add(key)) throw new FormatException("Duplicate original lyric key.");
            originals.Add(new(key, begin, end, Text(paragraph), Language(paragraph),
                Spans(paragraph, timing), Attributes(paragraph)));
        }
        if (originals.Count == 0) throw new FormatException("No synchronized native lyrics.");
        // Stable sorting retains simultaneous voices while binary selection follows original source order.
        var lines = originals.OrderBy(line => line.Begin).ToArray();
        var translations = Localizations(root, "translation", keys, timing);
        var pronunciations = Localizations(root, "transliteration", keys, timing);
        var duration = ReadTime(body, "dur", timing);
        return new(catalogId, resourceId, resourceType, parameters, rawTtml, metadata,
            lines, translations, pronunciations, duration);
    }

    private static IReadOnlyList<NativeLocalizedLyrics> Localizations(XElement root, string name,
        HashSet<string> originalKeys, TimingContext timing)
    {
        var result = new List<NativeLocalizedLyrics>();
        foreach (var group in root.Descendants().Where(element => element.Name.LocalName == name))
        {
            var lines = new List<NativeLocalizedLine>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var text in group.Elements().Where(element => element.Name.LocalName == "text"))
            {
                var key = Attribute(text, "for");
                if (string.IsNullOrEmpty(key) || !originalKeys.Contains(key) || !seen.Add(key))
                    throw new FormatException("Unresolved or duplicate supplemental lyric reference.");
                var begin = ReadTime(text, "begin", timing);
                var end = ReadTime(text, "end", timing);
                if (end < begin) throw new FormatException("Invalid supplemental interval.");
                lines.Add(new(key, Text(text), begin, end, Spans(text, timing), Attributes(text)));
            }
            result.Add(new(Language(group), Attribute(group, "type"),
                bool.TryParse(Attribute(group, "automaticallyCreated"), out var automatic) && automatic,
                lines, Attributes(group)));
        }
        return result;
    }

    private static IReadOnlyList<NativeLyricSpan> Spans(XElement parent, TimingContext timing) =>
        parent.Elements().Where(element => element.Name.LocalName == "span").Select(element =>
        {
            var begin = ReadTime(element, "begin", timing);
            var end = ReadTime(element, "end", timing);
            if (end < begin) throw new FormatException("Invalid span interval.");
            return new NativeLyricSpan(Text(element), begin, end, Language(element),
                Spans(element, timing), Attributes(element));
        }).ToArray();

    private static string Text(XElement element)
    {
        var text = new StringBuilder();
        AppendText(element, text);
        return text.ToString().Trim();
    }

    private static void AppendText(XElement element, StringBuilder target)
    {
        foreach (var node in element.Nodes())
            if (node is XText text) target.Append(text.Value);
            else if (node is XElement child)
            {
                if (child.Name.LocalName == "br") target.Append('\n');
                else AppendText(child, target);
            }
    }

    private static IReadOnlyDictionary<string, string> Attributes(XElement element) =>
        element.Attributes().ToDictionary(attribute => attribute.Name.ToString(), attribute => attribute.Value, StringComparer.Ordinal);

    private static string? Language(XElement element) =>
        element.AncestorsAndSelf().Select(ancestor => ancestor.Attribute(Xml + "lang")?.Value).FirstOrDefault(value => value is not null);

    private static string? Attribute(XElement element, string name)
    {
        var matches = element.Attributes().Where(attribute => attribute.Name.LocalName == name).Take(2).ToArray();
        if (matches.Length > 1) throw new FormatException("Ambiguous native lyric attribute.");
        return matches.FirstOrDefault()?.Value;
    }

    private static string? StringProperty(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString() : null;

    private static TimeSpan? ReadTime(XElement element, string name, TimingContext context)
    {
        var raw = Attribute(element, name);
        if (raw is null) return null;
        if (!TryTime(raw, context, out var time)) throw new FormatException("Unsupported TTML timestamp.");
        return time;
    }

    private static bool TryTime(string raw, TimingContext context, out TimeSpan time)
    {
        time = default;
        var value = raw.Trim();
        double seconds;
        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            if (parts.Length is < 2 or > 4) return false;
            var numbers = new double[parts.Length];
            for (var index = 0; index < parts.Length; index++)
                if (!double.TryParse(parts[index], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out numbers[index]) ||
                    !double.IsFinite(numbers[index]) || numbers[index] < 0) return false;
            if (parts.Length == 2)
            {
                if (numbers[1] >= 60 || numbers[0] != Math.Truncate(numbers[0])) return false;
                seconds = numbers[0] * 60 + numbers[1];
            }
            else
            {
                if (numbers[1] >= 60 || numbers[2] >= 60 || numbers[0] != Math.Truncate(numbers[0]) || numbers[1] != Math.Truncate(numbers[1])) return false;
                seconds = numbers[0] * 3600 + numbers[1] * 60 + numbers[2];
                if (parts.Length == 4)
                {
                    var frameParts = parts[3].Split('.');
                    if (frameParts.Length > 2 || numbers[2] != Math.Truncate(numbers[2]) ||
                        !int.TryParse(frameParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var frames) ||
                        frames >= context.NominalFrameRate) return false;
                    var subframes = 0;
                    if (frameParts.Length == 2 &&
                        (!int.TryParse(frameParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out subframes) ||
                         subframes >= context.SubFrameRate)) return false;
                    seconds += (frames + subframes / context.SubFrameRate) / context.FrameRate;
                }
            }
        }
        else
        {
            var units = new[] { "ms", "h", "m", "s", "f", "t" };
            var unit = units.FirstOrDefault(value.EndsWith);
            var number = unit is null ? value : value[..^unit.Length];
            if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return false;
            var scale = unit switch
            {
                "ms" => 0.001, "h" => 3600, "m" => 60, "f" => 1 / context.FrameRate,
                "t" when context.TickRate is > 0 => 1 / context.TickRate.Value,
                "t" => double.NaN, _ => 1
            };
            seconds = amount * scale;
        }
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds - 1) return false;
        time = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private sealed record TimingContext(double FrameRate, double NominalFrameRate, double SubFrameRate, double? TickRate)
    {
        public static TimingContext Create(XElement root)
        {
            var nominalFrameRate = Number(root, "frameRate") ?? 30;
            var frameRate = nominalFrameRate;
            var multiplier = Attribute(root, "frameRateMultiplier");
            if (multiplier is not null)
            {
                var parts = multiplier.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) ||
                    !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) || numerator <= 0 || denominator <= 0)
                    throw new FormatException("Invalid frame rate multiplier.");
                frameRate *= numerator / denominator;
            }
            if (!double.IsFinite(frameRate) || frameRate <= 0) throw new FormatException("Invalid frame rate.");
            return new(frameRate, nominalFrameRate, Number(root, "subFrameRate") ?? 1, Number(root, "tickRate"));
        }

        private static double? Number(XElement root, string name)
        {
            var raw = Attribute(root, name);
            if (raw is null) return null;
            if (!double.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
                throw new FormatException("Invalid timing parameter.");
            return value;
        }
    }
}
