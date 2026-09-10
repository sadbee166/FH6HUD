using System.Globalization;

namespace ForzaHud.App;

/// <summary>
/// Command-line switches. The HUD is normally started with no arguments at all; these
/// exist for development, diagnostics and replay.
/// </summary>
public sealed class CommandLineOptions
{
    /// <summary>Explicit configuration file path.</summary>
    public string? ConfigPath { get; private set; }

    /// <summary>Write a fully populated configuration file here and exit.</summary>
    public string? WriteConfigPath { get; private set; }

    /// <summary>Replay a recorded session instead of listening for FH6.</summary>
    public string? ReplayPath { get; private set; }

    /// <summary>Replay rate. 1.0 is real time.</summary>
    public double ReplaySpeed { get; private set; } = 1.0;

    /// <summary>Drive the HUD from scripted telemetry instead of FH6.</summary>
    public bool Simulate { get; private set; }

    /// <summary>Print decoded core telemetry values without rendering anything.</summary>
    public bool Probe { get; private set; }

    /// <summary>Record raw telemetry for this run, regardless of the configuration file.</summary>
    public bool Record { get; private set; }

    /// <summary>Render one frame to a PNG and exit instead of opening the overlay.</summary>
    public string? SnapshotPath { get; private set; }

    /// <summary>Scenario time, in seconds, at which the snapshot is taken.</summary>
    public double SnapshotAt { get; private set; }

    public bool ShowHelp { get; private set; }

    /// <summary>Parses <paramref name="args"/>.</summary>
    /// <param name="error">Set when a switch is malformed.</param>
    public static CommandLineOptions Parse(string[] args, out string? error)
    {
        var options = new CommandLineOptions();
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            switch (argument)
            {
                case "--help" or "-h" or "/?":
                    options.ShowHelp = true;
                    break;

                case "--simulate":
                    options.Simulate = true;
                    break;

                case "--probe":
                    options.Probe = true;
                    break;

                case "--record":
                    options.Record = true;
                    break;

                case "--config":
                    if (!TryNext(args, ref i, out var configPath))
                    {
                        error = "--config requires a file path.";
                        return options;
                    }

                    options.ConfigPath = configPath;
                    break;

                case "--write-config":
                    if (!TryNext(args, ref i, out var writePath))
                    {
                        error = "--write-config requires a file path.";
                        return options;
                    }

                    options.WriteConfigPath = writePath;
                    break;

                case "--replay":
                    if (!TryNext(args, ref i, out var replayPath))
                    {
                        error = "--replay requires a session file path.";
                        return options;
                    }

                    options.ReplayPath = replayPath;
                    break;

                case "--speed":
                    if (!TryNext(args, ref i, out var speedText)
                        || !double.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
                        || speed <= 0)
                    {
                        error = "--speed requires a positive number.";
                        return options;
                    }

                    options.ReplaySpeed = speed;
                    break;

                case "--snapshot":
                    if (!TryNext(args, ref i, out var snapshotPath))
                    {
                        error = "--snapshot requires a PNG file path.";
                        return options;
                    }

                    options.SnapshotPath = snapshotPath;
                    break;

                case "--at":
                    if (!TryNext(args, ref i, out var atText)
                        || !double.TryParse(atText, NumberStyles.Float, CultureInfo.InvariantCulture, out var at)
                        || at < 0)
                    {
                        error = "--at requires a non-negative number of seconds.";
                        return options;
                    }

                    options.SnapshotAt = at;
                    break;

                default:
                    error = $"Unknown argument '{argument}'.";
                    return options;
            }
        }

        return options;
    }

    /// <summary>Usage text.</summary>
    public static string HelpText =>
        """
        ForzaHud - aerial-style driving HUD for Forza Horizon 6.

        Usage:
          ForzaHud                       Run the overlay against FH6's Data Out stream.
          ForzaHud --simulate            Run the overlay against scripted telemetry.
          ForzaHud --replay <file>       Replay a recorded session through the overlay.
          ForzaHud --probe               Print decoded telemetry without rendering.
          ForzaHud --snapshot <png>      Render one frame to a PNG and exit.
          ForzaHud --write-config <json> Write a configuration file with all defaults.

        Options:
          --config <path>       Use a specific configuration file.
          --record              Record raw telemetry for this run.
          --speed <rate>        Replay rate (default 1.0).
          --at <seconds>        Scenario time for --snapshot (default 0).
          --help                Show this text.

        While the overlay is running, the configured global hotkeys can close it, delete
        calibration records for the current car, or reload the utility. Valid edits to the
        active configuration file are applied automatically before the next HUD frame.
        Invalid edits are ignored until the file becomes valid again.
        During replay, SPACE pauses, S steps a frame, R restarts, and +/- change speed.
        """;

    private static bool TryNext(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}
