namespace ForzaHud.Telemetry;

/// <summary>
/// On-disk format for recorded telemetry sessions.
///
/// The raw FH6 packets are the authoritative source, so a session stores unmodified
/// packet bytes plus just enough timing information to reproduce the original stream.
///
/// Layout (little-endian throughout):
/// <code>
///   Header
///     0  8  magic "FZHUDRAW"
///     8  2  format version
///     10 2  reserved (0)
///     12 8  session start, UTC ticks (Int64)
///   Repeating records until end of file
///     0  8  offset from session start, microseconds (UInt64)
///     8  2  payload length (UInt16)
///     10 n  payload
/// </code>
/// </summary>
public static class SessionFileFormat
{
    public const string Magic = "FZHUDRAW";
    public const int MagicLength = 8;
    public const ushort CurrentVersion = 1;
    public const int HeaderSize = 20;

    /// <summary>Extension used for recorded sessions.</summary>
    public const string FileExtension = ".fzh";
}
