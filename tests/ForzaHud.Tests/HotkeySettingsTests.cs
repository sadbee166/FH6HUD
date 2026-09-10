using ForzaHud.Configuration;
using Xunit;

namespace ForzaHud.Tests;

public sealed class HotkeySettingsTests
{
    [Fact]
    public void DefaultsExposeEveryOverlayHotkey()
    {
        var hotkeys = new HudConfiguration().Hotkeys;

        Assert.Equal("Ctrl+Alt+H", hotkeys.Exit);
        Assert.Equal("Ctrl+Alt+R", hotkeys.ToggleCalibration);
        Assert.Equal("Ctrl+Alt+K", hotkeys.DeleteCalibration);
        Assert.Equal("Ctrl+Alt+P", hotkeys.ToggleRpmCalibration);
        Assert.Equal("Ctrl+Alt+L", hotkeys.Reload);
    }

    [Fact]
    public void ConfigurationLoadsOverlayHotkeysFromTheRootSection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"forzahud-hotkeys-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
            {
              "hotkeys": {
                "exit": "Ctrl+Shift+X",
                "toggleCalibration": "Alt+R",
                "deleteCalibration": "Ctrl+K",
                "toggleRpmCalibration": "Shift+P",
                "reload": "Win+L"
              }
            }
            """);

            var result = ConfigurationLoader.Load(path);

            Assert.True(result.IsClean);
            Assert.Equal("Ctrl+Shift+X", result.Configuration.Hotkeys.Exit);
            Assert.Equal("Alt+R", result.Configuration.Hotkeys.ToggleCalibration);
            Assert.Equal("Ctrl+K", result.Configuration.Hotkeys.DeleteCalibration);
            Assert.Equal("Shift+P", result.Configuration.Hotkeys.ToggleRpmCalibration);
            Assert.Equal("Win+L", result.Configuration.Hotkeys.Reload);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
