using System.Diagnostics;
using ForzaHud.Configuration;
using ForzaHud.Rendering;
using ForzaHud.Rendering.Direct2D;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DResultCode = Vortice.Direct2D1.ResultCode;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;
using DxgiResultCode = Vortice.DXGI.ResultCode;

namespace ForzaHud.Platform.Windows;

/// <summary>
/// DirectComposition presenter backed by a hardware D3D11 composition swap chain.
/// Direct2D draws directly into the swap-chain back buffer, so no system-memory DIB is
/// uploaded during presentation.
/// </summary>
internal sealed class DirectCompositionPresenter : IDisposable
{
    private static readonly D3DFeatureLevel[] SupportedFeatureLevels =
    [
        D3DFeatureLevel.Level_11_1,
        D3DFeatureLevel.Level_11_0,
    ];

    private readonly IntPtr _windowHandle;
    private readonly ID2D1Factory _factory;
    private readonly IDWriteFactory _textFactory;
    private readonly VisualSettings _visual;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private IDXGIDevice? _dxgiDevice;
    private ID2D1Device? _d2dDevice;
    private IDXGIFactory2? _dxgiFactory;
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _compositionVisual;
    private IDXGISwapChain1? _swapChain;
    private IDXGISurface? _surface;
    private ID2D1Bitmap1? _targetBitmap;
    private ID2D1DeviceContext? _targetContext;
    private Direct2DRenderContext? _context;

    private int _pixelWidth;
    private int _pixelHeight;
    private uint _dpi;

    public DirectCompositionPresenter(
        IntPtr windowHandle,
        ID2D1Factory factory,
        IDWriteFactory textFactory,
        VisualSettings visual)
    {
        _windowHandle = windowHandle;
        _factory = factory;
        _textFactory = textFactory;
        _visual = visual;

        try
        {
            CreateDeviceResources();
        }
        catch
        {
            ReleaseDeviceResources();
            throw;
        }
    }

    public void Resize(int pixelWidth, int pixelHeight, uint dpi)
    {
        if (_context is not null
            && pixelWidth == _pixelWidth
            && pixelHeight == _pixelHeight
            && dpi == _dpi)
        {
            return;
        }

        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _dpi = dpi;

        try
        {
            if (_swapChain is null)
            {
                CreateSwapChainAndSurface();
                return;
            }

            ReleaseSurfaceResources();
            var result = _swapChain.ResizeBuffers(
                0,
                (uint)_pixelWidth,
                (uint)_pixelHeight,
                Format.B8G8R8A8_UNorm,
                SwapChainFlags.None);

            if (IsDeviceLoss(result.Code))
            {
                RecoverDeviceResources("ResizeBuffers", result.Code);
                return;
            }

            result.CheckError();
            CreateSurfaceResources();
        }
        catch (Exception exception) when (IsDeviceLoss(exception))
        {
            RecoverDeviceResources("resize", exception.HResult);
        }
    }

    public void Render(Action<IRenderContext> draw)
    {
        var context = _context
            ?? throw new InvalidOperationException("The DirectComposition surface has not been allocated.");
        var swapChain = _swapChain
            ?? throw new InvalidOperationException("The DirectComposition swap chain has not been allocated.");

        try
        {
            context.BeginDraw();
            draw(context);
            context.EndDraw();
            swapChain.Present(0, PresentFlags.None).CheckError();
        }
        catch (Exception exception) when (IsRecreateTarget(exception))
        {
            LogPresentationFailure("Direct2D target", exception.HResult);
            ReleaseSurfaceResources();
            try
            {
                CreateSurfaceResources();
            }
            catch (Exception recreateException) when (IsDeviceLoss(recreateException))
            {
                RecoverDeviceResources("Direct2D target recovery", recreateException.HResult);
            }
        }
        catch (Exception exception) when (IsDeviceLoss(exception))
        {
            RecoverDeviceResources("presentation", exception.HResult);
        }
    }

    private void CreateDeviceResources()
    {
        _dxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            SupportedFeatureLevels,
            out _device,
            out _deviceContext).CheckError();

        _dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var factory1 = _factory.QueryInterface<ID2D1Factory1>();
        _d2dDevice = factory1.CreateDevice(_dxgiDevice);
        _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
        _compositionDevice.CreateTargetForHwnd(_windowHandle, true, out _compositionTarget).CheckError();
        _compositionVisual = _compositionDevice.CreateVisual();

