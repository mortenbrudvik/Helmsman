using System.Drawing;
using System.Globalization;
using Box = Helmsman.FixtureApp.GlFrameCounterLayout.Box;

namespace Helmsman.E2ETests.Harness;

/// <summary>
/// The GL frame counter window's <see cref="FixtureContract.Events.GlOpened"/> event (<c>--open-gl</c>): where the fixture put
/// the window and what it could set up, as ground truth for the captures of it.
/// </summary>
public sealed record FixtureGlWindow(
    nint Handle,
    string Title,
    string Mode,
    ScreenRect WindowRect,
    ScreenRect ClientRect,
    ScreenRect MonitorRect,
    bool VSync,
    string Vendor,
    string Renderer,
    ScreenRect CloseRect,
    (int X, int Y) NeutralPoint)
{
    public static FixtureGlWindow From(FixtureEvent opened)
    {
        if (opened.Name != FixtureContract.Events.GlOpened)
        {
            throw new ArgumentException($"Expected a '{FixtureContract.Events.GlOpened}' event, got '{opened.Name}'.", nameof(opened));
        }

        var data = opened.Data;
        var neutral = data.GetProperty("neutralPoint");
        return new FixtureGlWindow(
            (nint)data.GetProperty("hwnd").GetInt64(),
            data.GetProperty("title").GetString() ?? "",
            data.GetProperty("mode").GetString() ?? "",
            ScreenRect.From(data.GetProperty("windowRect")),
            ScreenRect.From(data.GetProperty("clientRect")),
            ScreenRect.From(data.GetProperty("monitorRect")),
            data.GetProperty("vsync").GetBoolean(),
            data.GetProperty("vendor").GetString() ?? "",
            data.GetProperty("renderer").GetString() ?? "",
            ScreenRect.From(data.GetProperty("closeRect")),
            (neutral.GetProperty("x").GetInt32(), neutral.GetProperty("y").GetInt32()));
    }

    /// <summary>Waits for the GL window to open; fails with the fixture's reason when it could not.</summary>
    public static FixtureGlWindow WaitForOpen(FixtureAppSession fixture, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var opened = fixture.WaitForAnyEvent([FixtureContract.Events.GlOpened, FixtureContract.Events.GlFailed], timeout ?? TimeSpan.FromSeconds(10));
        return opened.Name == FixtureContract.Events.GlOpened
            ? From(opened)
            : throw new InvalidOperationException($"The fixture could not open its GL window: {opened.GetString("message")}");
    }

    public override string ToString() =>
        $"0x{Handle:X} '{Title}' {Mode}, window {WindowRect}, client {ClientRect}, monitor {MonitorRect}, vsync {VSync}, {Vendor} {Renderer}";
}

/// <summary>
/// The frames the GL window reported (<see cref="FixtureContract.Events.GlFrame"/>: the frame it last swapped, with the event's
/// time), so a test can tell which frame was current at a moment: between two reports the frame number is interpolated, as it
/// grows by one per vsync.
/// </summary>
public sealed class GlFrameTimeline
{
    private readonly IReadOnlyList<(DateTimeOffset Time, long Frame)> _points;

    public GlFrameTimeline(IEnumerable<(DateTimeOffset Time, long Frame)> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = points.OrderBy(p => p.Time).ToList();
    }

    /// <summary>How many reports there are.</summary>
    public int Count => _points.Count;

    /// <summary>The time of the last report, or null.</summary>
    public DateTimeOffset? LastTime => _points.Count == 0 ? null : _points[^1].Time;

    /// <summary>The frame rate over all the reports; 0 with fewer than two.</summary>
    public double FramesPerSecond =>
        _points.Count < 2 || _points[^1].Time <= _points[0].Time
            ? 0
            : (_points[^1].Frame - _points[0].Frame) / (_points[^1].Time - _points[0].Time).TotalSeconds;

    /// <summary>
    /// The frame rate over the reports at or after <paramref name="time"/>, so a window that stopped drawing at that moment
    /// shows 0 even though it drew before; 0 with fewer than two such reports.
    /// </summary>
    public double FramesPerSecondSince(DateTimeOffset time) => new GlFrameTimeline(_points.Where(p => p.Time >= time)).FramesPerSecond;

    /// <summary>The <see cref="FixtureContract.Events.GlFrame"/> events among <paramref name="events"/>.</summary>
    public static GlFrameTimeline From(IEnumerable<FixtureEvent> events) =>
        new(events.Where(e => e.Name == FixtureContract.Events.GlFrame).Select(e => (TimeOf(e), e.GetInt64("frame") ?? 0)));

