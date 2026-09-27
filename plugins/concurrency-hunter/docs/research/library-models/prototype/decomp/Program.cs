// Decompile each gold member and its direct callees (ICSharpCode.Decompiler 9.1 from the Visual Studio install) into
// ai-input-decompiled.md: the same entries as ai-input.md, with the implementation in place of nothing.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

var root = args[0];
var pkg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
string[] dirs =
[
    @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.31",
    @"C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App\8.0.31",
    Path.Combine(pkg, "polly", "7.2.3", "lib", "netstandard2.0"),
    Path.Combine(pkg, "google.protobuf", "3.21.9", "lib", "net5.0"),
    Path.Combine(pkg, "serilog.aspnetcore", "6.1.0-dev-00289", "lib", "net5.0"),
    Path.Combine(pkg, "serilog.extensions.hosting", "5.0.1", "lib", "netstandard2.1"),
    Path.Combine(pkg, "serilog", "2.12.0", "lib", "net5.0"),
    Path.Combine(pkg, "swashbuckle.aspnetcore.swaggergen", "6.4.0", "lib", "net6.0"),
    Path.Combine(pkg, "microsoft.aspnetcore.authentication.jwtbearer", "8.0.0", "lib", "net8.0"),
    Path.Combine(pkg, "grpc.aspnetcore.server", "2.50.0", "lib", "net7.0"),
    Path.Combine(pkg, "microsoft.entityframeworkcore", "8.0.0", "lib", "net8.0"),
    Path.Combine(pkg, "duende.identityserver", "6.2.0", "lib", "net7.0"),
];
const int BUDGET = 6000;

var decompilers = new Dictionary<string, CSharpDecompiler>(StringComparer.OrdinalIgnoreCase);
CSharpDecompiler DecompilerFor(string file)
{
    if (decompilers.TryGetValue(file, out var existing))
        return existing;
    var module = new PEFile(file);
    var resolver = new UniversalAssemblyResolver(file, false, module.DetectTargetFrameworkId());
    foreach (var dir in dirs)
        resolver.AddSearchDirectory(dir);
    var settings = new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false };
    return decompilers[file] = new CSharpDecompiler(module, resolver, settings);
}

var assemblies = dirs.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.dll")).ToList();
var byName = assemblies.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

IEntity Find(string id, string assemblyHint)
{
    var candidates = byName.TryGetValue(assemblyHint, out var hinted) ? new[] { hinted } : Array.Empty<string>();
    // the documented assembly is often a facade; fall back to every assembly that declares the type
    var typeName = Regex.Match(id, @"^[MP]:([^(]+)\.[^.(]+").Groups[1].Value.Split('`')[0];
    var others = assemblies.Where(a => !candidates.Contains(a));
    foreach (var file in candidates.Concat(others))
    {
        CSharpDecompiler decompiler;
        try
        {
            if (!candidates.Contains(file) && !ContainsType(file, typeName))
                continue;
            decompiler = DecompilerFor(file);
        }
        catch (Exception)
        {
            continue;
        }

        var entity = IdStringProvider.FindEntity(id, new SimpleTypeResolveContext(decompiler.TypeSystem.MainModule));
        if (entity is not null && entity.ParentModule == decompiler.TypeSystem.MainModule)
            return entity;
    }

    return null;
}

static bool ContainsType(string file, string typeName)
{
    try
    {
        using var stream = File.OpenRead(file);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (!pe.HasMetadata)
            return false;
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var dot = typeName.LastIndexOf('.');
        var ns = dot < 0 ? "" : typeName[..dot];
        var name = typeName[(dot + 1)..];
        return md.TypeDefinitions.Any(h =>
        {
            var t = md.GetTypeDefinition(h);
            return md.GetString(t.Name).Split('`')[0] == name && md.GetString(t.Namespace) == ns;
        });
    }
    catch (Exception)
    {
        return false;
    }
}

string Decompile(IEntity entity)
{
    var file = entity.ParentModule.MetadataFile.FileName;
    return DecompilerFor(file).DecompileAsString(entity.MetadataToken).Trim();
}

IEnumerable<IMethod> Callees(IEntity entity)
{
    var tree = DecompilerFor(entity.ParentModule.MetadataFile.FileName).Decompile(entity.MetadataToken);
    foreach (var node in tree.Descendants)
    {
        if (node is InvocationExpression or ObjectCreateExpression && node.GetSymbol() is IMethod method)
            yield return (IMethod)method.MemberDefinition;
    }
}

var gold = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, args.Length > 1 ? args[1] : "gold.json"))).RootElement;
var docs = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "members-docs.json"))).RootElement;
var output = new StringBuilder();
var number = 0;
foreach (var entry in gold.EnumerateArray())
{
    number++;
    var key = entry.GetProperty("key").GetString();
    var id = entry.GetProperty("id").GetString();
    var parameters = entry.GetProperty("delegateParams").EnumerateArray().Select(p => p.GetString()).ToArray();
    output.AppendLine($"### {number}. `{id}`");
    output.AppendLine($"Assembly: {entry.GetProperty("assembly").GetString()} {entry.GetProperty("version").GetString()}");
    if (docs.TryGetProperty(key, out var docList) && docList.GetArrayLength() > 0)
    {
        var doc = docList.EnumerateArray().First(d => d.GetProperty("id").GetString() == id);
        if (doc.GetProperty("summary").GetString() is { Length: > 0 } summary)
            output.AppendLine("Summary: " + summary);
    }

    output.AppendLine($"Parameter(s) to classify: {(parameters.Length > 0 ? string.Join(", ", parameters) : "(none: the member takes no delegate; classify the call itself)")}");
    var entity = Find(id, entry.GetProperty("assembly").GetString());
    if (entity is null)
    {
        output.AppendLine("Decompiled implementation: (not found)");
        output.AppendLine();
        Console.WriteLine($"{key}: not found");
        continue;
    }

    if (entity is IProperty property)
        entity = parameters.Length > 0 && property.Setter is not null ? property.Setter : property.Getter;
    var text = new StringBuilder();
    try
    {
        text.AppendLine("// " + entity.FullName);
        text.AppendLine(Decompile(entity));
        var seen = new HashSet<string> { entity.FullName };
        foreach (var callee in Callees(entity))
        {
            if (text.Length > BUDGET || !seen.Add(callee.FullName + callee.Parameters.Count) || callee.ParentModule?.MetadataFile is null ||
                callee.DeclaringType?.Namespace is "System" && callee.DeclaringType.Name is "ThrowHelper" or "ArgumentNullException" or "ArgumentException" ||
                callee.FullName.Contains("ThrowHelper"))
                continue;
            string body;
            try
            {
                body = Decompile(callee);
            }
            catch (Exception)
            {
                continue;
            }

            if (body.Length > 2500)
                body = body[..2500] + "\n// ... truncated";
            text.AppendLine();
            text.AppendLine("// callee " + callee.FullName);
            text.AppendLine(body);
        }
    }
    catch (Exception ex)
    {
        text.AppendLine("// decompilation failed: " + ex.GetType().Name);
    }

    var code = text.ToString();
    if (code.Length > BUDGET + 2500)
        code = code[..(BUDGET + 2500)] + "\n// ... truncated";
    output.AppendLine("Decompiled implementation (member, then its direct callees):");
    output.AppendLine("```csharp");
    output.AppendLine(code.TrimEnd());
    output.AppendLine("```");
    output.AppendLine();
    Console.WriteLine($"{key}: {code.Length} chars");
}

File.WriteAllText(Path.Combine(root, args.Length > 2 ? args[2] : "ai-input-decompiled.md"), output.ToString());
