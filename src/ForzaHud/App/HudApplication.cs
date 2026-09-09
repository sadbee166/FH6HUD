using ForzaHud.Configuration;
using ForzaHud.Hud;
using ForzaHud.Platform.Windows;
using ForzaHud.Rendering;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace ForzaHud.App;

/// <summary>
/// Wires the telemetry pipeline to the HUD and the overlay window.
///
/// Telemetry reception and rendering are fully independent. The receive thread decodes and
/// processes each packet and publishes the newest derived state; the render thread reads
/// whatever is newest at the time it draws. Nothing is queued, so a slow renderer can never
/// make the HUD work through a backlog of stale frames.
/// </summary>
public sealed class HudApplication : IDisposable
{
    /// <summary>How long without a packet before the HUD is considered disconnected.</summary>
    private static readonly TimeSpan TelemetryTimeout = TimeSpan.FromSeconds(2);
    private static readonly long TelemetryTimeoutStopwatchTicks =
        (long)(System.Diagnostics.Stopwatch.Frequency * TelemetryTimeout.TotalSeconds);

    private readonly HudConfiguration _configuration;
    private readonly ITelemetrySource _source;
    private readonly VehicleStateProcessor _processor;
    private readonly HudEngine _engine;
    private readonly HudRenderer _renderer;
    private readonly TelemetryRecorder? _recorder;
    private readonly FrameTcsAnalyzer? _frameTcs;
    private readonly ScreenRegionCapture? _frameCapture;

    private DerivedState _latest;
    private long _packetsParsed;
    private long _packetsRejected;
    private long _lastPacketTimestamp;
    private TimeSpan _previousFrameTime;
    private int _calibrationRecording;
    private int _frameTcsActive;

    public HudApplication(
        HudConfiguration configuration,
        ITelemetrySource source,
        ID2D1Factory factory,
        IDWriteFactory textFactory,
        bool enableRecording)
    {
        _configuration = configuration;
        _source = source;
        _processor = new VehicleStateProcessor(configuration, calibrationOutput: Console.WriteLine);
        _engine = new HudEngine(configuration);
        _renderer = new HudRenderer(configuration);

        if (configuration.Telemetry.TractionControl.Enabled
            && configuration.Telemetry.TractionControl.DetectionMode == TcsDetectionMode.Frame)
        {
            _frameTcs = new FrameTcsAnalyzer(configuration.Telemetry.TractionControl.Frame);
            _frameCapture = new ScreenRegionCapture(
                configuration.Overlay.Monitor,
                configuration.Telemetry.TractionControl.Frame);
        }

        if (enableRecording)
        {
            _recorder = new TelemetryRecorder();
        }

        _source.PacketReceived += OnPacketReceived;
        _latest = new DerivedState();

        Window = new OverlayWindow(configuration, factory, textFactory);
        Window.Render += OnRender;
        Window.CalibrationDeleteRequested += OnCalibrationDelete;
        Window.ReloadRequested += OnReload;
        if (!configuration.Calibration.Live.Enabled)
        {
            Window.CalibrationToggleRequested += OnCalibrationToggle;
        }
    }

    /// <summary>The overlay window. Valid until disposal.</summary>
    public OverlayWindow Window { get; }

    /// <summary>Number of packets successfully decoded.</summary>
    public long PacketsParsed => Interlocked.Read(ref _packetsParsed);

    /// <summary>Number of packets too short or malformed to decode.</summary>
    public long PacketsRejected => Interlocked.Read(ref _packetsRejected);

    /// <summary>True while telemetry is arriving.</summary>
    public bool HasTelemetry { get; private set; }

    /// <summary>Whether the RPM calibration recorder is currently active.</summary>
    public bool IsCalibrationRecording => _configuration.Calibration.Live.Enabled
        ? _processor.IsCalibrationRecording
        : Volatile.Read(ref _calibrationRecording) != 0;

    /// <summary>Latest derived vehicle state.</summary>
    public DerivedState State => Volatile.Read(ref _latest);

    /// <summary>Whether the reload hotkey requested a new process instance.</summary>
    public bool ShouldReload { get; private set; }

    /// <summary>Starts telemetry and opens the overlay. Blocks until the window closes.</summary>
    public void Run()
    {
        StartRecording();

        try
        {
            _source.Start();
            Window.Run();
        }
        finally
        {
            _source.Stop();
            _processor.FinishCalibrationRecording();
            Volatile.Write(ref _calibrationRecording, 0);
            StopRecording();
        }
    }

