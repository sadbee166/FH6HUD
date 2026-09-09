namespace ForzaHud.Common;

/// <summary>
/// Frame-rate independent exponential smoothing.
///
/// Given a time constant tau, the value reaches ~63% of the way to its target in tau
/// seconds regardless of frame rate. A time constant of zero passes the target straight
/// through, which is what driver inputs need.
/// </summary>
public struct SmoothValue
{
    private double _current;
    private bool _initialized;

    /// <summary>Currently held value.</summary>
    public double Current => _current;

    /// <summary>Sets the value without smoothing, e.g. on the first sample.</summary>
    public void Reset(double value)
    {
        _current = value;
        _initialized = true;
    }

    /// <summary>
    /// Moves towards <paramref name="target"/>.
    /// </summary>
    /// <param name="target">Newest measurement.</param>
    /// <param name="timeConstantSeconds">Smoothing time constant. Zero disables smoothing.</param>
    /// <param name="deltaSeconds">Time since the previous update.</param>
    public double Update(double target, double timeConstantSeconds, double deltaSeconds)
    {
        if (!_initialized)
        {
            Reset(target);
            return _current;
        }

        if (timeConstantSeconds <= 0 || deltaSeconds <= 0)
        {
            _current = target;
            return _current;
        }

        var alpha = 1.0 - Math.Exp(-deltaSeconds / timeConstantSeconds);
        _current += (target - _current) * alpha;
        return _current;
    }
}

/// <summary>
/// Small numeric helpers shared by the HUD and vehicle layers.
/// </summary>
public static class MathHelper
{
    public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    /// <summary>Maps <paramref name="value"/> from [inMin, inMax] to [0, 1], clamped.</summary>
    public static float Normalize(float value, float inMin, float inMax)
    {
        if (inMax <= inMin)
        {
            return 0f;
        }

        return Clamp01((value - inMin) / (inMax - inMin));
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
