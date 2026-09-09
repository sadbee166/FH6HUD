using System.Net;
using System.Net.Sockets;

namespace ForzaHud.Telemetry;

/// <summary>
/// Receives UDP packets from FH6's Data Out feature. This is the only type in the
/// application that touches the network.
///
/// It does not interpret vehicle behaviour, calculate grip, or render anything.
///
/// Packets are dispatched synchronously on the receive thread as they arrive and the
/// receive buffer is reused immediately after the handler returns.
/// </summary>
public sealed class TelemetryReceiver : IDisposable
{
    private readonly byte[] _buffer = new byte[ForzaPacketFormat.PacketSize];
    private readonly int _port;
    private readonly IPAddress _bindAddress;

    private Socket? _socket;
    private Thread? _thread;
    private volatile bool _stopRequested;

    /// <param name="bindAddress">Local address to bind. Use <see cref="IPAddress.Any"/> to receive from any machine.</param>
    /// <param name="port">UDP port matching the Data Out port configured in FH6.</param>
    public TelemetryReceiver(IPAddress bindAddress, int port)
    {
        _bindAddress = bindAddress;
        _port = port;
    }

    /// <summary>Raised for each received datagram. The buffer is reused after the handler returns.</summary>
    public event TelemetryPacketHandler? PacketReceived;

    /// <summary>Number of datagrams received.</summary>
    public long PacketsReceived { get; private set; }

    /// <summary>Number of datagrams rejected because they were too short to be a FH6 packet.</summary>
    public long MalformedPackets { get; private set; }

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Binds the socket and starts the receive thread.</summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _stopRequested = false;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = false,
            ReceiveBufferSize = 1 << 20,
        };
        _socket.Bind(new IPEndPoint(_bindAddress, _port));

        _thread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "ForzaHud.TelemetryReceiver",
        };
        _thread.Start();
    }

    /// <summary>Stops the receive thread and closes the socket.</summary>
    public void Stop()
    {
        _stopRequested = true;
        _socket?.Dispose();
        _socket = null;

        var thread = _thread;
        if (thread is not null && thread.IsAlive && thread.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
        _thread = null;
    }

    public void Dispose() => Stop();

    private void ReceiveLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (!_stopRequested)
        {
            int length;
            try
            {
                length = _socket!.Receive(_buffer);
            }
            catch (SocketException) when (_stopRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // A transient socket error must not take the HUD down. Keep reading.
                continue;
            }

            if (length <= 0)
            {
                continue;
            }

            if (length < ForzaPacketFormat.MinimumUsableSize)
            {
                MalformedPackets++;
                continue;
            }

            PacketsReceived++;
            PacketReceived?.Invoke(_buffer.AsSpan(0, length), clock.Elapsed);
        }
    }
}
