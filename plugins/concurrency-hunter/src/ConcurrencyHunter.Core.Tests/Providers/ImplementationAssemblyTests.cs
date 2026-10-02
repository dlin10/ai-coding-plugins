using System.Runtime.InteropServices;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The model generator's implementation assembly (SPEC TD-034b, question 41): the shared framework of the member's major
/// for a framework member, the exact package version for a package member, never a reference assembly, nothing downloaded.</summary>
public sealed class ImplementationAssemblyTests : IDisposable
{
    private const string NET8 = "net8.0";

    private readonly GenerationInstall _install = new();

    public void Dispose() => _install.Dispose();

    [Fact]
    public void Package_id_makes_a_package_member()
    {
        _install.SharedFramework("8.0.1", ["Other"]);
        var package = _install.Package("Foo", "1.2.3", "", "net8.0/Foo");

        var found = Found(_install.Resolver().Resolve("Foo", "1.2.3", "Foo", NET8));

        Assert.Equal(Path.Combine(package, "lib", "net8.0", "Foo.dll"), found.Path);
        Assert.Equal("Foo", found.PackageId);
        Assert.Equal("1.2.3", found.ImplementationVersion);
        Assert.Equal(NET8, found.Framework);
        Assert.Equal("0.0.0.0", found.AssemblyVersion);
        Assert.NotEqual(Guid.Empty, found.Mvid);
    }

    [Fact]
    public void Without_a_package_id_the_member_is_the_framework_s_even_beside_a_package_of_its_name()
    {
        var shared = _install.SharedFramework("8.0.1", ["System.Text.Json", "Other"]);
        _install.Package("System.Text.Json", "8.0.0", "", "net8.0/System.Text.Json");

        var found = Found(_install.Resolver().Resolve("System.Text.Json", "8.0", null, null));

        Assert.Equal(Path.Combine(shared, "System.Text.Json.dll"), found.Path);
        Assert.Null(found.PackageId);
        Assert.Equal("8.0.1", found.ImplementationVersion);
        Assert.Equal(NET8, found.Framework);
        Assert.Equal([Path.Combine(shared, "Other.dll")], found.References);
    }

    [Fact]
    public void Lib_folder_is_the_platform_s_own_first()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", "", "net8.0/Foo", "net6.0/Foo", "net9.0/Foo", "netstandard2.0/Foo");

