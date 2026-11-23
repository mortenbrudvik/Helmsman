namespace Helmsman.FixtureApp;

/// <summary>
/// What the GL frame counter window (<see cref="FixtureOptions.OpenGl"/>) draws, as fractions of its client area, so a capture
/// at any scale can be read back: a dark background, one row of <see cref="CellCount"/> solid cells near the top (a red start
/// marker, the frame number in <see cref="DataBits"/> bits, most significant first, white for 1 and black for 0, an even
/// parity bit, and a green end marker), and an amber close block with a dark cross in the bottom-right corner. The fixture
/// draws it and the E2E tests decode it; the E2E project compiles this file too (a linked source file), so both sides agree.
/// </summary>
/// <remarks>
/// The row spans 6 % to 22 % of the client height. That does not keep it clear of the fixture's main window: centred on the
/// primary monitor at 760x540 device-independent pixels, the main window reaches into that band on a small or highly scaled
/// monitor (at 125 % on 1920x1080 its top is at about 16 % to 19 % of the height). It does not matter for a window capture
/// whose method shows only the GL window's own pixels; it matters when the pixels are read from the screen (a screen or
/// region capture, or a window capture that falls back to copying the screen) while the main window is in front of a
/// full-monitor counter: the main window can then hide part of the row, and the capture does not decode. Coordinates are
/// client pixels with (0, 0) at the top left.
/// </remarks>
public static class GlFrameCounterLayout
{
    /// <summary>How many bits of the frame number the row shows: enough for any lifetime the fixture accepts, at any rate.</summary>
    public const int DataBits = 20;

    /// <summary>The cells of the row: the start marker, the data bits, the parity bit and the end marker.</summary>
    public const int CellCount = DataBits + 3;

    /// <summary>The part of the frame number the row shows (it wraps above it).</summary>
    public const long FrameMask = (1L << DataBits) - 1;

    /// <summary>The row's top and height, as fractions of the client height.</summary>
    public const double RowTop = 0.06;

    /// <summary>The row's height, as a fraction of the client height.</summary>
    public const double RowHeight = 0.16;

    /// <summary>The margin left and right of the row, as a fraction of the client width.</summary>
    public const double RowMargin = 0.04;

    /// <summary>The share of each cell's pitch that the cell fills; the rest is background between cells.</summary>
    public const double CellFill = 0.8;

    public static readonly Rgb Background = new(0x1A, 0x1F, 0x59);
    public static readonly Rgb One = new(0xFF, 0xFF, 0xFF);
    public static readonly Rgb Zero = new(0x00, 0x00, 0x00);
    public static readonly Rgb StartMarker = new(0xFF, 0x00, 0x00);
    public static readonly Rgb EndMarker = new(0x00, 0xFF, 0x00);
    public static readonly Rgb CloseBlockColor = new(0xFF, 0xB0, 0x00);
    public static readonly Rgb CloseMarkColor = new(0x20, 0x20, 0x20);

    /// <summary>The cells of the row in a client area of <paramref name="width"/> by <paramref name="height"/>, left to right.</summary>
    public static IReadOnlyList<Box> Cells(int width, int height)
    {
        var left = (int)Math.Round(width * RowMargin);
        var pitch = (width - 2.0 * left) / CellCount;
        var top = (int)Math.Round(height * RowTop);
        var cellHeight = Math.Max(1, (int)Math.Round(height * RowHeight));
        var cellWidth = Math.Max(1, (int)Math.Round(pitch * CellFill));
        var inset = (pitch - cellWidth) / 2;
        return Enumerable.Range(0, CellCount)
            .Select(i => new Box(left + (int)Math.Round(i * pitch + inset), top, cellWidth, cellHeight))
            .ToList();
    }

    /// <summary>The color of each cell for frame <paramref name="frame"/> (taken modulo 2^<see cref="DataBits"/>).</summary>
    public static IReadOnlyList<Rgb> CellColors(long frame)
    {
        var value = frame & FrameMask;
        var colors = new List<Rgb>(CellCount) { StartMarker };
        var ones = 0;
        for (var bit = DataBits - 1; bit >= 0; bit--)
        {
            var set = ((value >> bit) & 1) == 1;
            ones += set ? 1 : 0;
            colors.Add(set ? One : Zero);
        }

        // Even parity: the data bits and the parity bit hold an even number of ones
        colors.Add(ones % 2 == 1 ? One : Zero);
        colors.Add(EndMarker);
        return colors;
    }

    /// <summary>The close block: a click on it closes the window.</summary>
    public static Box CloseBlock(int width, int height)
    {
        var shorter = Math.Min(width, height);
        var size = Math.Max(8, (int)Math.Round(shorter * 0.14));
        var margin = (int)Math.Round(shorter * 0.04);
        return new Box(width - margin - size, height - margin - size, size, size);
    }

    /// <summary>A point where a click changes nothing: below the row, far from the close block.</summary>
    public static (int X, int Y) NeutralPoint(int width, int height) =>
        ((int)Math.Round(width * 0.25), (int)Math.Round(height * 0.75));

    /// <summary>A color, 8 bits per channel.</summary>
    public readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>A rectangle in client pixels, (0, 0) at the top left.</summary>
    public readonly record struct Box(int X, int Y, int Width, int Height)
    {
        public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;

        public bool IntersectsWith(Box other) =>
            X < other.X + other.Width && other.X < X + Width && Y < other.Y + other.Height && other.Y < Y + Height;
    }
}
