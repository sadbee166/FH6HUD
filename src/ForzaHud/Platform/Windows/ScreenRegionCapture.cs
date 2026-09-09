using System.Runtime.InteropServices;
using ForzaHud.Configuration;

namespace ForzaHud.Platform.Windows;

/// <summary>
/// Captures one configured monitor region into a reusable top-down BGRA buffer.
/// The capture uses SRCCOPY without CAPTUREBLT so the layered ForzaHud overlay is not fed
/// back into its own TCS detector.
/// </summary>
internal sealed class ScreenRegionCapture : IDisposable
{
    private readonly int _monitorIndex;
    private readonly FrameTcsSettings _settings;

    private IntPtr _memoryDeviceContext;
    private IntPtr _bitmap;
    private IntPtr _previousBitmap;
    private IntPtr _bits;
    private byte[] _pixels = [];
    private int _width;
    private int _height;

    public ScreenRegionCapture(int monitorIndex, FrameTcsSettings settings)
    {
        _monitorIndex = monitorIndex;
        _settings = settings;
    }

    public bool TryCapture(out ReadOnlyMemory<byte> pixels)
    {
        pixels = ReadOnlyMemory<byte>.Empty;
        var monitor = MonitorHelper.Get(_monitorIndex);
        var region = CalculateRegion(monitor, _settings);
        EnsureSurface(region.Width, region.Height);

        var screenDeviceContext = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDeviceContext == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!NativeMethods.BitBlt(
                    _memoryDeviceContext,
                    0,
                    0,
                    _width,
                    _height,
                    screenDeviceContext,
                    region.X,
                    region.Y,
                    NativeMethods.SRCCOPY))
            {
                return false;
            }

            Marshal.Copy(_bits, _pixels, 0, _pixels.Length);
            pixels = _pixels;
            return true;
        }
        finally
        {
            _ = NativeMethods.ReleaseDC(IntPtr.Zero, screenDeviceContext);
        }
    }

    public void Dispose() => ReleaseSurface();

    internal static (int X, int Y, int Width, int Height) CalculateRegion(
        MonitorHelper.MonitorBounds monitor,
        FrameTcsSettings settings)
    {
        var width = Math.Max(1, (int)MathF.Round(settings.RegionWidth * monitor.Width));
        var height = Math.Max(1, (int)MathF.Round(settings.RegionHeight * monitor.Height));
        var x = monitor.Left + (int)MathF.Round(settings.RegionX * monitor.Width);
        var y = monitor.Top + (int)MathF.Round(settings.RegionY * monitor.Height);
        x = Math.Clamp(x, monitor.Left, monitor.Left + monitor.Width - width);
        y = Math.Clamp(y, monitor.Top, monitor.Top + monitor.Height - height);
        return (x, y, width, height);
    }

    private void EnsureSurface(int width, int height)
    {
        if (_memoryDeviceContext != IntPtr.Zero && width == _width && height == _height)
        {
            return;
        }

        ReleaseSurface();
        _width = width;
        _height = height;
        _memoryDeviceContext = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (_memoryDeviceContext == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create a screen-capture device context.");
        }

        var info = new NativeMethods.BITMAPINFO
        {
            BmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                BiSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                BiWidth = width,
                BiHeight = -height,
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = NativeMethods.BI_RGB,
            },
        };

        _bitmap = NativeMethods.CreateDIBSection(
            _memoryDeviceContext,
            ref info,
            NativeMethods.DIB_RGB_COLORS,
            out _bits,
            IntPtr.Zero,
            0);
        if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            ReleaseSurface();
            throw new InvalidOperationException("Failed to allocate the screen-capture bitmap.");
        }

        _previousBitmap = NativeMethods.SelectObject(_memoryDeviceContext, _bitmap);
        _pixels = new byte[checked(width * height * 4)];
    }

    private void ReleaseSurface()
    {
        if (_memoryDeviceContext != IntPtr.Zero && _previousBitmap != IntPtr.Zero)
        {
            NativeMethods.SelectObject(_memoryDeviceContext, _previousBitmap);
        }

        if (_memoryDeviceContext != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memoryDeviceContext);
        }

        if (_bitmap != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_bitmap);
        }

        _memoryDeviceContext = IntPtr.Zero;
        _bitmap = IntPtr.Zero;
        _previousBitmap = IntPtr.Zero;
        _bits = IntPtr.Zero;
        _pixels = [];
        _width = 0;
        _height = 0;
    }

}
