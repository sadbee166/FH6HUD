using System.Runtime.InteropServices;
using ForzaHud.Configuration;
using ForzaHud.Rendering;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace ForzaHud.Platform.Windows;

/// <summary>
/// Owns the transparent Windows overlay window: borderless, always on top, click-through,
/// absent from the taskbar, never stealing focus, and DPI aware.
///
/// Drawing and presentation are delegated to a DirectComposition presenter so the HUD
/// renderer remains independent of the presentation technology.
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    private const string WindowClassName = "ForzaHudOverlayWindow";
    private const int ExitHotKeyId = 1;
    private const int CalibrationHotKeyId = 2;
    private const int DeleteCalibrationHotKeyId = 3;
    private const int ReloadHotKeyId = 4;
    private const uint VirtualKeyH = 0x48;
    private const uint VirtualKeyK = 0x4B;
    private const uint VirtualKeyL = 0x4C;

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private static NativeMethods.WndProc? _windowProcedure;
    private static OverlayWindow? _activeWindow;

    private readonly HudConfiguration _configuration;
    private readonly ID2D1Factory _factory;
    private readonly IDWriteFactory _textFactory;

    private IntPtr _handle;
    private DirectCompositionPresenter? _presenter;
    private int _pixelWidth;
    private int _pixelHeight;
    private int _activeMonitorIndex;
    private bool _calibrationHotkeyRegistered;
    private bool _presenterRefreshPending;

    /// <param name="configuration">HUD configuration.</param>
    /// <param name="factory">Shared Direct2D factory.</param>
    /// <param name="textFactory">Shared DirectWrite factory.</param>
    public OverlayWindow(HudConfiguration configuration, ID2D1Factory factory, IDWriteFactory textFactory)
    {
        _configuration = configuration;
        _factory = factory;
        _textFactory = textFactory;
    }

    /// <summary>
    /// Called once per rendered frame. The handler draws through the presentation-independent
    /// rendering API.
    /// </summary>
    public event Action<IRenderContext>? Render;

    /// <summary>Called when the window has been destroyed and the loop is about to exit.</summary>
    public event Action? Closed;

    /// <summary>Called by the configured global hotkey to toggle calibration recording.</summary>
    public event Action? CalibrationToggleRequested;

    /// <summary>Called by Ctrl+Alt+K to delete calibration records for the current vehicle.</summary>
    public event Action? CalibrationDeleteRequested;

    /// <summary>Called by Ctrl+Alt+L to restart the utility.</summary>
    public event Action? ReloadRequested;

    /// <summary>Window handle, valid once <see cref="Run"/> has been called.</summary>
    public IntPtr Handle => _handle;

    /// <summary>
    /// Creates the window and runs the message and render loop until the window is closed
    /// or a control hotkey is pressed. Ctrl+Alt+H exits, Ctrl+Alt+K deletes the current
    /// vehicle's calibration records, and Ctrl+Alt+L restarts the utility. Valid edits to the
    /// active configuration file are applied automatically before the next HUD frame. The
    /// calibration hotkey is registered from <see cref="CalibrationSettings.ToggleHotkey"/>
    /// only when live calibration is disabled.
    /// </summary>
    public void Run()
    {
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        RegisterWindowClass();

        var monitor = MonitorHelper.Get(_configuration.Overlay.Monitor);
        _activeMonitorIndex = _configuration.Overlay.Monitor;
        _pixelWidth = monitor.Width;
        _pixelHeight = monitor.Height;

        var extendedStyle = GetExtendedStyle();

        _handle = NativeMethods.CreateWindowExW(
            extendedStyle,
            WindowClassName,
            "ForzaHud",
            NativeMethods.WS_POPUP,
            monitor.Left, monitor.Top, monitor.Width, monitor.Height,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Failed to create the overlay window (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        NativeMethods.SetWindowPos(
            _handle, NativeMethods.HWND_TOPMOST,
            monitor.Left, monitor.Top, monitor.Width, monitor.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        var exitRegistered = false;
        var deleteCalibrationRegistered = false;
        var reloadRegistered = false;
        try
        {
            exitRegistered = NativeMethods.RegisterHotKey(
                _handle, ExitHotKeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
                VirtualKeyH);

            RegisterCalibrationHotkey();

            deleteCalibrationRegistered = NativeMethods.RegisterHotKey(
                _handle,
                DeleteCalibrationHotKeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
                VirtualKeyK);

            reloadRegistered = NativeMethods.RegisterHotKey(
                _handle,
                ReloadHotKeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
                VirtualKeyL);

            _activeWindow = this;
            MessageLoop();
        }
        finally
        {
            if (reloadRegistered)
            {
                NativeMethods.UnregisterHotKey(_handle, ReloadHotKeyId);
            }

            if (deleteCalibrationRegistered)
            {
                NativeMethods.UnregisterHotKey(_handle, DeleteCalibrationHotKeyId);
            }

            UnregisterCalibrationHotkey();

            if (exitRegistered)
            {
                NativeMethods.UnregisterHotKey(_handle, ExitHotKeyId);
            }

            if (ReferenceEquals(_activeWindow, this))
            {
                _activeWindow = null;
            }
        }

        Closed?.Invoke();
    }

    internal static uint GetExtendedStyle() =>
        NativeMethods.WS_EX_TOPMOST
        | NativeMethods.WS_EX_TRANSPARENT
        | NativeMethods.WS_EX_TOOLWINDOW
        | NativeMethods.WS_EX_NOACTIVATE
        | NativeMethods.WS_EX_LAYERED;

    /// <summary>Applies configuration changes that affect the window or its presentation.</summary>
    public void ApplyConfiguration()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        if (_activeMonitorIndex != _configuration.Overlay.Monitor)
        {
            var monitor = MonitorHelper.Get(_configuration.Overlay.Monitor);
            _activeMonitorIndex = _configuration.Overlay.Monitor;
            _pixelWidth = monitor.Width;
            _pixelHeight = monitor.Height;
            NativeMethods.SetWindowPos(
                _handle,
                NativeMethods.HWND_TOPMOST,
                monitor.Left,
                monitor.Top,
                monitor.Width,
                monitor.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        UnregisterCalibrationHotkey();
        RegisterCalibrationHotkey();

        // The DirectWrite formats cache the configured font family. Recreate the presenter so
        // a font change takes effect on the next frame. Defer disposal when this is called from
        // the active render callback; disposing the current render target there invalidates the
        // context before DirectCompositionPresenter.Render has finished with it.
        if (_presenter is not null)
        {
            _presenterRefreshPending = true;
        }
    }

    /// <summary>Stops the message loop on the window's thread.</summary>
    public void RequestClose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.PostQuitMessage(0);
        }
    }

    private void MessageLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var nextFrame = clock.Elapsed;

        while (true)
        {
            var frameInterval = TimeSpan.FromSeconds(
                1d / Math.Clamp(_configuration.Overlay.TargetFramesPerSecond, 10, 480));
            var waitMilliseconds = WaitMilliseconds(nextFrame - clock.Elapsed);
            var waitResult = NativeMethods.MsgWaitForMultipleObjectsEx(
                0,
                null,
                waitMilliseconds,
                NativeMethods.QS_ALLINPUT,
                NativeMethods.MWMO_INPUTAVAILABLE);

            if (waitResult == uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"MsgWaitForMultipleObjectsEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            if (ProcessMessages())
            {
                return;
            }

            if (clock.Elapsed < nextFrame)
            {
                continue;
            }

            nextFrame += frameInterval;
            if (nextFrame < clock.Elapsed)
            {
                nextFrame = clock.Elapsed + frameInterval;
            }

            RenderFrame();
        }
    }

    private void RegisterCalibrationHotkey()
    {
        if (_handle == IntPtr.Zero
            || _configuration.Calibration.Live.Enabled
            || !HotkeyDefinition.TryParse(_configuration.Calibration.ToggleHotkey, out var hotkey))
        {
            return;
        }

        _calibrationHotkeyRegistered = NativeMethods.RegisterHotKey(
            _handle,
            CalibrationHotKeyId,
            hotkey.Modifiers | NativeMethods.MOD_NOREPEAT,
            hotkey.VirtualKey);
    }

    private void UnregisterCalibrationHotkey()
    {
        if (_calibrationHotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_handle, CalibrationHotKeyId);
            _calibrationHotkeyRegistered = false;
        }
    }

    private static uint WaitMilliseconds(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return 0;
        }

        var milliseconds = (long)Math.Ceiling(delay.TotalMilliseconds);
        return (uint)Math.Clamp(milliseconds, 1L, uint.MaxValue);
    }

    private static bool ProcessMessages()
    {
        while (NativeMethods.PeekMessageW(out var message, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
        {
            if (message.Message == NativeMethods.WM_QUIT)
            {
                return true;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }

        return false;
    }

    private void RenderFrame()
    {
        var dpi = NativeMethods.GetDpiForWindow(_handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        if (NativeMethods.GetClientRect(_handle, out var clientRect)
            && clientRect.Width > 0
            && clientRect.Height > 0)
        {
            _pixelWidth = clientRect.Width;
            _pixelHeight = clientRect.Height;
        }

        _presenter ??= new DirectCompositionPresenter(_handle, _factory, _textFactory, _configuration.Visual);
        var presenter = _presenter;

        presenter.Resize(_pixelWidth, _pixelHeight, dpi);
        try
        {
            presenter.Render(context => Render?.Invoke(context));
        }
        finally
        {
            if (_presenterRefreshPending)
            {
                _presenterRefreshPending = false;
                presenter.Dispose();
                if (ReferenceEquals(_presenter, presenter))
                {
                    _presenter = null;
                }
            }
        }
    }

    public void Dispose()
    {
        _presenterRefreshPending = false;
        _presenter?.Dispose();
        _presenter = null;

        if (_handle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static void RegisterWindowClass()
    {
        // The delegate has to outlive the window, so it is rooted in a static field.
        _windowProcedure = WindowProcedure;

        var windowClass = new NativeMethods.WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            LpfnWndProc = _windowProcedure,
            HInstance = NativeMethods.GetModuleHandleW(null),
            LpszClassName = WindowClassName,
            HCursor = IntPtr.Zero,
            HbrBackground = IntPtr.Zero,
        };

        NativeMethods.RegisterClassExW(ref windowClass);
    }

    private static IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_NCHITTEST:
                // The HUD must never intercept mouse input intended for the game.
                return new IntPtr(NativeMethods.HTTRANSPARENT);

            case NativeMethods.WM_DESTROY:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;

            case NativeMethods.WM_HOTKEY:
                if (wParam.ToInt32() == CalibrationHotKeyId)
                {
                    var activeWindow = _activeWindow;
                    if (activeWindow is not null && !activeWindow._configuration.Calibration.Live.Enabled)
                    {
                        activeWindow.CalibrationToggleRequested?.Invoke();
                    }

                    return IntPtr.Zero;
                }

                if (wParam.ToInt32() == DeleteCalibrationHotKeyId)
                {
                    _activeWindow?.CalibrationDeleteRequested?.Invoke();
                    return IntPtr.Zero;
                }

                if (wParam.ToInt32() == ReloadHotKeyId)
                {
                    _activeWindow?.ReloadRequested?.Invoke();
                    return IntPtr.Zero;
                }

                if (wParam.ToInt32() == ExitHotKeyId)
                {
                    NativeMethods.PostQuitMessage(0);
                    return IntPtr.Zero;
                }

                return NativeMethods.DefWindowProcW(hWnd, message, wParam, lParam);

            default:
                return NativeMethods.DefWindowProcW(hWnd, message, wParam, lParam);
        }
    }

    private readonly record struct HotkeyDefinition(uint Modifiers, uint VirtualKey)
    {
        public static bool TryParse(string? value, out HotkeyDefinition definition)
        {
            definition = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return false;
            }

            var modifiers = 0u;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                modifiers |= parts[i].ToUpperInvariant() switch
                {
                    "CTRL" or "CONTROL" => NativeMethods.MOD_CONTROL,
                    "ALT" => NativeMethods.MOD_ALT,
                    "SHIFT" => NativeMethods.MOD_SHIFT,
                    "WIN" or "WINDOWS" => NativeMethods.MOD_WIN,
                    _ => 0u,
                };
            }

            if (modifiers == 0 || parts[^1].Length != 1)
            {
                return false;
            }

            var key = char.ToUpperInvariant(parts[^1][0]);
            if (key is < 'A' or > 'Z')
            {
                return false;
            }

            definition = new HotkeyDefinition(modifiers, key);
            return true;
        }
    }
}
