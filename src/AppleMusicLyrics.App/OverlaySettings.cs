using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AppleMusicLyrics.App;

public enum OverlayPositionMode { Default, Custom }

/// <summary>Portable user preferences. Coordinates and sizes are WPF device-independent pixels.</summary>
public sealed class OverlaySettings
{
    /// <summary>Empty follows the portable edition default; independent of lyric language preferences.</summary>
    public string UiLanguage { get; set; } = "";
    public double? Left { get; set; }
    public double? Top { get; set; }
    /// <summary>Persisted bottom edge prevents height-dependent position drift across restarts.</summary>
    public double? Bottom { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverlayPositionMode? PositionMode { get; set; }
    /// <summary>Win32 display device name; empty follows the primary display in default mode.</summary>
    public string SelectedMonitor { get; set; } = "";
    public int? PhysicalLeft { get; set; }
    public int? PhysicalBottom { get; set; }
    public double Width { get; set; } = 1000;
    public string FontFamily { get; set; } = "Segoe UI";
    public double ActiveFontSize { get; set; } = 28;
    public double UpcomingFontSize { get; set; } = 23;
    public string ActiveColor { get; set; } = "#FFFFFF";
    public string UpcomingColor { get; set; } = "#B8FFFFFF";
    public double Opacity { get; set; } = 1;
    public bool IsLocked { get; set; } = true;
    public bool IsVisible { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool ShowTranslation { get; set; }
    public bool ShowPronunciation { get; set; }
    /// <summary>Preferred BCP 47 language tag; empty selects an available native language automatically.</summary>
    public string TranslationLanguage { get; set; } = "";
    public string PronunciationLanguage { get; set; } = "";
    public string LyricsClientContact { get; set; } = "https://github.com/LOVE4KIMCHAEWON";
    public int GlobalOffsetMilliseconds { get; set; }
    public Dictionary<string, int> TrackOffsetsMilliseconds { get; set; } = new(StringComparer.Ordinal);

    public void Sanitize()
    {
        UiLanguage = UiText.NormalizeLanguage(UiLanguage) ?? "";
        Left = Left is { } left && double.IsFinite(left) ? Math.Clamp(left, -1000000, 1000000) : null;
        Top = Top is { } top && double.IsFinite(top) ? Math.Clamp(top, -1000000, 1000000) : null;
        Bottom = Bottom is { } bottom && double.IsFinite(bottom) ? Math.Clamp(bottom, -1000000, 1000000) : null;
        PositionMode = PositionMode is { } mode && Enum.IsDefined(mode) ? mode
            : Left.HasValue && Top.HasValue ? OverlayPositionMode.Custom : OverlayPositionMode.Default;
        SelectedMonitor = (SelectedMonitor ?? "").Trim();
        PhysicalLeft = PhysicalLeft is { } physicalLeft ? Math.Clamp(physicalLeft, -1000000, 1000000) : null;
        PhysicalBottom = PhysicalBottom is { } physicalBottom ? Math.Clamp(physicalBottom, -1000000, 1000000) : null;
        Width = FiniteClamp(Width, 360, 2400, 1000);
        ActiveFontSize = FiniteClamp(ActiveFontSize, 14, 72, 28);
        UpcomingFontSize = FiniteClamp(UpcomingFontSize, 12, 64, 23);
        Opacity = FiniteClamp(Opacity, 0.2, 1, 1);
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily.Trim();
        ActiveColor = string.IsNullOrWhiteSpace(ActiveColor) ? "#FFFFFF" : ActiveColor.Trim();
        UpcomingColor = string.IsNullOrWhiteSpace(UpcomingColor) ? "#B8FFFFFF" : UpcomingColor.Trim();
        LyricsClientContact = (LyricsClientContact ?? "").Trim();
        TranslationLanguage = (TranslationLanguage ?? "").Trim();
        PronunciationLanguage = (PronunciationLanguage ?? "").Trim();
        GlobalOffsetMilliseconds = Math.Clamp(GlobalOffsetMilliseconds, -60000, 60000);
        TrackOffsetsMilliseconds ??= new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in TrackOffsetsMilliseconds.Keys.ToArray())
            TrackOffsetsMilliseconds[key] = Math.Clamp(TrackOffsetsMilliseconds[key], -60000, 60000);
    }

    private static double FiniteClamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
