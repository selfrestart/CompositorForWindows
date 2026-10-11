// KernelProbe -- prove the Compositor C pixel kernel runs on Windows and is callable
// from C# through P/Invoke, with numerically verified output.
//
// Ported from spikes/c-kernel/KernelProbe/Program.cs, which validated the spike DLL
// `compositor_kernel.dll`. This copy is identical except for:
//   * the library name (`compositor_pixels`, the name src/Compositor.Pixels uses), and
//   * the DLL locator, which walks up from the test binary to
//     native/compositor_pixels/build/bin/compositor_pixels.dll (or takes
//     COMPOSITOR_PIXELS_DLL, used to point the same tests at the -OpenMp build).
// All ten checks, their reference implementations and their thresholds are unchanged,
// so "10 passed, 0 failed" here means the same thing it meant for the spike.
//
// Every check below either (a) compares against an INDEPENDENT C# reference
// implementation of the same math, written from the C source, or (b) checks an
// invariant that must hold for any correct implementation. Nothing here is a
// smoke test that merely asserts "did not crash" -- except where explicitly
// labelled as such.
//
// Coverage deliberately includes the kernels whose band loops had to be rewritten
// from Clang blocks to plain C (dither_quantize16, dither_glow from DitherPixels.c;
// camera_raw_statistics from AdjustPixels.c), because that rewrite is the one
// non-mechanical risk in this port.

using System.Runtime.InteropServices;

namespace KernelProbe;

