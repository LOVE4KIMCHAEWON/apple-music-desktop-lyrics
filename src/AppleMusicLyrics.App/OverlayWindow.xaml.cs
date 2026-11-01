using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AppleMusicLyrics.App;

public partial class OverlayWindow : Window
{
    private readonly OverlaySettings _settings;
    private NativeOverlay? _native;
    private bool _applying;
    private bool _resetOnLoad;
    private bool _initialPlacementComplete;
    private bool _positioning;
    private bool _dragging;
    private double? _bottomToRestore;
    private (int Left, int Bottom)? _physicalPositionToRestore;
    public event Action? SettingsChanged;

    public OverlayWindow(OverlaySettings settings)
    {
        _settings = settings;
        InitializeComponent();
        UiText.Localize(this);
        ApplySettings();
        SourceInitialized += OnSourceInitialized;
        LocationChanged += OnLocationChanged;
        SizeChanged += OnOverlaySizeChanged;
        Loaded += (_, _) =>
        {
            if (_resetOnLoad) ScheduleInitialPlacement();
            else CompleteRestoredPlacement();
        };
        // Windows/WPF can apply their first-show placement after Loaded. Wait until the
        // initial content is rendered, then run after pending layout/DPI work has settled.
        ContentRendered += OnFirstContentRendered;
        IsVisibleChanged += (_, _) => { if (IsVisible && _resetOnLoad) ScheduleInitialPlacement(); };
        Closed += (_, _) => _native?.Dispose();
    }

    public void SetLyrics(string active, string secondary, string? tertiary = null)
    {
        // Flatten embedded newlines so each supplied item occupies exactly one visible row.
        active = OneLine(active);
        secondary = OneLine(secondary);
        tertiary = OneLine(tertiary);
        if (ActiveLine.Text == active && UpcomingLine.Text == secondary && TertiaryLine.Text == tertiary) return;
        ActiveLine.Text = active;
        UpcomingLine.Text = secondary;
        TertiaryLine.Text = tertiary;
        TertiaryLine.Visibility = tertiary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Show the selected line at full opacity immediately; a transition must
        // never add perceptual delay after a seek or timing adjustment.
    }

