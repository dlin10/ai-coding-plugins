using System.Runtime.CompilerServices;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The metadata references library compilations share: one per reference file while the file is unchanged, held only as long as
/// a compilation holds it.</summary>
public sealed class LibraryReferencesTests
{
    [Fact]
    public void A_reference_file_whose_length_or_write_time_changed_gets_a_new_reference()
    {
        using var install = new GenerationInstall();
        var path = EmittedAssemblies.Write(Path.Combine(install.Root, "Fixture.Changing.dll"), "Fixture.Changing", "public sealed class Box { }");
        var references = new LibraryReferences();

        var first = references.Of(path);
        Assert.Same(first, references.Of(path));
        Assert.Same(first, references.Of(path.ToUpperInvariant()));

        var written = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, written.AddMinutes(1));
        var touched = references.Of(path);
        Assert.NotSame(first, touched);

        using (var stream = File.Open(path, FileMode.Append))
            stream.WriteByte(0);
        File.SetLastWriteTimeUtc(path, written.AddMinutes(1));
        var grown = references.Of(path);
        Assert.NotSame(touched, grown);
        Assert.NotSame(first, grown);
    }

    [Fact]
    public void A_reference_no_compilation_holds_is_collected_and_its_entry_leaves_the_table()
    {
        using var install = new GenerationInstall();
        var gone = EmittedAssemblies.Write(Path.Combine(install.Root, "Fixture.Gone.dll"), "Fixture.Gone", "public sealed class Gone { }");
        var kept = EmittedAssemblies.Write(Path.Combine(install.Root, "Fixture.Kept.dll"), "Fixture.Kept", "public sealed class Kept { }");
        var references = new LibraryReferences();

        var unheld = Unheld(references, gone);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(unheld.TryGetTarget(out _));

        var held = references.Of(kept);
        Assert.Equal(1, references.Count);
        GC.KeepAlive(held);
    }

    /// <summary>A weak handle on the reference to a file that nothing else holds once this method returns.</summary>
    /// <param name="references">The table.</param>
    /// <param name="path">The reference file.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<MetadataReference> Unheld(LibraryReferences references, string path) => new(references.Of(path));
}