        Assert.Equal("net8.0", LibOf(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8)));
    }

    [Fact]
    public void Lib_folder_falls_back_to_the_highest_lower_net()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", "", "net5.0/Foo", "net6.0/Foo", "net9.0/Foo", "netstandard2.1/Foo");

        Assert.Equal("net6.0", LibOf(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8)));
    }

    [Fact]
    public void Lib_folder_falls_back_to_netstandard2_1_before_netstandard2_0()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", "", "netstandard2.0/Foo", "netstandard2.1/Foo", "net48/Foo");

        Assert.Equal("netstandard2.1", LibOf(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8)));
    }

    [Fact]
    public void Lib_folder_falls_back_to_netstandard2_0_before_netstandard1()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", "", "netstandard1.6/Foo", "netstandard2.0/Foo");

        Assert.Equal("netstandard2.0", LibOf(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8)));
    }

    [Fact]
    public void Lib_folder_falls_back_to_the_highest_netstandard1()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", "", "netstandard1.3/Foo", "netstandard1.6/Foo", "netstandard1.1/Foo");

        Assert.Equal("netstandard1.6", LibOf(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8)));
    }

    [Fact]
    public void A_net_framework_lib_folder_is_never_taken()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", "", "net461/Foo", "net48/Foo", "net472/Foo");

        Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, _install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8).Reason);
    }

    [Fact]
    public void No_package_folder_is_no_implementation()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", "", "net8.0/Foo");

        var resolution = _install.Resolver().Resolve("Foo", "1.0.1", "Foo", NET8);

        Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, resolution.Reason);
        Assert.Null(resolution.Assembly);
    }

    [Fact]
    public void A_lib_folder_only_of_a_higher_platform_is_no_compatible_lib_folder()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", "", "net9.0/Foo", "net10.0/Foo");

        var resolution = _install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8);

        Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, resolution.Reason);
        Assert.Contains("no lib folder compatible with net8.0", resolution.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void No_such_assembly_in_the_lib_folder_is_no_implementation()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", "", "net8.0/Foo.Core");

        var resolution = _install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8);

        Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, resolution.Reason);
        Assert.Contains("Foo.dll is not in", resolution.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_member_on_an_uninstalled_platform_major_is_no_implementation()
    {
        _install.SharedFramework("10.0.1");
        _install.Package("Foo", "1.0.0", "", "netstandard2.0/Foo");

        var resolution = _install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8);

        Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, resolution.Reason);
        Assert.Contains("no shared framework of .NET 8", resolution.Detail, StringComparison.Ordinal);
        Assert.Equal("netstandard2.0", LibOf(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", "net10.0")));
    }

    [Fact]
    public void A_package_member_without_a_framework_takes_the_runtime_s_major()
    {
        _install.SharedFramework("9.0.3");
        _install.Package("Foo", "1.0.0", "", "net8.0/Foo", "net9.0/Foo");

        var found = Found(_install.Resolver(runtimeMajor: 9).Resolve("Foo", "1.0.0", "Foo", null));

        Assert.Equal("net9.0", found.Framework);
        Assert.Equal("net9.0", Path.GetFileName(Path.GetDirectoryName(found.Path)));
    }

    [Fact]
    public void A_framework_member_comes_from_the_highest_patch_of_its_major()
    {
        _install.SharedFramework("8.0.1", ["Foo"]);
        var highest = _install.SharedFramework("8.0.12", ["Foo"]);
        _install.SharedFramework("8.0.9", ["Foo"]);
        _install.SharedFramework("9.0.20", ["Foo"]);

        var found = Found(_install.Resolver().Resolve("Foo", "8.0.0.0", null, null));

        Assert.Equal(Path.Combine(highest, "Foo.dll"), found.Path);
        Assert.Equal("8.0.12", found.ImplementationVersion);
    }

    [Theory]
    [InlineData("8")]
    [InlineData("8.0")]
    [InlineData("8.0.0.0")]
    public void A_framework_member_of_an_uninstalled_major_never_takes_another(string version)
    {
        _install.SharedFramework("7.0.20", ["Foo"]);
        _install.SharedFramework("9.0.1", ["Foo"]);
        _install.SharedFramework("10.0.1", ["Foo"]);

        var resolution = _install.Resolver().Resolve("Foo", version, null, null);

        Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, resolution.Reason);
        Assert.Null(resolution.Assembly);
    }

    [Fact]
    public void A_reference_assembly_is_refused()
    {
        var shared = _install.SharedFramework("8.0.1", ["Other"]);
        EmittedAssemblies.Write(Path.Combine(shared, "Foo.dll"), "Foo", referenceAssembly: true);

        var resolution = _install.Resolver().Resolve("Foo", "8.0", null, null);

        Assert.Equal(GenerationReasons.REFERENCE_ASSEMBLY, resolution.Reason);
        Assert.Equal(Path.Combine(shared, "Foo.dll"), resolution.Assembly?.Path);
        Assert.Empty(resolution.Assembly!.References);
    }

    [Fact]
    public void A_nuspec_group_name_is_normalized_to_its_short_folder_name()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", """
            <dependencies>
              <group targetFramework=".NETFramework4.6.1"><dependency id="Legacy" version="1.0.0" /></group>
              <group targetFramework=".NETStandard2.0"><dependency id="Bar" version="1.0.0" /></group>
            </dependencies>
            """, "netstandard2.0/Foo");
        var bar = _install.Package("Bar", "1.0.0", "", "netstandard2.0/Bar");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Contains(Path.Combine(bar, "lib", "netstandard2.0", "Bar.dll"), found.References);
        Assert.Empty(found.MissingDependencies);
        Assert.Equal("netstandard2.0", ImplementationAssemblies.ShortFrameworkName(".NETStandard2.0"));
        Assert.Equal("net461", ImplementationAssemblies.ShortFrameworkName(".NETFramework4.6.1"));
        Assert.Equal("net5.0", ImplementationAssemblies.ShortFrameworkName("net5.0"));
        Assert.Equal("net6.0", ImplementationAssemblies.ShortFrameworkName(".NETCoreApp6.0"));
        Assert.Equal("netstandard2.1", ImplementationAssemblies.ShortFrameworkName(".NETStandard,Version=v2.1"));
    }

    [Fact]
    public void An_ungrouped_dependency_list_applies_to_every_framework()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", """<dependencies><dependency id="Bar" version="2.0.0" /></dependencies>""", "net8.0/Foo");
        var bar = _install.Package("Bar", "2.0.0", "", "net6.0/Bar");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Contains(Path.Combine(bar, "lib", "net6.0", "Bar.dll"), found.References);
    }

    [Fact]
    public void An_inclusive_lower_bound_takes_the_lowest_cached_version_it_admits()
    {
        var bars = PackageWithBar("[1.0.0, )", "0.9.0", "1.0.0", "2.0.0");

        Assert.Equal(bars["1.0.0"], BarReference());
    }

    [Fact]
    public void An_exclusive_lower_bound_skips_its_bound()
    {
        var bars = PackageWithBar("(1.0.0, )", "1.0.0", "1.5.0", "2.0.0");

        Assert.Equal(bars["1.5.0"], BarReference());
    }

    [Fact]
    public void An_exclusive_upper_bound_takes_the_lowest_version_below_it()
    {
        var bars = PackageWithBar("(, 2.0.0)", "2.0.0", "1.5.0");

        Assert.Equal(bars["1.5.0"], BarReference());
    }

    [Fact]
    public void A_dependency_whose_only_cached_version_lies_above_its_upper_bound_is_skipped_and_named()
    {
        PackageWithBar("(, 2.0.0)", "3.0.0");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal(["Bar@(, 2.0.0)"], found.MissingDependencies);
        Assert.DoesNotContain(found.References, reference => Path.GetFileName(reference) == "Bar.dll");
    }

    [Fact]
    public void A_bounded_range_takes_the_lowest_version_inside_it()
    {
        var bars = PackageWithBar("[1.0.0, 2.0.0)", "0.5.0", "2.0.0", "1.7.0", "1.2.0");

        Assert.Equal(bars["1.2.0"], BarReference());
    }

    [Fact]
    public void A_prerelease_lies_below_its_release()
    {
        var bars = PackageWithBar("[1.0.0, )", "1.0.0-beta.2", "1.0.0", "1.0.0-beta.11");

        Assert.Equal(bars["1.0.0"], BarReference());
        Assert.True(Version("1.0.0-beta.2").CompareTo(Version("1.0.0-beta.11")) < 0);
        Assert.True(Version("1.0.0-beta.11").CompareTo(Version("1.0.0-rc.1")) < 0);
        Assert.True(Version("1.0.0-rc.1").CompareTo(Version("1.0.0")) < 0);
        Assert.True(Version("1.0.0-alpha").CompareTo(Version("1.0.0-alpha.1")) < 0);
        Assert.True(Version("1.0.0-alpha.9").CompareTo(Version("1.0.0-alpha.beta")) < 0);
        Assert.Equal(0, Version("1.0.0+build.5").CompareTo(Version("1.0")));
    }

    [Fact]
    public void A_two_level_dependency_chain_is_followed()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", """<dependencies><dependency id="Bar" version="1.0.0" /></dependencies>""", "net8.0/Foo");
        _install.Package("Bar", "1.0.0", """<dependencies><dependency id="Baz" version="[3.0.0]" /></dependencies>""", "net8.0/Bar");
        _install.Package("Baz", "2.0.0", "", "net8.0/Baz");
        var baz = _install.Package("Baz", "3.0.0", "", "net8.0/Baz");
        _install.Package("Baz", "4.0.0", "", "net8.0/Baz");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Contains(found.References, reference => Path.GetFileName(reference) == "Bar.dll");
        Assert.Contains(Path.Combine(baz, "lib", "net8.0", "Baz.dll"), found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(ImplementationAssemblies.CLOSURE_ROUNDS - 1)]
    public void A_dependency_chain_deeper_than_any_fixed_small_bound_is_followed_to_its_end(int depth)
    {
        _install.SharedFramework("8.0.1");
        var last = Chain(depth);

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Contains(last, found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Fact]
    public void A_chain_past_the_round_bound_names_the_link_still_unsettled_and_follows_nothing_beyond_it()
    {
        _install.SharedFramework("8.0.1");
        var last = Chain(ImplementationAssemblies.CLOSURE_ROUNDS + 5);

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([$"P{ImplementationAssemblies.CLOSURE_ROUNDS}@1.0.0"], found.MissingDependencies);
        Assert.DoesNotContain(last, found.References);
    }

    [Fact]
    public void Oscillating_dependencies_are_skipped_and_named_and_never_taken_at_a_version_a_range_refuses()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", """
            <dependencies>
              <dependency id="A" version="[1.0.0, )" /><dependency id="B" version="[1.0.0, )" /><dependency id="C" version="1.0.0" />
            </dependencies>
            """, "net8.0/Foo");
        _install.Package("A", "1.0.0", """<dependencies><dependency id="B" version="[2.0.0, )" /></dependencies>""", "net8.0/A");
        _install.Package("A", "2.0.0", """<dependencies><dependency id="D" version="1.0.0" /></dependencies>""", "net8.0/A");
        _install.Package("B", "1.0.0", """<dependencies><dependency id="A" version="[2.0.0, )" /></dependencies>""", "net8.0/B");
        _install.Package("B", "2.0.0", "", "net8.0/B");
        var c = _install.Package("C", "1.0.0", "", "net8.0/C");
        _install.Package("D", "1.0.0", "", "net8.0/D");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal(["A@[1.0.0, )", "B@[1.0.0, )"], found.MissingDependencies);
        Assert.DoesNotContain(found.References, reference => Path.GetFileName(reference) is "A.dll" or "B.dll" or "D.dll");
        Assert.Contains(Path.Combine(c, "lib", "net8.0", "C.dll"), found.References);
    }

    [Theory]
    [InlineData("[1.0.0, ]")]
    [InlineData("[1.0.0,]")]
    [InlineData("(1.0.0,]")]
    [InlineData("[, 2.0.0]")]
    [InlineData("[,2.0.0)")]
    [InlineData("[,]")]
    [InlineData("(,)")]
    public void A_missing_bound_without_a_round_bracket_is_no_range_and_its_dependency_is_skipped_and_named(string range)
    {
        PackageWithBar(range, "1.0.0", "1.5.0", "2.0.0");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.False(PackageVersionRange.TryParse(range, out _), range);
        Assert.Equal([$"Bar@{range}"], found.MissingDependencies);
        Assert.DoesNotContain(found.References, reference => Path.GetFileName(reference) == "Bar.dll");
    }

    [Theory]
    [InlineData("[1.0.0,)", "1.0.0")]
    [InlineData("(1.0.0,)", "1.5.0")]
    [InlineData("(,2.0.0]", "1.0.0")]
    [InlineData("(, 1.5.0)", "1.0.0")]
    [InlineData("[1.5.0]", "1.5.0")]
    public void A_missing_bound_with_a_round_bracket_stays_a_range(string range, string taken)
    {
        var bars = PackageWithBar(range, "1.0.0", "1.5.0", "2.0.0");

        Assert.Equal(bars[taken], BarReference());
    }

    [Fact]
    public void Every_assembly_of_a_lib_folder_is_a_reference()
    {
        _install.SharedFramework("8.0.1");
        var foo = _install.Package("Foo", "1.0.0", """<dependencies><dependency id="Bar" version="1.0.0" /></dependencies>""",
                                   "net8.0/Foo", "net8.0/Foo.Helpers");
        var bar = _install.Package("Bar", "1.0.0", "", "net8.0/Bar", "net8.0/Bar.Extra");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Contains(Path.Combine(foo, "lib", "net8.0", "Foo.Helpers.dll"), found.References);
        Assert.Contains(Path.Combine(bar, "lib", "net8.0", "Bar.dll"), found.References);
        Assert.Contains(Path.Combine(bar, "lib", "net8.0", "Bar.Extra.dll"), found.References);
        Assert.DoesNotContain(found.Path, found.References);
    }

    [Fact]
    public void A_dependency_reached_by_two_ranges_is_resolved_once_at_the_lowest_version_both_admit()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", """
            <dependencies><dependency id="Bar" version="[1.0.0, )" /><dependency id="Baz" version="1.0.0" /></dependencies>
            """, "net8.0/Foo");
        _install.Package("Baz", "1.0.0", """<dependencies><dependency id="Bar" version="[1.5.0, 3.0.0)" /></dependencies>""", "net8.0/Baz");
        _install.Package("Bar", "1.0.0", "", "net8.0/Bar");
        var chosen = _install.Package("Bar", "1.5.0", "", "net8.0/Bar");
        _install.Package("Bar", "2.0.0", "", "net8.0/Bar");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([Path.Combine(chosen, "lib", "net8.0", "Bar.dll")], found.References.Where(reference => Path.GetFileName(reference) == "Bar.dll"));
        Assert.Empty(found.MissingDependencies);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0+build.7")]
    public void A_package_folder_is_found_from_a_version_by_normalization(string version)
    {
        _install.SharedFramework("8.0.1");
        var package = _install.Package("Foo", "1.0.0", "", "net8.0/Foo");

        var found = Found(_install.Resolver().Resolve("Foo", version, "Foo", NET8));

        Assert.Equal(Path.Combine(package, "lib", "net8.0", "Foo.dll"), found.Path);
        Assert.Equal("1.0.0", found.ImplementationVersion);
        Assert.Equal("1.2.3.4", Version("1.2.3.4").Normalized);
        Assert.Equal("1.0.0-beta", Version("1.0-Beta").Normalized);
    }

    [Fact]
    public void A_dependency_version_without_a_compatible_lib_folder_is_skipped_and_named()
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", """<dependencies><dependency id="Bar" version="1.0.0" /></dependencies>""", "net8.0/Foo");
        _install.Package("Bar", "1.0.0", """<dependencies><dependency id="Baz" version="1.0.0" /></dependencies>""", "net461/Bar");
        _install.Package("Baz", "1.0.0", "", "net8.0/Baz");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal(["Bar@1.0.0: no compatible lib folder"], found.MissingDependencies);
        Assert.DoesNotContain(found.References, reference => Path.GetFileName(reference) is "Bar.dll" or "Baz.dll");
    }

    [Fact]
    public void NUGET_PACKAGES_is_honoured()
    {
        var major = Environment.Version.Major;
        var package = _install.Package("Fixture.Only", "1.0.0", "", $"net{major}.0/Fixture.Only");

        var resolver = ImplementationAssemblies.ForThisProcess(name => name == "NUGET_PACKAGES" ? _install.Packages : null);
        var found = Found(resolver.Resolve("Fixture.Only", "1.0.0", "Fixture.Only", null));

        Assert.Equal(Path.Combine(package, "lib", $"net{major}.0", "Fixture.Only.dll"), found.Path);
        Assert.Contains(found.References, reference => Path.GetFileName(reference) == "System.Private.CoreLib.dll");
    }

    [Theory]
    [InlineData("System.Private.CoreLib")]
    [InlineData("system.private.corelib")]
    public void System_Private_CoreLib_is_answered_corelib_without_being_read(string name)
    {
        var resolver = new ImplementationAssemblies(Path.Combine(_install.Root, "absent-dotnet"), Path.Combine(_install.Root, "absent-packages"), 8);

        var resolution = resolver.Resolve(name, "8.0", null, null);

        Assert.Equal(GenerationReasons.CORELIB, resolution.Reason);
        Assert.Null(resolution.Assembly);
        Assert.Equal(GenerationReasons.CORELIB, resolver.Resolve(name, "not-a-version", "Any.Package", "not-a-framework").Reason);
    }

    [Fact]
    public void A_package_assembly_the_shared_framework_also_has_is_compiled_from_the_package_s_copy_only()
    {
        var shared = _install.SharedFramework("8.0.1", ["System.Text.Json", "System.Memory", "Other"]);
        var package = _install.Package("System.Text.Json", "8.0.5", """<dependencies><dependency id="System.Memory" version="4.5.0" /></dependencies>""",
                                       "net8.0/System.Text.Json");
        var memory = _install.Package("System.Memory", "4.5.0", "", "netstandard2.0/System.Memory");

        var found = Found(_install.Resolver().Resolve("System.Text.Json", "8.0.5", "System.Text.Json", NET8));

        Assert.Equal(Path.Combine(package, "lib", "net8.0", "System.Text.Json.dll"), found.Path);
        Assert.DoesNotContain(Path.Combine(shared, "System.Text.Json.dll"), found.References);
        Assert.DoesNotContain(Path.Combine(shared, "System.Memory.dll"), found.References);
        Assert.Contains(Path.Combine(memory, "lib", "netstandard2.0", "System.Memory.dll"), found.References);
        Assert.Contains(Path.Combine(shared, "Other.dll"), found.References);
        Assert.DoesNotContain(Path.Combine(shared, "coreclr.dll"), found.References);
    }

    [Fact]
    public void A_file_whose_full_path_leaves_its_chosen_folder_is_no_implementation()
    {
        _install.SharedFramework("8.0.1", ["Other"]);
        EmittedAssemblies.Write(Path.Combine(_install.DotnetRoot, "shared", "Escape.dll"), "Escape");
        _install.Package("Foo", "1.0.0", """<dependencies><dependency id="..\..\Bar" version="1.0.0" /></dependencies>""", "net8.0/Foo");
        EmittedAssemblies.Write(Path.Combine(_install.Root, "Bar", "1.0.0", "lib", "net8.0", "Bar.dll"), "Bar");
        EmittedAssemblies.Write(Path.Combine(_install.Root, "Outside.dll"), "Outside");

        Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, _install.Resolver().Resolve(@"..\..\Escape", "8.0", null, null).Reason);
        Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, _install.Resolver().Resolve(@"..\..\..\..\..\Outside", "1.0.0", "Foo", NET8).Reason);
        Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, _install.Resolver().Resolve("Bar", "1.0.0", @"..\Bar", NET8).Reason);
        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));
        Assert.Equal([@"..\..\Bar@1.0.0"], found.MissingDependencies);
        Assert.DoesNotContain(found.References, reference => Path.GetFileName(reference) == "Bar.dll");
    }

    [Fact]
    public void The_installed_runtime_s_framework_member_is_compiled_against_its_own_shared_framework()
    {
        var major = Environment.Version.Major;
        var runtimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var resolver = ImplementationAssemblies.ForThisProcess();

        var found = Found(resolver.Resolve("System.Linq", major.ToString(), null, null));

        Assert.Equal(Path.Combine(runtimeDirectory, "System.Linq.dll"), found.Path, ignoreCase: true);
        Assert.Equal($"net{major}.0", found.Framework);
        Assert.Contains(found.References, reference => Path.GetFileName(reference) == "System.Private.CoreLib.dll");
        Assert.DoesNotContain(found.References, reference => Path.GetFileName(reference) is "System.Linq.dll" or "coreclr.dll" or "clrjit.dll");
        Assert.All(found.References, reference => Assert.Equal(runtimeDirectory, Path.GetDirectoryName(reference), ignoreCase: true));
    }

    [Fact]
    public void Names_outside_the_grammar_are_refused_by_the_input_checks()
    {
        Assert.True(ImplementationAssemblies.IsAssemblyName("System.Text.Json"));
        Assert.True(ImplementationAssemblies.IsAssemblyName("Polly"));
        Assert.True(ImplementationAssemblies.IsPackageId("Google.Protobuf"));
        foreach (var bad in new[] { "", "..", "a..b", ".a", "a.", @"a\b", "a/b", "C:a", "a b" })
        {
            Assert.False(ImplementationAssemblies.IsAssemblyName(bad), bad);
            Assert.False(ImplementationAssemblies.IsPackageId(bad), bad);
        }

        Assert.False(ImplementationAssemblies.IsPackageId(new string('a', 101)));
        Assert.True(ImplementationAssemblies.TryParseFramework("net8.0", out var major) && major == 8);
        Assert.False(ImplementationAssemblies.TryParseFramework("net8", out _));
        Assert.False(ImplementationAssemblies.TryParseFramework("netstandard2.0", out _));
        Assert.True(ImplementationAssemblies.TryParseAssemblyMajor("8.0.0.0", out major) && major == 8);
        Assert.False(ImplementationAssemblies.TryParseAssemblyMajor("8.0.0.0.0", out _));
        Assert.False(ImplementationAssemblies.TryParseAssemblyMajor("8.0-preview", out _));
        Assert.Throws<ArgumentException>(() => _install.Resolver().Resolve("Foo", "x", null, null));
        Assert.Throws<ArgumentException>(() => _install.Resolver().Resolve("Foo", "1.0.0", "Foo", "net8"));
    }

    [Theory]
    [InlineData("1.0+")]
    [InlineData("1.0+?")]
    [InlineData("1.0.0+a..b")]
    [InlineData("1.0.0+a+b")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-rc.007")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-rc_1")]
    public void A_version_with_an_invalid_semver_identifier_does_not_parse(string text)
    {
        Assert.False(PackageVersion.TryParse(text, out _), text);
        Assert.False(PackageVersionRange.TryParse($"[{text},)", out _), text);
    }

    [Theory]
    [InlineData("1.0.0-0", "1.0.0-0")]
    [InlineData("1.0.0-0a", "1.0.0-0a")]
    [InlineData("1.0.0-rc.1+build.01", "1.0.0-rc.1")]
    [InlineData("1.0.0+001", "1.0.0")]
    [InlineData("1.0.0-x-y.z+a-b", "1.0.0-x-y.z")]
    [InlineData("01.2", "1.2.0")]
    public void A_version_whose_identifiers_are_valid_parses(string text, string normalized) =>
        Assert.Equal(normalized, Version(text).Normalized);

    [Fact]
    public void The_platform_chosen_is_named_also_when_no_assembly_is_found()
    {
        _install.SharedFramework("8.0.1", ["Other"]);
        var resolver = _install.Resolver();

        Assert.Equal(("net9.0", GenerationReasons.NO_IMPLEMENTATION), Chosen(resolver.Resolve("Other", "9.0", null, null)));
        Assert.Equal((NET8, GenerationReasons.NO_IMPLEMENTATION), Chosen(resolver.Resolve("Absent", "8.0.0.0", null, null)));
        Assert.Equal((NET8, GenerationReasons.NO_IMPLEMENTATION), Chosen(resolver.Resolve("Foo", "1.0.0", "Foo", null)));
        Assert.Equal(("net10.0", GenerationReasons.NO_IMPLEMENTATION), Chosen(resolver.Resolve("Foo", "1.0.0", "Foo", "net10.0")));
        Assert.Equal((null, GenerationReasons.CORELIB), Chosen(resolver.Resolve("System.Private.CoreLib", "8.0", null, null)));
        _install.Package("Foo", "1.0.0", "", "net8.0/Foo");
        Assert.Equal(((string?)NET8, (string?)null), Chosen(resolver.Resolve("Foo", "1.0.0", "Foo", NET8)));

        static (string? Framework, string? Reason) Chosen(ImplementationResolution resolution) => (resolution.Framework, resolution.Reason);
    }

    private static PackageVersion Version(string text)
    {
        Assert.True(PackageVersion.TryParse(text, out var version), text);
        return version;
    }

    /// <summary>A package Foo depending on Bar by one range, with Bar cached at the given versions; the Bar file per version.</summary>
    /// <param name="range">The range Foo's nuspec gives Bar.</param>
    /// <param name="versions">The cached versions of Bar.</param>
    private Dictionary<string, string> PackageWithBar(string range, params string[] versions)
    {
        _install.SharedFramework("8.0.1");
        _install.Package("Foo", "1.0.0", $"""<dependencies><dependency id="Bar" version="{range}" /></dependencies>""", "net8.0/Foo");
        return versions.ToDictionary(version => version, version => Path.Combine(_install.Package("Bar", version, "", "net8.0/Bar"), "lib", "net8.0", "Bar.dll"));
    }

    /// <summary>A package Foo heading a chain Foo → P1 → … → P<paramref name="depth"/>, each link at 1.0.0 with a compatible lib
    /// folder; only the last link's folder holds an assembly, so the chain is followed to its end exactly when it is referenced.</summary>
    /// <param name="depth">The number of links after Foo.</param>
    private string Chain(int depth)
    {
        _install.Package("Foo", "1.0.0", Next(1), "net8.0/Foo");
        for (var link = 1; link < depth; link++)
            Directory.CreateDirectory(Path.Combine(_install.Package($"P{link}", "1.0.0", Next(link + 1)), "lib", "net8.0"));
        return Path.Combine(_install.Package($"P{depth}", "1.0.0", "", $"net8.0/P{depth}"), "lib", "net8.0", $"P{depth}.dll");

        static string Next(int link) => $"""<dependencies><dependency id="P{link}" version="1.0.0" /></dependencies>""";
    }

    private string BarReference()
    {
        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));
        Assert.Empty(found.MissingDependencies);
        return Assert.Single(found.References, reference => Path.GetFileName(reference) == "Bar.dll");
    }

    private static string LibOf(ImplementationResolution resolution) => Path.GetFileName(Path.GetDirectoryName(Found(resolution).Path))!;

    private static ImplementationAssembly Found(ImplementationResolution resolution)
    {
        Assert.True(resolution.Reason is null, $"{resolution.Reason}: {resolution.Detail}");
        return resolution.Assembly!;
    }
}
