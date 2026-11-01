using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AppleMusicLyrics.Core;
using Forms = System.Windows.Forms;

namespace AppleMusicLyrics.App;

/// <summary>Window styles and monitor placement. All Win32 coordinates remain physical pixels.</summary>
internal sealed class NativeOverlay : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExLayered = 0x00080000;
    private const long WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;
    private const int MaNoActivate = 3;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly nint HwndTopmost = new(-1);
    private readonly Window _window;
    private readonly HwndSource _source;
    private readonly nint _handle;
    private readonly Action _monitorChanged;
    private bool _disposed;
    private int _placementGeneration;

    public NativeOverlay(Window window, Action monitorChanged)
    {
        _window = window;
        _monitorChanged = monitorChanged;
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("The overlay window must be initialized first.");
        _source.AddHook(WindowProcedure);
    }

    public void SetLocked(bool locked)
    {
        var style = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        style |= WsExLayered | WsExToolWindow | WsExNoActivate;
        style = locked ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(_handle, GwlExStyle, new nint(style));
        SetWindowPos(_handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoSize | 0x0002 /* SWP_NOMOVE */ | SwpNoActivate | SwpFrameChanged | SwpNoOwnerZOrder);
    }

    public Size CurrentWorkAreaSizeDip()
    {
        var work = GetWorkArea(MonitorFromWindow(_handle, MonitorDefaultToNearest));
        var scale = Math.Max(1, GetDpiForWindow(_handle)) / 96.0;
        return new Size((work.Right - work.Left) / scale, (work.Bottom - work.Top) / scale);
    }

    internal sealed record DisplayChoice(string DeviceName, string DisplayName);

    public static IReadOnlyList<DisplayChoice> GetDisplayChoices()
    {
        var choices = new List<DisplayChoice> { new("", "Primary display (automatic)") };
        foreach (var screen in Forms.Screen.AllScreens.OrderByDescending(screen => screen.Primary).ThenBy(screen => screen.DeviceName))
            choices.Add(new(screen.DeviceName, $"{screen.DeviceName} · {screen.Bounds.Width} × {screen.Bounds.Height}" + (screen.Primary ? " · primary" : "")));
        return choices;
    }

    public DesktopRect? GetPhysicalBounds() => GetWindowRect(_handle, out var bounds)
        ? new DesktopRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top) : null;

    public void ClampToVisibleWorkArea()
    {
        if (!GetWindowRect(_handle, out var bounds)) return;
        var work = GetWorkArea(MonitorFromWindow(_handle, MonitorDefaultToNearest));
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var position = OverlayPlacement.Clamp(ToDesktopRect(work), width, height, new DesktopPoint(bounds.Left, bounds.Top));
        MovePhysical(position.X, position.Y);
    }

    public void PositionAtBottomCenter(string selectedMonitor, Action completed)
    {
        var target = Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName.Equals(selectedMonitor, StringComparison.OrdinalIgnoreCase))
            ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        PlaceOnMonitor(target, (work, width, height) => OverlayPlacement.BottomCenter(work, width, height,
            (int)Math.Round(54 * Math.Max(1, GetDpiForWindow(_handle)) / 96.0)), completed);
    }

    public void RestoreCustomPosition(int left, int bottom, Action completed)
    {
        var target = Forms.Screen.FromPoint(new System.Drawing.Point(left, bottom - 1));
        PlaceOnMonitor(target, (work, width, height) => OverlayPlacement.Clamp(work, width, height,
            new DesktopPoint(left, bottom - height)), completed);
    }

    private void PlaceOnMonitor(Forms.Screen target, Func<DesktopRect, int, int, DesktopPoint> position, Action completed)
    {
        var generation = ++_placementGeneration;
        // Move to the target in physical pixels first, so WPF adopts its DPI before measuring.
        if (!Forms.Screen.FromHandle(_handle).DeviceName.Equals(target.DeviceName, StringComparison.OrdinalIgnoreCase))
            MovePhysical(target.WorkingArea.Left, target.WorkingArea.Top);
        _window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || generation != _placementGeneration) return;
            var currentTarget = Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == target.DeviceName)
                ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
            var work = currentTarget.WorkingArea;
            var scale = Math.Max(1, GetDpiForWindow(_handle)) / 96.0;
            _window.MaxWidth = Math.Max(100, work.Width / scale);
            _window.MaxHeight = Math.Max(80, work.Height / scale);
            _window.UpdateLayout();
            if (!GetWindowRect(_handle, out var bounds)) { completed(); return; }
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            var finalPosition = position(new DesktopRect(work.Left, work.Top, work.Width, work.Height), width, height);
            MovePhysical(finalPosition.X, finalPosition.Y);
            completed();
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void MovePhysical(int left, int top)
    {
        // Let WPF process WM_WINDOWPOSCHANGED rather than dividing global desktop
        // coordinates by one monitor's DPI, which is incorrect on mixed-DPI desktops.
        SetWindowPos(_handle, HwndTopmost, left, top, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private static DesktopRect ToDesktopRect(NativeRect bounds) =>
        new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RemoveHook(WindowProcedure);
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return MaNoActivate;
        }
        if (message is WmDisplayChange or WmSettingChange or WmDpiChanged)
            _window.Dispatcher.BeginInvoke(_monitorChanged, System.Windows.Threading.DispatcherPriority.Loaded);
        return nint.Zero;
    }

    private static NativeRect GetWorkArea(nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return info.Work;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
