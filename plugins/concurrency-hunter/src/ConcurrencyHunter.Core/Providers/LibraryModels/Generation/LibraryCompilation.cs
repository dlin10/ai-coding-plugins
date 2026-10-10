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
                                              IReadOnlyList<string> Errors);

/// <summary>A module decompiled in memory: one tree per top-level type and the assembly-attributes tree last, and the top-level types
/// the decompiler threw on.</summary>
/// <param name="Trees">The trees of the types decompiled.</param>
/// <param name="Failures">One entry per type the decompiler threw on, <c>&lt;type&gt;: &lt;exception&gt;: &lt;message&gt;</c>.</param>
public sealed record DecompiledModule(IReadOnlyList<SyntaxTree> Trees, IReadOnlyList<string> Failures);

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

    private static readonly ConcurrentDictionary<string, Lazy<LibraryCompilationResult>> Compilations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The compilation of an implementation assembly, built once per implementation path, shared framework directory and
    /// dependency paths.</summary>
    /// <param name="assembly">The implementation assembly, not a reference assembly.</param>
    /// <param name="cancellationToken">Cancels the decompilation and the compilation.</param>
    public static LibraryCompilationResult Compile(ImplementationAssembly assembly, CancellationToken cancellationToken) =>
        Compile(assembly, new HashSet<string>(StringComparer.Ordinal), cancellationToken);

    /// <summary>Compiles an implementation assembly with the selected members made extern as well as failing bodies.</summary>
    /// <param name="assembly">The implementation assembly.</param>
    /// <param name="selectedMembers">Documentation ids of whole members to make extern.</param>
    /// <param name="cancellationToken">Cancels the decompilation and compilation.</param>
    internal static LibraryCompilationResult Compile(ImplementationAssembly assembly, IReadOnlySet<string> selectedMembers,
                                                     CancellationToken cancellationToken)
    {
        var dependencies = assembly.References.Where(reference => !string.Equals(Path.GetDirectoryName(reference),
                                                                                Path.TrimEndingDirectorySeparator(assembly.SharedFrameworkDirectory),
                                                                                StringComparison.OrdinalIgnoreCase));
        var selection = selectedMembers.ToHashSet(StringComparer.Ordinal);
        // Paths compare ignoring case; hex-encoded member ids retain their case in that cache.
        var key = string.Join("\n", new[] { assembly.Path, assembly.SharedFrameworkDirectory }.Concat(dependencies)) +
                  "\nextern:\n" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", selection.Order(StringComparer.Ordinal))));
        var compilation = Compilations.GetOrAdd(key, _ => new Lazy<LibraryCompilationResult>(() =>
            CompileModule(assembly.AssemblyName, Decompile(assembly, cancellationToken),
                          assembly.References.Select(reference => MetadataReference.CreateFromFile(reference)).ToArray(), cancellationToken, selection)));
        try
        {
            return compilation.Value;
        }
        catch (OperationCanceledException)
        {
            Compilations.TryRemove(KeyValuePair.Create(key, compilation));
            throw;
        }
    }

    /// <summary>Decompiles the whole module in memory: one syntax tree per top-level type the whole-project decompiler includes,
    /// with its <c>EscapeInvalidIdentifiers</c> and <c>RemoveCLSCompliantAttribute</c> transforms, and the assembly-attributes
    /// tree last. A type the decompiler throws on has no tree and is named in <see cref="DecompiledModule.Failures"/>; it is never
    /// dropped silently.</summary>
    /// <param name="assembly">The implementation assembly.</param>
    /// <param name="cancellationToken">Cancels the decompilation.</param>
    internal static DecompiledModule Decompile(ImplementationAssembly assembly, CancellationToken cancellationToken)
    {
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
        Parallel.For(0, types.Length, new ParallelOptions { CancellationToken = cancellationToken }, index =>
        {
            var definition = metadata.GetTypeDefinition(types[index]);
            var fullName = metadata.GetString(definition.Namespace) is { Length: > 0 } ns
                ? ns + "." + metadata.GetString(definition.Name)
                : metadata.GetString(definition.Name);
            var name = string.Concat(fullName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var decompiler = project.Create(typeSystem);
            decompiler.CancellationToken = cancellationToken;
            try
            {
                trees[index] = Parse(decompiler.DecompileTypes([types[index]]), settings, $"{assembly.AssemblyName}/{name}.cs", cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures[index] = $"{fullName}: {error.GetType().Name}: {error.Message}";
            }
        });

        var attributes = project.Create(typeSystem);
        attributes.CancellationToken = cancellationToken;
        attributes.AstTransforms.Add(new RemoveCompilerGeneratedAssemblyAttributes());
        trees[^1] = Parse(attributes.DecompileModuleAndAssemblyAttributes(), settings, $"{assembly.AssemblyName}/AssemblyInfo.cs", cancellationToken);
        return new DecompiledModule(trees.OfType<SyntaxTree>().ToArray(), failures.OfType<string>().ToArray());
    }

    /// <summary>Compiles a decompiled module, or refuses it whole when the decompiler threw on one of its types: a missing type
    /// would hide declarations and bodies the analysis then never sees, and only what compiled is believed.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="module">The decompiled module.</param>
    /// <param name="references">The references of the platform version.</param>
    /// <param name="cancellationToken">Cancels the compilation.</param>
    /// <param name="selectedMembers">Additional whole-member documentation ids to make extern.</param>
    internal static LibraryCompilationResult CompileModule(string assemblyName, DecompiledModule module, IReadOnlyList<MetadataReference> references,
                                                           CancellationToken cancellationToken, IReadOnlySet<string>? selectedMembers = null)
    {
        if (module.Failures.Count > 0)
        {
            return new LibraryCompilationResult(null, GenerationReasons.LIBRARY_DOES_NOT_COMPILE, 0, 0, new SortedSet<string>(StringComparer.Ordinal),
                                                new SortedSet<string>(StringComparer.Ordinal),
                                                module.Failures.Select(failure => $"decompiler: {failure}").ToArray());
        }

        return CompileTrees(assemblyName, module.Trees, references, cancellationToken, selectedMembers);
    }

    /// <summary>Compiles decompiled trees as a library named as the assembly, unsafe code allowed, nullable disabled, makes every
    /// member of a type <see cref="HasNoBodies"/> names <c>extern</c>, then rewrites every member whose body has an error as
    /// <c>extern</c> and recompiles, at most three times. A declaration-level error other
    /// than the benign ones, a body still in error after the last rewrite, or an error the rewrite leaves makes the library
    /// unusable.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="trees">The decompiled trees.</param>
    /// <param name="references">The references of the platform version.</param>
    /// <param name="cancellationToken">Cancels the compilation.</param>
    /// <param name="selectedMembers">Additional whole-member documentation ids to make extern.</param>
    /// <param name="openFields">Whether to open seed and path fields for the model driver.</param>
    internal static LibraryCompilationResult CompileTrees(string assemblyName, IReadOnlyList<SyntaxTree> trees,
                                                          IReadOnlyList<MetadataReference> references, CancellationToken cancellationToken,
                                                          IReadOnlySet<string>? selectedMembers = null, bool openFields = true)
    {
        var bodies = trees.Sum(tree => tree.GetRoot(cancellationToken).DescendantNodes().Count(IsBody));
        var compilation = CSharpCompilation.Create(assemblyName, trees, references,
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                                                                                nullableContextOptions: NullableContextOptions.Disable));
        var externMembers = new SortedSet<string>(StringComparer.Ordinal);
        var externBodies = 0;
        if (selectedMembers is { Count: > 0 })
        {
            foreach (var tree in compilation.SyntaxTrees.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentTree = tree;
                var model = compilation.GetSemanticModel(currentTree);
                var root = currentTree.GetRoot(cancellationToken);
                // Each variable in a field-like event declaration is a separate member.
                while (root.DescendantNodes().OfType<EventFieldDeclarationSyntax>()
                           .FirstOrDefault(field => field.Declaration.Variables.Count > 1 && Selected(field, model, selectedMembers, cancellationToken)) is { } field)
                {
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
                    foreach (var variable in field.Declaration.Variables)
                        externMembers.UnionWith(DeclaredSymbols(model.GetDeclaredSymbol(variable, cancellationToken)));
                }

                var (rewritten, removed) = Rewrite(root, members, new HashSet<(TypeDeclarationSyntax, bool)>(), model,
                                                   externMembers, cancellationToken);
                externBodies += removed;
                compilation = compilation.ReplaceSyntaxTree(currentTree, CSharpSyntaxTree.ParseText(rewritten.ToFullString(), (CSharpParseOptions)currentTree.Options,
                                                                                                      currentTree.FilePath, cancellationToken: cancellationToken));
            }
        }

        // Unsafe has no body, as in every library that references it from metadata.
        foreach (var tree in compilation.SyntaxTrees.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = tree.GetRoot(cancellationToken);
            var types = root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<TypeDeclarationSyntax>()
                            .Where(type => type.Identifier.ValueText == "Unsafe").ToArray();
            if (types.Length == 0)
                continue;
            var model = compilation.GetSemanticModel(tree);
            var members = types.Where(type => model.GetDeclaredSymbol(type, cancellationToken) is { } symbol && HasNoBodies(symbol))
                               .SelectMany(type => type.Members).Where(member => member.DescendantNodesAndSelf().Any(IsBody)).ToHashSet();
            if (members.Count == 0)
                continue;
            var (rewritten, removed) = Rewrite(root, members, new HashSet<(TypeDeclarationSyntax, bool)>(), model, externMembers, cancellationToken);
            externBodies += removed;
            compilation = compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(rewritten.ToFullString(), (CSharpParseOptions)tree.Options,
                                                                                           tree.FilePath, cancellationToken: cancellationToken));
        }

        for (var round = 0; ; round++)
        {
            var targets = new Targets();
            var unusable = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var error in compilation.GetDiagnostics(cancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                if (!targets.Add(error) && !BenignDeclarationErrors.Contains(error.Id))
                    unusable.Add(error.Id);
            }

            if (unusable.Count == 0 && targets.IsEmpty)
            {
                var opened = openFields ? LibraryFieldOpening.Open(compilation, cancellationToken)
                                        : (compilation, (IReadOnlySet<string>)new SortedSet<string>(StringComparer.Ordinal));
                return new LibraryCompilationResult(opened.Item1, null, bodies, externBodies, externMembers, opened.Item2, []);
            }
            if (unusable.Count > 0 || round == REWRITE_ROUNDS)
            {
                return new LibraryCompilationResult(null, GenerationReasons.LIBRARY_DOES_NOT_COMPILE, bodies, externBodies, externMembers,
                                                    new SortedSet<string>(StringComparer.Ordinal),
                                                    unusable.Count > 0 ? unusable.ToArray() : targets.Codes.ToArray());
            }

            foreach (var tree in targets.Trees)
            {
                var model = compilation.GetSemanticModel(tree);
                var (rewritten, removed) = Rewrite(tree.GetRoot(cancellationToken), targets.MembersOf(tree), targets.InitializersOf(tree), model,
                                                   externMembers, cancellationToken);
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

    private static bool Selected(MemberDeclarationSyntax member, SemanticModel model, IReadOnlySet<string> selection,
                                 CancellationToken cancellationToken)
    {
        if (member is EventFieldDeclarationSyntax field)
            return field.Declaration.Variables.Any(variable => model.GetDeclaredSymbol(variable, cancellationToken)?.GetDocumentationCommentId() is { } id &&
                                                             selection.Contains(id));
        return member is MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax &&
               model.GetDeclaredSymbol(member, cancellationToken)?.GetDocumentationCommentId() is { } memberId && selection.Contains(memberId);
    }

    private static SyntaxTree Parse(ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree, DecompilerSettings settings, string path,
                                    CancellationToken cancellationToken)
    {
        var writer = new StringWriter();
        tree.AcceptVisitor(new CSharpOutputVisitor(writer, settings.CSharpFormattingOptions));
        return CSharpSyntaxTree.ParseText(writer.ToString(), ParseOptions, path, cancellationToken: cancellationToken);
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

    /// <summary>Rewrites one tree: each member in error made <c>extern</c>, and each type with an initializer in error stripped of
    /// that kind's initializers, its constructors of that kind made <c>extern</c>.</summary>
    /// <param name="root">The tree's root.</param>
    /// <param name="members">The members whose bodies are in error.</param>
    /// <param name="initializers">The types with an initializer in error, with the kind: static or instance.</param>
    /// <param name="model">The semantic model of the tree before the rewrite.</param>
    /// <param name="externMembers">Receives the documentation ids of the members made <c>extern</c>.</param>
    /// <param name="cancellationToken">Cancels the rewrite.</param>
    private static (SyntaxNode Root, int RemovedBodies) Rewrite(SyntaxNode root, IReadOnlySet<MemberDeclarationSyntax> members,
                                                                IReadOnlySet<(TypeDeclarationSyntax Type, bool Static)> initializers,
                                                                SemanticModel model, ISet<string> externMembers,
                                                                CancellationToken cancellationToken)
    {
        var removed = new HashSet<SyntaxNode>();
        foreach (var member in members)
        {
            removed.UnionWith(member.DescendantNodesAndSelf().Where(IsBody));
            foreach (var symbol in DeclaredSymbols(model.GetDeclaredSymbol(member, cancellationToken)))
                externMembers.Add(symbol);
        }

        foreach (var (type, isStatic) in initializers)
        {
            removed.UnionWith(type.Members.OfType<ConstructorDeclarationSyntax>().Where(constructor => IsStatic(constructor.Modifiers) == isStatic && IsBody(constructor)));
            if (model.GetDeclaredSymbol(type, cancellationToken) is not { } typeSymbol)
                continue;
            var constructors = isStatic ? typeSymbol.StaticConstructors : typeSymbol.InstanceConstructors;
            foreach (var constructor in constructors)
                externMembers.Add(constructor.GetDocumentationCommentId()!);
            if (constructors.IsEmpty)
                externMembers.Add($"M:{typeSymbol.GetDocumentationCommentId()![2..]}.{(isStatic ? "#cctor" : "#ctor")}");
        }

        var rewritten = root.ReplaceNodes(members.Cast<SyntaxNode>().Concat(initializers.Select(initializer => initializer.Type)).Distinct(),
                                          (original, current) =>
                                          {
                                              if (original is TypeDeclarationSyntax type && current is TypeDeclarationSyntax currentType &&
                                                  !members.Contains(type))
                                              {
                                                  foreach (var (_, isStatic) in initializers.Where(initializer => initializer.Type == type))
                                                      currentType = WithoutInitializers(currentType, isStatic);
                                                  return currentType;
                                              }

                                              return Extern((MemberDeclarationSyntax)current);
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
    /// field-like <c>extern</c> event.</summary>
    /// <param name="member">The member.</param>
    private static MemberDeclarationSyntax Extern(MemberDeclarationSyntax member)
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

    /// <summary>The members and type initializers the errors of one compilation are in, by tree.</summary>
    private sealed class Targets
    {
        private readonly Dictionary<SyntaxTree, HashSet<MemberDeclarationSyntax>> _members = [];
        private readonly Dictionary<SyntaxTree, HashSet<(TypeDeclarationSyntax Type, bool Static)>> _initializers = [];
        private readonly SortedSet<string> _codes = new(StringComparer.Ordinal);

        public bool IsEmpty => _members.Count == 0 && _initializers.Count == 0;

        public IEnumerable<SyntaxTree> Trees => _members.Keys.Union(_initializers.Keys).ToArray();

        public IEnumerable<string> Codes => _codes;

        public IReadOnlySet<MemberDeclarationSyntax> MembersOf(SyntaxTree tree) => _members.GetValueOrDefault(tree) ?? [];

        public IReadOnlySet<(TypeDeclarationSyntax Type, bool Static)> InitializersOf(SyntaxTree tree) => _initializers.GetValueOrDefault(tree) ?? [];

        /// <summary>Records the member or initializer an error is in; <c>false</c> for an error outside every body.</summary>
        /// <param name="error">The error.</param>
        public bool Add(Diagnostic error)
        {
            if (error.Location.SourceTree is not { } tree)
                return false;
            var span = error.Location.SourceSpan;
            var flow = FlowErrors.Contains(error.Id);
            foreach (var node in tree.GetRoot().FindNode(span, getInnermostNodeForTie: true).AncestorsAndSelf())
            {
                switch (node)
                {
                    case AccessorDeclarationSyntax accessor:
                        if (accessor.Parent?.Parent is not BasePropertyDeclarationSyntax property ||
                            !flow && !Within(accessor.Body, span) && !Within(accessor.ExpressionBody, span))
                            return false;
                        return AddMember(tree, property, error.Id);
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