    /// <summary>
    /// Starts telemetry without opening a window. Used by the replay and probe modes, where
    /// something other than the overlay drives the loop.
    /// </summary>
    public void StartTelemetry()
    {
        StartRecording();
        Interlocked.Exchange(ref _lastPacketTimestamp, 0);
        HasTelemetry = false;
        _source.Start();
    }

    /// <summary>Stops telemetry and closes any recording.</summary>
    public void StopTelemetry()
    {
        _source.Stop();
        _processor.FinishCalibrationRecording();
        Volatile.Write(ref _calibrationRecording, 0);
        StopRecording();
    }

    /// <summary>Draws one frame through <paramref name="context"/>.</summary>
    public void DrawFrame(IRenderContext context, TimeSpan now)
    {
        var state = Volatile.Read(ref _latest);
        var delta = _previousFrameTime == TimeSpan.Zero ? TimeSpan.Zero : now - _previousFrameTime;
        _previousFrameTime = now;

        HasTelemetry = IsTelemetryFresh();

        if (_frameTcs is not null && _frameCapture is not null)
        {
            if (_frameCapture.TryCapture(out var frame))
            {
                var frameTcsActive = _frameTcs.Update(frame.Span);
                Volatile.Write(ref _frameTcsActive, frameTcsActive ? 1 : 0);
                state.TractionControlActive = frameTcsActive;
            }
            else
            {
                _frameTcs.Reset();
                Volatile.Write(ref _frameTcsActive, 0);
                state.TractionControlActive = false;
            }
        }

        _engine.Update(state, delta.TotalSeconds);
        _renderer.Draw(context, _engine, state, HasTelemetry, IsCalibrationRecording);
    }

    private void OnPacketReceived(ReadOnlySpan<byte> packet, TimeSpan receivedAt)
    {
        if (!ForzaPacketParser.TryParse(packet, receivedAt, out var snapshot))
        {
            Interlocked.Increment(ref _packetsRejected);
            return;
        }

        Interlocked.Increment(ref _packetsParsed);
        Interlocked.Exchange(ref _lastPacketTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

        // Derived once per telemetry frame, on the receive thread. Rendering never reprocesses.
        Volatile.Write(
            ref _latest,
            _processor.Process(snapshot, Volatile.Read(ref _frameTcsActive) != 0));

        _recorder?.Record(packet, receivedAt);
    }

    private void OnRender(IRenderContext context) =>
        DrawFrame(context, CurrentTime());

    private void OnCalibrationToggle()
    {
        if (_configuration.Calibration.Live.Enabled)
        {
            return;
        }

        if (_configuration.Calibration.VerboseOutput)
        {
            ConsoleHost.Attach();
        }

        var result = _processor.ToggleCalibrationRecording();
        Volatile.Write(ref _calibrationRecording, result.IsRecording ? 1 : 0);
    }

    private void OnCalibrationDelete()
    {
        if (_configuration.Calibration.VerboseOutput)
        {
            ConsoleHost.Attach();
        }

        _processor.TryDeleteCurrentCalibration();
    }

    private void OnReload()
    {
        ShouldReload = true;
        Window.RequestClose();
    }

    private static TimeSpan CurrentTime() =>
        TimeSpan.FromTicks(System.Diagnostics.Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond
                           / System.Diagnostics.Stopwatch.Frequency);

    private bool IsTelemetryFresh()
    {
        var lastPacketTimestamp = Interlocked.Read(ref _lastPacketTimestamp);
        if (lastPacketTimestamp == 0)
        {
            return false;
        }

        var age = System.Diagnostics.Stopwatch.GetTimestamp() - lastPacketTimestamp;
        return age >= 0 && age <= TelemetryTimeoutStopwatchTicks;
    }

    private void StartRecording()
    {
        _recorder?.Start(Path.GetFullPath(_configuration.Recording.OutputDirectory));
    }

    private void StopRecording()
    {
        if (_recorder is null)
        {
            return;
        }

        var file = _recorder.CurrentFile;
        _recorder.Stop();
        Console.WriteLine($"Recorded {_recorder.PacketsWritten} packets to {file ?? "(none)"}");
    }

    public void Dispose()
    {
        _source.PacketReceived -= OnPacketReceived;
        _processor.FinishCalibrationRecording();
        Window.CalibrationDeleteRequested -= OnCalibrationDelete;
        Window.ReloadRequested -= OnReload;
        if (!_configuration.Calibration.Live.Enabled)
        {
            Window.CalibrationToggleRequested -= OnCalibrationToggle;
        }
        _recorder?.Dispose();
        _source.Dispose();
        _frameCapture?.Dispose();
        Window.Dispose();
    }
}
