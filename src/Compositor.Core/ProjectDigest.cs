using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Compositor.Core;

/// <summary>
/// A fingerprint of what a project package contains: the manifest byte for byte, plus each asset's
/// name and size.
/// </summary>
/// <remarks>
/// Mirrors <c>ProjectDigest</c> (ProjectDigest.swift:11-32). The design notes there are worth
/// keeping, because they explain why the digest looks weaker than it is:
///
/// <list type="bullet">
/// <item>
/// <b>Assets are not read.</b> Every save and open takes a fresh digest, and hashing every image of
/// a large project would hold each save for seconds. Anything that edits a project rewrites its
/// manifest, and a PNG whose pixels change all but always changes size — so names and sizes catch
/// the rest from the file system alone.
/// </item>
/// <item>
/// <b>A package that was only touched has the same digest.</b> A sync client rewriting metadata, a
/// permission change, or the same bytes saved again must not register as an edit, or the app would
/// nag about an external change that did not happen.
/// </item>
/// <item>
/// <b>It is computed outside file coordination on purpose.</b> It runs after a change was already
/// seen and must never wait on a writer; a half-written package yields a digest that matches
/// nothing, which makes the caller simply wait for the next change.
/// </item>
/// </list>
///
/// The hash is SHA-256, and the size of each asset is fed in as a little-endian 64-bit integer
/// exactly as Swift's <c>withUnsafeBytes(of:)</c> does on Apple silicon. Matching that byte order
/// matters if a digest is ever compared across platforms.
/// </remarks>
public readonly record struct ProjectDigest
{
    /// <summary>Length of the digest in bytes.</summary>
    public const int Length = 32;

    private ProjectDigest(byte[] value) => Value = value;

    /// <summary>The 32-byte SHA-256 digest.</summary>
    public byte[] Value { get; }

    /// <summary>The digest as upper-case hex, for logs and messages.</summary>
    public string Hex => Convert.ToHexString(Value);

    /// <summary>
    /// Computes the digest of a package on disk: <c>manifest.json</c>'s bytes, then every regular
    /// file directly inside <c>images/</c>, in name order, as its name followed by its size.
    /// </summary>
    /// <exception cref="ProjectException">The manifest is missing.</exception>
    public static ProjectDigest ComputeFor(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        var manifestPath = Path.Combine(packagePath, ProjectStore.ManifestName);
        if (!File.Exists(manifestPath))
        {
            throw new ProjectException(
                ProjectErrorKind.Invalid,
                $"'{ProjectStore.ManifestName}' is missing, so the project has no digest.");
        }

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(File.ReadAllBytes(manifestPath));

        var imagesDirectory = Path.Combine(packagePath, ProjectStore.ImagesDirectory);
        if (Directory.Exists(imagesDirectory))
        {
            // Ordinal sort, matching Swift's `sorted()` on the names.
            var names = Directory.GetFiles(imagesDirectory)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal);

            // Allocated once, outside the loop: a stackalloc inside it accumulates and would
            // overflow the stack on a project with thousands of layers.
            Span<byte> size = stackalloc byte[sizeof(ulong)];
            foreach (var name in names)
            {
                var info = new FileInfo(Path.Combine(imagesDirectory, name));
                if (!info.Exists)
                {
                    continue;
                }

                hasher.AppendData(Encoding.UTF8.GetBytes(name));
                BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)info.Length);
                hasher.AppendData(size);
            }
        }

        return new ProjectDigest(hasher.GetHashAndReset());
    }

    /// <summary>
    /// Computes the digest of an in-memory snapshot, using the manifest's canonical JSON and the
    /// asset sizes.
    /// </summary>
    /// <remarks>
    /// This is the form the tests use, and the form an editor takes before writing: it must agree
    /// with <see cref="ComputeFor"/> for a package that was just written, because the app compares
    /// a digest taken before a save against one taken after to decide whether anything changed.
    /// </remarks>
    public static ProjectDigest ComputeFor(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(Encoding.UTF8.GetBytes(ProjectStore.SerializeManifest(snapshot.Manifest)));

        // Only assets the manifest actually references are part of the package, and they are
        // enumerated by file name, so build the same name-to-size view the on-disk walk sees.
        var entries = new List<(string Name, long Size)>();
        foreach (var layer in snapshot.Manifest.Layers)
        {
            if (layer.ImageFile is { Length: > 0 } imageFile && snapshot.Images.TryGetValue(layer.Id, out var image))
            {
                entries.Add((imageFile, image.LongLength));
            }

            if (layer.MaskFile is { Length: > 0 } maskFile && snapshot.Masks.TryGetValue(layer.Id, out var mask))
            {
                entries.Add((maskFile, mask.LongLength));
            }
        }

        // Allocated once, outside the loop, for the same reason as above.
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        foreach (var (name, size) in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            hasher.AppendData(Encoding.UTF8.GetBytes(name));
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)size);
            hasher.AppendData(buffer);
        }

        return new ProjectDigest(hasher.GetHashAndReset());
    }

    /// <summary>Whether two digests are the same 32 bytes.</summary>
    public bool Equals(ProjectDigest other) =>
        Value is not null && other.Value is not null && Value.AsSpan().SequenceEqual(other.Value);

    /// <inheritdoc />
    public override int GetHashCode() =>
        Value is null ? 0 : BinaryPrimitives.ReadInt32LittleEndian(Value);
}
