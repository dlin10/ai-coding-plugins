using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ISymbol = Microsoft.CodeAnalysis.ISymbol;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using DecompilerLanguageVersion = ICSharpCode.Decompiler.CSharp.LanguageVersion;
using RoslynLanguageVersion = Microsoft.CodeAnalysis.CSharp.LanguageVersion;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>A decompiled library compiled as its own library, or the reason it cannot be used.</summary>
/// <param name="Compilation">The compilation, its failing bodies made <c>extern</c>; <c>null</c> when the library is unusable.</param>
/// <param name="Reason"><see cref="GenerationReasons.LIBRARY_DOES_NOT_COMPILE"/>, or <c>null</c>.</param>
/// <param name="Bodies">The method bodies of the decompiled library.</param>
/// <param name="ExternBodies">The bodies the rewrite removed.</param>
/// <param name="ExternMembers">The documentation ids of the members the rewrite made <c>extern</c>, accessors and constructors
/// declared in place of implicit ones included.</param>
/// <param name="OpenedFields">The sorted documentation ids of fields and auto-properties the generator's library copy opened.</param>
/// <param name="Errors">The codes of the errors that made the library unusable, or the types the decompiler threw on as
/// <c>decompiler: &lt;type&gt;: …</c>; empty when it is usable.</param>
public sealed record LibraryCompilationResult(CSharpCompilation? Compilation, string? Reason, int Bodies, int ExternBodies,
                                              IReadOnlySet<string> ExternMembers, IReadOnlySet<string> OpenedFields,
                                              IReadOnlyList<string> Errors)
{
    /// <summary>The documentation ids among <see cref="ExternMembers"/> whose decompiled body is not the code the runtime runs: every
    /// member of <c>Unsafe</c> and every intrinsic's placeholder.</summary>
    public IReadOnlySet<string> ReplacedMembers { get; init; } = new SortedSet<string>(StringComparer.Ordinal);

    internal readonly object RecipeLock = new();
    internal Lazy<Task<LibraryCompilationResult>>? RecipeOpening;
}

