// Feasibility spike: can System.Private.CoreLib be decompiled and compiled by Roslyn AS the corlib (no references), and can a
// driver bind against that compilation? Read-only for the repository; everything lives in the scratch folder.
//   corelib-spike decompile <outDir> [single]   one file per top-level type (parallel, like WholeProjectDecompiler) or one file
//   corelib-spike compile <dir>                 compile every *.cs in <dir> as System.Private.CoreLib, report, driver, emit
using System.Collections.Concurrent;
using System.Diagnostics;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using LanguageVersion = Microsoft.CodeAnalysis.CSharp.LanguageVersion;
using SpecialType = Microsoft.CodeAnalysis.SpecialType;
using TypeKind = Microsoft.CodeAnalysis.TypeKind;

const string CoreLibPath = @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.31\System.Private.CoreLib.dll";

Console.WriteLine($"ICSharpCode.Decompiler {typeof(CSharpDecompiler).Assembly.GetName().Version}; " +
                  $"Microsoft.CodeAnalysis.CSharp {FileVersionInfo.GetVersionInfo(typeof(CSharpCompilation).Assembly.Location).ProductVersion}");
switch (args[0])
{
    case "decompile":
        Decompile(args[1], args.Length > 2 && args[2] == "single");
        break;
    case "compile":
        Spike.Compile(args[1]);
        break;
}

static void Decompile(string dir, bool single)
{
    Directory.CreateDirectory(dir);
    var watch = Stopwatch.StartNew();
    var module = new PEFile(CoreLibPath);
    var resolver = new UniversalAssemblyResolver(CoreLibPath, false, module.DetectTargetFrameworkId());
    var settings = new DecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false };
    long chars;
    int files;
    var failures = new ConcurrentBag<string>();
    if (single)
    {
        var code = new CSharpDecompiler(module, resolver, settings).DecompileWholeModuleAsString();
        File.WriteAllText(Path.Combine(dir, "System.Private.CoreLib.cs"), code);
        chars = code.Length;
        files = 1;
    }
    else
    {
        var typeSystem = new DecompilerTypeSystem(module, resolver, settings);
        var metadata = module.Metadata;
        var types = metadata.GetTopLevelTypeDefinitions()
                            .Where(h => metadata.GetString(metadata.GetTypeDefinition(h).Name) != "<Module>" &&
                                        !CSharpDecompiler.MemberIsHidden(module, h, settings))
                            .ToList();
        long total = 0;
        Parallel.ForEach(types.Select((h, i) => (h, i)), item =>
        {
            var name = item.h.GetFullTypeName(metadata).ReflectionName;
            try
            {
                var decompiler = new CSharpDecompiler(typeSystem, settings);
                decompiler.AstTransforms.Add(new EscapeInvalidIdentifiers());
                decompiler.AstTransforms.Add(new RemoveCLSCompliantAttribute());
                var code = decompiler.DecompileTypesAsString([item.h]);
                var file = $"{item.i:D5}_{string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))}.cs";
                File.WriteAllText(Path.Combine(dir, file), code);
                Interlocked.Add(ref total, code.Length);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        });
        var attributes = new CSharpDecompiler(typeSystem, settings);
        attributes.AstTransforms.Add(new RemoveCompilerGeneratedAssemblyAttributes());
        var attributeCode = attributes.DecompileModuleAndAssemblyAttributesToString();
        File.WriteAllText(Path.Combine(dir, "_AssemblyAttributes.cs"), attributeCode);
        chars = total + attributeCode.Length;
        files = types.Count - failures.Count + 1;
    }

    Console.WriteLine($"decompiled {(single ? "whole module, one file" : "one file per top-level type")}: {watch.Elapsed.TotalSeconds:F1}s, " +
                      $"{files} files, {chars / 1024 / 1024.0:F1} M chars, {Directory.EnumerateFiles(dir, "*.cs").Sum(f => new FileInfo(f).Length) / 1024 / 1024.0:F1} MB on disk, " +
                      $"{failures.Count} types failed to decompile");
    foreach (var failure in failures.Take(20))
        Console.WriteLine($"    {failure}");
}

static class Spike
{
    private static readonly Dictionary<SyntaxTree, List<Diagnostic>> ErrorsByTree = [];

