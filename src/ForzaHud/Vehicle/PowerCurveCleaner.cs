using ForzaHud.Configuration;

namespace ForzaHud.Vehicle;

/// <summary>
/// Removes contaminated observations from an in-memory raw sample set before producing a
/// power curve. The cleaner never edits a finished curve: every refit is based on the samples
/// that remain after the previous rejection pass.
/// </summary>
internal static class PowerCurveCleaner
{
    public static PowerCurveCleaningResult Clean(
        IReadOnlyList<PowerCurveSample> samples,
        float idleRpm,
        float maxRpm,
        PowerbandSettings settings,
        TractionControlSettings tractionControl,
        IReadOnlyDictionary<int, int>? previousRejectedByBin = null)
    {
        var working = samples
            .Where(IsFinite)
            .ToList();
        var rejectedByBin = previousRejectedByBin?.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value)
                            ?? [];
        var rejectedSampleCount = 0;
        var iterations = settings.ShapeCleaningEnabled
            ? Math.Clamp(settings.ShapeCleaningIterations, 1, 8)
            : 0;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var model = BuildModel(working, idleRpm, maxRpm, settings, tractionControl);
            var rejected = FindRejectedSamples(working, model, maxRpm, settings, tractionControl);
            if (rejected.Count == 0)
            {
                break;
            }

