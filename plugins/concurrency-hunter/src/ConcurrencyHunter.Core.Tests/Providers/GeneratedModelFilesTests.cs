using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The generated files of a repository (R2, R3): where a file lives, what it keeps, and how a run adds to it.</summary>
public sealed class GeneratedModelFilesTests
{
    private const string RUN = "M:Lib.Api.Run(System.Object)";
    private const string FIRST = "M:Lib.Api.First(System.Object)";
    private const string SECOND = "M:Lib.Api.Second(System.Object)";
    private const string THIRD = "M:Lib.Api.Third(System.Object)";
    private const string FOURTH = "M:Lib.Api.Fourth(System.Object)";
    private static readonly Guid MVID = Guid.Parse("5c8d0b6e-7a4f-4b1e-9f2a-3d6c1e8b0a47");
    private static readonly TimeSpan PATIENCE = TimeSpan.FromSeconds(30);

    // R3: what a file keeps, word by word. A word that is not listed here fails the theory, so a new reason is decided on purpose.
    private static readonly Dictionary<string, bool> KEPT = new(StringComparer.Ordinal)
    {
        [GenerationReasons.CORELIB] = false,
        [GenerationReasons.NO_IMPLEMENTATION] = false,
        [GenerationReasons.REFERENCE_ASSEMBLY] = true,
        [GenerationReasons.LIBRARY_DOES_NOT_COMPILE] = true,
        [GenerationReasons.MEMBER_NOT_FOUND] = true,
        [GenerationReasons.UNNAMED_MEMBER] = false,
        [GenerationReasons.ACCESSOR] = true,
        [GenerationReasons.ENGINE_RECOGNIZED] = true,
        [GenerationReasons.DRIVER_NOT_SYNTHESIZED] = true,
        [GenerationReasons.NOT_A_CANDIDATE] = true,
        [GenerationReasons.BODY_DOES_NOT_COMPILE] = true,
        [GenerationReasons.OVERRIDABLE] = true,
        [GenerationReasons.CLOSURE_BOUND] = true,
        [GenerationReasons.ANALYSIS_FAILED] = true,
        [ModelReasons.UNKNOWN_TOUCH] = true,
        [ModelReasons.INCOMPLETE] = true,
        [ModelReasons.LIBRARY_STATE] = true,
        [ModelReasons.VOCABULARY] = true,
        [GenerationOutcome.TIMEOUT] = true,
        [GenerationOutcome.BUDGET] = false,
        [GenerationOutcome.CANCELLED] = false,
        [GenerationOutcome.FAILED] = false
    };

    [Fact]
    public void Package_answer_lives_at_package_version_mvid()
    {
        using var repository = new Repository();
        var key = PackageKey();

        Assert.Empty(Write(repository, key, GeneratedModelAnswer.OfModel(Model(RUN))));

        var path = Path.Combine(repository.Root, ".concurrency-hunter", "models", "generated", "Polly", "7.2.3", MVID.ToString("D") + ".json");
        Assert.Equal("Polly/7.2.3/" + MVID.ToString("D") + ".json", key.RelativePath);
        Assert.Equal(path, key.PathUnder(repository.Root));
        Assert.True(File.Exists(path));
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var assembly = document.RootElement.GetProperty("assembly");
        Assert.Equal(["name", "version", "mvid", "package", "packageVersion"], Names(assembly));
        Assert.Equal("Polly", assembly.GetProperty("name").GetString());
        Assert.Equal("7.0.0.0", assembly.GetProperty("version").GetString());
        Assert.Equal(MVID.ToString("D"), assembly.GetProperty("mvid").GetString());
        Assert.Equal("Polly", assembly.GetProperty("package").GetString());
        Assert.Equal("7.2.3", assembly.GetProperty("packageVersion").GetString());

        // Two assets of one assembly are two files.
        var other = PackageKey(Guid.Parse("0f0e0d0c-0b0a-4908-8706-050403020100"));
        Assert.Empty(Write(repository, other, GeneratedModelAnswer.OfModel(Model(RUN))));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.json").Length);

        // An asset that is not on disk keeps the package's id and version and names no file.
        var missing = GeneratedFileKey.ForPackage("Polly", new Version(7, 0, 0, 0), "Polly", "7.2.3", null);
        Assert.Equal(("Polly", "7.2.3"), (missing.Package, missing.PackageVersion));
        Assert.Null(missing.Mvid);
        Assert.Null(missing.RelativePath);
        Assert.Null(missing.PathUnder(repository.Root));
        var absent = GeneratedModelFiles.Read(repository.Root, missing);
        Assert.False(absent.IsUnreadable);
        Assert.Empty(absent.Entries);
        Assert.Empty(absent.Diagnostics);
    }

