using ForzaHud.Configuration;

namespace ForzaHud.Telemetry;

/// <summary>
/// Handles one raw telemetry packet. The buffer is only valid for the duration of the
/// call - it is reused by the receiver - so handlers must not retain it.
/// </summary>
public delegate void TelemetryPacketHandler(ReadOnlySpan<byte> packet, TimeSpan receivedAt);

/// <summary>
/// A stream of raw FH6 Data Out packets. HUD components consume telemetry through this
/// interface and never need to know whether it came from the game or from a recording.
/// </summary>
public interface ITelemetrySource : IDisposable
{
    /// <summary>Raised for every packet, on the source's own thread.</summary>
    event TelemetryPacketHandler PacketReceived;

    /// <summary>Whether the source is currently producing packets.</summary>
    bool IsRunning { get; }

    void Start();

#pragma warning disable CA1716 // Start/Stop is the intended API pair here.
    void Stop();
#pragma warning restore CA1716
}

/// <summary>Optional capability for telemetry sources whose endpoint can change at runtime.</summary>
public interface IReconfigurableTelemetrySource
{
    void ApplyConfiguration(UdpSettings settings);
}
