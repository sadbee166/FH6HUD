using System.Globalization;
using ForzaHud.Configuration;
using ForzaHud.Platform.Windows;
using ForzaHud.Rendering.Direct2D;
using ForzaHud.Telemetry;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace ForzaHud.App;

/// <summary>
/// Entry point and mode selection.
/// </summary>
public static class Program
{
    private const string LogFileName = "forzahud.log";

    [STAThread]
    public static int Main(string[] args)
    {
        ConsoleHost.Attach();
        Console.WriteLine("[INFO] ForzaHud starting.");

        var options = CommandLineOptions.Parse(args, out var parseError);
        if (parseError is not null)
        {
            ConsoleHost.Attach();
            Console.Error.WriteLine(parseError);
            Console.WriteLine(CommandLineOptions.HelpText);
            return 1;
        }

        if (options.ShowHelp)
        {
            ConsoleHost.Attach();
            Console.WriteLine(CommandLineOptions.HelpText);
            return 0;
        }

        var load = ConfigurationLoader.Load(options.ConfigPath);
        Log(load.Diagnostics, load.SourcePath);

        if (options.WriteConfigPath is not null)
        {
            ConfigurationLoader.Write(options.WriteConfigPath, load.Configuration);
            ConsoleHost.Attach();
            Console.WriteLine($"Wrote configuration to {Path.GetFullPath(options.WriteConfigPath)}");
            return 0;
        }

        // Modes that print or read keys use the launching console; the live overlay does not
        // allocate one when started without a terminal.
        if (options.Probe || options.ReplayPath is not null)
        {
            ConsoleHost.Attach();
        }

        var factory = D2D1.D2D1CreateFactory<ID2D1Factory>(
            Vortice.Direct2D1.FactoryType.SingleThreaded, Vortice.Direct2D1.DebugLevel.None);
        var textFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);

        var reloadRequested = false;
        using (var source = CreateSource(options, load.Configuration))
        using (var application = new HudApplication(
            load.Configuration, source, factory, textFactory,
            enableRecording: options.Record || load.Configuration.Recording.Enabled,
            configPath: options.ConfigPath))
        {
            if (options.Probe)
            {
                return RunProbe(application, source);
            }

            if (options.SnapshotPath is not null)
            {
                return RunSnapshot(application, load.Configuration, factory, textFactory, options);
            }

            if (options.ReplayPath is not null && source is ReplayTelemetrySource replay)
            {
                StartReplayControls(replay, options.ReplaySpeed);
            }

            PrintStartupBanner(load, options, source);
            application.Run();
            reloadRequested = application.ShouldReload;
        }