    public void SetLocked(bool locked)
    {
        _settings.IsLocked = locked;
        EditControls.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        EditOutline.BorderBrush = locked ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
        EditOutline.Background = locked ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(170, 22, 25, 34));
        Cursor = locked ? Cursors.Arrow : Cursors.SizeAll;
        _native?.SetLocked(locked);
        Dispatcher.BeginInvoke(new Action(KeepOnScreen), System.Windows.Threading.DispatcherPriority.Loaded);
        if (!_applying) SettingsChanged?.Invoke();
    }

    public void ApplySettings()
    {
        _applying = true;
        try
        {
            _settings.Sanitize();
            Width = _settings.Width;
            Opacity = _settings.Opacity;
            FontFamily family;
            try { family = new FontFamily(_settings.FontFamily); }
            catch (ArgumentException) { family = new FontFamily("Segoe UI"); }
            ActiveLine.FontFamily = family;
            UpcomingLine.FontFamily = family;
            TertiaryLine.FontFamily = family;
            ActiveLine.FontSize = _settings.ActiveFontSize;
            UpcomingLine.FontSize = _settings.UpcomingFontSize;
            TertiaryLine.FontSize = _settings.UpcomingFontSize;
            ActiveLine.MinHeight = Math.Ceiling(_settings.ActiveFontSize * 1.4);
            UpcomingLine.MinHeight = Math.Ceiling(_settings.UpcomingFontSize * 1.4);
            TertiaryLine.MinHeight = Math.Ceiling(_settings.UpcomingFontSize * 1.4);
            ActiveLine.Foreground = ParseBrush(_settings.ActiveColor, Colors.White);
            UpcomingLine.Foreground = ParseBrush(_settings.UpcomingColor, Color.FromArgb(184, 255, 255, 255));
            TertiaryLine.Foreground = ParseBrush(_settings.UpcomingColor, Color.FromArgb(184, 255, 255, 255));
            if (_native is null)
            {
                _bottomToRestore = _settings.Bottom;
                if (_settings.PhysicalLeft is { } physicalLeft && _settings.PhysicalBottom is { } physicalBottom)
                    _physicalPositionToRestore = (physicalLeft, physicalBottom);
                _resetOnLoad = _settings.PositionMode == OverlayPositionMode.Default
                    || (!_physicalPositionToRestore.HasValue && (!_settings.Left.HasValue || (!_settings.Top.HasValue && !_bottomToRestore.HasValue)));
                if (!_resetOnLoad)
                {
                    Left = _settings.Left ?? 0;
                    // Older settings contain only Top; retain that location on their first load.
                    Top = _settings.Top ?? 0;
                }
            }
            SetLocked(_settings.IsLocked);
            KeepOnScreen();
        }
        finally { _applying = false; }
    }

    public void ResetPosition()
    {
        _settings.PositionMode = OverlayPositionMode.Default;
        _bottomToRestore = null;
        _physicalPositionToRestore = null;
        if (_native is null) { _resetOnLoad = true; return; }
        _resetOnLoad = false;
        _positioning = true;
        _native.PositionAtBottomCenter(_settings.SelectedMonitor, () =>
        {
            _positioning = false;
            _initialPlacementComplete = true;
            SavePosition();
        });
    }

    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        if (_resetOnLoad)
            ScheduleInitialPlacement();
        else CompleteRestoredPlacement();
    }

    private void CompleteRestoredPlacement()
    {
        if (_positioning) return;
        if (_physicalPositionToRestore is { } physical && _native is not null)
        {
            _physicalPositionToRestore = null;
            _bottomToRestore = null;
            _positioning = true;
            _native.RestoreCustomPosition(physical.Left, physical.Bottom, () =>
            {
                _positioning = false;
                _initialPlacementComplete = true;
                KeepOnScreen();
            });
            return;
        }
        if (_bottomToRestore is { } bottom && ActualHeight > 0)
        {
            // Restore against the first measured height. Later 2/3-row changes then retain
            // this same bottom edge instead of subtracting another row on every restart.
            _positioning = true;
            try { Top = bottom - ActualHeight; }
            finally { _positioning = false; }
            _bottomToRestore = null;
        }
        _initialPlacementComplete = true;
        KeepOnScreen();
    }

    private void ScheduleInitialPlacement() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_resetOnLoad && _native is not null && IsVisible) ResetPosition();
    }), System.Windows.Threading.DispatcherPriority.ContextIdle);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _native = new NativeOverlay(this, KeepOnScreen);
        _native.SetLocked(_settings.IsLocked);
    }

    private void KeepOnScreen()
    {
        if (_native is null || !IsLoaded || _positioning || _dragging) return;
        if (_settings.PositionMode == OverlayPositionMode.Default)
        {
            ResetPosition();
            return;
        }
        var area = _native.CurrentWorkAreaSizeDip();
        MaxWidth = Math.Max(100, area.Width);
        MaxHeight = Math.Max(80, area.Height);
        UpdateLayout();
        _native.ClampToVisibleWorkArea();
        SavePosition();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (IsLoaded) SavePosition();
    }

    private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the user's bottom anchor while a third line, edit controls, or font changes
        // alter the window height. Initial/explicit monitor placement owns its own position.
        if (!_initialPlacementComplete || _positioning || _dragging || !IsLoaded || !e.HeightChanged
            || e.PreviousSize.Height <= 0 || !double.IsFinite(Top)) return;
        if (_settings.PositionMode == OverlayPositionMode.Default)
        {
            Dispatcher.BeginInvoke(new Action(KeepOnScreen), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        _positioning = true;
        try { Top += e.PreviousSize.Height - e.NewSize.Height; }
        finally { _positioning = false; }
        Dispatcher.BeginInvoke(new Action(KeepOnScreen), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SavePosition()
    {
        if (!_initialPlacementComplete || _positioning) return;
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || ActualHeight <= 0) return;
        var bottom = Top + ActualHeight;
        var physical = _native?.GetPhysicalBounds();
        var physicalBottom = physical is { } bounds ? bounds.Top + bounds.Height : (int?)null;
        if (_settings.Left == Left && _settings.Top == Top && _settings.Bottom == bottom
            && _settings.PhysicalLeft == physical?.Left && _settings.PhysicalBottom == physicalBottom) return;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Bottom = bottom;
        _settings.PhysicalLeft = physical?.Left;
        _settings.PhysicalBottom = physicalBottom;
        if (!_applying) SettingsChanged?.Invoke();
    }

    private void OnDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.IsLocked || e.ChangedButton != MouseButton.Left || e.OriginalSource is System.Windows.Controls.Button) return;
        var before = _native?.GetPhysicalBounds();
        _dragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { /* The mouse may be released before Windows starts a drag. */ }
        finally { _dragging = false; }
        var after = _native?.GetPhysicalBounds();
        if (before is { } oldBounds && after is { } newBounds
            && (oldBounds.Left != newBounds.Left || oldBounds.Top != newBounds.Top))
        {
            _settings.PositionMode = OverlayPositionMode.Custom;
            SettingsChanged?.Invoke();
        }
        KeepOnScreen();
        e.Handled = true;
    }

    private void OnLockClick(object sender, RoutedEventArgs e) { SetLocked(true); e.Handled = true; }

    private static Brush ParseBrush(string color, Color fallback)
    {
        try { return new SolidColorBrush(ColorConverter.ConvertFromString(color) is Color parsed ? parsed : fallback); }
        catch (FormatException) { return new SolidColorBrush(fallback); }
        catch (NotSupportedException) { return new SolidColorBrush(fallback); }
    }

    private static string OneLine(string? text) => (text ?? "").Replace('\r', ' ').Replace('\n', ' ')
        .Replace('\u0085', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ').Trim();
}
