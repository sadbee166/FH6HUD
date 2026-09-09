using ForzaHud.Platform.Windows;
using Xunit;

namespace ForzaHud.Tests;

public sealed class PresentationWindowTests
{
    [Fact]
    public void DirectCompositionOverlayUsesLayeredCrossProcessClickThroughStyle()
    {
        var style = OverlayWindow.GetExtendedStyle();

        Assert.Equal(
            NativeMethods.WS_EX_TOPMOST
            | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE
            | NativeMethods.WS_EX_LAYERED,
            style);
    }
}