        _compositionTarget.SetRoot(_compositionVisual).CheckError();
        _compositionDevice.Commit().CheckError();
    }

    private void CreateSwapChainAndSurface()
    {
        var description = new SwapChainDescription1(
            (uint)_pixelWidth,
            (uint)_pixelHeight,
            Format.B8G8R8A8_UNorm,
            false,
            Usage.RenderTargetOutput,
            2,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            DxgiAlphaMode.Premultiplied,
            SwapChainFlags.None);

        try
        {
            _swapChain = _dxgiFactory!.CreateSwapChainForComposition(_device!, description, null);
            _compositionVisual!.SetContent(_swapChain).CheckError();
            _compositionDevice!.Commit().CheckError();
            CreateSurfaceResources();
        }
        catch
        {
            ReleaseSurfaceResources();
            try
            {
                _compositionVisual?.SetContent(null).CheckError();
            }
            catch
            {
                // The device may already be lost; cleanup continues below.
            }

            _swapChain?.Dispose();
            _swapChain = null;
            throw;
        }
    }

    private void CreateSurfaceResources()
    {
        IDXGISurface? surface = _swapChain!.GetBuffer<IDXGISurface>(0);
        ID2D1Bitmap1? targetBitmap = null;
        ID2D1DeviceContext? targetContext = null;
        Direct2DRenderContext? context = null;

        try
        {
            targetContext = _d2dDevice!.CreateDeviceContext(DeviceContextOptions.None);
            targetContext.SetDpi(_dpi, _dpi);
            targetBitmap = targetContext.CreateBitmapFromDxgiSurface(
                surface,
                new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(
                        Format.B8G8R8A8_UNorm,
                        D2DAlphaMode.Premultiplied),
                    _dpi,
                    _dpi,
                    BitmapOptions.Target | BitmapOptions.CannotDraw));
            targetContext.Target = targetBitmap;
            context = new Direct2DRenderContext(
                _factory,
                targetContext,
                _textFactory,
                _visual,
                targetContext,
                targetBitmap);

            _surface = surface;
            _targetBitmap = targetBitmap;
            _targetContext = targetContext;
            _context = context;
            surface = null!;
            targetBitmap = null;
            targetContext = null;
            context = null;
        }
        finally
        {
            context?.Dispose();
            targetContext?.Dispose();
            targetBitmap?.Dispose();
            surface?.Dispose();
        }
    }

    private void ReleaseSurfaceResources()
    {
        _context?.Dispose();
        _context = null;

        if (_targetContext is not null)
        {
            _targetContext.Target = null;
        }

        _targetContext?.Dispose();
        _targetContext = null;

        _targetBitmap?.Dispose();
        _targetBitmap = null;

        _surface?.Dispose();
        _surface = null;
    }

    private void RecoverDeviceResources(string operation, int errorCode)
    {
        LogPresentationFailure(operation, errorCode);
        ReleaseDeviceResources();
        CreateDeviceResources();
        CreateSwapChainAndSurface();
    }

    private void ReleaseDeviceResources()
    {
        ReleaseSurfaceResources();

        if (_compositionVisual is not null)
        {
            try
            {
                _compositionVisual.SetContent(null).CheckError();
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"ForzaHud: failed to detach composition content: 0x{exception.HResult:X8}");
            }
        }

        _swapChain?.Dispose();
        _swapChain = null;
        _compositionVisual?.Dispose();
        _compositionVisual = null;
        _compositionTarget?.Dispose();
        _compositionTarget = null;
        _compositionDevice?.Dispose();
        _compositionDevice = null;
        _dxgiFactory?.Dispose();
        _dxgiFactory = null;
        _d2dDevice?.Dispose();
        _d2dDevice = null;
        _dxgiDevice?.Dispose();
        _dxgiDevice = null;
        _deviceContext?.Dispose();
        _deviceContext = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose() => ReleaseDeviceResources();

    private static bool IsRecreateTarget(Exception exception) =>
        exception.HResult == D2DResultCode.RecreateTarget.Code;

    private static bool IsDeviceLoss(Exception exception) =>
        IsDeviceLoss(exception.HResult);

    private static bool IsDeviceLoss(int errorCode) =>
        errorCode == DxgiResultCode.DeviceRemoved.Code
        || errorCode == DxgiResultCode.DeviceReset.Code
        || errorCode == DxgiResultCode.DeviceHung.Code;

    private static void LogPresentationFailure(string operation, int errorCode) =>
        Trace.WriteLine($"ForzaHud: {operation} failed with HRESULT 0x{errorCode:X8}; presentation resources recreated.");
}
