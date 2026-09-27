// Can decompiled library code feed a Roslyn frontend? Decompile a whole assembly, compile it against the reference packs,
// and count the method bodies Roslyn binds without an error.
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var pkg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
string[] referenceDirs =
[
    @"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\8.0.31\ref\net8.0",
    @"C:\Program Files\dotnet\packs\Microsoft.AspNetCore.App.Ref\8.0.31\ref\net8.0",
    Path.Combine(pkg, "microsoft.entityframeworkcore.abstractions", "8.0.0", "lib", "net8.0"),
];

foreach (var assembly in args)
{
    var started = DateTime.UtcNow;
    var module = new PEFile(assembly);
    var resolver = new UniversalAssemblyResolver(assembly, false, module.DetectTargetFrameworkId());
    foreach (var dir in referenceDirs.Append(Path.GetDirectoryName(assembly)))
        resolver.AddSearchDirectory(dir);
    var decompiler = new CSharpDecompiler(module, resolver, new DecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false });
    var code = decompiler.DecompileWholeModuleAsString();
    var decompileSeconds = (DateTime.UtcNow - started).TotalSeconds;
    if (Environment.GetEnvironmentVariable("DUMP_DIR") is { Length: > 0 } dump)
        File.WriteAllText(Path.Combine(dump, Path.GetFileNameWithoutExtension(assembly) + ".cs"), code);

    var self = Path.GetFileName(assembly);
    var references = referenceDirs.Where(Directory.Exists)
                                  .SelectMany(d => Directory.EnumerateFiles(d, "*.dll"))
                                  .Concat(Directory.EnumerateFiles(Path.GetDirectoryName(assembly), "*.dll"))
                                  .Where(p => !string.Equals(Path.GetFileName(p), self, StringComparison.OrdinalIgnoreCase))
                                  .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                                  .Select(g => MetadataReference.CreateFromFile(g.First()));
    var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview));
    var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(assembly), [tree], references,
                                               new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                                                                            nullableContextOptions: NullableContextOptions.Enable));
    var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    var bodies = tree.GetRoot().DescendantNodes()
                     .Where(n => n is BaseMethodDeclarationSyntax { Body: not null } or BaseMethodDeclarationSyntax { ExpressionBody: not null } or
                                     AccessorDeclarationSyntax { Body: not null } or AccessorDeclarationSyntax { ExpressionBody: not null })
                     .ToList();
    var errorSpans = errors.Select(e => e.Location.SourceSpan).ToList();
    var clean = bodies.Count(b => !errorSpans.Any(s => b.Span.Contains(s)));
    Console.WriteLine($"{Path.GetFileName(assembly)}: {code.Length / 1024} KB decompiled in {decompileSeconds:F1}s; " +
                      $"{errors.Count} errors; method bodies without an error {clean}/{bodies.Count} ({100.0 * clean / Math.Max(1, bodies.Count):F1}%)");
    foreach (var group in errors.GroupBy(e => e.Id).OrderByDescending(g => g.Count()).Take(6))
        Console.WriteLine($"    {group.Key} x{group.Count()}: {group.First().GetMessage()[..Math.Min(110, group.First().GetMessage().Length)]}");
}
