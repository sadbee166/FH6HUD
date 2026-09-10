using ForzaHud.Configuration;

namespace ForzaHud.Vehicle;

/// <summary>
/// Finds the activated cyan TCR shape inside a captured speedometer region.
/// The detector is deliberately stateless: the game indicator itself supplies the state and
/// each captured frame replaces the previous result.
/// </summary>
public sealed class FrameTcsAnalyzer
{
    private readonly FrameTcsSettings _settings;
    private string _loadedTemplate = string.Empty;
    private bool[] _template = [];
    private int _templateWidth;
    private int _templateHeight;
    private int _foregroundCellCount;
    private int _backgroundCellCount;
    private int[] _prefix = [];
    private int _captureWidth;
    private int _captureHeight;

    public FrameTcsAnalyzer(FrameTcsSettings settings) => _settings = settings;

    public bool IsActive { get; private set; }

    /// <summary>Total cyan pixels in the captured speedometer region.</summary>
    public int LastOnPixelCount { get; private set; }

    /// <summary>Best shape score found in the most recent region.</summary>
    public float LastShapeMatch { get; private set; }

    /// <summary>Reads BGRA pixels and searches for the configured cyan TCR shape.</summary>
    public bool Update(ReadOnlySpan<byte> bgraPixels, int width, int height)
    {
        EnsureTemplate();
        EnsureBuffers(width, height);

        var minimumChannel = _settings.MinimumCyanChannel;
        var minimumDominance = _settings.MinimumCyanDominance;
        var stride = width + 1;
        var count = 0;

        for (var y = 0; y < height; y++)
        {
            var rowSum = 0;
            var sourceOffset = y * width * 4;
            var prefixOffset = (y + 1) * stride;
            var previousPrefixOffset = y * stride;
            _prefix[prefixOffset] = 0;

            for (var x = 0; x < width; x++)
            {
                var blue = bgraPixels[sourceOffset];
                var green = bgraPixels[sourceOffset + 1];
                var red = bgraPixels[sourceOffset + 2];
                var cyan = green >= minimumChannel
                    && blue >= minimumChannel
                    && green - red >= minimumDominance
                    && blue - red >= minimumDominance;

                rowSum += cyan ? 1 : 0;
                _prefix[prefixOffset + x + 1] = _prefix[previousPrefixOffset + x + 1] + rowSum;
                count += cyan ? 1 : 0;
                sourceOffset += 4;
            }
        }

        LastOnPixelCount = count;
        LastShapeMatch = 0f;
        IsActive = FindShape(width, height);
        return IsActive;
    }

    public void Reset()
    {
        LastOnPixelCount = 0;
        LastShapeMatch = 0f;
        IsActive = false;
    }

    private bool FindShape(int width, int height)
    {
        var shapeWidth = Math.Max(_templateWidth, (int)MathF.Round(width * _settings.TemplateWidthFraction));
        var shapeHeight = Math.Max(_templateHeight, (int)MathF.Round(height * _settings.TemplateHeightFraction));
        if (shapeWidth > width || shapeHeight > height)
        {
            return false;
        }

        var step = Math.Max(1, _settings.SearchStepPixels);
        var minimumCandidatePixels = Math.Max(1, _settings.MinimumOnPixels);
        var bestScore = 0f;

        for (var y = 0; y <= height - shapeHeight; y += step)
        {
            for (var x = 0; x <= width - shapeWidth; x += step)
            {
                if (RectangleSum(x, y, shapeWidth, shapeHeight, width) < minimumCandidatePixels)
                {
                    continue;
                }

                var score = ScoreShape(
                    x,
                    y,
                    shapeWidth,
                    shapeHeight,
                    width,
                    _settings.MinimumForegroundCellCoverage,
                    _settings.MaximumBackgroundCellCoverage);
                bestScore = Math.Max(bestScore, score);
                if (score >= _settings.MinimumShapeMatch)
                {
                    LastShapeMatch = score;
                    return true;
                }
            }
        }

        LastShapeMatch = bestScore;
        return false;
    }

    private float ScoreShape(
        int x,
        int y,
        int width,
        int height,
        int captureWidth,
        float minimumForegroundCoverage,
        float maximumBackgroundCoverage)
    {
        var foregroundMatches = 0;
        var backgroundMatches = 0;

        for (var row = 0; row < _templateHeight; row++)
        {
            var top = y + row * height / _templateHeight;
            var bottom = y + (row + 1) * height / _templateHeight;
            for (var column = 0; column < _templateWidth; column++)
            {
                var left = x + column * width / _templateWidth;
                var right = x + (column + 1) * width / _templateWidth;
                var cellWidth = Math.Max(1, right - left);
                var cellHeight = Math.Max(1, bottom - top);
                var coverage = (float)RectangleSum(left, top, cellWidth, cellHeight, captureWidth)
                    / (cellWidth * cellHeight);

                if (_template[row * _templateWidth + column])
                {
                    if (coverage >= minimumForegroundCoverage)
                    {
                        foregroundMatches++;
                    }
                }
                else if (coverage <= maximumBackgroundCoverage)
                {
                    backgroundMatches++;
                }
            }
        }

        var foregroundScore = (float)foregroundMatches / _foregroundCellCount;
        var backgroundScore = (float)backgroundMatches / _backgroundCellCount;
        var score = foregroundScore * 0.75f + backgroundScore * 0.25f;
        return foregroundScore >= _settings.MinimumForegroundMatch ? score : 0f;
    }

    private void EnsureTemplate()
    {
        if (_loadedTemplate == _settings.Template)
        {
            return;
        }

        var rows = _settings.Template.Split('/');
        if (rows.Length == 0 || rows[0].Length == 0)
        {
            throw new InvalidOperationException("The TCR shape template must contain at least one cell.");
        }

        var width = rows[0].Length;
        var template = new bool[checked(width * rows.Length)];
        var foreground = 0;
        for (var row = 0; row < rows.Length; row++)
        {
            if (rows[row].Length != width)
            {
                throw new InvalidOperationException("Every row in the TCR shape template must have the same width.");
            }

            for (var column = 0; column < width; column++)
            {
                var cell = rows[row][column];
                if (cell is not ('#' or '.'))
                {
                    throw new InvalidOperationException("The TCR shape template may contain only '#' and '.'.");
                }

                template[row * width + column] = cell == '#';
                foreground += cell == '#' ? 1 : 0;
            }
        }

        _loadedTemplate = _settings.Template;
        _template = template;
        _templateWidth = width;
        _templateHeight = rows.Length;
        _foregroundCellCount = foreground;
        _backgroundCellCount = template.Length - foreground;
    }

    private void EnsureBuffers(int width, int height)
    {
        if (width == _captureWidth && height == _captureHeight)
        {
            return;
        }

        _captureWidth = width;
        _captureHeight = height;
        _prefix = new int[checked((width + 1) * (height + 1))];
    }

    private int RectangleSum(int x, int y, int width, int height, int captureWidth)
    {
        var stride = captureWidth + 1;
        var left = x;
        var top = y;
        var right = x + width;
        var bottom = y + height;
        return _prefix[bottom * stride + right]
            - _prefix[top * stride + right]
            - _prefix[bottom * stride + left]
            + _prefix[top * stride + left];
    }
}
