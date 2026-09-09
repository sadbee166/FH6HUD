using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;

namespace ForzaHud.Telemetry;

/// <summary>
/// Writes raw FH6 packets to a session file.
///
/// Recording runs on its own thread and is fed through a bounded channel, which keeps it
/// off the receive path. If the writer ever falls behind, packets are dropped and counted
/// rather than allowed to stall live HUD updates.
/// </summary>
public sealed class TelemetryRecorder : IDisposable
{
    private const int ChannelCapacity = 8192;
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    private Channel<byte[]> _queue = CreateQueue();

    private FileStream? _stream;
    private Thread? _writerThread;
    private long _startTicks;
    private long _droppedPackets;
    private TimeSpan? _firstTimestamp;

    /// <summary>Whether a session is currently being recorded.</summary>
    public bool IsRecording => _stream is not null;

    /// <summary>Path of the file being written, or <see langword="null"/> when not recording.</summary>
    public string? CurrentFile { get; private set; }

    /// <summary>Number of packets written to the current session.</summary>
    public long PacketsWritten { get; private set; }

    /// <summary>Number of packets dropped because the writer fell behind.</summary>
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);

    /// <summary>
    /// Opens a new session file in <paramref name="directory"/> and begins recording.
    /// </summary>
    public void Start(string directory)
    {
        if (IsRecording)
        {
            return;
        }

        Directory.CreateDirectory(directory);

        _queue = CreateQueue();
        _startTicks = DateTime.UtcNow.Ticks;
        _firstTimestamp = null;
        PacketsWritten = 0;
        Interlocked.Exchange(ref _droppedPackets, 0);

        var path = Path.Combine(
            directory,
            $"{DateTime.Now:yyyyMMdd-HHmmss}{SessionFileFormat.FileExtension}");

        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        CurrentFile = path;

        using var header = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        header.Write(Encoding.ASCII.GetBytes(SessionFileFormat.Magic));
        header.Write(SessionFileFormat.CurrentVersion);
        header.Write((ushort)0);
        header.Write(_startTicks);
        header.Flush();

        _writerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "ForzaHud.TelemetryRecorder",
        };
        _writerThread.Start();
    }

    /// <summary>
    /// Queues one raw packet for recording. Safe to call from the telemetry thread.
    /// The first packet establishes the session's zero point.
    /// </summary>
    public void Record(ReadOnlySpan<byte> packet, TimeSpan timestamp)
    {
        if (!IsRecording)
        {
            return;
        }

        _firstTimestamp ??= timestamp;
        var offsetSinceSessionStart = timestamp - _firstTimestamp.Value;

        var record = new byte[8 + 2 + packet.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), (ulong)(offsetSinceSessionStart.Ticks / TicksPerMicrosecond));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8, 2), (ushort)packet.Length);
        packet.CopyTo(record.AsSpan(10));

        if (!_queue.Writer.TryWrite(record))
        {
            Interlocked.Increment(ref _droppedPackets);
        }
    }

    /// <summary>Flushes and closes the current session file.</summary>
    public void Stop()
    {
        _queue.Writer.TryComplete();

        var thread = _writerThread;
        if (thread is not null && thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
        _writerThread = null;

        _stream?.Dispose();
        _stream = null;
        CurrentFile = null;
    }

    public void Dispose() => Stop();

    private void WriteLoop()
    {
        var reader = _queue.Reader;
        var stream = _stream!;

        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var record))
            {
                stream.Write(record, 0, record.Length);
                PacketsWritten++;
            }
        }

        stream.Flush(flushToDisk: true);
    }

    private static Channel<byte[]> CreateQueue() => Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            // Wait mode makes TryWrite return false when full, allowing the recorder to
            // report the drop without ever blocking the telemetry receive thread.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
}