            foreach (var sampleIndex in rejected.OrderByDescending(index => index))
            {
                var bin = BinFor(working[sampleIndex].Rpm, idleRpm, maxRpm, settings.BinCount);
                if (bin >= 0)
                {
                    rejectedByBin[bin] = rejectedByBin.GetValueOrDefault(bin) + 1;
                }

                working.RemoveAt(sampleIndex);
                rejectedSampleCount++;
            }
        }

        var finalModel = BuildModel(working, idleRpm, maxRpm, settings, tractionControl);
        return new PowerCurveCleaningResult(
            working,
            finalModel.Bins
                .Select(bin => new PowerCurvePoint(bin.Rpm, bin.SmoothedPower))
                .ToList(),
            finalModel.Bins
                .Select(bin => new PowerCurveBinConfidence(
                        bin.Rpm,
                        bin.Samples.Count,
                        bin.IndependentPassCount,
                        bin.SpreadFraction,
                        rejectedByBin.GetValueOrDefault(bin.Index),
                        bin.Status)
                    {
                        ObservationAge = ObservationAge(bin.Samples),
                    })
                .ToList(),
            rejectedByBin,
            rejectedSampleCount);
    }

    private static CurveModel BuildModel(
        List<PowerCurveSample> samples,
        float idleRpm,
        float maxRpm,
        PowerbandSettings settings,
        TractionControlSettings tractionControl)
    {
        var passIds = AssignPasses(samples, settings, tractionControl);
        var grouped = new Dictionary<int, List<IndexedSample>>();
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            var bin = BinFor(sample.Rpm, idleRpm, maxRpm, settings.BinCount);
            if (bin < 0)
            {
                continue;
            }

            grouped.TryAdd(bin, []);
            grouped[bin].Add(new IndexedSample(sample, index, passIds[index]));
        }

        var bins = grouped
            .OrderBy(pair => pair.Key)
            .Select(pair => CreateBin(
                pair.Key,
                pair.Value,
                idleRpm,
                maxRpm,
                settings))
            .ToList();
        var smoothed = Smooth(bins, settings);
        for (var index = 0; index < bins.Count; index++)
        {
            bins[index].SmoothedPower = smoothed[index];
        }

        return new CurveModel(bins, passIds);
    }

    private static BinEstimate CreateBin(
        int index,
        IReadOnlyList<IndexedSample> samples,
        float idleRpm,
        float maxRpm,
        PowerbandSettings settings)
    {
        var powers = samples.Select(sample => sample.Sample.Power).OrderBy(power => power).ToArray();
        var quantilePower = Quantile(powers, settings.UpperPowerQuantile);
        var lower = Quantile(powers, 0.10f);
        var upper = Quantile(powers, 0.90f);
        var spread = quantilePower <= 0f ? 1f : Math.Max(0f, (upper - lower) / quantilePower);
        var status = samples.Count >= settings.MinimumSamplesPerBin
            && samples.Select(sample => sample.PassId).Distinct().Count() >= settings.MinimumIndependentPassesPerBin
            && spread <= settings.MaximumReliableSpreadFraction
            ? PowerCurveBinStatus.Reliable
            : samples.Count > 0
                ? PowerCurveBinStatus.Provisional
                : PowerCurveBinStatus.Unlearned;

        return new BinEstimate(
            index,
            RpmAt(index, idleRpm, maxRpm, settings.BinCount),
            samples.ToList(),
            quantilePower,
            quantilePower,
            samples.Select(sample => sample.PassId).Distinct().Count(),
            spread,
            status);
    }

    private static float[] Smooth(IReadOnlyList<BinEstimate> bins, PowerbandSettings settings)
    {
        var values = bins.Select(bin => bin.RawPower).ToArray();
        var iterations = Math.Clamp(settings.RobustSmoothingIterations, 1, 8);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var next = new float[values.Length];
            for (var index = 0; index < bins.Count; index++)
            {
                var weightedValues = bins
                    .Select((bin, neighborIndex) => (bin, neighborIndex))
                    .Where(pair => Math.Abs(pair.bin.Rpm - bins[index].Rpm) <= settings.SmoothingWindowRpm)
                    .Select(pair =>
                    (
                        Value: values[pair.neighborIndex],
                        Weight: 1f / (1f + Math.Abs(pair.bin.Rpm - bins[index].Rpm)
                                      / settings.SmoothingWindowRpm)))
                    .ToArray();
                next[index] = WeightedMedian(weightedValues);
            }

            values = next;
        }

        return values;
    }

    private static HashSet<int> FindRejectedSamples(
        IReadOnlyList<PowerCurveSample> samples,
        CurveModel model,
        float maxRpm,
        PowerbandSettings settings,
        TractionControlSettings tractionControl)
    {
        var rejected = new HashSet<int>();
        if (model.Bins.Count == 0)
        {
            return rejected;
        }

        var valleyBins = FindNarrowValleys(model.Bins, maxRpm, settings);
        var threshold = Math.Clamp(settings.SampleResidualThreshold, 0f, 1f);
        foreach (var bin in model.Bins)
        {
            foreach (var indexed in bin.Samples)
            {
                var expected = Interpolate(model.Bins, indexed.Sample.Rpm);
                if (expected <= 0f
                    || indexed.Sample.Power >= expected * (1f - threshold))
                {
                    continue;
                }

                var hasHighCounterpart = bin.Samples.Any(other =>
                    other.OriginalIndex != indexed.OriginalIndex
                    && other.Sample.Power >= expected * (1f - threshold * 0.5f));
                if (valleyBins.Contains(bin.Index)
                    || hasHighCounterpart
                    || HasTelemetryEvidence(
                        samples,
                        indexed.OriginalIndex,
                        model.PassIds,
                        settings,
                        tractionControl))
                {
                    rejected.Add(indexed.OriginalIndex);
                }
            }
        }

        return rejected;
    }

    private static HashSet<int> FindNarrowValleys(
        IReadOnlyList<BinEstimate> bins,
        float maxRpm,
        PowerbandSettings settings)
    {
        var valleys = new HashSet<int>();
        var low = bins
            .Select(bin => bin.SmoothedPower > 0f
                           && bin.RawPower < bin.SmoothedPower * (1f - settings.PowerCurveDipFraction))
            .ToArray();

        for (var start = 0; start < bins.Count; start++)
        {
            if (!low[start])
            {
                continue;
            }

            var end = start;
            while (end + 1 < bins.Count
                   && low[end + 1]
                   && bins[end + 1].Index == bins[end].Index + 1)
            {
                end++;
            }

            var leftIndex = start - 1;
            var rightIndex = end + 1;
            if (leftIndex < 0 || rightIndex >= bins.Count
                || !IsReliableAnchor(bins[leftIndex])
                || !IsReliableAnchor(bins[rightIndex]))
            {
                start = end;
                continue;
            }

            var left = bins[leftIndex];
            var right = bins[rightIndex];
            var valleyWidth = bins[end].Rpm - bins[start].Rpm;
            var anchorWidth = right.Rpm - left.Rpm;
            if (valleyWidth > settings.MaximumNarrowValleyRpmSpan
                || anchorWidth > settings.MaximumNarrowValleyRpmSpan * 2.5f
                || !HasRecoveryBeforeEffectiveRedline(
                    bins,
                    rightIndex,
                    maxRpm,
                    settings.EffectiveRedlinePercentile))
            {
                start = end;
                continue;
            }

            var isValley = false;
            for (var index = start; index <= end; index++)
            {
                var expected = Interpolate(left, right, bins[index].Rpm);
                if (expected <= 0f
                    || bins[index].RawPower >= expected * (1f - settings.PowerCurveDipFraction))
                {
                    isValley = false;
                    break;
                }

                isValley = true;
            }

            var recovered = right.RawPower >= right.SmoothedPower * settings.MinimumValleyRecoveryFraction
                            && right.RawPower > bins[end].RawPower;
            if (isValley && recovered)
            {
                for (var index = start; index <= end; index++)
                {
                    valleys.Add(bins[index].Index);
                }
            }

            start = end;
        }

        return valleys;
    }

    private static bool HasRecoveryBeforeEffectiveRedline(
        IReadOnlyList<BinEstimate> bins,
        int rightIndex,
        float maxRpm,
        float effectiveRedlinePercentile)
    {
        var effectiveRedline = maxRpm * Math.Clamp(effectiveRedlinePercentile, 0f, 1f);
        if (bins[rightIndex].Rpm < effectiveRedline)
        {
            return true;
        }

        return bins
            .Skip(rightIndex + 1)
            .Any(bin => bin.Rpm < effectiveRedline && IsReliableAnchor(bin));
    }

    private static bool HasTelemetryEvidence(
        IReadOnlyList<PowerCurveSample> samples,
        int index,
        IReadOnlyList<int> passIds,
        PowerbandSettings settings,
        TractionControlSettings tractionControl)
    {
        var sample = samples[index];
        if (sample.WheelSlip >= TireSlipTcsAnalyzer.MinimumSlip(
                tractionControl.TireSlip,
                sample.DrivetrainType))
        {
            return true;
        }

        var samePassBefore = index > 0 && passIds[index] == passIds[index - 1];
        var samePassAfter = index + 1 < samples.Count && passIds[index] == passIds[index + 1];
        var abruptDrop = samePassBefore
                         && samples[index - 1].Power > 0f
                         && sample.Power <= samples[index - 1].Power
                            * (1f - tractionControl.TransientPowerDropFraction);
        var recovered = samePassAfter
                        && samples[index + 1].Power > sample.Power
                        && samples[index + 1].Power >= samples[Math.Max(0, index - 1)].Power
                            * (1f - settings.SampleResidualThreshold);
        if (abruptDrop && recovered)
        {
            return true;
        }

        var cuts = 0;
        var first = Math.Max(1, index - Math.Max(1, tractionControl.RepeatedCutWindowSamples));
        for (var candidate = first; candidate <= index; candidate++)
        {
            if (passIds[candidate] != passIds[candidate - 1]
                || samples[candidate - 1].Power <= 0f)
            {
                continue;
            }

            if (samples[candidate].Power <= samples[candidate - 1].Power
                * (1f - tractionControl.TransientPowerDropFraction))
            {
                cuts++;
            }
        }

        if (cuts >= Math.Max(1, tractionControl.MinimumRepeatedCuts))
        {
            return true;
        }

        if (!samePassBefore || !samePassAfter
            || sample.RpmRate <= 0f
            || samples[index - 1].RpmRate <= 0f)
        {
            return false;
        }

        return sample.RpmRate <= samples[index - 1].RpmRate
                   * (1f - settings.RpmRateChangeFraction)
               && samples[index + 1].RpmRate > sample.RpmRate;
    }

    private static int[] AssignPasses(
        List<PowerCurveSample> samples,
        PowerbandSettings settings,
        TractionControlSettings tractionControl)
    {
        var passIds = new int[samples.Count];
        for (var index = 1; index < samples.Count; index++)
        {
            passIds[index] = passIds[index - 1];
            var previous = samples[index - 1];
            var current = samples[index];
            if (current.Gear != previous.Gear
                || current.Rpm < previous.Rpm
                    - settings.MaximumRpmDrop
                || IsTelemetryGap(previous.Timestamp, current.Timestamp, tractionControl.MaximumTelemetryGapMilliseconds))
            {
                passIds[index]++;
            }
        }

        return passIds;
    }

    private static bool IsReliableAnchor(BinEstimate bin) =>
        bin.Status == PowerCurveBinStatus.Reliable;

    private static float Interpolate(IReadOnlyList<BinEstimate> bins, float rpm)
    {
        if (rpm <= bins[0].Rpm)
        {
            return bins[0].SmoothedPower;
        }

        if (rpm >= bins[^1].Rpm)
        {
            return bins[^1].SmoothedPower;
        }

        for (var index = 1; index < bins.Count; index++)
        {
            if (rpm > bins[index].Rpm)
            {
                continue;
            }

            var lower = bins[index - 1];
            var upper = bins[index];
            var span = upper.Rpm - lower.Rpm;
            if (span <= 0f)
            {
                return upper.SmoothedPower;
            }

            var fraction = (rpm - lower.Rpm) / span;
            return lower.SmoothedPower + fraction * (upper.SmoothedPower - lower.SmoothedPower);
        }

        return bins[^1].SmoothedPower;
    }

    private static float Interpolate(BinEstimate lower, BinEstimate upper, float rpm)
    {
        var span = upper.Rpm - lower.Rpm;
        if (span <= 0f)
        {
            return lower.RawPower;
        }

        var fraction = Math.Clamp((rpm - lower.Rpm) / span, 0f, 1f);
        return lower.RawPower + fraction * (upper.RawPower - lower.RawPower);
    }

    private static float WeightedMedian((float Value, float Weight)[] values)
    {
        if (values.Length == 0)
        {
            return 0f;
        }

        var ordered = values.OrderBy(value => value.Value).ToArray();
        var totalWeight = ordered.Sum(value => value.Weight);
        var accumulated = 0f;
        foreach (var value in ordered)
        {
            accumulated += value.Weight;
            if (accumulated >= totalWeight * 0.5f)
            {
                return value.Value;
            }
        }

        return ordered[^1].Value;
    }

    private static float Quantile(float[] sortedValues, float quantile)
    {
        if (sortedValues.Length == 0)
        {
            return 0f;
        }

        var position = Math.Clamp(quantile, 0f, 1f) * (sortedValues.Length - 1);
        var lower = (int)MathF.Floor(position);
        var upper = Math.Min(sortedValues.Length - 1, lower + 1);
        var fraction = position - lower;
        return sortedValues[lower] + fraction * (sortedValues[upper] - sortedValues[lower]);
    }

    private static TimeSpan ObservationAge(IReadOnlyList<IndexedSample> samples)
    {
        var timestamps = samples
            .Select(sample => sample.Sample.Timestamp)
            .Where(timestamp => timestamp > TimeSpan.Zero)
            .ToArray();
        return timestamps.Length < 2
            ? TimeSpan.Zero
            : timestamps.Max() - timestamps.Min();
    }

    private static bool IsFinite(PowerCurveSample sample) =>
        float.IsFinite(sample.Rpm)
        && float.IsFinite(sample.Power)
        && float.IsFinite(sample.Throttle)
        && float.IsFinite(sample.RpmRate)
        && float.IsFinite(sample.WheelSlip)
        && sample.Rpm > 0f
        && sample.Power > 0f;

    private static int BinFor(float rpm, float idleRpm, float maxRpm, int binCount)
    {
        binCount = Math.Clamp(binCount, 8, 256);
        if (maxRpm <= idleRpm || rpm < idleRpm || rpm > maxRpm)
        {
            return -1;
        }

        var bin = (int)((rpm - idleRpm) / (maxRpm - idleRpm) * binCount);
        return Math.Clamp(bin, 0, binCount - 1);
    }

    private static float RpmAt(int bin, float idleRpm, float maxRpm, int binCount) =>
        idleRpm + ((bin + 0.5f) / Math.Clamp(binCount, 8, 256)) * (maxRpm - idleRpm);

    private static bool IsTelemetryGap(TimeSpan previous, TimeSpan current, double maximumMilliseconds) =>
        previous > TimeSpan.Zero
        && current > TimeSpan.Zero
        && ((current - previous) <= TimeSpan.Zero
            || (current - previous).TotalMilliseconds > maximumMilliseconds);

    private sealed class CurveModel(IReadOnlyList<BinEstimate> bins, IReadOnlyList<int> passIds)
    {
        public IReadOnlyList<BinEstimate> Bins { get; } = bins;

        public IReadOnlyList<int> PassIds { get; } = passIds;
    }

    private sealed class BinEstimate(
        int index,
        float rpm,
        List<IndexedSample> samples,
        float rawPower,
        float smoothedPower,
        int independentPassCount,
        float spreadFraction,
        PowerCurveBinStatus status)
    {
        public int Index { get; } = index;

        public float Rpm { get; } = rpm;

        public List<IndexedSample> Samples { get; } = samples;

        public float RawPower { get; } = rawPower;

        public float SmoothedPower { get; set; } = smoothedPower;

        public int IndependentPassCount { get; } = independentPassCount;

        public float SpreadFraction { get; } = spreadFraction;

        public PowerCurveBinStatus Status { get; } = status;
    }

    private readonly record struct IndexedSample(
        PowerCurveSample Sample,
        int OriginalIndex,
        int PassId);
}
