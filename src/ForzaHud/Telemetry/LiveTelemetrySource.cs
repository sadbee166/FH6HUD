using System.Net;

namespace ForzaHud.Telemetry;

/// <summary>
/// Adapts <see cref="TelemetryReceiver"/> to <see cref="ITelemetrySource"/> so live and
/// replayed telemetry are interchangeable downstream.
/// </summary>
public sealed class LiveTelemetrySource : ITelemetrySource
{
    private readonly TelemetryReceiver _receiver;

    public LiveTelemetrySource(IPAddress bindAddress, int port) =>
        _receiver = new TelemetryReceiver(bindAddress, port);

    public event TelemetryPacketHandler? PacketReceived;

    public bool IsRunning => _receiver.IsRunning;

    /// <summary>Diagnostics counters from the underlying socket.</summary>
    public long PacketsReceived => _receiver.PacketsReceived;

    /// <summary>Number of datagrams too short to be a FH6 packet.</summary>
    public long MalformedPackets => _receiver.MalformedPackets;

    public void Start()
    {
        _receiver.PacketReceived += OnPacketReceived;
        _receiver.Start();
    }

    public void Stop()
    {
        _receiver.Stop();
        _receiver.PacketReceived -= OnPacketReceived;
    }

    public void Dispose() => Stop();

    private void OnPacketReceived(ReadOnlySpan<byte> packet, TimeSpan receivedAt) =>
        PacketReceived?.Invoke(packet, receivedAt);
}