internal static class Native
{
    public const string Lib = "compositor_pixels";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lens_distort(byte[] source, byte[] destination,
        nuint width, nuint height, nuint stride, double k);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void levels_apply(byte[] pixels, nuint count, float[] tables);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void camera_raw_statistics(byte[] rgba, nuint width, nuint height, nuint stride, double[] @out);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dither_quantize16(ushort[] wide, byte[] rgba, nuint width, nuint height, nuint stride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dither_glow(byte[] rgba, byte[] glow, nuint width, nuint height, nuint stride, float amount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void brush_alpha_bounds(byte[] bytes, nuint width, nuint height, nuint stride, nuint[] bounds);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void layer_extract_alpha(byte[] rgba, nuint rgbaStride, byte[] gray, nuint grayStride, nuint width, nuint height);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void layer_restore_alpha(byte[] rgba, nuint stride, byte[] alpha, nuint alphaStride, nuint width, nuint height);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int content_fill(byte[] pixels, nuint stride, byte[] mask, nuint ms, int w, int h);
}

internal static class Program
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}"); }
        Console.WriteLine($"         {detail}");
    }

    // ---------------------------------------------------------------- helpers
    private static byte[] Gradient(int w, int h)
    {
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                b[i + 0] = (byte)(x * 255 / Math.Max(1, w - 1));
                b[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                b[i + 2] = (byte)((x + y) & 0xFF);
                b[i + 3] = (byte)(128 + ((x * 7 + y * 13) % 128));
            }
        return b;
    }

    // ---- C's lroundf for the positive range: round half away from zero.
    private static long LroundF(float x) => (long)MathF.Floor(x + 0.5f);

    // ---- Reimplemented from DitherPixels.c:314-318 (static inline hash_noise).
    private static float HashNoise(uint x, uint y, uint draw)
    {
        unchecked
        {
            uint h = x * 0x9E3779B1u ^ y * 0x85EBCA77u ^ draw * 0xC2B2AE3Du;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (float)(h >> 8) / (float)(1u << 24);
        }
    }

    // ---- Reimplemented from AdjustPixels.c:214-226.
    private static double SrgbDecode(double e) => e <= 0.04045 ? e / 12.92 : Math.Pow((e + 0.055) / 1.055, 2.4);
    private static double SrgbEncode(double l)
    {
        if (l <= 0) return 0;
        if (l >= 1) return 1;
        return l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1.0 / 2.4) - 0.055;
    }
    private static double Rec709(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    // ================================================================ the checks

    private static void TestLensDistort()
    {
        const int w = 64, h = 48;
        var src = Gradient(w, h);
        var dst = new byte[src.Length];

        // k = 0 is documented as "copies the source exactly".
        Native.lens_distort(src, dst, w, h, (nuint)(w * 4), 0.0);
        int diff = 0;
        for (int i = 0; i < src.Length; i++) if (src[i] != dst[i]) diff++;
        Check("lens_distort k=0 is an exact identity", diff == 0,
              $"64x48 RGBA, {src.Length} bytes compared, {diff} differing bytes (expected 0)");

        // k > 0 must actually resample, keep alpha sane, and leave the exact
        // center pixel alone (its offset from the center is zero).
        var dst2 = new byte[src.Length];
        Native.lens_distort(src, dst2, w, h, (nuint)(w * 4), 0.35);
        int moved = 0, alphaOutOfRange = 0;
        for (int i = 0; i < src.Length; i += 4)
        {
            if (src[i] != dst2[i] || src[i + 1] != dst2[i + 1] || src[i + 2] != dst2[i + 2]) moved++;
            if (dst2[i + 3] > 255) alphaOutOfRange++;
        }
        int ci = ((h / 2) * w + (w / 2)) * 4;
        bool centreHeld = src[ci] == dst2[ci] && src[ci + 1] == dst2[ci + 1]
                          && src[ci + 2] == dst2[ci + 2] && src[ci + 3] == dst2[ci + 3];
        Check("lens_distort k=0.35 resamples and pins the centre pixel",
              moved > src.Length / 4 / 2 && alphaOutOfRange == 0 && centreHeld,
              $"moved {moved}/{src.Length / 4} px, alpha-out-of-range {alphaOutOfRange}, centre unchanged={centreHeld}");
    }

    private static void TestLevelsApply()
    {
        const int w = 32, h = 32;
        const int n = w * h;
        var px = Gradient(w, h);
        var orig = (byte[])px.Clone();

        // Linear ramp tables, one per channel: table[c*256+v] = v * factor.
        var tables = new float[3 * 256];
        float[] factor = { 1.0f, 0.5f, 0.25f };
        for (int c = 0; c < 3; c++)
            for (int v = 0; v < 256; v++) tables[c * 256 + v] = v * factor[c];

        Native.levels_apply(px, n, tables);

        // Independent reference, transcribed from LevelsPixels.c:3-16.
        int mismatch = 0; string firstBad = "";
        for (int i = 0; i < n; i++)
        {
            float alpha = orig[i * 4 + 3];
            if (alpha == 0) continue;
            for (int c = 0; c < 3; c++)
            {
                float x = MathF.Min(255f, orig[i * 4 + c] * 255.0f / alpha);
                int lo = (int)x, hi = lo < 255 ? lo + 1 : 255;
                float result = tables[c * 256 + lo] + (tables[c * 256 + hi] - tables[c * 256 + lo]) * (x - lo);
                byte expect = (byte)MathF.Min(alpha, MathF.Max(0, MathF.Round(result * alpha, MidpointRounding.AwayFromZero)));
                if (px[i * 4 + c] != expect)
                {
                    mismatch++;
                    if (firstBad.Length == 0)
                        firstBad = $"px{i} ch{c}: got {px[i * 4 + c]} want {expect} (a={alpha})";
                }
            }
        }
        Check("levels_apply matches an independent C# reference exactly", mismatch == 0,
              $"{n * 3} channel writes, {mismatch} mismatched. {firstBad}");
    }

    private static void TestDitherQuantize16()
    {
        // This kernel runs its row loop through the rewritten band helper, so it
        // is the sharpest test that the block->standard-C port is faithful.
        const int w = 70, h = 45;
        var wide = new ushort[w * h * 4];
        var rnd = new Random(1234);
        for (int i = 0; i < wide.Length; i++) wide[i] = (ushort)rnd.Next(0, 65536);

        var got = new byte[w * h * 4];
        Native.dither_quantize16(wide, got, w, h, (nuint)(w * 4));

        // Independent reference, transcribed from DitherPixels.c:320-338.
        var want = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                long alpha = LroundF(wide[i + 3] * (255.0f / 65535.0f));
                for (uint c = 0; c < 3; c++)
                {
                    float value = wide[i + c] * (255.0f / 65535.0f)
                                  + HashNoise((uint)x, (uint)y, c)
                                  + HashNoise((uint)x, (uint)y, c + 3) - 1.0f;
                    long rounded = LroundF(value);
                    want[i + c] = (byte)(rounded < 0 ? 0 : rounded > alpha ? alpha : rounded);
                }
                want[i + 3] = (byte)alpha;
            }

        int mismatch = 0; string firstBad = "";
        for (int i = 0; i < got.Length; i++)
            if (got[i] != want[i])
            {
                mismatch++;
                if (firstBad.Length == 0)
                    firstBad = $"byte{i} (px{i / 4} c{i % 4}): got {got[i]} want {want[i]}";
            }
        Check("dither_quantize16 (ported band loop) matches reference byte-for-byte",
              mismatch == 0, $"{got.Length} bytes compared, {mismatch} mismatched. {firstBad}");
    }

    private static void TestCameraRawStatistics()
    {
        // Runs 32 bands -> the rewritten dispatch_apply path. An independent
        // single-loop reference proves the band partition covers each row once.
        const int w = 61, h = 90;               // h intentionally not a multiple of 32
        var px = Gradient(w, h);
        var got = new double[3];
        Native.camera_raw_statistics(px, w, h, (nuint)(w * 4), got);

        var decoded = new double[256];
        for (int i = 0; i < 256; i++) decoded[i] = SrgbDecode(i / 255.0);
        double brightest = 0, luminance = 0, logBrightest = 0, weight = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                double alpha = px[i + 3];
                if (alpha == 0) continue;
                double r, g, b;
                if (px[i + 3] == 255)
                {
                    r = decoded[px[i]]; g = decoded[px[i + 1]]; b = decoded[px[i + 2]];
                }
                else
                {
                    r = SrgbDecode(Math.Min(1.0, px[i] / alpha));
                    g = SrgbDecode(Math.Min(1.0, px[i + 1] / alpha));
                    b = SrgbDecode(Math.Min(1.0, px[i + 2] / alpha));
                }
                double top = Math.Max(r, Math.Max(g, b)), wt = alpha / 255.0;
                brightest += wt * top;
                luminance += wt * Rec709(r, g, b);
                logBrightest += wt * Math.Log(top + 3e-4);
                weight += wt;
            }
        var want = new double[3];
        if (weight <= 0) { want[0] = want[1] = want[2] = 0.5; }
        else
        {
            want[0] = SrgbEncode(brightest / weight);
            want[1] = SrgbEncode(luminance / weight);
            want[2] = SrgbEncode(Math.Exp(logBrightest / weight) - 3e-4);
        }

        double maxDiff = 0;
        for (int i = 0; i < 3; i++) maxDiff = Math.Max(maxDiff, Math.Abs(got[i] - want[i]));
        Check("camera_raw_statistics (32-band GCD path) matches single-loop reference",
              maxDiff < 1e-9,
              $"h={h} across 32 bands; got [{got[0]:F12}, {got[1]:F12}, {got[2]:F12}]; max |diff| = {maxDiff:E3}");
    }

    private static void TestBrushAndAlpha()
    {
        const int w = 20, h = 12;
        var px = new byte[w * h * 4];
        // Mark an L-shaped opaque region: rows 3..7, columns 4..11.
        for (int y = 3; y <= 7; y++)
            for (int x = 4; x <= 11; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = 200; px[i + 1] = 100; px[i + 2] = 50; px[i + 3] = 255;
            }

        var bounds = new nuint[4];
        Native.brush_alpha_bounds(px, w, h, (nuint)(w * 4), bounds);
        bool ok = bounds[0] == 4 && bounds[1] == 3 && bounds[2] == 12 && bounds[3] == 8;
        Check("brush_alpha_bounds finds [left,top,right,bottom]", ok,
              $"got [{bounds[0]},{bounds[1]},{bounds[2]},{bounds[3]}], expected [4,3,12,8] (right/bottom are exclusive)");

        var gray = new byte[w * h];
        Native.layer_extract_alpha(px, (nuint)(w * 4), gray, (nuint)w, w, h);
        var wantGray = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                wantGray[y * w + x] = px[(y * w + x) * 4 + 3];
        int gm = 0;
        for (int i = 0; i < gray.Length; i++) if (gray[i] != wantGray[i]) gm++;
        Check("layer_extract_alpha copies the alpha plane", gm == 0, $"{w * h} bytes, {gm} mismatched");

        // Restore with half alpha: every colour must scale to ~50%.
        var half = new byte[w * h];
        for (int i = 0; i < half.Length; i++) half[i] = 128;
        var before = (byte[])px.Clone();
        Native.layer_restore_alpha(px, (nuint)(w * 4), half, (nuint)w, w, h);
        int rm = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    byte expect = (byte)((before[i + c] * 128u + 127) / 255);
                    if (px[i + c] != expect) rm++;
                }
                if (px[i + 3] != 128) rm++;
            }
        Check("layer_restore_alpha rescales by the alpha plane", rm == 0, $"{w * h * 4} bytes checked, {rm} mismatched");
    }

    private static void TestSmoke()
    {
        const int w = 32, h = 32;
        var px = Gradient(w, h);
        var glow = Gradient(w, h);

        // dither_glow also runs through the ported band loop; check determinism
        // and that it only ever brightens toward (never past) alpha.
        var a = (byte[])px.Clone();
        var b = (byte[])px.Clone();
        Native.dither_glow(a, glow, w, h, (nuint)(w * 4), 0.8f);
        Native.dither_glow(b, glow, w, h, (nuint)(w * 4), 0.8f);
        int nondet = 0, overAlpha = 0, changed = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) nondet++;
            for (int c = 0; c < 3; c++)
            {
                if (a[i + c] > a[i + 3]) overAlpha++;
                if (a[i + c] != px[i + c]) changed++;
            }
        }
        Check("dither_glow is deterministic and never exceeds alpha",
              nondet == 0 && overAlpha == 0 && changed > 0,
              $"nondeterministic px={nondet}, channel>alpha={overAlpha}, channels changed={changed}");

        var fill = (byte[])px.Clone();
        var mask = new byte[w * h];
        for (int y = 8; y < 20; y++)
            for (int x = 8; x < 20; x++) mask[y * w + x] = 255;
        int rc = Native.content_fill(fill, (nuint)(w * 4), mask, (nuint)w, w, h);
        Check("content_fill executes and reports a coherent result", rc >= 0,
              $"returned {rc} (smoke test: no independent reference written)");
    }

    private static int Main()
    {
        Console.WriteLine("KernelProbe -- Compositor C pixel kernel on Windows via P/Invoke");
        Console.WriteLine($"runtime : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"process : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine();

        // Resolve the native library from native/compositor_pixels/build/bin so the
        // probe works no matter which directory it is launched from. COMPOSITOR_PIXELS_DLL
        // overrides it, which is how the same tests are run against the -OpenMp build.
        var overridePath = Environment.GetEnvironmentVariable("COMPOSITOR_PIXELS_DLL");
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
        {
            if (name != Native.Lib) return IntPtr.Zero;
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(overridePath)) candidates.Add(overridePath);
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                candidates.Add(Path.Combine(dir.FullName, "compositor_pixels.dll"));
                candidates.Add(Path.Combine(dir.FullName, "build", "bin", "compositor_pixels.dll"));
                candidates.Add(Path.Combine(dir.FullName, "libcompositor_pixels.so"));
                candidates.Add(Path.Combine(dir.FullName, "build", "bin", "libcompositor_pixels.so"));
            }
            foreach (var candidate in candidates)
                if (File.Exists(candidate))
                {
                    Console.WriteLine($"loaded  : {candidate}");
                    return System.Runtime.InteropServices.NativeLibrary.Load(candidate);
                }
            return IntPtr.Zero;
        });

        Console.WriteLine();
        TestLensDistort();
        TestLevelsApply();
        TestDitherQuantize16();
        TestCameraRawStatistics();
        TestBrushAndAlpha();
        TestSmoke();

        Console.WriteLine();
        Console.WriteLine($"==== {_pass} passed, {_fail} failed ====");
        return _fail == 0 ? 0 : 1;
    }
}
