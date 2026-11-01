using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AppleMusicLyrics.App;

public partial class SettingsWindow : Window
{
    private readonly OverlaySettings _settings;
    public event Action? SettingsApplied;
    public event Func<bool>? GlobalOffsetApplied;

    public SettingsWindow(OverlaySettings settings)
    {
        _settings = settings;
        InitializeComponent();
        UiText.Localize(this);
        FontFamilyBox.ItemsSource = Fonts.SystemFontFamilies.Select(family => family.Source).OrderBy(name => name).ToArray();
        FontFamilyBox.Text = settings.FontFamily;
        ActiveSizeBox.Text = settings.ActiveFontSize.ToString(CultureInfo.CurrentCulture);
        UpcomingSizeBox.Text = settings.UpcomingFontSize.ToString(CultureInfo.CurrentCulture);
        ActiveColorBox.Text = settings.ActiveColor;
        UpcomingColorBox.Text = settings.UpcomingColor;
        WidthBox.Text = settings.Width.ToString(CultureInfo.CurrentCulture);
        OpacitySlider.Value = settings.Opacity;
        LockedBox.IsChecked = settings.IsLocked;
        ShowTranslationBox.IsChecked = settings.ShowTranslation;
        ShowPronunciationBox.IsChecked = settings.ShowPronunciation;
        TranslationLanguageBox.Text = settings.TranslationLanguage;
        PronunciationLanguageBox.Text = settings.PronunciationLanguage;
        PositionModeBox.ItemsSource = Enum.GetValues<OverlayPositionMode>()
            .Select(mode => new PositionChoice(mode, UiText.T(mode.ToString()))).ToArray();
        PositionModeBox.SelectedValue = settings.PositionMode ?? OverlayPositionMode.Default;
        var monitors = NativeOverlay.GetDisplayChoices()
            .Select(monitor => new NativeOverlay.DisplayChoice(monitor.DeviceName,
                monitor.DeviceName.Length == 0 ? UiText.T(monitor.DisplayName)
                    : monitor.DisplayName.Replace(" · primary", UiText.T(" · primary"), StringComparison.Ordinal)))
            .ToList();
        if (settings.SelectedMonitor.Length > 0 && !monitors.Any(monitor => monitor.DeviceName == settings.SelectedMonitor))
            monitors.Add(new NativeOverlay.DisplayChoice(settings.SelectedMonitor, UiText.T("Unavailable display · primary fallback")));
        MonitorBox.ItemsSource = monitors;
        MonitorBox.SelectedValue = settings.SelectedMonitor;
        OffsetBox.Text = settings.GlobalOffsetMilliseconds.ToString(CultureInfo.CurrentCulture);
        UpdateGlobalOffsetStatus();
        ContactBox.Text = settings.LyricsClientContact;
        StartWithWindowsBox.IsChecked = settings.StartWithWindows;
    }

    private void OnApply(object sender, RoutedEventArgs e) => TryApply();

    public void RefreshGlobalOffset(bool saved = true)
    {
        OffsetBox.Text = _settings.GlobalOffsetMilliseconds.ToString(CultureInfo.CurrentCulture);
        UpdateGlobalOffsetStatus(saved);
    }

    private void OnApplyGlobalOffset(object sender, RoutedEventArgs e) => TryApplyGlobalOffset();

    private void OnStepGlobalOffset(object sender, RoutedEventArgs e)
    {
        if (!TryGlobalOffset(out var offset)) return;
        if (sender is not Button { Tag: string stepText }
            || !int.TryParse(stepText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var step)) return;
        var adjusted = offset + step;
        if (adjusted is < -60000 or > 60000)
        {
            Invalid("Global timing offset must stay between -60000 and 60000 milliseconds.");
            return;
        }
        OffsetBox.Text = adjusted.ToString(CultureInfo.CurrentCulture);
        TryApplyGlobalOffset();
    }