    [Fact]
    public void Framework_answer_lives_at_framework_major_assembly()
    {
        using var repository = new Repository();
        var key = GeneratedFileKey.ForFramework("System.Linq", new Version(8, 0, 0, 0), "Microsoft.NETCore.App", 8);

        Assert.Empty(Write(repository, key, GeneratedModelAnswer.OfModel(Model(RUN, "System.Linq"))));

        var path = Path.Combine(repository.Root, ".concurrency-hunter", "models", "generated", "Microsoft.NETCore.App", "8.0", "System.Linq.json");
        Assert.Equal("Microsoft.NETCore.App/8.0/System.Linq.json", key.RelativePath);
        Assert.Equal(path, key.PathUnder(repository.Root));
        Assert.True(File.Exists(path));
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var assembly = document.RootElement.GetProperty("assembly");
        Assert.Equal(["name", "version", "mvid", "framework", "major"], Names(assembly));
        Assert.Equal("System.Linq", assembly.GetProperty("name").GetString());
        Assert.Equal("8.0.0.0", assembly.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, assembly.GetProperty("mvid").ValueKind);
        Assert.Equal("Microsoft.NETCore.App", assembly.GetProperty("framework").GetString());
        Assert.Equal(8, assembly.GetProperty("major").GetInt32());

        // Another framework, another assembly and another major are other files; the file reads back for its own key only.
        var aspNet = GeneratedFileKey.ForFramework("Microsoft.AspNetCore.Http.Abstractions", new Version(8, 0, 0, 0), "Microsoft.AspNetCore.App", 8);
        Assert.Equal("Microsoft.AspNetCore.App/8.0/Microsoft.AspNetCore.Http.Abstractions.json", aspNet.RelativePath);
        var next = GeneratedFileKey.ForFramework("System.Linq", new Version(9, 0, 0, 0), "Microsoft.NETCore.App", 9);
        Assert.Equal("Microsoft.NETCore.App/9.0/System.Linq.json", next.RelativePath);
        Assert.Empty(GeneratedModelFiles.Read(repository.Root, next).Entries);
        Assert.Single(GeneratedModelFiles.Read(repository.Root, key).Entries);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    public void Key_part_that_is_not_one_name_is_refused(string part)
    {
        Assert.Throws<ArgumentException>(() => GeneratedFileKey.ForPackage("Polly", new Version(7, 0, 0, 0), part, "7.2.3", MVID));
        Assert.Throws<ArgumentException>(() => GeneratedFileKey.ForPackage("Polly", new Version(7, 0, 0, 0), "Polly", part, MVID));
        Assert.Throws<ArgumentException>(() => GeneratedFileKey.ForFramework(part, new Version(8, 0, 0, 0), "Microsoft.NETCore.App", 8));
        Assert.Throws<ArgumentException>(() => GeneratedFileKey.ForFramework("System.Linq", new Version(8, 0, 0, 0), part, 8));
    }

    [Fact]
    public void Model_round_trips()
    {
        using var repository = new Repository();
        var key = PackageKey();
        var model = new LibraryModel(RUN, [new SupportedAssemblyVersion("Polly", new Version(7, 0, 0, 0), new Version(7, 0, 0, 1))],
                                     [LibraryEffect.DeepReadOf("value"), LibraryEffect.WriteOf("target")])
        {
            Result = LibraryResult.Parse("[arg:value]"),
            Fates = [new LibraryFate("callback", LibraryFateKind.InvokeNow, null, null)]
        };

        Assert.Empty(Write(repository, key, GeneratedModelAnswer.OfModel(model)));

        var file = GeneratedModelFiles.Read(repository.Root, key);
        Assert.False(file.IsUnreadable);
        Assert.Empty(file.Diagnostics);
        var entry = Assert.Single(file.Entries.Values);
        Assert.Equal(RUN, entry.Member);
        Assert.Null(entry.Refusal);
        Assert.Equal(ModelEntryWriter.Write(model).ToJsonString(), ModelEntryWriter.Write(Rebuilt(entry.Model!)).ToJsonString());

        // A team moves an entry into a project file by hand: the model is the project entry form, as ModelEntryWriter writes it.
        using var document = JsonDocument.Parse(File.ReadAllBytes(key.PathUnder(repository.Root)!));
        var stored = document.RootElement.GetProperty("members")[0];
        Assert.Equal(["member", "model"], Names(stored));
        Assert.True(JsonNode.DeepEquals(ModelEntryWriter.Write(model), JsonNode.Parse(stored.GetProperty("model").GetRawText())));
    }

    [Fact]
    public void Refusal_round_trips()
    {
        using var repository = new Repository();
        var key = PackageKey();
        var refusal = new GeneratedRefusal(ModelReasons.UNKNOWN_TOUCH, "a probe was handed to a call the analysis does not follow (café)",
                                           ["probe-handed-to-unseen", "unseeded-read"]);
        var limit = new GeneratedRefusal(GenerationOutcome.TIMEOUT, "the member ran past its limit", []);

        Assert.Empty(Write(repository, key, GeneratedModelAnswer.OfRefusal(FIRST, refusal), GeneratedModelAnswer.OfRefusal(SECOND, limit)));

        var file = GeneratedModelFiles.Read(repository.Root, key);
        Assert.False(file.IsUnreadable);
        Assert.Empty(file.Diagnostics);
        Assert.Equal([FIRST, SECOND], file.Entries.Keys.Order(StringComparer.Ordinal));
        foreach (var (member, expected) in new[] { (FIRST, refusal), (SECOND, limit) })
        {
            var entry = file.Entries[member];
            Assert.Null(entry.Model);
            Assert.Equal(expected.Reason, entry.Refusal!.Reason);
            Assert.Equal(expected.Detail, entry.Refusal.Detail);
            Assert.Equal(expected.Causes, entry.Refusal.Causes);
        }
    }

    [Fact]
    public void Members_are_in_ordinal_order()
    {
        using var repository = new Repository();
        var key = PackageKey();
        // Ordinal order puts every capital before every lower-case letter, as no culture does.
        string[] members = ["M:Lib.Api.b", "M:Lib.Api.B", "M:Lib.Api.a", "M:Lib.Api.A"];

        Write(repository, key, members.Select(member => GeneratedModelAnswer.OfModel(Model(member))).ToArray());
        Write(repository, key, GeneratedModelAnswer.OfModel(Model("M:Lib.Api.0")));

        Assert.Equal(members.Append("M:Lib.Api.0").Order(StringComparer.Ordinal), StoredMembers(repository, key));
        Assert.Equal(["M:Lib.Api.0", "M:Lib.Api.A", "M:Lib.Api.B", "M:Lib.Api.a", "M:Lib.Api.b"], StoredMembers(repository, key));
    }

    [Fact]
    public void Written_file_is_utf8_without_bom_lf_two_space_indent_final_newline()
    {
        using var repository = new Repository();
        var key = PackageKey();

        Write(repository, key, GeneratedModelAnswer.OfModel(Model(RUN)),
              GeneratedModelAnswer.OfRefusal(FIRST, new GeneratedRefusal(ModelReasons.VOCABULARY, "naïve — 漢字", ["result-unnamed"])));

        var bytes = File.ReadAllBytes(key.PathUnder(repository.Root)!);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        var text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\t', text);
        Assert.EndsWith("}\n", text);
        Assert.DoesNotContain("\n\n", text);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal("{", lines[0]);
        Assert.Equal("  \"schemaVersion\": 2,", lines[1]);
        Assert.Equal($"  \"generator\": \"{GeneratorVersion.Fingerprint}\",", lines[2]);
        Assert.Equal("  \"assembly\": {", lines[3]);
        Assert.Equal("    \"name\": \"Polly\",", lines[4]);
        Assert.All(lines, line => Assert.Equal(0, (line.Length - line.TrimStart(' ').Length) % 2));
        Assert.Contains("naïve — 漢字", text);
    }

    [Fact]
    public void Unchanged_bytes_are_not_rewritten()
    {
        using var repository = new Repository();
        var key = PackageKey();
        var answers = new[]
        {
            GeneratedModelAnswer.OfModel(Model(RUN)),
            GeneratedModelAnswer.OfRefusal(FIRST, new GeneratedRefusal(GenerationReasons.ACCESSOR, "a property", []))
        };
        Write(repository, key, answers);
        var path = key.PathUnder(repository.Root)!;
        var before = File.ReadAllBytes(path);
        var old = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, old);

        Assert.Empty(Write(repository, key, answers));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(old, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void Failed_replace_keeps_the_previous_file()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var repository = new Repository();
        var key = PackageKey();
        Write(repository, key, GeneratedModelAnswer.OfModel(Model(FIRST)));
        var path = key.PathUnder(repository.Root)!;
        var before = File.ReadAllBytes(path);
        var file = GeneratedModelFiles.Read(repository.Root, key);

        IReadOnlyList<string> diagnostics;
        // A reader that shares reads only, so the file can be read but not replaced.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            diagnostics = GeneratedModelFiles.Write(file, [GeneratedModelAnswer.OfModel(Model(SECOND))], CancellationToken.None);

        var diagnostic = Assert.Single(diagnostics);
        Assert.StartsWith("library-models: generated file .concurrency-hunter/models/generated/Polly/7.2.3/" + MVID.ToString("D") + ".json: cannot write file: ",
                          diagnostic);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));

