using ForzaHud.Telemetry;

namespace ForzaHud.Vehicle;

/// <summary>Captures raw RPM drops observed across valid forward-gear upshifts.</summary>
public sealed class ShiftUpRpmDropRatioLogger
{
    private const float MinimumMeaningfulPostNeutralDropRatio = 0.95f;

    private readonly Dictionary<int, List<float>> _ratios = [];
    private readonly List<float> _durationsMilliseconds = [];
    private TelemetrySnapshot? _previous;
    private PendingShift? _pendingShift;
    private bool _neutralSeen;

    /// <summary>All valid drop ratios observed for each outgoing gear.</summary>
    public IReadOnlyDictionary<int, List<float>> Ratios => _ratios;

    /// <summary>Measured elapsed time across upshifts, shared across all gears.</summary>
    public IReadOnlyList<float> DurationMilliseconds => _durationsMilliseconds;

    public Dictionary<int, List<float>> Snapshot() =>
        RatiosIncludingPending().ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Count == 0 ? [] : new List<float> { pair.Value.Average() });

    public List<float> DurationSnapshot() => DurationsIncludingPending();

    public void RemoveGear(int gear)
    {
        _ratios.Remove(gear);
    }

    /// <summary>Resets the transition history and all ratios collected for the current run.</summary>
    public void Reset()
    {
        _previous = null;
        _pendingShift = null;
        _neutralSeen = false;
        _ratios.Clear();
        _durationsMilliseconds.Clear();
    }

    /// <summary>Commits a post-neutral shift whose lowest RPM has not been followed by a rise yet.</summary>
    public void FlushPending()
    {
        if (_pendingShift is { } pending)
        {
            CommitPending(pending);
            _pendingShift = null;
        }
    }

    /// <summary>Observes one frame and returns a newly captured ratio, if one was completed.</summary>
    public ShiftUpRpmDropRatio? Observe(TelemetrySnapshot snapshot)
    {
        var previous = _previous;
        if (IsNeutralFrame(snapshot))
        {
            _neutralSeen = previous is not null;
            return null;
        }

        if (!IsValidFrame(snapshot))
        {
            _previous = null;
            _pendingShift = null;
            _neutralSeen = false;
            return null;
        }

        if (_pendingShift is { } pending)
        {
            if (VehicleIdentity.From(snapshot) == pending.VehicleIdentity
                && snapshot.Gear == pending.IncomingGear)
            {
                var afterShiftRpm = Math.Min(snapshot.CurrentEngineRpm, pending.AfterShiftRpm);
                var meaningfulDrop = pending.MeaningfulDrop
                    || afterShiftRpm / pending.BeforeShiftRpm <= MinimumMeaningfulPostNeutralDropRatio;
                if (!meaningfulDrop || snapshot.CurrentEngineRpm < pending.AfterShiftRpm)
                {
                    _pendingShift = pending with
                    {
                        AfterShiftRpm = afterShiftRpm,
                        MeaningfulDrop = meaningfulDrop,
                        AfterShiftAt = snapshot.CurrentEngineRpm < pending.AfterShiftRpm
                            ? snapshot.ReceivedAt
                            : pending.AfterShiftAt,
                    };
                    _previous = snapshot;
                    return null;
                }

                _pendingShift = null;
                _previous = snapshot;
                _neutralSeen = false;
                return CommitPending(pending);
            }

            _pendingShift = null;
        }

        if (previous is null
            || !IsValidFrame(previous)
            || VehicleIdentity.From(previous) != VehicleIdentity.From(snapshot)
            || snapshot.Gear != previous.Gear + 1
            || previous.Gear < 1)
        {
            _previous = snapshot;
            _neutralSeen = false;
            return null;
        }

        if (_neutralSeen)
        {
            _pendingShift = new PendingShift(
                VehicleIdentity.From(previous),
                previous.Gear,
                snapshot.Gear,
                previous.CurrentEngineRpm,
                snapshot.CurrentEngineRpm,
                previous.ReceivedAt,
                snapshot.ReceivedAt,
                snapshot.CurrentEngineRpm / previous.CurrentEngineRpm
                    <= MinimumMeaningfulPostNeutralDropRatio);
            _previous = snapshot;
            _neutralSeen = false;
            return null;
        }

        if (snapshot.CurrentEngineRpm >= previous.CurrentEngineRpm)
        {
            _previous = snapshot;
            _neutralSeen = false;
            return null;
        }

        _previous = snapshot;
        _neutralSeen = false;
        return AddRatio(
            previous.Gear,
            previous.CurrentEngineRpm,
            snapshot.CurrentEngineRpm,
            previous.ReceivedAt,
            snapshot.ReceivedAt);
    }

    private ShiftUpRpmDropRatio CommitPending(PendingShift pending) =>
        AddRatio(
            pending.OutgoingGear,
            pending.BeforeShiftRpm,
            pending.AfterShiftRpm,
            pending.BeforeShiftAt,
            pending.AfterShiftAt);

    private ShiftUpRpmDropRatio AddRatio(
        int outgoingGear,
        float beforeShiftRpm,
        float afterShiftRpm,
        TimeSpan beforeShiftAt,
        TimeSpan afterShiftAt)
    {
        var ratio = afterShiftRpm / beforeShiftRpm;
        if (!_ratios.TryGetValue(outgoingGear, out var samples))
        {
            samples = [];
            _ratios[outgoingGear] = samples;
        }

        samples.Add(ratio);
        var durationMilliseconds = afterShiftAt > beforeShiftAt
            ? (float)(afterShiftAt - beforeShiftAt).TotalMilliseconds
            : 0f;
        if (durationMilliseconds > 0f && float.IsFinite(durationMilliseconds))
        {
            _durationsMilliseconds.Add(durationMilliseconds);
        }

        return new ShiftUpRpmDropRatio(
            outgoingGear,
            ratio,
            beforeShiftRpm,
            afterShiftRpm,
            durationMilliseconds);
    }

    private Dictionary<int, List<float>> RatiosIncludingPending()
    {
        var ratios = _ratios.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        if (_pendingShift is { } pending)
        {
            if (!ratios.TryGetValue(pending.OutgoingGear, out var samples))
            {
                samples = [];
                ratios[pending.OutgoingGear] = samples;
            }

            samples.Add(pending.AfterShiftRpm / pending.BeforeShiftRpm);
        }

        return ratios;
    }

    private List<float> DurationsIncludingPending()
    {
        var durations = _durationsMilliseconds.ToList();
        if (_pendingShift is { } pending
            && pending.AfterShiftAt > pending.BeforeShiftAt)
        {
            durations.Add((float)(pending.AfterShiftAt - pending.BeforeShiftAt).TotalMilliseconds);
        }

        return durations;
    }

    private static bool IsValidFrame(TelemetrySnapshot snapshot) =>
        IsValidRpmFrame(snapshot)
        && snapshot.Gear is >= 1 and <= 10;

    private static bool IsNeutralFrame(TelemetrySnapshot snapshot) =>
        IsValidRpmFrame(snapshot)
        && snapshot.Gear == 11;

    private static bool IsValidRpmFrame(TelemetrySnapshot snapshot) =>
        snapshot.IsRaceOn
        && snapshot.NumCylinders != 0
        && snapshot.EngineMaxRpm > snapshot.EngineIdleRpm
        && snapshot.EngineMaxRpm > 0f
        && snapshot.CurrentEngineRpm >= snapshot.EngineIdleRpm
        && snapshot.CurrentEngineRpm <= snapshot.EngineMaxRpm;

    private readonly record struct PendingShift(
        VehicleIdentity VehicleIdentity,
        int OutgoingGear,
        int IncomingGear,
        float BeforeShiftRpm,
        float AfterShiftRpm,
        TimeSpan BeforeShiftAt,
        TimeSpan AfterShiftAt,
        bool MeaningfulDrop);
}

/// <summary>One observed outgoing-gear RPM drop.</summary>
public readonly record struct ShiftUpRpmDropRatio(
    int OutgoingGear,
    float Ratio,
    float BeforeShiftRpm,
    float AfterShiftRpm,
    float ShiftDurationMilliseconds = 0f);
