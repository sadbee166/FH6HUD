using System.Buffers.Binary;
using ForzaHud.Configuration;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;
using Xunit.Sdk;

namespace ForzaHud.Tests;

public sealed class RecordedSessionTcsTests
{
    [Fact]
    public void RecordedSessionsMeetTcsSuccessCriteria()
    {
        var root = FindRepositoryRoot();
        var sessionDirectory = Path.Combine(root, "sessions");
        var sessionFiles = new[]
        {
            Path.Combine(sessionDirectory, "TCS_ABSENT_AWD.fzh"),
            Path.Combine(sessionDirectory, "TCS_ABSENT_CORNERING_AWD.fzh"),
            Path.Combine(sessionDirectory, "TCS_PRESENT_AWD.fzh"),
            Path.Combine(sessionDirectory, "TCS_PRESENT_FWD.fzh"),
            Path.Combine(sessionDirectory, "TCS_PRESENT_RWD.fzh"),
            Path.Combine(sessionDirectory, "TCS_UNSTABLE_PRESENT_RWD.fzh"),
        };
        if (sessionFiles.Any(path => !File.Exists(path)))
        {
            throw SkipException.ForSkip("Recorded TCS session fixtures are not available.");
        }

        var settings = ConfigurationLoader.Load(
            Path.Combine(root, "config", "hud.json")).Configuration.Telemetry.TractionControl;
        var results = new[]
        {
            Evaluate(root, "TCS_ABSENT_AWD.fzh", settings),
            Evaluate(root, "TCS_ABSENT_CORNERING_AWD.fzh", settings),
            Evaluate(root, "TCS_PRESENT_AWD.fzh", settings),
            Evaluate(root, "TCS_PRESENT_FWD.fzh", settings),
            Evaluate(root, "TCS_PRESENT_RWD.fzh", settings),
            Evaluate(root, "TCS_UNSTABLE_PRESENT_RWD.fzh", settings),
        };

        var summary = string.Join(
            "; ",
            results.Select(result =>
                $"{result.Name}: {result.ActiveRate:P2} ({result.ActiveFrames}/{result.DrivingFrames})"));

        Assert.True(results[0].ActiveRate < 0.10, summary);
        Assert.True(results[1].ActiveRate < 0.10, summary);
        Assert.True(results[2].ActiveRate > 0.67, summary);
        Assert.True(results[3].ActiveRate > 0.80, summary);
        Assert.True(results[4].ActiveRate > 0.80, summary);
        Assert.InRange(results[5].ActiveRate, 0.10, 0.33);
    }

    [Fact]
    public void UnstableRwdSessionHasPartialDetection()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "sessions", "TCS_UNSTABLE_PRESENT_RWD.fzh");
        if (!File.Exists(path))
        {
            throw SkipException.ForSkip("Recorded TCS session fixtures are not available.");
        }

        var settings = ConfigurationLoader.Load(
            Path.Combine(root, "config", "hud.json")).Configuration.Telemetry.TractionControl;
        var result = Evaluate(root, "TCS_UNSTABLE_PRESENT_RWD.fzh", settings);

        Assert.InRange(result.ActiveRate, 0.10, 0.33);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "config", "hud.json"))
                && Directory.Exists(Path.Combine(directory.FullName, "sessions")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root containing config and sessions.");
    }

    private static SessionResult Evaluate(
        string sessionRoot,
        string fileName,
        TractionControlSettings settings)
    {
        var analyzer = new TractionControlAnalyzer(settings);
        var path = Path.Combine(sessionRoot, "sessions", fileName);
        var drivingFrames = 0;
        var activeFrames = 0;

        using var stream = File.OpenRead(path);
        stream.Position = SessionFileFormat.HeaderSize;
        var recordHeader = new byte[10];
        while (stream.Position < stream.Length)
        {
            stream.ReadExactly(recordHeader);
            var microseconds = BinaryPrimitives.ReadUInt64LittleEndian(recordHeader.AsSpan(0, 8));
            var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(recordHeader.AsSpan(8, 2));
            var payload = new byte[payloadLength];
            stream.ReadExactly(payload);

            if (!ForzaPacketParser.TryParse(
                    payload,
                    TimeSpan.FromTicks(checked((long)microseconds * 10)),
                    out var snapshot))
            {
                continue;
            }

            var active = analyzer.Update(snapshot);
            if (snapshot.IsRaceOn)
            {
                drivingFrames++;
                if (active)
                {
                    activeFrames++;
                }
            }
        }

        return new SessionResult(fileName, activeFrames / (double)drivingFrames, activeFrames, drivingFrames);
    }

    private sealed record SessionResult(string Name, double ActiveRate, int ActiveFrames, int DrivingFrames);
}