        // The answers stay with the run; the same write goes through once the file can be replaced.
        Assert.Empty(GeneratedModelFiles.Write(file, [GeneratedModelAnswer.OfModel(Model(SECOND))], CancellationToken.None));
        Assert.Equal([FIRST, SECOND], StoredMembers(repository, key));
    }

    [Fact]
    public void Unreadable_file_counts_as_empty_is_diagnosed_and_is_not_rewritten()
    {
        using var repository = new Repository();
        var key = PackageKey();
        var path = key.PathUnder(repository.Root)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"schemaVersion\": 1, \"members\": [");
        var before = File.ReadAllBytes(path);

        var file = GeneratedModelFiles.Read(repository.Root, key);

        Assert.True(file.IsUnreadable);
        Assert.Empty(file.Entries);
        var diagnostic = Assert.Single(file.Diagnostics);
        Assert.StartsWith("library-models: generated file .concurrency-hunter/models/generated/Polly/7.2.3/" + MVID.ToString("D") + ".json: ", diagnostic);
        Assert.Empty(GeneratedModelFiles.Write(file, [GeneratedModelAnswer.OfModel(Model(RUN))], CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(path));

        // A file that is readable when the run reads it and no longer when the run writes it is not rewritten either.
        File.Delete(path);
        Write(repository, key, GeneratedModelAnswer.OfModel(Model(FIRST)));
        var readable = GeneratedModelFiles.Read(repository.Root, key);
        File.WriteAllText(path, "not JSON");
        before = File.ReadAllBytes(path);

        var diagnostics = GeneratedModelFiles.Write(readable, [GeneratedModelAnswer.OfModel(Model(SECOND))], CancellationToken.None);

        Assert.StartsWith("library-models: generated file .concurrency-hunter/models/generated/Polly/7.2.3/", Assert.Single(diagnostics));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("refusal reason")]
    [InlineData("refusal causes")]
    [InlineData("model")]
    [InlineData("model versions")]
    public void Property_repeated_at_any_depth_makes_the_file_unreadable_and_it_is_not_rewritten(string place)
    {
        using var repository = new Repository();
        var key = PackageKey();
        var path = key.PathUnder(repository.Root)!;
        // A sound entry beside it: a reader that lost only the broken entry would leave the file readable and open to a rewrite.
        WriteFile(repository, key, ModelEntry(SECOND), RepeatedProperty(place));
        var before = File.ReadAllBytes(path);

        var file = GeneratedModelFiles.Read(repository.Root, key);

        Assert.True(file.IsUnreadable);
        Assert.Empty(file.Entries);
        Assert.Contains("uplicate", Assert.Single(file.Diagnostics), StringComparison.Ordinal);
        Assert.Empty(GeneratedModelFiles.Write(file, [GeneratedModelAnswer.OfModel(Model(THIRD))], CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(path));

        // A file readable when the run reads it and broken in that way when the run writes it is not rewritten either.
        File.Delete(path);
        Write(repository, key, GeneratedModelAnswer.OfModel(Model(FIRST)));
        var readable = GeneratedModelFiles.Read(repository.Root, key);
        Assert.False(readable.IsUnreadable);
        WriteFile(repository, key, ModelEntry(SECOND), RepeatedProperty(place));

        var diagnostics = GeneratedModelFiles.Write(readable, [GeneratedModelAnswer.OfModel(Model(THIRD))], CancellationToken.None);

        Assert.Contains("uplicate", Assert.Single(diagnostics), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Invalid_entry_is_absent_and_replaced_alone()
    {
        using var repository = new Repository();
        var key = PackageKey();
        // SECOND's model describes another member; FIRST and THIRD hold. A refusal of a word no file keeps is invalid too.
        WriteFile(repository, key, ModelEntry(FIRST), Raw(SECOND, "model", ModelJson(THIRD)), ModelEntry(THIRD),
                  Raw(FOURTH, "refusal", """{"reason":"budget","detail":"","causes":[]}"""));

        var file = GeneratedModelFiles.Read(repository.Root, key);

        Assert.False(file.IsUnreadable);
        Assert.Equal([FIRST, THIRD], file.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, file.Diagnostics.Count);
        Assert.Contains(file.Diagnostics, diagnostic => diagnostic.Contains(SECOND, StringComparison.Ordinal));
        var before = StoredEntries(repository, key);

        Assert.Empty(GeneratedModelFiles.Write(file, [GeneratedModelAnswer.OfModel(Model(SECOND))], CancellationToken.None));

        // The member answered is a valid entry now; the others, valid and invalid, stand as they were.
        var after = StoredEntries(repository, key);
        Assert.Equal([FIRST, FOURTH, SECOND, THIRD], after.Keys.Order(StringComparer.Ordinal));
        var again = GeneratedModelFiles.Read(repository.Root, key);
        Assert.Equal([FIRST, SECOND, THIRD], again.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(FOURTH, Assert.Single(again.Diagnostics), StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(before[FIRST], after[FIRST]));
        Assert.True(JsonNode.DeepEquals(before[THIRD], after[THIRD]));
        Assert.True(JsonNode.DeepEquals(before[FOURTH], after[FOURTH]));
        Assert.False(JsonNode.DeepEquals(before[SECOND], after[SECOND]));
    }

    [Fact]
    public void Member_named_twice_is_replaced_by_one_entry()
    {
        using var repository = new Repository();
        var key = PackageKey();
        WriteFile(repository, key, ModelEntry(FIRST), ModelEntry(SECOND),
                  Raw(SECOND, "refusal", """{"reason":"accessor","detail":"a property","causes":[]}"""));

        var file = GeneratedModelFiles.Read(repository.Root, key);

        Assert.Equal([FIRST], file.Entries.Keys);
        Assert.Equal(2, file.Diagnostics.Count(diagnostic => diagnostic.Contains(SECOND, StringComparison.Ordinal)));

        Assert.Empty(GeneratedModelFiles.Write(file, [GeneratedModelAnswer.OfModel(Model(SECOND))], CancellationToken.None));

        Assert.Equal([FIRST, SECOND], StoredMembers(repository, key));
        var again = GeneratedModelFiles.Read(repository.Root, key);
        Assert.Empty(again.Diagnostics);
        Assert.Equal([FIRST, SECOND], again.Entries.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Cancelled_writer_stops_waiting_for_the_lock()
    {
        using var repository = new Repository();
        var key = PackageKey();
        Write(repository, key, GeneratedModelAnswer.OfModel(Model(FIRST)));
        var holding = GeneratedModelFiles.Read(repository.Root, key);
        var waiting = GeneratedModelFiles.Read(repository.Root, key);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => GeneratedModelFiles.Write(holding, [GeneratedModelAnswer.OfModel(Model(SECOND))], CancellationToken.None, () =>
        {
            entered.Set();
            release.Wait(PATIENCE);
        }));
        Assert.True(entered.Wait(PATIENCE));
        using var cancellation = new CancellationTokenSource();
        var second = Task.Run(() => GeneratedModelFiles.Write(waiting, [GeneratedModelAnswer.OfModel(Model(THIRD))], cancellation.Token));
        Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(500))));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(PATIENCE));
        Assert.False(first.IsCompleted);
        release.Set();
        Assert.Empty(await first.WaitAsync(PATIENCE));
        Assert.Equal([FIRST, SECOND], StoredMembers(repository, key));
    }

    [Fact]
    public void Abandoned_lock_is_taken_and_the_file_keeps_its_members()
    {
        using var repository = new Repository();
        var key = PackageKey();
        Write(repository, key, GeneratedModelAnswer.OfModel(Model(FIRST)));
        var file = GeneratedModelFiles.Read(repository.Root, key);
        var name = GeneratedModelFiles.MutexName(file.FullPath!);

        // The setup does abandon a mutex: the next one to take it is told so.
        using (Abandon(name + ".probe"))
        {
            using var probe = new Mutex(false, name + ".probe");
            Assert.Throws<AbandonedMutexException>(() => probe.WaitOne(PATIENCE));
            probe.ReleaseMutex();
        }

        using (Abandon(name))
            Assert.Empty(GeneratedModelFiles.Write(file, [GeneratedModelAnswer.OfModel(Model(SECOND))], CancellationToken.None));

        Assert.Equal([FIRST, SECOND], StoredMembers(repository, key));
        Assert.Empty(GeneratedModelFiles.Read(repository.Root, key).Diagnostics);

        static Mutex Abandon(string mutexName)
        {
            Mutex? held = null;
            var thread = new Thread(() =>
            {
                held = new Mutex(false, mutexName);
                held.WaitOne();
            });
            thread.Start();
            thread.Join();
            return held!;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transient_write_failure_retries_the_same_temporary_file(bool existing)
    {
        using var repository = new Repository();
        var key = PackageKey();
        if (existing)
            Write(repository, key, GeneratedModelAnswer.OfModel(Model(FIRST)));
        var file = GeneratedModelFiles.Read(repository.Root, key);
        var attempts = 0;
        var temporaries = new HashSet<string>(StringComparer.Ordinal);

        var diagnostics = GeneratedModelFiles.Write(file, [GeneratedModelAnswer.OfModel(Model(SECOND))], CancellationToken.None,
            replaceFile: (temporary, path) =>
            {
                temporaries.Add(temporary);
                Assert.True(File.Exists(temporary));
                if (++attempts <= 2)
                    throw new IOException("temporarily held by another process");
                if (existing)
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            });

        Assert.Empty(diagnostics);
        Assert.Equal(3, attempts);
        Assert.Single(temporaries);
        Assert.Equal(existing ? new[] { FIRST, SECOND } : new[] { SECOND }, StoredMembers(repository, key));
        Assert.Empty(Directory.GetFiles(repository.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Persistent_write_failure_preserves_the_original_and_removes_the_temporary(bool existing)
    {
        using var repository = new Repository();
        var key = PackageKey();
        if (existing)
            Write(repository, key, GeneratedModelAnswer.OfModel(Model(FIRST)));
        var file = GeneratedModelFiles.Read(repository.Root, key);
        var original = existing ? File.ReadAllBytes(file.FullPath!) : null;
        var attempts = 0;
        var temporaries = new HashSet<string>(StringComparer.Ordinal);

        var diagnostics = GeneratedModelFiles.Write(file, [GeneratedModelAnswer.OfModel(Model(SECOND))], CancellationToken.None,
            replaceFile: (temporary, _) =>
            {
                temporaries.Add(temporary);
                attempts++;
                throw new IOException("still held by another process");
            });

        Assert.Contains("cannot write file", Assert.Single(diagnostics), StringComparison.Ordinal);
        Assert.Equal(5, attempts);
        Assert.Single(temporaries);
        Assert.Equal(existing, File.Exists(file.FullPath));
        if (existing)
            Assert.Equal(original, File.ReadAllBytes(file.FullPath!));
        Assert.Empty(Directory.GetFiles(repository.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Non_IO_write_failure_is_not_retried(bool security)
    {
        using var repository = new Repository();
        var key = PackageKey();
        Write(repository, key, GeneratedModelAnswer.OfModel(Model(FIRST)));
        var file = GeneratedModelFiles.Read(repository.Root, key);
        var original = File.ReadAllBytes(file.FullPath!);
        var attempts = 0;

        var diagnostics = GeneratedModelFiles.Write(file, [GeneratedModelAnswer.OfModel(Model(SECOND))], CancellationToken.None,
            replaceFile: (_, _) =>
            {
                attempts++;
                if (security)
                    throw new System.Security.SecurityException("denied");
                throw new UnauthorizedAccessException("denied");
            });

        Assert.Contains("cannot write file", Assert.Single(diagnostics), StringComparison.Ordinal);
        Assert.Equal(1, attempts);
        Assert.Equal(original, File.ReadAllBytes(file.FullPath!));
        Assert.Empty(Directory.GetFiles(repository.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Missing_concurrency_hunter_folder_is_created()
    {
        using var repository = new Repository();
        var key = GeneratedFileKey.ForFramework("System.Linq", new Version(8, 0, 0, 0), "Microsoft.NETCore.App", 8);
        Assert.False(Directory.Exists(Path.Combine(repository.Root, ".concurrency-hunter")));

        Assert.Empty(Write(repository, key, GeneratedModelAnswer.OfModel(Model(RUN, "System.Linq"))));

        Assert.True(File.Exists(Path.Combine(repository.Root, ".concurrency-hunter", "models", "generated", "Microsoft.NETCore.App", "8.0", "System.Linq.json")));

        // A repository with .concurrency-hunter but no models folder, and one with models but no generated folder.
        using var second = new Repository();
        Directory.CreateDirectory(Path.Combine(second.Root, ".concurrency-hunter"));
        Assert.Empty(Write(second, key, GeneratedModelAnswer.OfModel(Model(RUN, "System.Linq"))));
        Assert.Single(StoredMembers(second, key));
        using var third = new Repository();
        Directory.CreateDirectory(Path.Combine(third.Root, ".concurrency-hunter", "models"));
        Assert.Empty(Write(third, key, GeneratedModelAnswer.OfModel(Model(RUN, "System.Linq"))));
        Assert.Single(StoredMembers(third, key));
    }

    [Fact]
    public void Added_entry_keeps_the_others()
    {
        using var repository = new Repository();
        var key = PackageKey();
        Write(repository, key, GeneratedModelAnswer.OfModel(Model(FIRST)),
              GeneratedModelAnswer.OfRefusal(SECOND, new GeneratedRefusal(ModelReasons.INCOMPLETE, "a field was not seeded", ["unseeded-read"])));
        var before = StoredEntries(repository, key);

        Write(repository, key, GeneratedModelAnswer.OfModel(Model(THIRD)));

        var after = StoredEntries(repository, key);
        Assert.Equal([FIRST, SECOND, THIRD], after.Keys.Order(StringComparer.Ordinal));
        Assert.True(JsonNode.DeepEquals(before[FIRST], after[FIRST]));
        Assert.True(JsonNode.DeepEquals(before[SECOND], after[SECOND]));
    }

    [Fact]
    public void No_file_or_entry_is_deleted()
    {
        using var repository = new Repository();
        var polly = PackageKey();
        var linq = GeneratedFileKey.ForFramework("System.Linq", new Version(8, 0, 0, 0), "Microsoft.NETCore.App", 8);
        Write(repository, polly, GeneratedModelAnswer.OfModel(Model(FIRST)));
        Write(repository, linq, GeneratedModelAnswer.OfModel(Model(RUN, "System.Linq")));
        // An entry the run cannot use stays: this one is invalid, and no answer names it.
        var path = polly.PathUnder(repository.Root)!;
        var text = File.ReadAllText(path).Replace("\"members\": [", "\"members\": [\n    {\n      \"member\": \"M:Lib.Api.Gone(System.Object)\",\n" +
                                                    "      \"refusal\": { \"reason\": \"failed\", \"detail\": \"\", \"causes\": [] }\n    },");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        var files = Directory.GetFiles(repository.Root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var before = StoredEntries(repository, polly);

        // Answers that a file does not keep, and an answer for another file, delete nothing.
        Write(repository, polly, GeneratedModelAnswer.OfRefusal(SECOND, new GeneratedRefusal(GenerationOutcome.BUDGET, "", [])),
              GeneratedModelAnswer.OfRefusal(THIRD, new GeneratedRefusal(GenerationReasons.CORELIB, "", [])),
              GeneratedModelAnswer.OfRefusal(RUN, new GeneratedRefusal(GenerationOutcome.FAILED, "", [])));
        Write(repository, polly, GeneratedModelAnswer.OfModel(Model(THIRD)));
        Write(repository, linq, GeneratedModelAnswer.OfModel(Model(FIRST, "System.Linq")));

        Assert.Equal(files.Length, Directory.GetFiles(repository.Root, "*.json", SearchOption.AllDirectories).Length);
        var after = StoredEntries(repository, polly);
        Assert.All(before.Keys, member => Assert.Contains(member, after.Keys));
        Assert.True(JsonNode.DeepEquals(before["M:Lib.Api.Gone(System.Object)"], after["M:Lib.Api.Gone(System.Object)"]));
        Assert.Equal([RUN, FIRST], StoredEntries(repository, linq).Keys.OrderByDescending(member => member, StringComparer.Ordinal));
        Assert.DoesNotContain(SECOND, after.Keys);
    }

    [Fact]
    public async Task Overlapping_writers_keep_both_members()
    {
        using var repository = new Repository();
        var key = PackageKey();
        Write(repository, key, GeneratedModelAnswer.OfModel(Model(FIRST)));
        var holding = GeneratedModelFiles.Read(repository.Root, key);
        var waiting = GeneratedModelFiles.Read(repository.Root, key);
        var events = new List<string>();
        void Log(string text)
        {
            lock (events)
                events.Add(text);
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => GeneratedModelFiles.Write(holding, [GeneratedModelAnswer.OfModel(Model(SECOND))], CancellationToken.None, () =>
        {
            Log("first holds");
            entered.Set();
            release.Wait(PATIENCE);
        }));
        Assert.True(entered.Wait(PATIENCE));
        var second = Task.Run(() => GeneratedModelFiles.Write(waiting, [GeneratedModelAnswer.OfModel(Model(THIRD))], CancellationToken.None,
                                                              () => Log("second holds")));

        Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(500))));
        // Only a named mutex, which other processes see too, can be found by name.
        Assert.True(Mutex.TryOpenExisting(GeneratedModelFiles.MutexName(holding.FullPath!), out var opened));
        opened!.Dispose();

        release.Set();
        Assert.Empty(await first.WaitAsync(PATIENCE));
        Assert.Empty(await second.WaitAsync(PATIENCE));

        Assert.Equal(["first holds", "second holds"], events);
        Assert.Equal([FIRST, SECOND, THIRD], StoredMembers(repository, key));
    }

    [Fact]
    public void Equivalent_path_spellings_take_one_mutex()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var names = new[] { @"C:\x\y\..\y\f.json", "c:/x/y/f.json", @"C:\X\Y\F.JSON" }.Select(GeneratedModelFiles.MutexName).Distinct().ToArray();

        Assert.Single(names);
        Assert.NotEqual(names[0], GeneratedModelFiles.MutexName(@"C:\x\y\g.json"));
        Assert.NotEqual(names[0], GeneratedModelFiles.MutexName(@"D:\x\y\f.json"));
    }

    [Fact]
    public void Writer_keeps_an_entry_another_writer_added_after_it_read()
    {
        using var repository = new Repository();
        var key = PackageKey();
        Write(repository, key, GeneratedModelAnswer.OfModel(Model(FIRST)));
        var stale = GeneratedModelFiles.Read(repository.Root, key);

        Write(repository, key, GeneratedModelAnswer.OfModel(Model(SECOND)));

        Assert.DoesNotContain(SECOND, stale.Entries.Keys);
        Assert.Empty(GeneratedModelFiles.Write(stale, [GeneratedModelAnswer.OfModel(Model(THIRD))], CancellationToken.None));
        Assert.Equal([FIRST, SECOND, THIRD], StoredMembers(repository, key));
    }

    [Theory]
    [MemberData(nameof(Words))]
    public void Keeps_every_outcome_as_R3_says(string word)
    {
        Assert.True(KEPT.TryGetValue(word, out var kept), $"'{word}' is no word this test lists; decide whether a file keeps it.");

        Assert.Equal(kept, GeneratedModelFiles.Keeps(GenerationOutcome.OfReason(word)));

        // A stored refusal is valid exactly when a file keeps its word.
        using var repository = new Repository();
        var key = PackageKey();
        WriteFile(repository, key, Raw(FIRST, "refusal", new JsonObject { ["reason"] = word, ["detail"] = "", ["causes"] = new JsonArray() }.ToJsonString()));
        var file = GeneratedModelFiles.Read(repository.Root, key);
        Assert.Equal(kept, file.Entries.ContainsKey(FIRST));
        Assert.Equal(kept ? 0 : 1, file.Diagnostics.Count);
    }

    [Fact]
    public void Keeps_a_model_and_a_timeout_and_neither_a_word_nor_a_state_of_the_run()
    {
        Assert.True(GeneratedModelFiles.Keeps(GenerationOutcome.Model));
        Assert.True(GeneratedModelFiles.Keeps(GenerationOutcome.Timeout));
        Assert.False(GeneratedModelFiles.Keeps(GenerationOutcome.Budget));
        Assert.False(GeneratedModelFiles.Keeps(GenerationOutcome.Cancelled));
        Assert.False(GeneratedModelFiles.Keeps(GenerationOutcome.Failed));
        Assert.False(GeneratedModelFiles.Keeps(GenerationOutcome.OfReason("a-word-nobody-knows")));
    }

    /// <summary>Every word of the generator's reasons and every word R3 names.</summary>
    public static IEnumerable<object[]> Words() =>
        GenerationReasons.Ordered.Concat(ModelReasons.Ordered)
                         .Concat([GenerationOutcome.TIMEOUT, GenerationOutcome.BUDGET, GenerationOutcome.CANCELLED, GenerationOutcome.FAILED,
                                  GenerationReasons.UNNAMED_MEMBER])
                         .Distinct(StringComparer.Ordinal)
                         .Select(word => new object[] { word });

    private static GeneratedFileKey PackageKey(Guid? mvid = null) =>
        GeneratedFileKey.ForPackage("Polly", new Version(7, 0, 0, 0), "Polly", "7.2.3", mvid ?? MVID);

    private static LibraryModel Model(string member, string assembly = "Polly") =>
        new(member, [new SupportedAssemblyVersion(assembly, new Version(7, 0, 0, 0), new Version(7, 0, 0, 1))], [LibraryEffect.DeepReadOf("value")])
        {
            Result = LibraryResult.Parse("[arg:value]")
        };

    private static IReadOnlyList<string> Write(Repository repository, GeneratedFileKey key, params GeneratedModelAnswer[] answers) =>
        GeneratedModelFiles.Write(GeneratedModelFiles.Read(repository.Root, key), answers, CancellationToken.None);

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToArray();

    private static IReadOnlyDictionary<string, JsonNode> StoredEntries(Repository repository, GeneratedFileKey key)
    {
        var root = JsonNode.Parse(File.ReadAllBytes(key.PathUnder(repository.Root)!))!;
        return root["members"]!.AsArray().ToDictionary(entry => entry!["member"]!.GetValue<string>(), entry => entry!, StringComparer.Ordinal);
    }

    /// <summary>The members of the file in the order it lists them.</summary>
    /// <param name="repository">The repository the file is in.</param>
    /// <param name="key">The key whose file is read.</param>
    private static string[] StoredMembers(Repository repository, GeneratedFileKey key)
    {
        var root = JsonNode.Parse(File.ReadAllBytes(key.PathUnder(repository.Root)!))!;
        return root["members"]!.AsArray().Select(entry => entry!["member"]!.GetValue<string>()).ToArray();
    }

    private static string ModelJson(string member) => ModelEntryWriter.Write(Model(member)).ToJsonString();

    private static string ModelEntry(string member) => Raw(member, "model", ModelJson(member));

    /// <summary>An entry of <see cref="FIRST"/> in which one property is named twice, in the place the argument names.</summary>
    /// <param name="place">Where the property is repeated: the entry, a refusal's reason or causes, a model's member or its versions.</param>
    private static string RepeatedProperty(string place) => place switch
    {
        "entry" => "{\"member\":\"" + FIRST + "\",\"member\":\"" + FIRST + "\",\"refusal\":{\"reason\":\"accessor\",\"detail\":\"\",\"causes\":[]}}",
        "refusal reason" => Raw(FIRST, "refusal", """{"reason":"accessor","reason":"accessor","detail":"","causes":[]}"""),
        "refusal causes" => Raw(FIRST, "refusal", """{"reason":"accessor","detail":"","causes":[],"causes":[]}"""),
        "model" => Raw(FIRST, "model", ModelJson(FIRST)[..^1] + $",\"member\":\"{FIRST}\"}}"),
        "model versions" => Raw(FIRST, "model", ModelJson(FIRST).Replace("\"minimum\":\"7.0.0.0\"", "\"minimum\":\"7.0.0.0\",\"minimum\":\"7.0.0.0\"", StringComparison.Ordinal)),
        _ => throw new ArgumentOutOfRangeException(nameof(place), place, "Not a place of the file.")
    };

    private static string Raw(string member, string kind, string json) => $"{{\"member\":{JsonSerializer.Serialize(member)},\"{kind}\":{json}}}";

    /// <summary>Writes a file with the key's header and the entries as given, whatever they hold.</summary>
    /// <param name="repository">The repository the file is written in.</param>
    /// <param name="key">The key whose file is written.</param>
    /// <param name="entries">The entries, each a JSON object.</param>
    private static void WriteFile(Repository repository, GeneratedFileKey key, params string[] entries)
    {
        var path = key.PathUnder(repository.Root)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var header = $"{{\"name\":\"{key.AssemblyName}\",\"version\":\"{key.AssemblyVersion}\",\"mvid\":\"{key.Mvid:D}\",\"package\":\"{key.Package}\"," +
                     $"\"packageVersion\":\"{key.PackageVersion}\"}}";
        File.WriteAllText(path, $"{{\"schemaVersion\":2,\"generator\":\"{GeneratorVersion.Fingerprint}\",\"assembly\":{header},\"members\":[{string.Join(',', entries)}]}}", new UTF8Encoding(false));
    }

    private static LibraryModel Rebuilt(ProjectModelEntry entry)
    {
        var version = entry.Versions ?? (new Version(0, 0, 0, 0), new Version(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue));
        return new LibraryModel(entry.Member,
                                entry.Assemblies.Select(assembly => new SupportedAssemblyVersion(assembly, version.Minimum, version.Maximum)).ToArray(),
                                entry.Effects, Receiver: entry.Receiver)
        {
            Result = entry.Result,
            Fates = entry.Fates,
            Stores = entry.Stores,
            Outputs = entry.Outputs,
            Keeps = entry.Keeps,
            Calls = entry.Calls
        };
    }

    private sealed class Repository : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ch-generated-").FullName;

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