    private void OnPresetGlobalOffset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string presetText }
            || !int.TryParse(presetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var preset)) return;
        OffsetBox.Text = preset.ToString(CultureInfo.CurrentCulture);
        TryApplyGlobalOffset();
    }

    // A timing adjustment must not apply unrelated edits in the still-open settings form.
    private bool TryApplyGlobalOffset()
    {
        if (!TryGlobalOffset(out var offset)) return false;
        _settings.GlobalOffsetMilliseconds = offset;
        ErrorText.Visibility = Visibility.Collapsed;
        var saved = GlobalOffsetApplied?.Invoke() == true;
        UpdateGlobalOffsetStatus(saved);
        return true;
    }

    private bool TryGlobalOffset(out int offset)
    {
        if (int.TryParse(OffsetBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out offset)
            && offset is >= -60000 and <= 60000) return true;
        return Invalid("Global timing offset must be a whole number between -60000 and 60000 milliseconds.");
    }

    private void UpdateGlobalOffsetStatus(bool saved = true)
    {
        var offset = _settings.GlobalOffsetMilliseconds;
        var timing = offset == 0 ? UiText.T("no global adjustment")
            : string.Format(CultureInfo.CurrentCulture,
                UiText.T(offset > 0 ? "lyrics {0} ms earlier" : "lyrics {0} ms later"), Math.Abs(offset));
        GlobalOffsetStatus.Foreground = saved ? new SolidColorBrush(Color.FromRgb(53, 107, 67))
            : new SolidColorBrush(Color.FromRgb(185, 35, 53));
        GlobalOffsetStatus.Text = string.Format(CultureInfo.CurrentCulture, UiText.T(saved
            ? "Saved for all songs: {0}."
            : "Applied for all songs: {0}. Could not save; this adjustment may be lost after restarting."), timing);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (TryApply()) DialogResult = true;
    }

    private bool TryApply()
    {
        if (!TryNumber(ActiveSizeBox.Text, 14, 72, out var activeSize))
            return Invalid("Active line size must be between 14 and 72.");
        if (!TryNumber(UpcomingSizeBox.Text, 12, 64, out var upcomingSize))
            return Invalid("Additional line size must be between 12 and 64.");
        if (!TryNumber(WidthBox.Text, 360, 2400, out var width))
            return Invalid("Overlay width must be between 360 and 2400.");
        if (!TryColor(ActiveColorBox.Text) || !TryColor(UpcomingColorBox.Text))
            return Invalid("Enter a valid color, such as #FFFFFF or #B8FFFFFF.");
        if (string.IsNullOrWhiteSpace(FontFamilyBox.Text))
            return Invalid("Choose or enter a font name.");
        if (!TryGlobalOffset(out var offset)) return false;
        var contact = ContactBox.Text.Trim();
        if (contact.Length > 0 && !IsContact(contact))
            return Invalid("Enter a public HTTPS project URL or a valid contact email, or leave it empty for local lyrics.");
        if (!IsLanguageTag(TranslationLanguageBox.Text) || !IsLanguageTag(PronunciationLanguageBox.Text))
            return Invalid("Use a language tag such as en-US, ko-Latn, or ja-Kana, or leave it empty for automatic selection.");
        if (PositionModeBox.SelectedValue is not OverlayPositionMode positionMode)
            return Invalid("Choose Default or Custom positioning.");

        _settings.FontFamily = FontFamilyBox.Text.Trim();
        _settings.ActiveFontSize = activeSize;
        _settings.UpcomingFontSize = upcomingSize;
        _settings.ActiveColor = ActiveColorBox.Text.Trim();
        _settings.UpcomingColor = UpcomingColorBox.Text.Trim();
        _settings.Width = width;
        _settings.Opacity = OpacitySlider.Value;
        _settings.IsLocked = LockedBox.IsChecked == true;
        _settings.ShowTranslation = ShowTranslationBox.IsChecked == true;
        _settings.ShowPronunciation = ShowPronunciationBox.IsChecked == true;
        _settings.TranslationLanguage = TranslationLanguageBox.Text.Trim();
        _settings.PronunciationLanguage = PronunciationLanguageBox.Text.Trim();
        _settings.PositionMode = positionMode;
        _settings.SelectedMonitor = MonitorBox.SelectedValue as string ?? "";
        _settings.GlobalOffsetMilliseconds = offset;
        _settings.LyricsClientContact = contact;
        _settings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        ErrorText.Visibility = Visibility.Collapsed;
        SettingsApplied?.Invoke();
        UpdateGlobalOffsetStatus();
        return true;
    }

    private bool Invalid(string message)
    {
        ErrorText.Text = UiText.T(message);
        ErrorText.Visibility = Visibility.Visible;
        return false;
    }

    private static bool TryNumber(string text, double minimum, double maximum, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        && double.IsFinite(value) && value >= minimum && value <= maximum;

    private static bool TryColor(string text)
    {
        try { return ColorConverter.ConvertFromString(text.Trim()) is Color; }
        catch (FormatException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static bool IsContact(string text)
    {
        if (text.Any(char.IsWhiteSpace) || text.Contains('(') || text.Contains(')')) return false;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == "https" && !string.IsNullOrWhiteSpace(uri.Host))
            return true;
        return System.Net.Mail.MailAddress.TryCreate(text, out var email) && email.Address == text;
    }

    private static bool IsLanguageTag(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return true;
        if (text.Length > 100) return false;
        // Do not use CultureInfo: script tags provided by Apple need not be installed cultures.
        var parts = text.Split('-');
        return parts[0].Length is >= 2 and <= 8 && parts[0].All(char.IsAsciiLetter)
            && parts.Skip(1).All(part => part.Length is >= 1 and <= 8 && part.All(char.IsAsciiLetterOrDigit));
    }

    private sealed record PositionChoice(OverlayPositionMode Value, string DisplayName);
}
