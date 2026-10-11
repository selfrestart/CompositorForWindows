using System.Runtime.InteropServices;

namespace Compositor.Pixels;

/// <summary>
/// One Camera Raw stage: up to eight measured color tables (<c>size</c>³ sRGB colors, red slowest,
/// 3 bytes each) blended by weight, plus the weight of each.
/// </summary>
/// <remarks>
/// Byte-for-byte layout of the C <c>CameraRawStage</c> in <c>AdjustPixels.h</c>:
/// <code>
/// typedef struct {
///     const uint8_t *table[8];   // 8 × 8 bytes = 64, offset 0
///     float weight[8];           // 8 × 4 bytes = 32, offset 64
/// } CameraRawStage;              // sizeof = 96, align 8
/// </code>
/// Pointers are 8 bytes only on 64-bit; this struct is therefore x64/arm64-only by nature.
/// Use <see cref="TableCount"/> to fill the array without hard-coding 8.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct CameraRawStage
{
    /// <summary>Number of tables a single stage can carry (the C array bound).</summary>
    public const int TableCount = 8;

    /// <summary>The measured color tables. A null entry means no change.</summary>
    public fixed ulong Table[TableCount];

    /// <summary>The blend weight of each corresponding table entry.</summary>
    public fixed float Weight[TableCount];

    /// <summary>Sets the table pointer at <paramref name="index"/>.</summary>
    public void SetTable(int index, IntPtr table)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, TableCount);
        fixed (ulong* p = Table) p[index] = unchecked((ulong)table.ToInt64());
    }

    /// <summary>Sets the weight at <paramref name="index"/>.</summary>
    public void SetWeight(int index, float weight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, TableCount);
        fixed (float* p = Weight) p[index] = weight;
    }
}

/// <summary>
/// Filter › Dither settings. Layout mirrors the C <c>DitherParams</c> in <c>DitherPixels.h</c> field
/// for field, including the natural padding the C compiler inserts.
/// </summary>
/// <remarks>
/// The C struct mixes <c>int</c> (4), <c>float</c> (4), <c>uint8_t[3]</c> (3 + 1 pad) and pointers
/// (8 on x64). Sequential layout with the default Pack reproduces it exactly; do not add
/// <c>Pack = 1</c>, which would break the padding after <c>dark</c>/<c>light</c>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DitherParams
{
    public int Style;
    /// <summary>Tones per channel for diffusion and ordered styles, 2–8. Two is pure 1-bit.</summary>
    public int Levels;
    /// <summary>How much of each pixel's error diffusion passes on, 0–1.</summary>
    public float Diffusion;
    /// <summary>−1…1: darker (more ink) or lighter, and flatter or punchier, before dithering.</summary>
    public float Density;
    public float Contrast;
    /// <summary>Halftone and glyph cells, in pixels.</summary>
    public int Cell;
    /// <summary>Halftone screen angle, in radians.</summary>
    public float Angle;
    /// <summary>Halftone dots, patterns and glyphs mark the light tones on the dark color instead.</summary>
    public int LightOnDark;
    /// <summary>0: the result is made of <see cref="Dark"/> and <see cref="Light"/>. 1: it keeps the image's own colors.</summary>
    public int OriginalColors;
    public fixed byte Dark[3];
    public fixed byte Light[3];
    /// <summary>Glyphs: coverage maps are <c>glyphWidth × glyphHeight</c> bytes.</summary>
    public int GlyphWidth;
    public int GlyphHeight;
    public IntPtr Glyphs;
    public IntPtr GlyphCoverage;
    public int GlyphCount;

    /// <summary>Sets the three straight-sRGB bytes of <see cref="Dark"/>.</summary>
    public void SetDark(byte r, byte g, byte b) { fixed (byte* p = Dark) { p[0] = r; p[1] = g; p[2] = b; } }

    /// <summary>Sets the three straight-sRGB bytes of <see cref="Light"/>.</summary>
    public void SetLight(byte r, byte g, byte b) { fixed (byte* p = Light) { p[0] = r; p[1] = g; p[2] = b; } }
}

/// <summary>
/// Filter › Scanlines settings. Layout mirrors the C <c>ScanlinesParams</c> in <c>DitherPixels.h</c>.
/// </summary>
/// <remarks>
/// Trailing size is padded out to the struct's 4-byte alignment: 1 <c>int</c> + 10 <c>float</c> +
/// 2 <c>int</c> + <c>uint8_t[3]</c> + <c>uint8_t[3]</c> = 38 bytes of content, <c>sizeof</c> = 40.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ScanlinesParams
{
    /// <summary>Pixels from one line to the next.</summary>
    public int Spacing;
    /// <summary>How much of the gap a full-bright line fills, 0–1.</summary>
    public float Thickness;
    /// <summary>0–1: tones darker than this break the lines into round dots.</summary>
    public float Dots;
    /// <summary>Pixels a line wavers sideways, in a wave down the screen.</summary>
    public float Wobble;
    /// <summary>Pixels a line rises where the picture under it is bright.</summary>
    public float Displace;
    /// <summary>0–1: tones darker than this draw no line at all.</summary>
    public float Threshold;
    /// <summary>Pixels the red and blue are moved apart, for colored fringes.</summary>
    public float Split;
    /// <summary>−1…1: darker or lighter, and flatter or punchier, before the lines are drawn.</summary>
    public float Density;
    public float Contrast;
    /// <summary>0–1: how bright the lines are where the picture is black.</summary>
    public float BlackLevel;
    /// <summary>0–1: how far the brightness is smoothed before it displaces the lines.</summary>
    public float Smoothness;
    /// <summary>0: lines of <see cref="Light"/> on <see cref="Dark"/>. 1: lines in the picture's colors.</summary>
    public int OriginalColors;
    public fixed byte Dark[3];
    public fixed byte Light[3];

    /// <summary>Sets the three straight-sRGB bytes of <see cref="Dark"/>.</summary>
    public void SetDark(byte r, byte g, byte b) { fixed (byte* p = Dark) { p[0] = r; p[1] = g; p[2] = b; } }

    /// <summary>Sets the three straight-sRGB bytes of <see cref="Light"/>.</summary>
    public void SetLight(byte r, byte g, byte b) { fixed (byte* p = Light) { p[0] = r; p[1] = g; p[2] = b; } }
}

/// <summary>
/// The dither styles, in the order the Filter panel lists them (<c>DitherPixels.h</c>).
/// </summary>
public enum DitherStyle
{
    Atkinson = 0,
    FloydSteinberg = 1,
    Bayer2 = 2,
    Bayer4 = 3,
    Bayer8 = 4,
    Dots = 5,
    Lines = 6,
    Diamonds = 7,
    Patterns = 8,
    Glyphs = 9,
}
