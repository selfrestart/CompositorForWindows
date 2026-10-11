using System.Runtime.InteropServices;

namespace Compositor.Pixels;

/// <summary>
/// Releases memory that a native kernel allocated with <c>malloc</c>.
/// </summary>
/// <remarks>
/// <para>
/// Named <c>KernelHeap</c> rather than <c>NativeMemory</c> because the latter collides with
/// <see cref="System.Runtime.InteropServices.NativeMemory"/>, which consumers almost always have in
/// scope alongside this library.
/// </para>
/// <para>
/// This exists because <see cref="Marshal.FreeHGlobal"/> is the <em>wrong</em> deallocator for these
/// buffers, and using it corrupts the heap. <c>wand_trace</c> is the only entry point that hands
/// ownership of <c>malloc</c>'d memory back to the caller (<c>WandPixels.h:24</c>), and on Windows each
/// module can carry a separate C runtime heap: freeing a pointer on the .NET runtime's heap when it
/// came from the DLL's CRT aborts or corrupts the process. The symptom is easy to misread, because the
/// crash lands after the trace has already returned successfully.
/// </para>
/// <para>
/// So the pointer must go back to a <c>free</c> that shares the kernel's allocator. This type tries,
/// in order: the <c>free</c> exported by the kernel module itself, then the C runtime the process
/// already has loaded. If neither resolves it fails closed with an <see cref="InvalidOperationException"/>
/// rather than calling the wrong one.
/// </para>
/// </remarks>
public static class KernelHeap
{
    private static readonly object Gate = new();
    private static IntPtr _freeAddress;

    /// <summary>
    /// Frees memory returned by a native kernel (<c>wand_trace</c>'s <c>points</c> and <c>loops</c>).
    /// A null pointer is ignored.
    /// </summary>
    public static unsafe void Free(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return;
        var free = ResolveFree();
        ((delegate* unmanaged[Cdecl]<IntPtr, void>)free)(pointer);
    }

    /// <summary>Copies a native <c>int32</c> array of <paramref name="count"/> elements into managed memory.</summary>
    public static int[] ReadInt32(IntPtr pointer, int count)
    {
        if (count <= 0) return Array.Empty<int>();
        var result = new int[count];
        Marshal.Copy(pointer, result, 0, count);
        return result;
    }

    private static IntPtr ResolveFree()
    {
        if (_freeAddress != IntPtr.Zero) return _freeAddress;
        lock (Gate)
        {
            if (_freeAddress != IntPtr.Zero) return _freeAddress;

            var tried = new List<string>();

            // 1) The kernel module's own free. gcc's Windows CRT resolves malloc/free to a shared
            //    per-process heap, so this is the correct one whenever it is reachable.
            if (NativeKernel.IsAvailable && NativeKernel.LoadedPath is { } path)
            {
                if (NativeLibrary.TryLoad(path, out var module))
                {
                    foreach (var name in new[] { "free", "_free" })
                    {
                        tried.Add($"kernel module {name}");
                        if (NativeLibrary.TryGetExport(module, name, out var address))
                            return _freeAddress = address;
                    }
                }
            }

            // 2) The process's C runtime, which shares the same heap on Windows.
            foreach (var runtime in new[] { "msvcrt.dll", "ucrtbase.dll", "libc.so.6", "libc.dylib" })
            {
                foreach (var name in new[] { "free", "_free" })
                {
                    tried.Add($"{runtime}!{name}");
                    if (NativeLibrary.TryLoad(runtime, out var handle)
                        && NativeLibrary.TryGetExport(handle, name, out var address))
                        return _freeAddress = address;
                }
            }

            throw new InvalidOperationException(
                "could not resolve a 'free' that shares the native kernel's allocator; tried " +
                string.Join(", ", tried) + ". Do not substitute Marshal.FreeHGlobal: it uses a " +
                "different heap and will corrupt the process.");
        }
    }
}