/// <summary>A module decompiled in memory: one tree per top-level type and the assembly-attributes tree last, and the top-level types
/// the decompiler threw on.</summary>
/// <param name="Trees">The trees of the types decompiled.</param>
/// <param name="Failures">One entry per type the decompiler threw on, <c>&lt;type&gt;: &lt;exception&gt;: &lt;message&gt;</c>.</param>
public sealed record DecompiledModule(IReadOnlyList<SyntaxTree> Trees, IReadOnlyList<string> Failures)
{
    /// <summary>The names of the assemblies the decompiler's type system held, the module's own among them: a simple type name it wrote
    /// names the one type of that name it saw there.</summary>
    public IReadOnlySet<string> DecompilerAssemblies { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What a test hooks into one library compilation to hold it at a known place; <c>null</c> in production.</summary>
internal sealed class LibraryCompilationProbe
{
    /// <summary>Told the full name of each type a worker starts, before its cancellation check.</summary>
    public Action<string>? TypeStarting { get; init; }

    /// <summary>Told the full name of each type after its output pass, on the worker that printed it.</summary>
    public Action<string>? TypePrinted { get; init; }

    /// <summary>The degree of parallelism of the decompilation, or <c>null</c> for the default.</summary>
    public int? Workers { get; init; }

    /// <summary>Called on the compilation's own worker before it decompiles anything.</summary>
    public Action? Started { get; init; }

    /// <summary>Called by a caller that has found its compilation, its own or another's, just before it waits for it.</summary>
    public Action? Waiting { get; init; }
}

/// <summary>Decompiles an implementation assembly in memory as the whole-project decompiler does and compiles it against its
/// references (SPEC TD-034b). A body that does not compile has no body: its member is rewritten as <c>extern</c>, which the engine
/// already reads as a call without a body. One compilation per implementation path, reference set and additional extern selection per process.</summary>
public static class LibraryCompilation
{
    private const int REWRITE_ROUNDS = 3;

    /// <summary>The type whose decompiled bodies are not the code that runs: CoreLib's are placeholders the runtime replaces, which
    /// throw <c>PlatformNotSupportedException</c>, and IL that C# cannot write (<c>ldobj</c>, <c>stobj</c>) the decompiler renders
    /// as calls to the member itself.</summary>
    private const string UNSAFE = "T:System.Runtime.CompilerServices.Unsafe";

    /// <summary>Declaration-level codes measured as decompiler artefacts that bind nothing wrongly: a field-like event beside its
    /// backing field, <c>==</c> without the <c>!=</c> a trimmer removed, module attributes.</summary>
    internal static readonly HashSet<string> BenignDeclarationErrors = new(["CS0102", "CS0216", "CS8335"], StringComparer.Ordinal);

    /// <summary>Flow-analysis errors the compiler reports on a member's declaration rather than in its body.</summary>
    private static readonly HashSet<string> FlowErrors = new(["CS0161", "CS0171", "CS0177", "CS0843"], StringComparer.Ordinal);

    private static readonly CSharpParseOptions ParseOptions = new(RoslynLanguageVersion.Preview);

    private static readonly ConcurrentDictionary<string, Lazy<Task<LibraryCompilationResult>>> Compilations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The compilation of an implementation assembly, built once per implementation path, shared framework directory and
    /// dependency paths.</summary>
    /// <param name="assembly">The implementation assembly, not a reference assembly.</param>
    /// <param name="cancellationToken">Cancels the decompilation and the compilation.</param>
    // bounded: overload forwarding terminates at the implementation overload.
    public static LibraryCompilationResult Compile(ImplementationAssembly assembly, CancellationToken cancellationToken) =>
        Compile(assembly, new HashSet<string>(StringComparer.Ordinal), cancellationToken);

    /// <summary>Compiles an implementation assembly with the selected members made extern as well as failing bodies.</summary>
    /// <param name="assembly">The implementation assembly.</param>
    /// <param name="selectedMembers">Documentation ids of whole members to make extern.</param>
    /// <param name="cancellationToken">Cancels the decompilation and compilation.</param>
    // bounded: overload forwarding terminates at the implementation overload.
    internal static LibraryCompilationResult Compile(ImplementationAssembly assembly, IReadOnlySet<string> selectedMembers,
                                                     CancellationToken cancellationToken) =>
        Compile(assembly, selectedMembers, new GenerationChecks(cancellationToken));

    /// <summary>The compilation of an implementation assembly as a stage of a generation.</summary>
    /// <param name="assembly">The implementation assembly, not a reference assembly.</param>
    /// <param name="checks">The generation's checks, which cancel the decompilation, the rewrite rounds, the compilation and the wait for
    /// one another caller started.</param>
    // bounded: overload forwarding terminates at the implementation overload.
    internal static LibraryCompilationResult Compile(ImplementationAssembly assembly, GenerationChecks checks) =>
        Compile(assembly, new HashSet<string>(StringComparer.Ordinal), checks);

    /// <summary>Compiles an implementation assembly with the selected members made extern as well as failing bodies. A caller that finds
    /// the compilation under way, started by another caller under that caller's token, waits for it with its own: when its own token
    /// fires it stops waiting and the compilation goes on. A compilation cancelled by its starter leaves the cache, and a caller whose
    /// own token has not fired starts it again.</summary>
    /// <param name="assembly">The implementation assembly.</param>
    /// <param name="selectedMembers">Documentation ids of whole members to make extern.</param>
    /// <param name="checks">The generation's checks, which cancel the decompilation, the rewrite rounds, the compilation and the wait.</param>
    /// <param name="probe">Test hooks, or <c>null</c>.</param>
    internal static LibraryCompilationResult Compile(ImplementationAssembly assembly, IReadOnlySet<string> selectedMembers, GenerationChecks checks,
                                                     LibraryCompilationProbe? probe = null)
    {
        checks.Check(GenerationStage.CompileLibrary);
        var dependencies = assembly.References.Where(reference => !string.Equals(Path.GetDirectoryName(reference),
                                                                                Path.TrimEndingDirectorySeparator(assembly.SharedFrameworkDirectory),
                                                                                StringComparison.OrdinalIgnoreCase));
        var selection = selectedMembers.ToHashSet(StringComparer.Ordinal);
        // Paths compare ignoring case; hex-encoded member ids retain their case in that cache.
        var key = string.Join("\n", new[] { assembly.Path, assembly.SharedFrameworkDirectory }.Concat(dependencies)) +
                  "\nextern:\n" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", selection.Order(StringComparer.Ordinal))));
        while (true)
        {
            checks.Check(GenerationStage.CompileLibrary);
            // The value overload: the factory overload may run for several callers, and only the caller whose instance the cache holds
            // owns the entry it records.
            var created = Started(key, assembly, selection, checks, probe);
            var entry = Compilations.GetOrAdd(key, created);
            if (ReferenceEquals(entry, created))
                CompilationOwnership.Record(key, entry);
            var compiling = entry.Value;
            probe?.Waiting?.Invoke();
            try
            {
                return compiling.WaitAsync(checks.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!checks.Token.IsCancellationRequested && WasCancelled(compiling))
            {
                Compilations.TryRemove(KeyValuePair.Create(key, entry));
            }
        }
    }

    /// <summary>The recipe-opened copy owned by this compilation result, opened lazily on its own worker. A waiter cancels
    /// only its wait; a cancelled opener releases the slot so a live waiter can try again.</summary>
    /// <param name="library">The ordinary compiled library.</param>
    /// <param name="checks">The attempt's cancellation checks.</param>
    /// <param name="probe">Test hooks, or null.</param>
    internal static LibraryCompilationResult RecipeCopy(LibraryCompilationResult library, GenerationChecks checks,
                                                         LibraryCompilationProbe? probe = null)
    {
        while (true)
        {
            checks.Check(GenerationStage.CompileLibrary);
            Lazy<Task<LibraryCompilationResult>> entry;
            lock (library.RecipeLock)
                entry = library.RecipeOpening ??= Started(library, checks, probe);
            var opening = entry.Value;
            probe?.Waiting?.Invoke();
            try
            {
                return opening.WaitAsync(checks.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!checks.Token.IsCancellationRequested && WasCancelled(opening))
            {
                lock (library.RecipeLock)
                {
                    if (ReferenceEquals(library.RecipeOpening, entry))
                        library.RecipeOpening = null;
                }
            }
        }
    }

    private static Lazy<Task<LibraryCompilationResult>> Started(LibraryCompilationResult library, GenerationChecks checks,
                                                                 LibraryCompilationProbe? probe)
    {
        Lazy<Task<LibraryCompilationResult>>? entry = null;
        entry = new Lazy<Task<LibraryCompilationResult>>(() =>
        {
            var opening = Task.Run(() =>
            {
                using var work = WorkCancellation.Enter(checks.Token);
                probe?.Started?.Invoke();
                checks.Check(GenerationStage.CompileLibrary);
                var opened = LibraryFieldOpening.OpenRecipe(library.Compilation!, checks.Token);
                checks.Check(GenerationStage.CompileLibrary);
                return library with { Compilation = opened.Compilation, OpenedFields = opened.OpenedFields };
            });
            opening.ContinueWith(finished =>
            {
                if (!WasCancelled(finished))
                    return;
                lock (library.RecipeLock)
                {
                    if (ReferenceEquals(library.RecipeOpening, entry))
                        library.RecipeOpening = null;
                }
            }, TaskScheduler.Default);
            return opening;
        });
        return entry;
    }

    /// <summary>Removes the entries a run created from the cache (R12), each only while its key still maps to the instance the run added:
    /// an entry that left the cache and was created again by another caller stays.</summary>
    /// <param name="created">The entries the run's <see cref="CompilationOwnership"/> recorded.</param>
    // cleanup: удаляет записи кеша, созданные раном, и после отмены
    internal static void Release(IEnumerable<OwnedCompilation> created)
    {
        foreach (var (key, entry) in created)
            Compilations.TryRemove(KeyValuePair.Create(key, entry));
    }

    /// <summary>A cache entry whose compilation starts on first use, on a worker of its own, so that a caller waiting for it can stop
    /// waiting. The entry leaves the cache when the compilation is cancelled, whoever is still waiting.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="assembly">The implementation assembly.</param>
    /// <param name="selection">Documentation ids of whole members to make extern.</param>
    /// <param name="checks">The starter's checks, which govern the compilation.</param>
    /// <param name="probe">Test hooks, or <c>null</c>.</param>
    private static Lazy<Task<LibraryCompilationResult>> Started(string key, ImplementationAssembly assembly, IReadOnlySet<string> selection,
                                                                GenerationChecks checks, LibraryCompilationProbe? probe)
    {
        Lazy<Task<LibraryCompilationResult>>? entry = null;
        entry = new Lazy<Task<LibraryCompilationResult>>(() =>
        {
            var compiling = Task.Run(() =>
            {
                probe?.Started?.Invoke();
                return CompileModule(assembly.AssemblyName, Decompile(assembly, checks, probe),
                                     assembly.References.Select(reference => MetadataReference.CreateFromFile(reference)).ToArray(), checks, selection);
            });
            compiling.ContinueWith(finished =>
            {
                if (WasCancelled(finished))
                    Compilations.TryRemove(KeyValuePair.Create(key, entry!));
            }, TaskScheduler.Default);
            return compiling;
        });
        return entry;
    }

    /// <summary>Whether a task ended cancelled, or failed with nothing but cancellations; one still running has not.</summary>
    /// <param name="task">The task.</param>
    private static bool WasCancelled(Task task) =>
        task.IsCanceled || task.IsFaulted && task.Exception!.InnerExceptions.All(error => error is OperationCanceledException);

    /// <summary>Decompiles the whole module in memory: one syntax tree per top-level type the whole-project decompiler includes,
    /// with its <c>EscapeInvalidIdentifiers</c> and <c>RemoveCLSCompliantAttribute</c> transforms, and the assembly-attributes
    /// tree last. A type the decompiler throws on has no tree and is named in <see cref="DecompiledModule.Failures"/>; it is never
    /// dropped silently.</summary>
    /// <param name="assembly">The implementation assembly.</param>
    /// <param name="cancellationToken">Cancels the decompilation.</param>
    // bounded: overload forwarding terminates at the implementation overload.
    internal static DecompiledModule Decompile(ImplementationAssembly assembly, CancellationToken cancellationToken) =>
        Decompile(assembly, new GenerationChecks(cancellationToken));

    /// <summary>Decompiles the whole module in memory (<see cref="Decompile(ImplementationAssembly, CancellationToken)"/>) as a stage of
    /// a generation. Each worker checks at the start of its type and before its output pass, and a cancelled decompilation throws
    /// <see cref="OperationCanceledException"/>, never an <see cref="AggregateException"/> of them; the output a worker is printing when
    /// the token fires runs to its end, at most one type per worker.</summary>
    /// <param name="assembly">The implementation assembly.</param>
    /// <param name="checks">The generation's checks, which cancel the decompilation.</param>
    /// <param name="probe">Test hooks, or <c>null</c>.</param>
    internal static DecompiledModule Decompile(ImplementationAssembly assembly, GenerationChecks checks, LibraryCompilationProbe? probe = null)
    {
        var cancellationToken = checks.Token;
        using var resolver = new ReferenceResolver(assembly.References);
        using var module = new PEFile(assembly.Path, PEStreamOptions.PrefetchEntireImage);
        var settings = new DecompilerSettings(DecompilerLanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false };
        var project = new ProjectDecompiler(settings, resolver);
        var typeSystem = new DecompilerTypeSystem(module, resolver, settings);
        var metadata = module.Metadata;
        var types = metadata.TypeDefinitions.Where(handle => metadata.GetTypeDefinition(handle).GetDeclaringType().IsNil &&
                                                             project.Includes(module, handle))
                            .ToArray();
        var trees = new SyntaxTree?[types.Length + 1];
        var failures = new string?[types.Length];
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        if (probe?.Workers is { } workers)
            options.MaxDegreeOfParallelism = workers;
        Parallel.For(0, types.Length, options, index =>
        {
            var definition = metadata.GetTypeDefinition(types[index]);
            var fullName = metadata.GetString(definition.Namespace) is { Length: > 0 } ns
                ? ns + "." + metadata.GetString(definition.Name)
                : metadata.GetString(definition.Name);
            probe?.TypeStarting?.Invoke(fullName);
            try
            {
                checks.Check(GenerationStage.CompileLibrary);
                var name = string.Concat(fullName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var decompiler = project.Create(typeSystem);
                decompiler.CancellationToken = cancellationToken;
                trees[index] = Parse(decompiler.DecompileTypes([types[index]]), settings, $"{assembly.AssemblyName}/{name}.cs", checks);
                probe?.TypePrinted?.Invoke(fullName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The cancellation is thrown once, after the workers stop, so that no worker's exception reaches the caller wrapped.
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures[index] = $"{fullName}: {error.GetType().Name}: {error.Message}";
            }
        });
        WorkCancellation.Check();
        cancellationToken.ThrowIfCancellationRequested();

        var attributes = project.Create(typeSystem);
        attributes.CancellationToken = cancellationToken;
        attributes.AstTransforms.Add(new RemoveCompilerGeneratedAssemblyAttributes());
        trees[^1] = Parse(attributes.DecompileModuleAndAssemblyAttributes(), settings, $"{assembly.AssemblyName}/AssemblyInfo.cs", checks);
        return new DecompiledModule(trees.OfType<SyntaxTree>().ToArray(), failures.OfType<string>().ToArray())
        {
            DecompilerAssemblies = typeSystem.Modules.Select(loaded => loaded.AssemblyName).ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>Compiles a decompiled module, or refuses it whole when the decompiler threw on one of its types: a missing type
    /// would hide declarations and bodies the analysis then never sees, and only what compiled is believed.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="module">The decompiled module.</param>
    /// <param name="references">The references of the platform version.</param>
    /// <param name="cancellationToken">Cancels the compilation.</param>
    /// <param name="selectedMembers">Additional whole-member documentation ids to make extern.</param>
    /// <param name="repairArtefacts">Whether the decompiler's artefacts are repaired (<see cref="Repaired"/>); <c>false</c> only in a test
    /// that measures what the repair changes.</param>
    // bounded: overload forwarding terminates at the implementation overload.
    internal static LibraryCompilationResult CompileModule(string assemblyName, DecompiledModule module, IReadOnlyList<MetadataReference> references,
                                                           CancellationToken cancellationToken, IReadOnlySet<string>? selectedMembers = null,
                                                           bool repairArtefacts = true) =>
        CompileModule(assemblyName, module, references, new GenerationChecks(cancellationToken), selectedMembers, repairArtefacts);

    /// <summary>Compiles a decompiled module as a stage of a generation (<see cref="CompileModule(string, DecompiledModule, IReadOnlyList{MetadataReference}, CancellationToken, IReadOnlySet{string}?, bool)"/>).</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="module">The decompiled module.</param>
    /// <param name="references">The references of the platform version.</param>
    /// <param name="checks">The generation's checks, which cancel the compilation.</param>
    /// <param name="selectedMembers">Additional whole-member documentation ids to make extern.</param>
    /// <param name="repairArtefacts">Whether the decompiler's artefacts are repaired (<see cref="Repaired"/>).</param>
    internal static LibraryCompilationResult CompileModule(string assemblyName, DecompiledModule module, IReadOnlyList<MetadataReference> references,
                                                           GenerationChecks checks, IReadOnlySet<string>? selectedMembers = null,
                                                           bool repairArtefacts = true)
    {
        if (module.Failures.Count > 0)
        {
            return new LibraryCompilationResult(null, GenerationReasons.LIBRARY_DOES_NOT_COMPILE, 0, 0, new SortedSet<string>(StringComparer.Ordinal),
                                                new SortedSet<string>(StringComparer.Ordinal),
                                                module.Failures.Select(failure => $"decompiler: {failure}").ToArray());
        }

        return CompileTrees(assemblyName, module.Trees, references, checks, selectedMembers, repairArtefacts: repairArtefacts,
                            decompilerAssemblies: module.DecompilerAssemblies);
    }

    /// <summary>Compiles decompiled trees as a library named as the assembly, unsafe code allowed, nullable disabled, makes every
    /// member of a type <see cref="HasNoBodies"/> names and every intrinsic's placeholder (<see cref="IsPlaceholder"/>)
    /// <c>extern</c>, then rewrites every member whose body has an error as
    /// <c>extern</c> and recompiles, at most three times. A declaration-level error other
    /// than the benign ones, a body still in error after the last rewrite, or an error the rewrite leaves makes the library
    /// unusable.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="trees">The decompiled trees.</param>
    /// <param name="references">The references of the platform version.</param>
    /// <param name="cancellationToken">Cancels the compilation.</param>
    /// <param name="selectedMembers">Additional whole-member documentation ids to make extern.</param>
    /// <param name="openFields">Whether to open seed and path fields for the model driver.</param>
    // bounded: overload forwarding terminates at the implementation overload.
    internal static LibraryCompilationResult CompileTrees(string assemblyName, IReadOnlyList<SyntaxTree> trees,
                                                          IReadOnlyList<MetadataReference> references, CancellationToken cancellationToken,
                                                          IReadOnlySet<string>? selectedMembers = null, bool openFields = true) =>
        CompileTrees(assemblyName, trees, references, new GenerationChecks(cancellationToken), selectedMembers, openFields);

    /// <summary>Compiles decompiled trees as a stage of a generation (<see cref="CompileTrees(string, IReadOnlyList{SyntaxTree}, IReadOnlyList{MetadataReference}, CancellationToken, IReadOnlySet{string}?, bool)"/>):
    /// it checks before each tree it rewrites and before each rewrite round. The decompiler's artefacts are repaired once, before any
    /// body is rewritten, so that a body whose only error was one keeps its body.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="trees">The decompiled trees.</param>
    /// <param name="references">The references of the platform version.</param>
    /// <param name="checks">The generation's checks, which cancel the compilation.</param>
    /// <param name="selectedMembers">Additional whole-member documentation ids to make extern.</param>
    /// <param name="openFields">Whether to open seed and path fields for the model driver.</param>
    /// <param name="repairArtefacts">Whether the decompiler's artefacts are repaired (<see cref="Repaired"/>).</param>
    /// <param name="decompilerAssemblies">The assemblies the decompiler saw (<see cref="DecompiledModule.DecompilerAssemblies"/>), or
    /// <c>null</c> when only the library itself is known to be among them.</param>
    internal static LibraryCompilationResult CompileTrees(string assemblyName, IReadOnlyList<SyntaxTree> trees,
                                                          IReadOnlyList<MetadataReference> references, GenerationChecks checks,
                                                          IReadOnlySet<string>? selectedMembers = null, bool openFields = true,
                                                          bool repairArtefacts = true, IReadOnlySet<string>? decompilerAssemblies = null)
    {
        var cancellationToken = checks.Token;
        var bodies = trees.Sum(tree => tree.GetRoot(cancellationToken).DescendantNodes().Count(IsBody));
        var compilation = CSharpCompilation.Create(assemblyName, trees, references,
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                                                                                nullableContextOptions: NullableContextOptions.Disable));
        var externMembers = new SortedSet<string>(StringComparer.Ordinal);
        var issuedNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var externBodies = 0;
        if (selectedMembers is { Count: > 0 })
        {
            foreach (var tree in compilation.SyntaxTrees.ToArray())
            {
                checks.Check(GenerationStage.CompileLibrary);
                var currentTree = tree;
                var model = compilation.GetSemanticModel(currentTree);
                var root = currentTree.GetRoot(cancellationToken);
                // Each variable in a field-like event declaration is a separate member.
                while (root.DescendantNodes().OfType<EventFieldDeclarationSyntax>()
                           .FirstOrDefault(field => field.Declaration.Variables.Count > 1 && Selected(field, model, selectedMembers, cancellationToken)) is { } field)
                {
                    WorkCancellation.Check();
                    cancellationToken.ThrowIfCancellationRequested();
                    root = root.ReplaceNode(field, field.Declaration.Variables.Select(variable =>
                        field.WithDeclaration(field.Declaration.WithVariables(SingletonSeparatedList(variable)))));
                    var splitTree = CSharpSyntaxTree.ParseText(root.ToFullString(), (CSharpParseOptions)currentTree.Options,
                                                               currentTree.FilePath, cancellationToken: cancellationToken);
                    compilation = compilation.ReplaceSyntaxTree(currentTree, splitTree);
                    currentTree = splitTree;
                    root = currentTree.GetRoot(cancellationToken);
                    model = compilation.GetSemanticModel(currentTree);
                }

                var members = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                                  .Where(member => Selected(member, model, selectedMembers, cancellationToken)).ToHashSet();
                if (members.Count == 0)
                    continue;
                foreach (var field in members.OfType<EventFieldDeclarationSyntax>())
                {
                    WorkCancellation.Check();
                    foreach (var variable in field.Declaration.Variables)
                    {
                        WorkCancellation.Check();
                        externMembers.UnionWith(DeclaredSymbols(model.GetDeclaredSymbol(variable, cancellationToken)));
                    }
                }

                var (rewritten, removed) = Rewrite(root, members, new HashSet<(TypeDeclarationSyntax, bool)>(), model,
                                                   externMembers, issuedNames, cancellationToken);
                externBodies += removed;
                compilation = compilation.ReplaceSyntaxTree(currentTree, CSharpSyntaxTree.ParseText(rewritten.ToFullString(), (CSharpParseOptions)currentTree.Options,
                                                                                                      currentTree.FilePath, cancellationToken: cancellationToken));
            }
        }

        // Unsafe has no body, as in every library that references it from metadata, and neither has an intrinsic's placeholder: a body
        // whose only statement calls the member itself, IL the JIT replaces, which as recursion with no way out returns nothing.
        var replacedMembers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees.ToArray())
        {
            checks.Check(GenerationStage.CompileLibrary);
            var root = tree.GetRoot(cancellationToken);
            var types = root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<TypeDeclarationSyntax>()
                            .Where(type => type.Identifier.ValueText == "Unsafe").ToArray();
            var placeholders = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(method => OnlyCallOfItsName(method) is not null).ToArray();
            if (types.Length == 0 && placeholders.Length == 0)
                continue;
            var model = compilation.GetSemanticModel(tree);
            var members = types.Where(type => model.GetDeclaredSymbol(type, cancellationToken) is { } symbol && HasNoBodies(symbol))
                               .SelectMany(type => type.Members).Where(member => member.DescendantNodesAndSelf().Any(IsBody)).ToHashSet();
            members.UnionWith(placeholders.Where(method => IsPlaceholder(method, model, cancellationToken)));
            if (members.Count == 0)
                continue;
            foreach (var member in members)
            {
                WorkCancellation.Check();
                replacedMembers.UnionWith(DeclaredSymbols(model.GetDeclaredSymbol(member, cancellationToken)));
            }
            var (rewritten, removed) = Rewrite(root, members, new HashSet<(TypeDeclarationSyntax, bool)>(), model, externMembers, issuedNames, cancellationToken);
            externBodies += removed;
            compilation = compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(rewritten.ToFullString(), (CSharpParseOptions)tree.Options,
                                                                                           tree.FilePath, cancellationToken: cancellationToken));
        }

        var repairs = repairArtefacts;
        for (var round = 0; ; round++)
        {
            checks.Check(GenerationStage.CompileLibrary);
            var targets = new Targets();
            issuedNames.Clear();
            var unusable = new SortedSet<string>(StringComparer.Ordinal);
            var errors = Errors(compilation, cancellationToken);
            if (repairs)
            {
                // The repair is no rewrite round: a library is not refused a round for an artefact it had.
                repairs = false;
                var repaired = Repaired(compilation, errors, decompilerAssemblies, cancellationToken);
                if (repaired != compilation)
                {
                    compilation = repaired;
                    errors = Errors(compilation, cancellationToken);
                }
            }
            foreach (var error in errors)
            {
                WorkCancellation.Check();
                if (!targets.Add(error) && !BenignDeclarationErrors.Contains(error.Id))
                    unusable.Add(error.Id);
            }

            if (unusable.Count == 0 && targets.IsEmpty)
            {
                var opened = openFields ? LibraryFieldOpening.Open(compilation, cancellationToken)
                                        : (compilation, (IReadOnlySet<string>)new SortedSet<string>(StringComparer.Ordinal));
                return new LibraryCompilationResult(opened.Item1, null, bodies, externBodies, externMembers, opened.Item2, [])
                {
                    ReplacedMembers = replacedMembers
                };
            }
            if (unusable.Count > 0 || round == REWRITE_ROUNDS)
            {
                return new LibraryCompilationResult(null, GenerationReasons.LIBRARY_DOES_NOT_COMPILE, bodies, externBodies, externMembers,
                                                    new SortedSet<string>(StringComparer.Ordinal),
                                                    unusable.Count > 0 ? unusable.ToArray() : targets.Codes.ToArray())
                {
                    ReplacedMembers = replacedMembers
                };
            }

            foreach (var tree in targets.Trees)
            {
                WorkCancellation.Check();
                var model = compilation.GetSemanticModel(tree);
                var (rewritten, removed) = Rewrite(tree.GetRoot(cancellationToken), targets.MembersOf(tree), targets.InitializersOf(tree), model,
                                                   externMembers, issuedNames, cancellationToken, targets.GettersOf(tree));
                externBodies += removed;
                compilation = compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(rewritten.ToFullString(), (CSharpParseOptions)tree.Options,
                                                                                               tree.FilePath, cancellationToken: cancellationToken));
            }
        }
    }

    /// <summary>Whether the compilation makes every member of a type <c>extern</c>, because its decompiled bodies are not the code
    /// that runs: <c>System.Runtime.CompilerServices.Unsafe</c>.</summary>
    /// <param name="type">The type.</param>
    internal static bool HasNoBodies(INamedTypeSymbol type) => type.GetDocumentationCommentId() == UNSAFE;

    /// <summary>The errors of a compilation, CS8051 first: getter targets are known before CS0276 is assigned to the same property,
    /// regardless of diagnostic order.</summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    private static Diagnostic[] Errors(CSharpCompilation compilation, CancellationToken cancellationToken) =>
        compilation.GetDiagnostics(cancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                   .OrderBy(error => error.Id == "CS8051" ? 0 : 1).ToArray();

    /// <summary>The compilation with the decompiler's artefacts repaired, or the same compilation when it has none. A simple type name
    /// the decompiler wrote after a <c>using</c> because it saw one type of that name, which the full reference set makes ambiguous
    /// (CS0104), is qualified as the one candidate the decompiler saw (<see cref="SeenQualifier"/>). A local function the decompiler
    /// left as a method, whose signature names a display class less accessible than the method (CS0050, CS0051), is made private
    /// (<see cref="IsUnfoldedLocalFunction"/>): only its own type and the types nested in it call it, as only they see the display
    /// class. Any other error, and an artefact neither rule decides, is left to the rewrite rounds.</summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="errors">Its errors.</param>
    /// <param name="decompilerAssemblies">The assemblies the decompiler saw, or <c>null</c> when only the library itself is known.</param>
    /// <param name="cancellationToken">Cancels the binding and the rewrite.</param>
    private static CSharpCompilation Repaired(CSharpCompilation compilation, IReadOnlyList<Diagnostic> errors, IReadOnlySet<string>? decompilerAssemblies,
                                              CancellationToken cancellationToken)
    {
        var repairs = new Dictionary<SyntaxTree, Dictionary<SyntaxNode, Func<SyntaxNode, SyntaxNode>>>();
        foreach (var error in errors)
        {
            WorkCancellation.Check();
            if (error.Id is not ("CS0104" or "CS0050" or "CS0051") || error.Location.SourceTree is not { } tree)
                continue;
            var model = compilation.GetSemanticModel(tree);
            var location = tree.GetRoot(cancellationToken).FindNode(error.Location.SourceSpan, getInnermostNodeForTie: true);
            SyntaxNode? target = null;
            Func<SyntaxNode, SyntaxNode>? repair = null;
            if (error.Id == "CS0104")
            {
                if (location.AncestorsAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault() is { } name &&
                    SeenQualifier(name, model, decompilerAssemblies, cancellationToken) is { } qualifier)
                {
                    target = name;
                    repair = current => QualifiedName(qualifier, ((SimpleNameSyntax)current).WithoutTrivia()).WithTriviaFrom(current);
                }
            }
            else if (location.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault() is { } method &&
                     IsUnfoldedLocalFunction(method, model, cancellationToken))
            {
                target = method;
                repair = current => Private((MethodDeclarationSyntax)current);
            }
            if (target is null || repair is null)
                continue;
            if (!repairs.TryGetValue(tree, out var nodes))
                repairs[tree] = nodes = [];
            nodes[target] = repair;
        }

        foreach (var (tree, nodes) in repairs)
        {
            WorkCancellation.Check();
            var rewritten = tree.GetRoot(cancellationToken).ReplaceNodes(nodes.Keys, (original, current) => nodes[original](current));
            compilation = compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(rewritten.ToFullString(), (CSharpParseOptions)tree.Options,
                                                                                           tree.FilePath, cancellationToken: cancellationToken));
        }
        return compilation;
    }

    /// <summary>The namespace, as <c>global::</c>-qualified name, of the one candidate of an ambiguous simple type name that the decompiler
    /// saw: declared in the library itself or in an assembly the decompiler held. The decompiler writes a simple name only when it sees
    /// one type of that name, and it always sees the library. <c>null</c> when no candidate or more than one was seen, or the one seen
    /// is not a type of a named namespace.</summary>
    /// <param name="name">The ambiguous name.</param>
    /// <param name="model">The semantic model of its tree.</param>
    /// <param name="decompilerAssemblies">The assemblies the decompiler saw, or <c>null</c> when only the library itself is known.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    private static NameSyntax? SeenQualifier(SimpleNameSyntax name, SemanticModel model, IReadOnlySet<string>? decompilerAssemblies,
                                             CancellationToken cancellationToken)
    {
        var seen = model.GetSymbolInfo(name, cancellationToken).CandidateSymbols
                        .Select(candidate => candidate is IMethodSymbol { MethodKind: Microsoft.CodeAnalysis.MethodKind.Constructor } constructor
                                    ? constructor.ContainingType
                                    : candidate)
                        .OfType<INamedTypeSymbol>().Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                        .Where(type => SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, model.Compilation.Assembly) ||
                                       decompilerAssemblies?.Contains(type.ContainingAssembly.Identity.Name) == true)
                        .ToArray();
        return seen is [{ ContainingSymbol: INamespaceSymbol { IsGlobalNamespace: false } containing }]
            ? ParseName(containing.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            : null;
    }

    /// <summary>Whether a method is a local function the decompiler left as a method: compiler-generated, its signature naming a
    /// compiler-generated type, a display class.</summary>
    /// <param name="method">The method.</param>
    /// <param name="model">The semantic model of its tree.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    private static bool IsUnfoldedLocalFunction(MethodDeclarationSyntax method, SemanticModel model, CancellationToken cancellationToken) =>
        model.GetDeclaredSymbol(method, cancellationToken) is { } symbol && IsCompilerGenerated(symbol) &&
        symbol.Parameters.Select(parameter => parameter.Type).Append(symbol.ReturnType).Any(type => type is INamedTypeSymbol named && IsCompilerGenerated(named));

    private static bool IsCompilerGenerated(ISymbol symbol) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

    /// <summary>A method made private: its accessibility modifiers give way to <c>private</c>.</summary>
    /// <param name="method">The method.</param>
    private static MethodDeclarationSyntax Private(MethodDeclarationSyntax method)
    {
        var leading = method.Modifiers.Count > 0 ? method.Modifiers[0].LeadingTrivia : default;
        var kept = method.Modifiers.Where(modifier => !SyntaxFacts.IsAccessibilityModifier(modifier.Kind()));
        return method.WithModifiers(TokenList(kept.Prepend(Token(leading, SyntaxKind.PrivateKeyword, TriviaList(Space)))));
    }

    /// <summary>The call a method's body consists of when that call names the method's own name: its expression body or its only
    /// statement, a <c>return</c> of it or the call alone, through <c>ref</c>; otherwise <c>null</c>. Only syntax: whether the call
    /// binds to the method itself <see cref="IsPlaceholder"/> decides.</summary>
    /// <param name="method">The method.</param>
    private static InvocationExpressionSyntax? OnlyCallOfItsName(MethodDeclarationSyntax method)
    {
        var only = method.ExpressionBody?.Expression ?? (method.Body is { Statements: [var statement] }
            ? statement switch
            {
                ReturnStatementSyntax ret => ret.Expression,
                ExpressionStatementSyntax call => call.Expression,
                _ => null
            }
            : null);
        while (only is RefExpressionSyntax reference)
        {
            WorkCancellation.Check();
            only = reference.Expression;
        }
        if (only is not InvocationExpressionSyntax invocation)
            return null;
        var name = invocation.Expression switch
        {
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null
        };
        return name == method.Identifier.ValueText ? invocation : null;
    }

    /// <summary>Whether a method is an intrinsic's placeholder: its body only calls the method itself, the IL the decompiler renders for
    /// a body the JIT replaces (<c>MemoryMarshal.GetArrayDataReference</c>, the hardware intrinsics).</summary>
    /// <param name="method">The method, one <see cref="OnlyCallOfItsName"/> finds.</param>
    /// <param name="model">The semantic model of its tree.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    private static bool IsPlaceholder(MethodDeclarationSyntax method, SemanticModel model, CancellationToken cancellationToken) =>
        OnlyCallOfItsName(method) is { } call && model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol called &&
        model.GetDeclaredSymbol(method, cancellationToken) is { } declared &&
        SymbolEqualityComparer.Default.Equals(called.OriginalDefinition, declared.OriginalDefinition);

    private static bool Selected(MemberDeclarationSyntax member, SemanticModel model, IReadOnlySet<string> selection,
                                 CancellationToken cancellationToken)
    {
        if (member is EventFieldDeclarationSyntax field)
            return field.Declaration.Variables.Any(variable => model.GetDeclaredSymbol(variable, cancellationToken)?.GetDocumentationCommentId() is { } id &&
                                                             selection.Contains(id));
        return member is MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax &&
               model.GetDeclaredSymbol(member, cancellationToken)?.GetDocumentationCommentId() is { } memberId && selection.Contains(memberId);
    }

    /// <summary>The text of a decompiled tree, parsed. It checks before the output pass, which the token does not interrupt.</summary>
    /// <param name="tree">The decompiler's tree.</param>
    /// <param name="settings">The decompiler settings.</param>
    /// <param name="path">The path the parsed tree reports.</param>
    /// <param name="checks">The generation's checks.</param>
    private static SyntaxTree Parse(ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree, DecompilerSettings settings, string path,
                                    GenerationChecks checks)
    {
        checks.Check(GenerationStage.CompileLibrary);
        var writer = new StringWriter();
        tree.AcceptVisitor(new CSharpOutputVisitor(writer, settings.CSharpFormattingOptions));
        return CSharpSyntaxTree.ParseText(writer.ToString(), ParseOptions, path, cancellationToken: checks.Token);
    }

    /// <summary>Whether a node is a method body: a method, constructor, destructor, operator or conversion with a body, an accessor
    /// with one, or an expression-bodied property or indexer.</summary>
    /// <param name="node">The node.</param>
    private static bool IsBody(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
        AccessorDeclarationSyntax accessor => accessor.Body is not null || accessor.ExpressionBody is not null,
        PropertyDeclarationSyntax property => property.ExpressionBody is not null,
        IndexerDeclarationSyntax indexer => indexer.ExpressionBody is not null,
        _ => false
    };

    /// <summary>Rewrites one tree: failing bodies become opaque, trimmed auto-properties gain a getter, and each type with an
    /// initializer in error loses that kind's initializers and gets bodiless constructors of that kind.</summary>
    /// <param name="root">The tree's root.</param>
    /// <param name="members">The members whose bodies are in error.</param>
    /// <param name="initializers">The types with an initializer in error, with the kind: static or instance.</param>
    /// <param name="model">The semantic model of the tree before the rewrite.</param>
    /// <param name="externMembers">Receives the documentation ids of the members made <c>extern</c>.</param>
    /// <param name="issuedNames">The helper names already issued to each type in this rewrite round, across its partial declarations.</param>
    /// <param name="cancellationToken">Cancels the rewrite.</param>
    /// <param name="getters">The trimmed auto-properties that receive a getter without losing their auto setter.</param>
    private static (SyntaxNode Root, int RemovedBodies) Rewrite(SyntaxNode root, IReadOnlySet<MemberDeclarationSyntax> members,
                                                                IReadOnlySet<(TypeDeclarationSyntax Type, bool Static)> initializers,
                                                                SemanticModel model, ISet<string> externMembers,
                                                                Dictionary<string, HashSet<string>> issuedNames, CancellationToken cancellationToken,
                                                                IReadOnlySet<PropertyDeclarationSyntax>? getters = null)
    {
        var removed = new HashSet<SyntaxNode>();
        var eventNames = new Dictionary<EventDeclarationSyntax, string>();
        var helpers = new Dictionary<TypeDeclarationSyntax, List<MethodDeclarationSyntax>>();
        foreach (var member in members)
        {
            WorkCancellation.Check();
            removed.UnionWith(member.DescendantNodesAndSelf().Where(IsBody));
            foreach (var symbol in DeclaredSymbols(model.GetDeclaredSymbol(member, cancellationToken)))
            {
                WorkCancellation.Check();
                externMembers.Add(symbol);
            }
            if (member is EventDeclarationSyntax { ExplicitInterfaceSpecifier: not null, Parent: TypeDeclarationSyntax type } @event &&
                model.GetDeclaredSymbol(@event, cancellationToken) is IEventSymbol { AddMethod: { } add } eventSymbol)
            {
                var owner = eventSymbol.ContainingType;
                var typeId = owner.GetDocumentationCommentId()!;
                if (!issuedNames.TryGetValue(typeId, out var names))
                {
                    names = owner.MemberNames.Concat(owner.TypeParameters.Select(parameter => parameter.Name)).Append(owner.Name)
                                 .ToHashSet(StringComparer.Ordinal);
                    issuedNames.Add(typeId, names);
                }
                var stem = @event.Identifier.ValueText + "__extern";
                var name = stem;
                for (var suffix = 1; !names.Add(name); suffix++)
                {
                    WorkCancellation.Check();
                    name = stem + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                eventNames.Add(@event, name);
                var modifiers = IsStatic(@event.Modifiers) ? TokenList(Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.ExternKeyword))
                                                          : TokenList(Token(SyntaxKind.ExternKeyword));
                var helper = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), name).WithModifiers(modifiers)
                    .WithParameterList(ParameterList(SingletonSeparatedList(Parameter(Identifier("value")).WithType(@event.Type))))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
                if (!helpers.TryGetValue(type, out var declarations))
                    helpers[type] = declarations = [];
                declarations.Add(helper);
                var accessorId = add.GetDocumentationCommentId()!;
                externMembers.Add($"M:{typeId[2..]}.{name}{accessorId[accessorId.IndexOf('(')..]}");
            }
        }

        foreach (var (type, isStatic) in initializers)
        {
            WorkCancellation.Check();
            removed.UnionWith(type.Members.OfType<ConstructorDeclarationSyntax>().Where(constructor => IsStatic(constructor.Modifiers) == isStatic && IsBody(constructor)));
            if (model.GetDeclaredSymbol(type, cancellationToken) is not { } typeSymbol)
                continue;
            var constructors = isStatic ? typeSymbol.StaticConstructors : typeSymbol.InstanceConstructors;
            foreach (var constructor in constructors)
            {
                WorkCancellation.Check();
                externMembers.Add(constructor.GetDocumentationCommentId()!);
            }
            if (constructors.IsEmpty)
                externMembers.Add($"M:{typeSymbol.GetDocumentationCommentId()![2..]}.{(isStatic ? "#cctor" : "#ctor")}");
        }

        var rewritten = root.ReplaceNodes(members.Cast<SyntaxNode>().Concat(initializers.Select(initializer => initializer.Type))
                                                .Concat(helpers.Keys).Concat(getters?.Cast<SyntaxNode>() ?? []).Distinct(),
                                          (original, current) =>
                                          {
                                              if (original is TypeDeclarationSyntax type && current is TypeDeclarationSyntax currentType &&
                                                  !members.Contains(type))
                                              {
                                                  foreach (var (_, isStatic) in initializers.Where(initializer => initializer.Type == type))
                                                  {
                                                      WorkCancellation.Check();
                                                      currentType = WithoutInitializers(currentType, isStatic);
                                                  }
                                                  if (helpers.TryGetValue(type, out var declarations))
                                                      currentType = currentType.AddMembers(declarations.Select(declaration =>
                                                          declaration.NormalizeWhitespace().WithLeadingTrivia(LineFeed).WithTrailingTrivia(LineFeed)).ToArray());
                                                  return currentType;
                                              }

                                              if (original is PropertyDeclarationSyntax property && getters?.Contains(property) == true)
                                                  return ((PropertyDeclarationSyntax)current).AddAccessorListAccessors(
                                                      AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)))
                                                      .NormalizeWhitespace().WithLeadingTrivia(LineFeed).WithTrailingTrivia(LineFeed);
                                              return Extern((MemberDeclarationSyntax)current,
                                                            original is EventDeclarationSyntax @event ? eventNames.GetValueOrDefault(@event) : null);
                                          });
        return (rewritten, removed.Count);
    }

