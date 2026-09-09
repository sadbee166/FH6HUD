using ForzaHud.Configuration;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;

namespace ForzaHud.Rendering.Direct2D;

/// <summary>
/// Renders a single HUD frame to a PNG file, with no window involved.
///
/// This is a development tool, not part of the running HUD: it makes HUD geometry
/// verifiable without FH6, without a display, and without a screenshot. It drives the same
/// <see cref="IRenderContext"/> implementation the overlay uses, so what it produces is
/// what the overlay draws.
/// </summary>
public static class WicSnapshot
{
    /// <summary>
    /// Renders <paramref name="draw"/> at the given size and saves it as a PNG.
    /// </summary>
    public static void Save(
        string path,
        int width,
        int height,
        VisualSettings visual,
        ID2D1Factory factory,
        IDWriteFactory textFactory,
        Action<IRenderContext> draw)
    {
        using var imaging = new IWICImagingFactory2();
        using var bitmap = imaging.CreateBitmap(
            (uint)width, (uint)height, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);

        var properties = new RenderTargetProperties(
            RenderTargetType.Default,
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
            96f, 96f, RenderTargetUsage.None, FeatureLevel.Default);

        using var target = factory.CreateWicBitmapRenderTarget(bitmap, properties);
        using var context = new Direct2DRenderContext(factory, target, textFactory, visual);

        context.BeginDraw();

        // A dark backdrop stands in for the game so the HUD is visible in the output.
        context.FillRect(
            new HudRect(0, 0, width, height),
            new HudPaint(new HudColor(16, 20, 24, 255), 1f));

        draw(context);
        context.EndDraw();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using var stream = imaging.CreateStream(path, FileAccess.Write);
        using var encoder = imaging.CreateEncoder(ContainerFormat.Png);
        encoder.Initialize(stream, BitmapEncoderCacheOption.NoCache);

        using var frame = encoder.CreateNewFrame(out _);
        frame.Initialize();
        frame.SetSize(new SizeI(width, height));

        var format = Vortice.WIC.PixelFormat.Format32bppPBGRA;
        frame.SetPixelFormat(format);
        frame.WriteSource(bitmap);
        frame.Commit();

        encoder.Commit();
    }
}
