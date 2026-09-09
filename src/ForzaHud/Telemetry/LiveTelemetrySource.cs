using System.Net;
using ForzaHud.Configuration;

namespace ForzaHud.Telemetry;

/// <summary>
/// Adapts <see cref="TelemetryReceiver"/> to <see cref="ITelemetrySource"/> so live and
/// replayed telemetry are interchangeable downstream.
/// </summary>
public sealed class LiveTelemetrySource : ITelemetrySource, IReconfigurableTelemetrySource
{
    private TelemetryReceiver _receiver;

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

    /// <summary>Rebinds the UDP receiver when the endpoint changes in hud.json.</summary>
    public void ApplyConfiguration(UdpSettings settings)
    {
        var address = IPAddress.Parse(settings.BindAddress);
        if (_receiver.BindAddress.Equals(address) && _receiver.Port == settings.Port)
        {
            return;
        }

        var previousAddress = _receiver.BindAddress;
        var previousPort = _receiver.Port;
        var wasRunning = _receiver.IsRunning;
        Stop();
        _receiver.Dispose();
        try
        {
            _receiver = new TelemetryReceiver(address, settings.Port);
            if (wasRunning)
            {
                Start();
            }
        }
        catch
        {
            _receiver.Dispose();
            _receiver = new TelemetryReceiver(previousAddress, previousPort);
            if (wasRunning)
            {
                Start();
            }

            throw;
        }
    }

    public void Dispose() => Stop();

    private void OnPacketReceived(ReadOnlySpan<byte> packet, TimeSpan receivedAt) =>
        PacketReceived?.Invoke(packet, receivedAt);
}
