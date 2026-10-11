using System.Runtime.InteropServices;
using Compositor.Pixels;

namespace Compositor.Tests;

/// <summary>
/// A callback receiving buffers that are pinned for its duration. Declared explicitly because
/// <see cref="PinnedBuffers"/> is a ref struct and so cannot be a type argument of
/// <see cref="Action{T}"/>.
/// </summary>
internal delegate void PinnedCall(PinnedBuffers buffers);

/// <summary>
/// Keeps managed arrays pinned for the duration of a native call.
/// </summary>
/// <remarks>
/// The pointers produced by <see cref="Ptr{T}"/> are only valid while this scope is alive. This is
/// deliberately a real pin rather than <c>Unsafe.AsPointer</c> over an unpinned reference: the array
/// may sit on the heap, and a collection during the call would move it out from under the kernel.
/// </remarks>
internal readonly ref struct PinnedBuffers
{
    private readonly GCHandle[] _handles;

    private PinnedBuffers(GCHandle[] handles) => _handles = handles;

    /// <summary>Pins every given array. Null entries are carried through as null pointers.</summary>
    public static PinnedBuffers Pin(params Array?[] arrays)
    {
        var handles = new GCHandle[arrays.Length];
        try
        {
            for (int i = 0; i < arrays.Length; i++)
                handles[i] = arrays[i] is null ? default : GCHandle.Alloc(arrays[i], GCHandleType.Pinned);
        }
        catch
        {
            foreach (var h in handles) if (h.IsAllocated) h.Free();
            throw;
        }
        return new PinnedBuffers(handles);
    }

    /// <summary>Address of the pinned array's first element, or <see cref="IntPtr.Zero"/> for null/empty.</summary>
    public IntPtr Ptr<T>(int index) where T : unmanaged
    {
        var handle = _handles[index];
        return handle.IsAllocated ? handle.AddrOfPinnedObject() : IntPtr.Zero;
    }

    /// <summary>
    /// Pins the given arrays, runs <paramref name="call"/> with them pinned, then releases them. The
    /// pinned pointers must not escape <paramref name="call"/>.
    /// </summary>
    public static void Invoke(Array?[] arrays, PinnedCall call)
    {
        var pinned = Pin(arrays);
        try { call(pinned); }
        finally { pinned.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var handle in _handles) if (handle.IsAllocated) handle.Free();
    }
}

/// <summary>
/// Shared helpers for the native parity tests: fixture generation, pinned-pointer extraction and the
/// missing-library skip.
/// </summary>
internal static class NativeTest
{
    /// <summary>
    /// Skips the calling test when the native kernel is not built, instead of failing it. This is the
    /// whole graceful-degradation contract: a clean checkout with no C toolchain reports skips, and
    /// xUnit v3 reports that as a skipped count rather than as a silent pass.
    /// </summary>
    public static void RequireKernel()
    {
        if (!NativeKernel.IsAvailable)
            Assert.Skip("native kernel unavailable: " + NativeKernel.UnavailableReason);
    }

    /// <summary>
    /// Deterministic RGBA fixture: a different value in every byte position so that a stride or a
    /// channel-order mistake cannot hide. Alpha stays in 128..255 so no pixel is skipped by the
    /// kernels and no zero-alpha early-out masks a bug.
    /// </summary>
    public static byte[] Gradient(int w, int h)
    {
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                b[i + 0] = (byte)(x * 255 / Math.Max(1, w - 1));
                b[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                b[i + 2] = (byte)((x * 7 + y * 3) & 0xFF);
                b[i + 3] = (byte)(128 + ((x * 7 + y * 13) % 128));
            }
        return b;
    }

    /// <summary>
    /// Index of the first differing byte, or -1 when the spans agree. Never returns a position that
    /// cannot be used to index <paramref name="got"/>; callers clamp with <see cref="Safe"/>.
    /// </summary>
    public static int FirstDiff(ReadOnlySpan<byte> got, ReadOnlySpan<byte> want)
    {
        int n = Math.Min(got.Length, want.Length);
        for (int i = 0; i < n; i++) if (got[i] != want[i]) return i;
        return got.Length == want.Length ? -1 : n;
    }

    /// <summary>Counts differing bytes, including a length mismatch.</summary>
    public static int CountDiff(ReadOnlySpan<byte> got, ReadOnlySpan<byte> want)
    {
        int n = Math.Min(got.Length, want.Length);
        int d = Math.Abs(got.Length - want.Length);
        for (int i = 0; i < n; i++) if (got[i] != want[i]) d++;
        return d;
    }

    /// <summary>An index that is always safe to use in a failure message for <paramref name="array"/>.</summary>
    public static int Safe(int index, int length) => index < 0 || index >= length ? 0 : index;
}

/// <summary>
/// Independent reimplementations of the C math the parity tests compare against, transcribed from the
/// kernel sources. Deliberately written from the C, not from the managed bindings.
/// </summary>
internal static class CRef
{
    /// <summary>C's <c>lroundf</c> over the positive range: round half away from zero.</summary>
    public static long LroundF(float x) => (long)MathF.Floor(x + 0.5f);

    /// <summary>Reimplemented from DitherPixels.c, <c>static inline hash_noise</c>.</summary>
    public static float HashNoise(uint x, uint y, uint draw)
    {
        unchecked
        {
            uint h = x * 0x9E3779B1u ^ y * 0x85EBCA77u ^ draw * 0xC2B2AE3Du;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (float)(h >> 8) / (float)(1u << 24);
        }
    }

    /// <summary>Reimplemented from AdjustPixels.c (sRGB transfer functions).</summary>
    public static double SrgbDecode(double e) => e <= 0.04045 ? e / 12.92 : Math.Pow((e + 0.055) / 1.055, 2.4);

    public static double SrgbEncode(double l)
    {
        if (l <= 0) return 0;
        if (l >= 1) return 1;
        return l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1.0 / 2.4) - 0.055;
    }

    /// <summary>Rec. 709 luminance.</summary>
    public static double Rec709(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
