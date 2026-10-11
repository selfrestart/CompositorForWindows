using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Compositor.Pixels;

/// <summary>
/// Locates and loads the native C pixel kernel, and reports whether it is usable.
/// </summary>
/// <remarks>
/// <para>
/// The library is loaded <em>lazily</em> and <em>never fatally</em>: on a machine without a native
/// build (a clean checkout, or ubuntu/Windows CI before <c>native/compositor_pixels/build.ps1</c>
/// has run) <see cref="IsAvailable"/> reports <c>false</c> and the parity tests skip. Callers must
/// gate every native call on <see cref="IsAvailable"/> — the C entry points themselves are not
/// tolerant of a missing library, since the P/Invoke stub is what throws
/// <see cref="DllNotFoundException"/>.
/// </para>
/// <para>
/// This type is deliberately free of any Windows API: it is <see cref="NativeLibrary"/>, which is
/// cross-platform, so <c>Compositor.Pixels</c> still builds and tests on ubuntu-latest.
/// </para>
/// </remarks>
public static class NativeKernel
{
    /// <summary>The library name every <c>[LibraryImport]</c> in this assembly is declared against.</summary>
    public const string LibraryName = "compositor_pixels";

    private static readonly object Gate = new();
    private static bool _probed;
    private static IntPtr _handle;
    private static string? _failure;

    /// <summary>
    /// True when the native kernel is loaded and callable. Probed once, on first access.
    /// </summary>
    public static bool IsAvailable
    {
        get { EnsureProbed(); return _handle != IntPtr.Zero; }
    }

    /// <summary>
    /// Why the native kernel is unavailable, or <c>null</c> when it is available.
    /// Useful as the reason string for a skipped test.
    /// </summary>
    public static string? UnavailableReason
    {
        get { EnsureProbed(); return _failure; }
    }

    /// <summary>The resolved path of the loaded library, or <c>null</c> when unavailable.</summary>
    public static string? LoadedPath { get; private set; }

    /// <summary>
    /// Environment variable holding an explicit path to the native library. Checked first, ahead of
    /// every search directory, so a CI job or a developer can point the tests at a specific build
    /// (for example an artifact from a different toolchain) without copying files around.
    /// </summary>
    public const string PathOverrideVariable = "COMPOSITOR_PIXELS_LIBRARY";

    /// <summary>
    /// The directories searched for the native library, in order. The app base directory is always
    /// first, so the copy MSBuild places next to the test binary wins; the source-tree fallbacks
    /// make the tests runnable straight from a checkout after only the native build has run.
    /// </summary>
    private static IEnumerable<string> SearchDirectories()
    {
        var overridden = Environment.GetEnvironmentVariable(PathOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(overridden));
            if (!string.IsNullOrEmpty(directory)) yield return directory;
        }

        yield return AppContext.BaseDirectory;
        // tests/Compositor.Tests/bin/<config>/net8.0 -> repository root
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        // working directory, for `dotnet test` from the repository root
        yield return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// The file names the library may have, in the order tried. The Windows DLL and the ELF/Mach-O
    /// shared objects are all listed because the same test suite runs on all three platforms.
    /// </summary>
    private static IEnumerable<string> FileNames()
    {
        var overridden = Environment.GetEnvironmentVariable(PathOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            yield return Path.GetFileName(Path.GetFullPath(overridden));

        yield return LibraryName + ".dll";           // Windows
        yield return "lib" + LibraryName + ".so";    // Linux
        yield return "lib" + LibraryName + ".dylib"; // macOS
        yield return LibraryName;                    // let the OS default mapping apply
    }

    private static void EnsureProbed()
    {
        if (_probed) return;
        lock (Gate)
        {
            if (_probed) return;
            try
            {
                // A resolver is what makes this work regardless of launch directory: NativeLibrary
                // does not consult AppContext.BaseDirectory for an assembly-relative name.
                NativeLibrary.SetDllImportResolver(
                    typeof(NativeKernel).Assembly,
                    static (name, _, _) => name == LibraryName ? _handle : IntPtr.Zero);

                var tried = new List<string>();
                foreach (var dir in SearchDirectories())
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in FileNames())
                    {
                        var candidate = Path.Combine(dir, file);
                        tried.Add(candidate);
                        if (!File.Exists(candidate)) continue;
                        if (NativeLibrary.TryLoad(candidate, out var handle))
                        {
                            _handle = handle;
                            LoadedPath = candidate;
                            _probed = true;
                            return;
                        }
                    }
                }

                // Last resort: let the OS resolve a bare name (e.g. from PATH or a system location).
                foreach (var file in FileNames())
                {
                    tried.Add(file + " (OS default search path)");
                    if (NativeLibrary.TryLoad(file, typeof(NativeKernel).Assembly, null, out var handle))
                    {
                        _handle = handle;
                        LoadedPath = file;
                        _probed = true;
                        return;
                    }
                }

                _failure = "native library '" + LibraryName + "' was not found; tried: " + string.Join("; ", tried);
            }
            catch (Exception ex)
            {
                // Never let a probe failure escape: the contract is graceful degradation.
                _failure = "probing for native library '" + LibraryName + "' threw " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                _probed = true;
            }
        }
    }

    /// <summary>
    /// Throws with an actionable message when the native kernel is missing. Intended for callers
    /// that have already decided a missing library should be an error rather than a skip.
    /// </summary>
    public static void ThrowIfUnavailable()
    {
        if (!IsAvailable)
            throw new DllNotFoundException(UnavailableReason);
    }
}
