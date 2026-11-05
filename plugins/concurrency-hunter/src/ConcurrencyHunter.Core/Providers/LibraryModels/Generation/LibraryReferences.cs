using System.Globalization;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>One Roslyn metadata reference per reference file, shared by the library compilations that compile against the file. A
/// reference reads its whole file into native memory, and a library compilation references every assembly of its shared framework and
/// packages, so a reference of its own per compilation would hold one copy of the same metadata per library. A file is the same while its
/// full path, compared ignoring case, its length and its last write time are; a replaced file gets a new reference. The references are
/// held weakly: one lives while a compilation that uses it does, and its entry leaves the table at the next lookup that reads a file.</summary>
internal sealed class LibraryReferences
{
    private readonly Dictionary<string, WeakReference<MetadataReference>> _references = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The entries the table holds, whether their reference is still alive or not.</summary>
    internal int Count
    {
        get
        {
            lock (_references)
                return _references.Count;
        }
    }

    /// <summary>The reference to a file: the one something still holds while the file is unchanged, otherwise one read now. A read first
    /// drops the entries whose reference nothing holds any more.</summary>
    /// <param name="path">The reference file.</param>
    internal MetadataReference Of(string path)
    {
        var file = new FileInfo(path);
        var key = string.Join("\n", file.FullName, file.Length.ToString(CultureInfo.InvariantCulture),
                              file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        lock (_references)
        {
            if (_references.TryGetValue(key, out var held) && held.TryGetTarget(out var reference))
                return reference;
            // bounded: one pass over the table, which holds at most one entry per reference file and version.
            foreach (var (other, entry) in _references)
            {
                if (!entry.TryGetTarget(out _))
                    _references.Remove(other);
            }
            reference = MetadataReference.CreateFromFile(path);
            _references[key] = new WeakReference<MetadataReference>(reference);
            return reference;
        }
    }
}
