using System.Runtime.InteropServices;

namespace ForzaHud.Platform.Windows;

/// <summary>
/// Console management for a windowed application.
///
/// The HUD ships as a windowed executable so launching it never flashes a console over the
/// game, which means any mode that needs text output or keyboard input has to attach to the
/// console that launched it.
/// </summary>
public static class ConsoleHost
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    /// <summary>
    /// Attaches to the launching process's console and redirects standard input and output to it.
    /// </summary>
    public static void Attach()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            return;
        }

        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(output);
        Console.SetError(output);
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }
}
