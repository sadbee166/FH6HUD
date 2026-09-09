using System.Buffers.Binary;

namespace ForzaHud.Telemetry;

/// <summary>
/// Plays back a recorded session through the same <see cref="ITelemetrySource"/> contract
/// used by live telemetry, so no downstream component can tell the difference.
///
/// Playback honours the original inter-packet timing, scaled by <see cref="Speed"/>.
/// </summary>
public sealed class ReplayTelemetrySource : ITelemetrySource
{
    private readonly List<(TimeSpan Offset, byte[] Payload)> _frames = [];

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _paused;
    private double _speed = 1.0;
    private volatile bool _loop = true;
    private int _stepRequests;
    private int _restartRequested;
    private int _currentFrameIndex;

    /// <param name="sessionFile">Path to a file written by <see cref="TelemetryRecorder"/>.</param>
    public ReplayTelemetrySource(string sessionFile)
    {
        Load(sessionFile);
    }

    public event TelemetryPacketHandler? PacketReceived;

    /// <summary>Number of frames in the loaded session.</summary>
    public int FrameCount => _frames.Count;

    /// <summary>Zero-based index of the most recently played frame.</summary>
    public int CurrentFrameIndex
    {
        get => Volatile.Read(ref _currentFrameIndex);
        private set => Volatile.Write(ref _currentFrameIndex, value);
    }

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Playback rate. 1.0 is real time.</summary>
    public double Speed
    {
        get => _speed;
        set => _speed = value <= 0 ? 1.0 : value;
    }

    /// <summary>Whether playback restarts from the beginning after the last frame.</summary>
    public bool Loop
    {
        get => _loop;
        set => _loop = value;
    }

    /// <summary>Whether playback is currently held.</summary>
    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>True when the loaded session has an index file worth displaying.</summary>
    public TimeSpan Duration => _frames.Count == 0 ? TimeSpan.Zero : _frames[^1].Offset;

    public void Start()
    {
        if (IsRunning || _frames.Count == 0)
        {
            return;
        }

        _stopRequested = false;
        _thread = new Thread(PlaybackLoop)
        {
            IsBackground = true,
            Name = "ForzaHud.Replay",
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stopRequested = true;
        var thread = _thread;
        if (thread is not null && thread.IsAlive && thread.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
        _thread = null;
    }

    /// <summary>Holds playback at the current frame.</summary>
    public void Pause() => _paused = true;

    /// <summary>Resumes playback.</summary>
    public void Resume() => _paused = false;

    /// <summary>Seeks back to the first frame.</summary>
    public void Restart()
    {
        CurrentFrameIndex = 0;
        Interlocked.Exchange(ref _stepRequests, 0);
        Interlocked.Exchange(ref _restartRequested, 1);
    }

    /// <summary>
    /// Advances one frame while paused. Each call queues a single frame, which makes frame
    /// stepping usable for inspecting grip, smoothing or powerband behaviour.
    /// </summary>
    public void Step() => Interlocked.Increment(ref _stepRequests);

    public void Dispose() => Stop();

    private void Load(string sessionFile)
    {
        using var stream = File.OpenRead(sessionFile);

        Span<byte> header = stackalloc byte[SessionFileFormat.HeaderSize];
        try
        {
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"'{sessionFile}' is too short to be a ForzaHud session.");
        }

        var magic = System.Text.Encoding.ASCII.GetString(header[..SessionFileFormat.MagicLength]);
        if (magic != SessionFileFormat.Magic)
        {
            throw new InvalidDataException($"'{sessionFile}' is not a ForzaHud session file (magic '{magic}').");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (version != SessionFileFormat.CurrentVersion)
        {
            throw new InvalidDataException($"'{sessionFile}' uses session format version {version}; expected {SessionFileFormat.CurrentVersion}.");
        }

        Span<byte> recordHeader = stackalloc byte[10];
        while (true)
        {
            var firstByte = stream.ReadByte();
            if (firstByte < 0)
            {
                break;
            }

            recordHeader[0] = (byte)firstByte;
            try
            {
                stream.ReadExactly(recordHeader[1..]);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException($"'{sessionFile}' ends in the middle of a telemetry record header.");
            }

            var microseconds = BinaryPrimitives.ReadUInt64LittleEndian(recordHeader[..8]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(recordHeader[8..]);

            var payload = new byte[length];
            try
            {
                stream.ReadExactly(payload);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException($"'{sessionFile}' ends in the middle of a telemetry packet.");
            }

            _frames.Add((TimeSpan.FromTicks((long)microseconds * (TimeSpan.TicksPerMillisecond / 1000)), payload));
        }
    }

    private void PlaybackLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (!_stopRequested)
        {
            if (CurrentFrameIndex >= _frames.Count)
            {
                if (!_loop)
                {
                    return;
                }

                Restart();
            }

            if (Interlocked.Exchange(ref _restartRequested, 0) != 0)
            {
                clock.Restart();
            }

            var frame = _frames[CurrentFrameIndex];

            if (_paused)
            {
                if (TryTakeStep())
                {
                    PacketReceived?.Invoke(frame.Payload, frame.Offset);
                    CurrentFrameIndex++;
                }

                Thread.Sleep(8);
                continue;
            }

            // Target position in the recording, scaled by playback speed.
            var target = TimeSpan.FromTicks((long)(clock.Elapsed.Ticks * _speed));

            if (frame.Offset > target)
            {
                Thread.Sleep(1);
                continue;
            }

            PacketReceived?.Invoke(frame.Payload, frame.Offset);
            CurrentFrameIndex++;
        }
    }

    private bool TryTakeStep()
    {
        while (true)
        {
            var requests = Volatile.Read(ref _stepRequests);
            if (requests <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _stepRequests, requests - 1, requests) == requests)
            {
                return true;
            }
        }
    }
}
