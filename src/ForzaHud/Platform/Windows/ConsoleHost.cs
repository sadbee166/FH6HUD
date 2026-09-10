using System.Runtime.InteropServices;

namespace ForzaHud.Platform.Windows;

/// <summary>
/// Console management for the HUD executable.
///
/// The HUD is a console executable so Windows creates a console when it is launched directly;
/// attaching remains useful when a launcher already owns the console.
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