        return reloadRequested ? Restart(args) : 0;
    }

    private static int Restart(string[] args)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            Console.Error.WriteLine("Could not reload ForzaHud because its executable path is unavailable.");
            return 1;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            System.Diagnostics.Process.Start(startInfo);
            return 0;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Console.Error.WriteLine($"Could not reload ForzaHud: {exception.Message}");
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"Could not reload ForzaHud: {exception.Message}");
            return 1;
        }
    }

    private static ITelemetrySource CreateSource(CommandLineOptions options, HudConfiguration configuration)
    {
        if (options.ReplayPath is not null)
        {
            return new ReplayTelemetrySource(options.ReplayPath);
        }

        if (options.Simulate || options.SnapshotPath is not null)
        {
            return new SyntheticTelemetrySource(startOffsetSeconds: options.SnapshotAt);
        }

        var address = string.IsNullOrWhiteSpace(configuration.Udp.BindAddress)
            ? System.Net.IPAddress.Loopback
            : System.Net.IPAddress.Parse(configuration.Udp.BindAddress);

        return new LiveTelemetrySource(address, configuration.Udp.Port);
    }

    /// <summary>
    /// Phase 1 milestone: receive packets, verify the structure, print the core values.
    /// No rendering.
    /// </summary>
    private static int RunProbe(HudApplication application, ITelemetrySource source)
    {
        Console.WriteLine("ForzaHud telemetry probe. Waiting for FH6 Data Out packets...");
        Console.WriteLine("Press Q to quit.");
        Console.WriteLine();

        source.Start();

        while (!Console.KeyAvailable || Console.ReadKey(intercept: true).Key != ConsoleKey.Q)
        {
            application.ApplyPendingConfiguration();
            var state = application.State;

            Console.Write("\r                                                            \r");
            Console.WriteLine(
                $"Speed: {state.DisplaySpeed,6:0.0} {state.SpeedUnitLabel}   " +
                $"RPM: {state.Rpm,6:0}   " +
                $"Boost: {state.BoostPsi,5:0.0}   " +
                $"Throttle: {state.Throttle * 100,3:0}%   " +
                $"Brake: {state.Brake * 100,3:0}%   " +
                $"Gear: {state.GearLabel}   " +
                $"Packets: {application.PacketsParsed}");

            if (application.PacketsParsed == 0)
            {
                Console.WriteLine("No packets yet. Is Data Out enabled in FH6, and does the port match hud.json?");
            }

            Thread.Sleep(150);
        }

        source.Stop();
        return 0;
    }

    /// <summary>
    /// Renders a single frame to a PNG so HUD geometry can be checked without a display,
    /// without FH6 and without a screenshot.
    /// </summary>
    private static int RunSnapshot(
        HudApplication application,
        HudConfiguration configuration,
        ID2D1Factory factory,
        IDWriteFactory textFactory,
        CommandLineOptions options)
    {
        var monitor = MonitorHelper.Get(configuration.Overlay.Monitor);
        var width = Math.Min(monitor.Width, 1920);
        var height = Math.Min(monitor.Height, 1080);

        application.StartTelemetry();
        try
        {
            // Let enough frames through for the display values to settle.
            Thread.Sleep(400);

            WicSnapshot.Save(
                options.SnapshotPath!,
                width, height,
                configuration.Visual,
                factory, textFactory,
                context => application.DrawFrame(context, TimeSpan.FromSeconds(options.SnapshotAt)));
        }
        finally
        {
            application.StopTelemetry();
        }

        ConsoleHost.Attach();
        Console.WriteLine($"Wrote {Path.GetFullPath(options.SnapshotPath!)} ({width}x{height})");
        return 0;
    }

    private static void StartReplayControls(ReplayTelemetrySource replay, double speed)
    {
        replay.Speed = speed;

        Console.WriteLine("Replay controls: SPACE pause/resume, S step, R restart, +/- speed, Q quit.");

        var controls = new Thread(() =>
        {
            while (replay.IsRunning || !Console.KeyAvailable)
            {
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(50);
                    continue;
                }

                switch (Console.ReadKey(intercept: true).Key)
                {
                    case ConsoleKey.Spacebar:
                        replay.IsPaused = !replay.IsPaused;
                        break;

                    case ConsoleKey.S:
                        replay.Pause();
                        replay.Step();
                        break;

                    case ConsoleKey.R:
                        replay.Restart();
                        break;

                    case ConsoleKey.OemPlus:
                    case ConsoleKey.Add:
                        replay.Speed = Math.Min(8.0, replay.Speed * 1.5);
                        Console.WriteLine($"Speed {replay.Speed:0.##}x");
                        break;

                    case ConsoleKey.OemMinus:
                    case ConsoleKey.Subtract:
                        replay.Speed = Math.Max(0.1, replay.Speed / 1.5);
                        Console.WriteLine($"Speed {replay.Speed:0.##}x");
                        break;

                    case ConsoleKey.Q:
                        Environment.Exit(0);
                        break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "ForzaHud.ReplayControls",
        };

        controls.Start();
    }

    private static void PrintStartupBanner(
        ConfigurationLoader.Result load,
        CommandLineOptions options,
        ITelemetrySource source)
    {
        var lines = new List<string>();

        foreach (var diagnostic in load.Diagnostics)
        {
            lines.Add($"config: {diagnostic}");
        }

        lines.Add(source switch
        {
            ReplayTelemetrySource replay => $"Replaying {replay.FrameCount} frames at {replay.Speed:0.##}x.",
            SyntheticTelemetrySource => "Simulated telemetry. FH6 is not required.",
            _ => $"Listening for FH6 Data Out on {load.Configuration.Udp.BindAddress}:{load.Configuration.Udp.Port}.",
        });

        Log(lines, load.SourcePath);
    }

    private static void Log(IReadOnlyList<string> messages, string? configurationPath)
    {
        if (messages.Count == 0 && configurationPath is null)
        {
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, LogFileName);
        var lines = new List<string>
        {
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ForzaHud starting",
            $"  configuration: {configurationPath ?? "(built-in defaults)"}",
        };

        foreach (var message in messages)
        {
            lines.Add($"  {message}");
        }

        File.AppendAllLines(path, lines, System.Text.Encoding.UTF8);
    }
}