    /// <summary>
    /// The fixture's frames once it has reported one after <paramref name="time"/>, so <see cref="FrameAt"/> can interpolate
    /// at <paramref name="time"/> rather than stop at the last report.
    /// </summary>
    public static GlFrameTimeline After(FixtureAppSession fixture, DateTimeOffset time, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        fixture.WaitForEvent(FixtureContract.Events.GlFrame, e => TimeOf(e) > time, timeout ?? TimeSpan.FromSeconds(3));
        return From(fixture.Events.ReadAll());
    }

    /// <summary>The time a fixture event was written.</summary>
    public static DateTimeOffset TimeOf(FixtureEvent fixtureEvent)
    {
        ArgumentNullException.ThrowIfNull(fixtureEvent);
        return DateTimeOffset.Parse(fixtureEvent.Data.GetProperty("time").GetString() ?? "", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    /// <summary>
    /// The frame the fixture had last swapped at <paramref name="time"/>, interpolated between the reports around it; the
    /// first report's frame before it, the last report's after it, and 0 without reports.
    /// </summary>
    public long FrameAt(DateTimeOffset time)
    {
        if (_points.Count == 0)
        {
            return 0;
        }
        if (time <= _points[0].Time)
        {
            return _points[0].Frame;
        }

        for (var i = 1; i < _points.Count; i++)
        {
            var (afterTime, afterFrame) = _points[i];
            if (time > afterTime)
            {
                continue;
            }

            var (beforeTime, beforeFrame) = _points[i - 1];
            var span = (afterTime - beforeTime).TotalMilliseconds;
            var share = span <= 0 ? 1 : (time - beforeTime).TotalMilliseconds / span;
            return beforeFrame + (long)Math.Floor((afterFrame - beforeFrame) * share);
        }

        return _points[^1].Frame;
    }

    /// <summary>
    /// The frames a capture taken between <paramref name="before"/> and <paramref name="after"/> can show if it is current: from
    /// the frame swapped <paramref name="lag"/> before <paramref name="before"/> (the compositor shows a swap a frame or a few
    /// later) to the frame swapped at <paramref name="after"/>.
    /// </summary>
    public (long Lowest, long Highest) CurrentRange(DateTimeOffset before, DateTimeOffset after, TimeSpan lag) =>
        (FrameAt(before - lag), FrameAt(after));

    /// <summary>
    /// Whether <paramref name="decodedFrame"/>, a frame number as the row shows it (<see cref="GlFrameDecoder"/>: modulo
    /// 2^<see cref="GlFrameCounterLayout.DataBits"/>), is one of the fixture's frames <paramref name="lowest"/> to
    /// <paramref name="highest"/> (its own, unmasked numbers): compared modulo <see cref="GlFrameCounterLayout.FrameMask"/>,
    /// so a range across the row's wrap still holds the frames on both sides of it.
    /// </summary>
    public static bool IsInRange(long decodedFrame, long lowest, long highest)
    {
        if (highest < lowest)
        {
            return false;
        }

        var span = highest - lowest;
        return span >= GlFrameCounterLayout.FrameMask || ((decodedFrame - lowest) & GlFrameCounterLayout.FrameMask) <= span;
    }
}

/// <summary>What a capture taken right after input showed, compared with the capture of the same window right before it.</summary>
public enum GlFrameAfterInput
{
    /// <summary>
    /// Both decode to the same frame: the pixels are plausibly identical, so the server should warn
    /// CaptureUnchangedAfterInput.
    /// </summary>
    SameFrame,

    /// <summary>
    /// Both decode, to different frames: the pixels differ, and the server, which warns CaptureUnchangedAfterInput only on
    /// identical pixels, must not warn.
    /// </summary>
    OtherFrame,

    /// <summary>At least one did not decode (a one-color image never does): nothing can be said about the warning.</summary>
    Unreadable
}

/// <summary>What <see cref="GlFrameDecoder"/> read from an image: the frame number, or null, and what it saw.</summary>
public sealed record GlFrameReading(long? Frame, string Description)
{
    public bool Found => Frame != null;

    /// <summary>
    /// Compares <paramref name="after"/>, read from a capture taken right after input, with <paramref name="before"/>, read
    /// from the capture of the same window with the same parameters right before the input (<see cref="GlFrameAfterInput"/>).
    /// The decoder reads the same image the same way, so two different frames mean different pixels.
    /// </summary>
    public static GlFrameAfterInput CompareAfterInput(GlFrameReading before, GlFrameReading after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return !before.Found || !after.Found ? GlFrameAfterInput.Unreadable
            : before.Frame == after.Frame ? GlFrameAfterInput.SameFrame
            : GlFrameAfterInput.OtherFrame;
    }

    public override string ToString() => Description;
}

/// <summary>
/// Reads the frame number the GL window drew (<see cref="GlFrameCounterLayout"/>) back from the pixels of a capture of its
/// client area, at any scale (a capture's <c>maxDimension</c> scales the whole image, so the layout's fractions still hold),
/// and through JPEG's color noise: each cell is the average of a grid of points in its middle, and must be clearly white,
/// black, red or green. Pure: it needs no desktop.
/// </summary>
public static class GlFrameDecoder
{
    /// <summary>A channel at least this high counts as on.</summary>
    public const int High = 160;

    /// <summary>A channel at most this high counts as off.</summary>
    public const int Low = 95;

    /// <summary>The points sampled per cell along each axis.</summary>
    private const int SamplesPerAxis = 5;

    /// <summary>The middle part of a cell that is sampled, away from its edges where scaling and JPEG blend colors.</summary>
    private const double SampledShare = 0.4;

    /// <summary>Reads the frame number from <paramref name="image"/>, a capture of the GL window's client area.</summary>
    public static GlFrameReading Decode(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Decode(image, new Rectangle(0, 0, image.Width, image.Height));
    }

    /// <summary>Reads the frame number from the part <paramref name="clientArea"/> of <paramref name="image"/> that shows the client area.</summary>
    public static GlFrameReading Decode(Bitmap image, Rectangle clientArea)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (clientArea.Width < GlFrameCounterLayout.CellCount || clientArea.Height < 4)
        {
            return new GlFrameReading(null, $"not found: a {clientArea.Width}x{clientArea.Height} area is too small for the row");
        }

        var cells = GlFrameCounterLayout.Cells(clientArea.Width, clientArea.Height);
        var kinds = new char[cells.Count];
        var colors = new (int R, int G, int B)[cells.Count];
        for (var i = 0; i < cells.Count; i++)
        {
            colors[i] = Sample(image, clientArea, cells[i]);
            kinds[i] = Classify(colors[i]);
        }

        var seen = new string(kinds);
        string? problem = null;
        if (kinds[0] != 'R')
        {
            problem = $"the start cell is {Hex(colors[0])}, not red";
        }
        else if (kinds[^1] != 'G')
        {
            problem = $"the end cell is {Hex(colors[^1])}, not green";
        }
        else if (Array.FindIndex(kinds, 1, cells.Count - 2, k => k is not ('0' or '1')) is var bad and >= 0)
        {
            problem = $"cell {bad} is {Hex(colors[bad])}, neither white nor black";
        }
        else if (kinds.Count(k => k == '1') % 2 != 0)
        {
            problem = "the parity bit does not match";
        }

        if (problem != null)
        {
            return new GlFrameReading(null, $"not found: {problem} (cells {seen})");
        }

        long frame = 0;
        for (var i = 1; i <= GlFrameCounterLayout.DataBits; i++)
        {
            frame = (frame << 1) | (kinds[i] == '1' ? 1L : 0L);
        }

        return new GlFrameReading(frame, $"frame {frame} (cells {seen})");
    }

    /// <summary>The average color of a grid of points in the middle of <paramref name="cell"/>.</summary>
    private static (int R, int G, int B) Sample(Bitmap image, Rectangle area, Box cell)
    {
        var margin = (1 - SampledShare) / 2;
        double left = area.X + cell.X + cell.Width * margin, right = area.X + cell.X + cell.Width * (1 - margin);
        double top = area.Y + cell.Y + cell.Height * margin, bottom = area.Y + cell.Y + cell.Height * (1 - margin);
        int r = 0, g = 0, b = 0, count = 0;
        for (var row = 0; row < SamplesPerAxis; row++)
        {
            var y = Math.Clamp((int)(top + (bottom - top) * row / (SamplesPerAxis - 1)), 0, image.Height - 1);
            for (var column = 0; column < SamplesPerAxis; column++)
            {
                var x = Math.Clamp((int)(left + (right - left) * column / (SamplesPerAxis - 1)), 0, image.Width - 1);
                var pixel = image.GetPixel(x, y);
                r += pixel.R;
                g += pixel.G;
                b += pixel.B;
                count++;
            }
        }

        return (r / count, g / count, b / count);
    }

    /// <summary>'1' white, '0' black, 'R' red, 'G' green, '?' anything else.</summary>
    private static char Classify((int R, int G, int B) c) => c switch
    {
        _ when c.R >= High && c.G >= High && c.B >= High => '1',
        _ when c.R <= Low && c.G <= Low && c.B <= Low => '0',
        _ when c.R >= High && c.G <= Low && c.B <= Low => 'R',
        _ when c.G >= High && c.R <= Low && c.B <= Low => 'G',
        _ => '?'
    };

    private static string Hex((int R, int G, int B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