    private static IEnumerable<string> DeclaredSymbols(ISymbol? symbol) => (symbol switch
    {
        IPropertySymbol property => new ISymbol?[] { property, property.GetMethod, property.SetMethod },
        IEventSymbol @event => [@event, @event.AddMethod, @event.RemoveMethod],
        _ => [symbol]
    }).OfType<ISymbol>().Select(declared => declared.GetDocumentationCommentId()).OfType<string>();

    /// <summary>A member without its body: a method, operator, conversion or destructor gains <c>extern</c> and loses <c>async</c>;
    /// a constructor also loses its initializer; a property or indexer gets bodiless accessors; an event with accessors becomes a
    /// field-like <c>extern</c> event, or forwards to a bodiless helper when it explicitly implements an interface event.</summary>
    /// <param name="member">The member.</param>
    /// <param name="eventHelper">The bodiless helper an explicit interface event forwards its value to.</param>
    private static MemberDeclarationSyntax Extern(MemberDeclarationSyntax member, string? eventHelper = null)
    {
        var semicolon = Token(SyntaxKind.SemicolonToken);
        MemberDeclarationSyntax bodiless = member switch
        {
            ConstructorDeclarationSyntax constructor => constructor.WithInitializer(null).WithBody(null).WithExpressionBody(null).WithSemicolonToken(semicolon)
                                                                   .WithModifiers(ExternModifiers(constructor.Modifiers)),
            BaseMethodDeclarationSyntax method => method.WithBody(null).WithExpressionBody(null).WithSemicolonToken(semicolon)
                                                        .WithModifiers(ExternModifiers(method.Modifiers)),
            PropertyDeclarationSyntax property => property.WithExpressionBody(null).WithInitializer(null).WithSemicolonToken(default)
                                                          .WithAccessorList(BodilessAccessors(property.AccessorList))
                                                          .WithModifiers(ExternModifiers(property.Modifiers)),
            IndexerDeclarationSyntax indexer => indexer.WithExpressionBody(null).WithSemicolonToken(default)
                                                       .WithAccessorList(BodilessAccessors(indexer.AccessorList))
                                                       .WithModifiers(ExternModifiers(indexer.Modifiers)),
            EventFieldDeclarationSyntax field => field.WithModifiers(ExternModifiers(field.Modifiers))
                                                      .WithDeclaration(WithoutInitializers(field.Declaration)),
            EventDeclarationSyntax { ExplicitInterfaceSpecifier: not null } @event => @event.WithAccessorList(
                AccessorList(List(@event.AccessorList!.Accessors.Select(accessor => accessor.WithExpressionBody(null).WithSemicolonToken(default)
                    .WithBody(Block(ExpressionStatement(InvocationExpression(IdentifierName(eventHelper!))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(IdentifierName("value")))))))))))),
            EventDeclarationSyntax @event => EventFieldDeclaration(@event.AttributeLists, ExternModifiers(@event.Modifiers),
                                                                   VariableDeclaration(@event.Type, SingletonSeparatedList(VariableDeclarator(@event.Identifier)))),
            _ => member
        };
        return bodiless.NormalizeWhitespace().WithLeadingTrivia(LineFeed).WithTrailingTrivia(LineFeed);
    }

    private static AccessorListSyntax BodilessAccessors(AccessorListSyntax? accessors) =>
        AccessorList(List(accessors?.Accessors.Select(accessor => accessor.WithBody(null).WithExpressionBody(null)
                                                                          .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))) ??
                          [AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken))]));

    private static SyntaxTokenList ExternModifiers(SyntaxTokenList modifiers)
    {
        var kept = modifiers.Where(modifier => !modifier.IsKind(SyntaxKind.AsyncKeyword)).ToList();
        if (!kept.Any(modifier => modifier.IsKind(SyntaxKind.ExternKeyword)))
            kept.Add(Token(SyntaxKind.ExternKeyword));
        return TokenList(kept);
    }

    /// <summary>A type without its initializers of one kind, every constructor of that kind made <c>extern</c>, the implicit one
    /// declared when there is none.</summary>
    /// <param name="type">The type.</param>
    /// <param name="isStatic">Whether the static initializers and constructor are meant, else the instance ones.</param>
    private static TypeDeclarationSyntax WithoutInitializers(TypeDeclarationSyntax type, bool isStatic)
    {
        WorkCancellation.Check();
        var members = type.Members.Select(member => member switch
        {
            FieldDeclarationSyntax field when !field.Modifiers.Any(SyntaxKind.ConstKeyword) && IsStatic(field.Modifiers) == isStatic =>
                field.WithDeclaration(WithoutInitializers(field.Declaration)),
            EventFieldDeclarationSyntax field when IsStatic(field.Modifiers) == isStatic => field.WithDeclaration(WithoutInitializers(field.Declaration)),
            PropertyDeclarationSyntax { Initializer: not null } property when IsStatic(property.Modifiers) == isStatic =>
                property.WithInitializer(null).WithSemicolonToken(default),
            ConstructorDeclarationSyntax constructor when IsStatic(constructor.Modifiers) == isStatic => Extern(constructor),
            _ => member
        }).ToList();
        if (!type.Members.OfType<ConstructorDeclarationSyntax>().Any(constructor => IsStatic(constructor.Modifiers) == isStatic))
        {
            var access = isStatic ? SyntaxKind.StaticKeyword : type.Modifiers.Any(SyntaxKind.AbstractKeyword) ? SyntaxKind.ProtectedKeyword : SyntaxKind.PublicKeyword;
            members.Add(Extern(ConstructorDeclaration(type.Identifier.WithoutTrivia()).WithModifiers(TokenList(Token(access)))
                                                                                      .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))));
        }

        return type.WithMembers(List(members)).NormalizeWhitespace().WithLeadingTrivia(LineFeed).WithTrailingTrivia(LineFeed);
    }

    private static VariableDeclarationSyntax WithoutInitializers(VariableDeclarationSyntax declaration) =>
        declaration.WithVariables(SeparatedList(declaration.Variables.Select(variable => variable.WithInitializer(null))));

    private static bool IsStatic(SyntaxTokenList modifiers) => modifiers.Any(SyntaxKind.StaticKeyword);

    /// <summary>The failing bodies, initializers and trimmed auto-properties of one compilation, by tree.</summary>
    private sealed class Targets
    {
        private readonly Dictionary<SyntaxTree, HashSet<MemberDeclarationSyntax>> _members = [];
        private readonly Dictionary<SyntaxTree, HashSet<PropertyDeclarationSyntax>> _getters = [];
        private readonly Dictionary<SyntaxTree, HashSet<(TypeDeclarationSyntax Type, bool Static)>> _initializers = [];
        private readonly SortedSet<string> _codes = new(StringComparer.Ordinal);

        public bool IsEmpty => _members.Count == 0 && _initializers.Count == 0 && _getters.Count == 0;

        public IEnumerable<SyntaxTree> Trees => _members.Keys.Union(_initializers.Keys).Union(_getters.Keys).ToArray();

        public IEnumerable<string> Codes => _codes;

        public IReadOnlySet<MemberDeclarationSyntax> MembersOf(SyntaxTree tree) => _members.GetValueOrDefault(tree) ?? [];

        public IReadOnlySet<PropertyDeclarationSyntax> GettersOf(SyntaxTree tree) => _getters.GetValueOrDefault(tree) ?? [];

        public IReadOnlySet<(TypeDeclarationSyntax Type, bool Static)> InitializersOf(SyntaxTree tree) => _initializers.GetValueOrDefault(tree) ?? [];

        /// <summary>Records a failing body or initializer, or a trimmed auto-property; <c>false</c> for other declaration errors.</summary>
        /// <param name="error">The error.</param>
        public bool Add(Diagnostic error)
        {
            WorkCancellation.Check();
            if (error.Location.SourceTree is not { } tree)
                return false;
            var span = error.Location.SourceSpan;
            var flow = FlowErrors.Contains(error.Id);
            var location = tree.GetRoot().FindNode(span, getInnermostNodeForTie: true);
            if (error.Id is "CS8051" or "CS0276" && location.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().FirstOrDefault() is { } property)
            {
                if (error.Id == "CS8051" && property.AccessorList?.Accessors is [{ Body: null, ExpressionBody: null } setter] &&
                    setter.Kind() is SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration)
                {
                    if (!_getters.TryGetValue(tree, out var getters))
                        _getters[tree] = getters = [];
                    getters.Add(property);
                    _codes.Add(error.Id);
                    return true;
                }
                if (error.Id == "CS0276" && GettersOf(tree).Contains(property))
                    return true;
            }
            foreach (var node in location.AncestorsAndSelf())
            {
                WorkCancellation.Check();
                switch (node)
                {
                    case AccessorDeclarationSyntax accessor:
                        if (accessor.Parent?.Parent is not BasePropertyDeclarationSyntax accessorOwner ||
                            !flow && !Within(accessor.Body, span) && !Within(accessor.ExpressionBody, span))
                            return false;
                        return AddMember(tree, accessorOwner, error.Id);
                    case ArrowExpressionClauseSyntax { Parent: MemberDeclarationSyntax arrowOwner } when arrowOwner is PropertyDeclarationSyntax or IndexerDeclarationSyntax:
                        return AddMember(tree, arrowOwner, error.Id);
                    case BaseMethodDeclarationSyntax method:
                        if (!flow && !Within(method.Body, span) && !Within(method.ExpressionBody, span) &&
                            !(method is ConstructorDeclarationSyntax { Initializer: { } initializer } && Within(initializer, span)))
                            return false;
                        return AddMember(tree, method, error.Id);
                    case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax field } } }:
                        if (field.Modifiers.Any(SyntaxKind.ConstKeyword) || field.Parent is not TypeDeclarationSyntax fieldType)
                            return false;
                        return AddInitializer(tree, fieldType, IsStatic(field.Modifiers), error.Id);
                    case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax { Parent: TypeDeclarationSyntax propertyType } initialized }:
                        return AddInitializer(tree, propertyType, IsStatic(initialized.Modifiers), error.Id);
                    case MemberDeclarationSyntax:
                        return false;
                }
            }

            return false;
        }

        private static bool Within(SyntaxNode? node, Microsoft.CodeAnalysis.Text.TextSpan span) => node is not null && node.Span.Contains(span);

        private bool AddMember(SyntaxTree tree, MemberDeclarationSyntax member, string code)
        {
            if (!_members.TryGetValue(tree, out var members))
                _members[tree] = members = [];
            members.Add(member);
            _codes.Add(code);
            return true;
        }

        private bool AddInitializer(SyntaxTree tree, TypeDeclarationSyntax type, bool isStatic, string code)
        {
            if (!_initializers.TryGetValue(tree, out var initializers))
                _initializers[tree] = initializers = [];
            initializers.Add((type, isStatic));
            _codes.Add(code);
            return true;
        }
    }

    /// <summary>The whole-project decompiler's own choices, without its file output: which types a project includes, and a
    /// decompiler with its transforms.</summary>
    /// <param name="settings">The decompiler settings.</param>
    /// <param name="resolver">The reference resolver.</param>
    private sealed class ProjectDecompiler(DecompilerSettings settings, IAssemblyResolver resolver)
        : WholeProjectDecompiler(settings, resolver, null, null, null)
    {
        public bool Includes(MetadataFile module, TypeDefinitionHandle type) => IncludeTypeWhenDecompilingProject(module, type);

        public CSharpDecompiler Create(DecompilerTypeSystem typeSystem) => CreateDecompiler(typeSystem);
    }

    /// <summary>Resolves the decompiler's assembly references to the reference files G-1 chose, and to nothing else.</summary>
    private sealed class ReferenceResolver : IAssemblyResolver, IDisposable
    {
        private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<MetadataFile>> _opened = new(StringComparer.OrdinalIgnoreCase);

        public ReferenceResolver(IEnumerable<string> references)
        {
            foreach (var reference in references)
            {
                WorkCancellation.Check();
                if (AssemblyFile.Read(reference) is { } file)
                    _paths.TryAdd(file.Name, reference);
            }
        }

        public MetadataFile? Resolve(IAssemblyReference reference) =>
            _paths.TryGetValue(reference.Name, out var path)
                ? _opened.GetOrAdd(path, file => new Lazy<MetadataFile>(() => new PEFile(file, PEStreamOptions.PrefetchEntireImage))).Value
                : null;

        public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) => null;

        public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));

        public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult<MetadataFile?>(null);

        /// <summary>A snapshot changes nothing: the references are fixed for the resolver's life.</summary>
        public IDisposable BeginSnapshot() => new Snapshot();

        // bounded: forwards disposal to the opened PE files, without re-entering this resolver.
        public void Dispose()
        {
            foreach (var opened in _opened.Values.Where(opened => opened.IsValueCreated))
                opened.Value.Dispose();
        }

        private sealed class Snapshot : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
