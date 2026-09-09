using ForzaHud.Configuration;

namespace ForzaHud.Vehicle;

/// <summary>
/// Classifies the in-game TCR indicator from a BGRA screen-region capture.
/// The detector is deliberately stateless: the game indicator itself supplies the state and
/// each captured frame replaces the previous result.
/// </summary>
public sealed class FrameTcsAnalyzer
{
    private readonly FrameTcsSettings _settings;

    public FrameTcsAnalyzer(FrameTcsSettings settings) => _settings = settings;

    public bool IsActive { get; private set; }

    public int LastOnPixelCount { get; private set; }

    /// <summary>Reads BGRA pixels and returns whether enough cyan indicator pixels are present.</summary>
    public bool Update(ReadOnlySpan<byte> bgraPixels)
    {
        var count = 0;
        var minimumChannel = _settings.MinimumCyanChannel;
        var minimumDominance = _settings.MinimumCyanDominance;

        for (var offset = 0; offset + 2 < bgraPixels.Length; offset += 4)
        {
            var blue = bgraPixels[offset];
            var green = bgraPixels[offset + 1];
            var red = bgraPixels[offset + 2];
            if (green >= minimumChannel
                && blue >= minimumChannel
                && green - red >= minimumDominance
                && blue - red >= minimumDominance)
            {
                count++;
            }
        }

        LastOnPixelCount = count;
        IsActive = count >= _settings.MinimumOnPixels;
        return IsActive;
    }

    public void Reset()
    {
        LastOnPixelCount = 0;
        IsActive = false;
    }
}
