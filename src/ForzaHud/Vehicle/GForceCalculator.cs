using ForzaHud.Configuration;

namespace ForzaHud.Vehicle;

/// <summary>
/// Converts FH6's car-local acceleration into lateral and longitudinal G.
///
/// FH6 documents its axes as X = right, Y = up, Z = forward in the car's local space, so
/// lateral G comes from X and longitudinal G from Z. The sign of the reported values has
/// historically disagreed with that description, which is why the signs are configuration
/// rather than hard-coded: verify them against a recorded session and set
/// <see cref="GForceSettings.InvertLateral"/> / <see cref="GForceSettings.InvertLongitudinal"/>
/// accordingly.
///
/// The lateral/longitudinal split is intentionally all this does. Yaw, understeer and
/// oversteer are separate problems and are not inferred here.
/// </summary>
public static class GForceCalculator
{
    /// <summary>Standard gravity in m/s^2.</summary>
    public const float Gravity = 9.80665f;

    /// <summary>Lateral acceleration in G. Positive to the car's right after sign correction.</summary>
    public static float Lateral(float accelerationX, GForceSettings settings)
    {
        var g = accelerationX / Gravity;
        return settings.InvertLateral ? -g : g;
    }

    /// <summary>Longitudinal acceleration in G. Positive under acceleration after sign correction.</summary>
    public static float Longitudinal(float accelerationZ, GForceSettings settings)
    {
        var g = accelerationZ / Gravity;
        return settings.InvertLongitudinal ? -g : g;
    }

    /// <summary>Vertical acceleration in G. Positive upwards after sign correction.</summary>
    public static float Vertical(float accelerationY, GForceSettings settings)
    {
        var g = accelerationY / Gravity;
        return settings.InvertVertical ? -g : g;
    }
}