    public static void Compile(string dir)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var watch = Stopwatch.StartNew();
        var trees = Directory.GetFiles(dir, "*.cs").AsParallel()
                             .Select(f => CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(f)), parse, f))
                             .ToArray();
        Console.WriteLine($"parsed {trees.Length} trees in {watch.Elapsed.TotalSeconds:F1}s");

        watch.Restart();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                                                   nullableContextOptions: NullableContextOptions.Disable);
        var compilation = CSharpCompilation.Create("System.Private.CoreLib", trees, [], options);
        Console.WriteLine($"assembly identity: {compilation.Assembly.Identity}");
        var obj = compilation.GetSpecialType(SpecialType.System_Object);
        Console.WriteLine($"System.Object kind {obj.TypeKind}, from {obj.ContainingAssembly?.Name}, is own corlib: " +
                          $"{SymbolEqualityComparer.Default.Equals(obj.ContainingAssembly, compilation.Assembly)}");
        var missing = new List<string>();
        foreach (var special in Enum.GetValues<SpecialType>().Distinct())
        {
            if (special == SpecialType.None || special.ToString() == "Count")
                continue;
            try
            {
                if (compilation.GetSpecialType(special) is { TypeKind: TypeKind.Error })
                    missing.Add(special.ToString());
            }
            catch (Exception ex)
            {
                missing.Add($"{special} ({ex.GetType().Name})");
            }
        }

        Console.WriteLine($"missing special types: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");

        var declarationErrors = compilation.GetDeclarationDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
        Console.WriteLine($"declaration diagnostics: {declarationErrors} errors in {watch.Elapsed.TotalSeconds:F1}s");
        watch.Restart();
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Console.WriteLine($"GetDiagnostics: {watch.Elapsed.TotalSeconds:F1}s, {errors.Count} errors; " +
                          $"peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MB");
        foreach (var group in errors.GroupBy(e => e.Id).OrderByDescending(g => g.Count()).Take(15))
            Console.WriteLine($"    {group.Key} x{group.Count()}: {Short(group.First().GetMessage(), 120)}");

        foreach (var error in errors.Where(e => e.Location.SourceTree is not null))
        {
            if (!ErrorsByTree.TryGetValue(error.Location.SourceTree, out var list))
                ErrorsByTree[error.Location.SourceTree] = list = [];
            list.Add(error);
        }

        // Every error is mapped to the innermost member body (method, constructor incl. its initializer, accessor,
        // expression-bodied property/indexer) that contains it; the rest is declaration-level or has no location.
        var bodies = trees.SelectMany(t => t.GetRoot().DescendantNodes()).Count(n => BodyRegion(n) is not null);
        var erroneousBodies = new HashSet<SyntaxNode>();
        var outside = new List<Diagnostic>();
        var noLocation = 0;
        foreach (var error in errors)
        {
            if (error.Location.SourceTree is not { } tree)
            {
                noLocation++;
                continue;
            }

            var holder = Holder(tree.GetRoot().FindToken(error.Location.SourceSpan.Start).Parent, error.Location.SourceSpan);
            if (holder is null)
                outside.Add(error);
            else
                erroneousBodies.Add(holder);
        }

        Console.WriteLine($"bodies with at least one error: {erroneousBodies.Count}/{bodies} ({100.0 * erroneousBodies.Count / bodies:F1}%); " +
                          $"errors inside bodies {errors.Count - outside.Count - noLocation}, outside bodies {outside.Count}, without location {noLocation}");
        foreach (var error in outside)
            Console.WriteLine($"    outside {error.Id}: {Short(error.GetMessage(), 110)} @ {Where(error)}");
        Console.WriteLine($"files with errors: {errors.Select(e => e.Location.SourceTree).Distinct().Count()}/{trees.Length}; top:");
        foreach (var group in errors.GroupBy(e => Path.GetFileName(e.Location.SourceTree?.FilePath ?? "")).OrderByDescending(g => g.Count()).Take(10))
            Console.WriteLine($"    {group.Key} x{group.Count()} [{string.Join(", ", group.GroupBy(e => e.Id).Select(g => $"{g.Key}x{g.Count()}"))}]");

        Members(compilation);
        Driver(compilation, parse);

        if (Environment.GetEnvironmentVariable("PATCH") == "1")
            Patched(compilation, outside, erroneousBodies, parse);
    }

    // What emit takes: the declaration errors fixed by hand-shaped text patches, every body with an error replaced by `throw null`.
    private static void Patched(CSharpCompilation compilation, List<Diagnostic> outside, HashSet<SyntaxNode> erroneousBodies, CSharpParseOptions parse)
    {
        Console.WriteLine();
        Console.WriteLine("==== patched: declaration fixes + erroneous bodies stubbed with throw null");
        var watch = Stopwatch.StartNew();
        var trees = compilation.SyntaxTrees.Select(tree =>
        {
            var text = tree.GetText();
            var changes = erroneousBodies.Where(b => b.SyntaxTree == tree)
                                         .Select(b => new TextChange(BodyRegion(b)!.Value, b is ArrowExpressionClauseSyntax || ((b as BaseMethodDeclarationSyntax)?.ExpressionBody ?? (b as AccessorDeclarationSyntax)?.ExpressionBody) is not null
                                                                         ? "=> throw null" : "{ throw null; }"))
                                         .ToList();
            var source = text.WithChanges(changes).ToString();
            var name = Path.GetFileName(tree.FilePath);
            if (name == "_AssemblyAttributes.cs")
                source = source.Replace("[module: RefSafetyRules(11)]", "").Replace("[module: NullablePublicOnly(false)]", "");
            if (outside.Any(e => e.Id == "CS0102" && e.Location.SourceTree == tree))
            {
                // a field-like event and its compiler-generated backing field both decompiled: keep the event, drop the field
                var root = CSharpSyntaxTree.ParseText(source, parse).GetRoot();
                var events = root.DescendantNodes().OfType<EventFieldDeclarationSyntax>().SelectMany(e => e.Declaration.Variables).Select(v => v.Identifier.Text).ToHashSet();
                var fields = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                                 .Where(f => f.Declaration.Variables.Count == 1 && events.Contains(f.Declaration.Variables[0].Identifier.Text))
                                 .ToList();
                source = root.RemoveNodes(fields, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString();
            }

            if (outside.Any(e => e.Id == "CS0216" && e.Location.SourceTree == tree))
            {
                // the trimmed framework dropped an unused operator of a pair: add it back as the negation
                var root = CSharpSyntaxTree.ParseText(source, parse).GetRoot();
                var lonely = root.DescendantNodes().OfType<OperatorDeclarationSyntax>()
                                 .Where(o => o.OperatorToken.Text == "==" &&
                                             !o.Parent!.ChildNodes().OfType<OperatorDeclarationSyntax>().Any(p => p.OperatorToken.Text == "!="))
                                 .ToList();
                source = root.GetText().WithChanges(lonely.Select(o => new TextChange(new TextSpan(o.Span.End, 0),
                    $"\npublic static bool operator !=({o.ParameterList.Parameters[0].Type} lhs, {o.ParameterList.Parameters[1].Type} rhs) => !(lhs == rhs);\n"))).ToString();
            }

            return CSharpSyntaxTree.ParseText(SourceText.From(source), parse, tree.FilePath);
        }).ToArray();
        var patched = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(trees);
        var errors = patched.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Console.WriteLine($"patched compilation: {errors.Count} errors ({watch.Elapsed.TotalSeconds:F1}s)");
        foreach (var error in errors.Take(10))
            Console.WriteLine($"    {error.Id}: {Short(error.GetMessage(), 120)} @ {Where(error)}");
        ErrorsByTree.Clear();
        foreach (var group in errors.Where(e => e.Location.SourceTree is not null).GroupBy(e => e.Location.SourceTree))
            ErrorsByTree[group.Key] = group.ToList();
        Driver(patched, parse);
    }

    private static void Members(CSharpCompilation compilation)
    {
        Console.WriteLine();
        Console.WriteLine("==== members");
        (string Type, string Name, string[] Parameters)[] targets =
        [
            ("System.String", "Join", ["string", "string[]"]),
            ("System.String", "Join", ["string", "System.Collections.Generic.IEnumerable<string>"]),
            ("System.String", "Join", ["string", "System.Collections.Generic.IEnumerable<T>"]),
            ("System.String", "Join", ["string", "object[]"]),
            ("System.String", "Split", ["char[]"]),
            ("System.Collections.Generic.EqualityComparer`1", "Equals", ["T", "T"]),
            ("System.Collections.Generic.EqualityComparer`1", "get_Default", []),
            ("System.Collections.Generic.ComparerHelpers", "CreateDefaultEqualityComparer", ["System.Type"]),
            ("System.Collections.Generic.GenericEqualityComparer`1", "Equals", ["T", "T"]),
            ("System.Collections.Generic.ObjectEqualityComparer`1", "Equals", ["T", "T"]),
            ("System.Collections.Generic.EnumEqualityComparer`1", "Equals", ["T", "T"]),
            ("System.Collections.Generic.NullableEqualityComparer`1", "Equals", ["T?", "T?"]),
            ("System.Threading.CancellationToken", "Register", ["System.Action"]),
            ("System.Runtime.InteropServices.CollectionsMarshal", "SetCount", ["System.Collections.Generic.List<T>", "int"]),
        ];
        foreach (var (typeName, name, parameters) in targets)
        {
            var type = compilation.GetTypeByMetadataName(typeName);
            var method = type?.GetMembers(name).OfType<IMethodSymbol>()
                              .FirstOrDefault(m => m.Parameters.Select(p => p.Type.ToDisplayString().Replace("?", "")).SequenceEqual(parameters.Select(p => p.Replace("?", ""))) &&
                                                   (!parameters.Contains("T?") || m.Parameters[0].Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T));
            if (method is null)
            {
                Console.WriteLine($"{typeName}.{name}({string.Join(", ", parameters)}): NOT FOUND " +
                                  $"(candidates: {string.Join(" | ", type?.GetMembers(name).Select(m => m.ToDisplayString()) ?? [])})");
                continue;
            }

            var own = BodyErrors(method);
            Console.WriteLine($"{method.ToDisplayString()}: {Status(method, own)}");
            foreach (var error in own.Take(5))
                Console.WriteLine($"      {error.Id}: {Short(error.GetMessage(), 140)}");
            foreach (var callee in Callees(compilation, method))
            {
                var calleeErrors = BodyErrors(callee);
                Console.WriteLine($"    -> {callee.ToDisplayString()}: {Status(callee, calleeErrors)}");
                foreach (var error in calleeErrors.Take(3))
                    Console.WriteLine($"          {error.Id}: {Short(error.GetMessage(), 130)}");
            }
        }
    }

    private static void Driver(CSharpCompilation coreLib, CSharpParseOptions parse)
    {
        Console.WriteLine();
        Console.WriteLine("==== driver");
        const string Source = """
            public sealed class Probe
            {
                public override string ToString() => "probe";
            }

            public static class Driver
            {
                public static string Run() => string.Join(",", new object[] { new Probe() });
            }
            """;
        var watch = Stopwatch.StartNew();
        ReportDriver("CompilationReference", coreLib.ToMetadataReference(), Source, parse, coreLib);
        Console.WriteLine($"  ({watch.Elapsed.TotalSeconds:F1}s)");

        watch.Restart();
        using var metadataOnly = new MemoryStream();
        var metadataResult = coreLib.Emit(metadataOnly, options: new EmitOptions(metadataOnly: true));
        Console.WriteLine($"CoreLib metadata-only emit: success {metadataResult.Success}, " +
                          $"{metadataResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} errors, {metadataOnly.Length / 1024} KB, {watch.Elapsed.TotalSeconds:F1}s");
        foreach (var group in metadataResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).GroupBy(d => d.Id).OrderByDescending(g => g.Count()).Take(8))
            Console.WriteLine($"    {group.Key} x{group.Count()}: {Short(group.First().GetMessage(), 110)} @ {Where(group.First())}");
        if (metadataResult.Success)
            ReportDriver("metadata-only image", MetadataReference.CreateFromImage(metadataOnly.ToArray()), Source, parse, null);

        watch.Restart();
        using var full = new MemoryStream();
        var fullResult = coreLib.Emit(full);
        Console.WriteLine($"CoreLib full emit: success {fullResult.Success}, " +
                          $"{fullResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} errors, {full.Length / 1024} KB, {watch.Elapsed.TotalSeconds:F1}s; " +
                          $"peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MB");
        if (fullResult.Success)
            ReportDriver("full image", MetadataReference.CreateFromImage(full.ToArray()), Source, parse, null);
    }

    private static void ReportDriver(string label, MetadataReference reference, string source, CSharpParseOptions parse, CSharpCompilation coreLib)
    {
        var tree = CSharpSyntaxTree.ParseText(source, parse);
        var driver = CSharpCompilation.Create("Driver", [tree], [reference], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = driver.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var model = driver.GetSemanticModel(tree);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var target = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        var toString = model.GetDeclaredSymbol(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First()) as IMethodSymbol;
        var corlib = driver.GetSpecialType(SpecialType.System_Object).ContainingAssembly;
        Console.WriteLine($"driver against {label}: {errors.Count} errors; corlib {corlib?.Identity}; string.Join binds to " +
                          $"{target?.ToDisplayString() ?? "nothing"}; Probe.ToString overrides {toString?.OverriddenMethod?.ToDisplayString() ?? "nothing"}");
        foreach (var error in errors.Take(8))
            Console.WriteLine($"    {error.Id}: {Short(error.GetMessage(), 140)}");
        if (coreLib is not null && target is not null)
        {
            var source2 = target.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault();
            Console.WriteLine($"    bound overload is source in the CoreLib compilation: {source2 is not null}; its body: {Status(target.OriginalDefinition, BodyErrors(target.OriginalDefinition))}");
        }

        using var stream = new MemoryStream();
        var emit = driver.Emit(stream);
        Console.WriteLine($"    driver emit: success {emit.Success}, {stream.Length} bytes" +
                          (emit.Success ? "" : "; " + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3).Select(d => $"{d.Id} {Short(d.GetMessage(), 100)}"))));
    }

    // Direct callees of a member's body: invoked methods, constructors, property accessors, method groups, user-defined operators.
    private static List<IMethodSymbol> Callees(CSharpCompilation compilation, IMethodSymbol method)
    {
        var declaration = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (declaration is null)
            return [];
        var model = compilation.GetSemanticModel(declaration.SyntaxTree);
        var root = model.GetOperation(declaration);
        var operations = root is not null
            ? root.DescendantsAndSelf()
            : declaration.DescendantNodes().Select(n => model.GetOperation(n)).Where(o => o is not null);
        var callees = new List<IMethodSymbol>();
        foreach (var operation in operations)
        {
            IMethodSymbol callee = operation switch
            {
                IInvocationOperation invocation => invocation.TargetMethod,
                IObjectCreationOperation creation => creation.Constructor,
                IPropertyReferenceOperation property => operation.Parent is ISimpleAssignmentOperation assignment && assignment.Target == operation
                    ? property.Property.SetMethod
                    : property.Property.GetMethod,
                IMethodReferenceOperation reference => reference.Method,
                IBinaryOperation binary => binary.OperatorMethod,
                IUnaryOperation unary => unary.OperatorMethod,
                IConversionOperation conversion => conversion.OperatorMethod,
                _ => null,
            };
            if (callee?.OriginalDefinition is { } definition && !callees.Contains(definition, SymbolEqualityComparer.Default))
                callees.Add(definition);
        }

        return callees;
    }

    private static List<Diagnostic> BodyErrors(IMethodSymbol method)
    {
        var declaration = method.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (declaration is null || BodyRegion(declaration) is not { } region)
            return [];
        return ErrorsByTree.TryGetValue(declaration.SyntaxTree, out var list)
            ? list.Where(e => region.Contains(e.Location.SourceSpan)).ToList()
            : [];
    }

    private static string Status(IMethodSymbol method, List<Diagnostic> errors)
    {
        var declaration = method.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (declaration is null)
            return "no source (implicit or metadata)";
        if (BodyRegion(declaration) is null)
            return method.IsExtern ? "no body (extern)" : method.IsAbstract ? "no body (abstract)" : "no body";
        return errors.Count == 0 ? "body clean" : $"body has {errors.Count} errors [{string.Join(", ", errors.Select(e => e.Id).Distinct())}]";
    }

    private static SyntaxNode Holder(SyntaxNode node, TextSpan span)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (BodyRegion(current) is { } region && region.Contains(span))
                return current;
        }

        return null;
    }

    private static TextSpan? BodyRegion(SyntaxNode node)
    {
        SyntaxNode body = node switch
        {
            BaseMethodDeclarationSyntax method => (SyntaxNode)method.Body ?? method.ExpressionBody,
            AccessorDeclarationSyntax accessor => (SyntaxNode)accessor.Body ?? accessor.ExpressionBody,
            // the getter of an expression-bodied property or indexer is declared by its arrow clause
            ArrowExpressionClauseSyntax arrow when arrow.Parent is PropertyDeclarationSyntax or IndexerDeclarationSyntax => arrow,
            _ => null,
        };
        if (body is null)
            return null;
        var start = node is ConstructorDeclarationSyntax { Initializer: { } initializer } ? initializer.SpanStart : body.SpanStart;
        return TextSpan.FromBounds(start, body.Span.End);
    }

    private static string Where(Diagnostic diagnostic)
    {
        var line = diagnostic.Location.GetLineSpan();
        return $"{Path.GetFileName(line.Path)}:{line.StartLinePosition.Line + 1}";
    }

    private static string Short(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}
