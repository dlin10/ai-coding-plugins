// IL probe: what does a library member do with one of its delegate parameters?
// Prototype for a research question; not production code. Taint-style abstract interpretation over IL, interprocedural
// with per-(method, argument) summaries and class-hierarchy analysis over the loaded library assemblies.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

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

var world = new World(dirs);
Console.WriteLine($"assemblies: {world.Asms.Count}, types: {world.Types.Count}");
var analyzer = new Analyzer(world);
var gold = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "gold.json"))).RootElement;
var output = new List<Dictionary<string, object>>();

foreach (var entry in gold.EnumerateArray())
{
    var key = entry.GetProperty("key").GetString();
    var id = entry.GetProperty("id").GetString();
    var goldLabel = entry.GetProperty("label").GetString();
    var parameters = entry.GetProperty("delegateParams").EnumerateArray().Select(p => p.GetString()).ToArray();
    var row = new Dictionary<string, object> { ["key"] = key, ["id"] = id, ["gold"] = goldLabel };
    output.Add(row);
    if (parameters.Length == 0 || goldLabel == "non-delegate" && key != "Ok")
    {
        row["probe"] = "skipped";
        continue;
    }

    var method = world.FindByDocId(id);
    if (method is not { } target)
    {
        row["probe"] = "not-found";
        Console.WriteLine($"{key}: NOT FOUND {id}");
        continue;
    }

    var perParameter = new Dictionary<string, object>();
    foreach (var parameter in parameters)
    {
        var argument = world.ArgumentIndex(target, parameter);
        if (argument < 0)
        {
            perParameter[parameter] = new { label = "param-not-found" };
            continue;
        }

        var events = new HashSet<Event>();
        var bodies = world.Targets(target, isVirtualCall: true);
        if (bodies is null or { Count: 0 })
            events.Add(new Event(Ev.Escaped, "target itself: " + world.Display(target)));
        else
            foreach (var body in bodies)
                events.UnionWith(analyzer.Summary(body, argument, 0));
        var label = Classify(events, world.ReturnsSequence(target));
        perParameter[parameter] = new
        {
            label,
            clean = !events.Any(e => e.Kind is Ev.Escaped or Ev.DepthLimit),
            events = events.Select(e => $"{e.Kind}{(e.Detail.Length > 0 ? ": " + e.Detail : "")}").OrderBy(s => s).ToArray()
        };
    }

    row["probe"] = perParameter;
    Console.WriteLine($"{key,-42} gold={goldLabel,-16} probe=" +
                      string.Join(", ", perParameter.Select(p => $"{p.Key}:{((dynamic)p.Value).label}{(((dynamic)p.Value).clean ? "" : "*")}")));
}

File.WriteAllText(Path.Combine(root, "il-verdicts.json"), JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));

static string Classify(HashSet<Event> events, bool returnsSequence)
{
    bool Has(Ev kind) => events.Any(e => e.Kind == kind);
    var invoked = Has(Ev.Invoked);
    var stored = Has(Ev.StoredInReceiver) || Has(Ev.StoredInArg) || Has(Ev.StoredInStatic);
    var returned = Has(Ev.Returned);
    if (Has(Ev.RegisteredDI))
        return "di-registration";
    if (returned && !invoked)
        return returnsSequence ? "iterator" : "holder";
    if (invoked && !stored && !returned)
        return "invoke-now";
    if (invoked)
        return "invoke-now+stored";
    if (stored)
        return "stored";
    return events.Count == 0 ? "no-effect" : "escaped";
}

enum Ev { Invoked, StoredInReceiver, StoredInArg, StoredInStatic, Returned, RegisteredDI, Escaped, DepthLimit }

readonly record struct Event(Ev Kind, string Detail);

sealed class Asm
{
    public string Name;
    public PEReader Pe;
    public MetadataReader Md;
}

readonly record struct TypeKey(Asm Asm, TypeDefinitionHandle Handle);

readonly record struct MethodKey(Asm Asm, MethodDefinitionHandle Handle);

sealed record Sig(string Doc, string Def);

sealed class SigProvider : ISignatureTypeProvider<Sig, object>
{
    public static readonly SigProvider I = new();

    public Sig GetArrayType(Sig element, ArrayShape shape) =>
        new(element.Doc + "[" + string.Join(",", Enumerable.Range(0, shape.Rank).Select(_ => "0:")) + "]", null);

    public Sig GetByReferenceType(Sig element) => new(element.Doc + "@", element.Def);

    public Sig GetFunctionPointerType(MethodSignature<Sig> signature) => new("=FUNC", null);

    public Sig GetGenericInstantiation(Sig generic, ImmutableArray<Sig> arguments) =>
        new(Regex.Replace(generic.Doc, @"`\d+$", "") + "{" + string.Join(",", arguments.Select(a => a.Doc)) + "}", generic.Def);

    public Sig GetGenericMethodParameter(object context, int index) => new("``" + index, null);

    public Sig GetGenericTypeParameter(object context, int index) => new("`" + index, null);

    public Sig GetModifiedType(Sig modifier, Sig unmodified, bool isRequired) => unmodified;

    public Sig GetPinnedType(Sig element) => element;

    public Sig GetPointerType(Sig element) => new(element.Doc + "*", null);

    public Sig GetPrimitiveType(PrimitiveTypeCode code) => new("System." + code, "System." + code);

    public Sig GetSZArrayType(Sig element) => new(element.Doc + "[]", null);

    public Sig GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var name = World.DefName(reader, handle);
        return new Sig(name, name);
    }

    public Sig GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var name = World.RefName(reader, handle);
        return new Sig(name, name);
    }

    public Sig GetTypeFromSpecification(MetadataReader reader, object context, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, context);
}

sealed record Callee(string DeclType, string Name, MethodSignature<Sig> Sig, MethodKey? Def)
{
    public string Display => $"{DeclType}.{Name}";
}

sealed class World
{
    public readonly List<Asm> Asms = [];
    public readonly Dictionary<string, List<TypeKey>> Types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TypeKey>> _subtypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _delegates = new(StringComparer.Ordinal);

    public World(IEnumerable<string> directories)
    {
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                Console.WriteLine("missing directory " + directory);
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
            {
                try
                {
                    var pe = new PEReader(ImmutableArray.Create(File.ReadAllBytes(path)));
                    if (!pe.HasMetadata)
                        continue;
                    var md = pe.GetMetadataReader();
                    if (!md.IsAssembly)
                        continue;
                    var name = md.GetString(md.GetAssemblyDefinition().Name);
                    if (Asms.Any(a => a.Name == name))
                        continue;
                    var asm = new Asm { Name = name, Pe = pe, Md = md };
                    Asms.Add(asm);
                    Index(asm);
                }
                catch (BadImageFormatException)
                {
                }
            }
        }
    }

    public static string DefName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        var name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
            return DefName(md, declaring) + "." + name;
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string RefName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        var name = md.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return RefName(md, (TypeReferenceHandle)type.ResolutionScope) + "." + name;
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string TypeName(MetadataReader md, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => DefName(md, (TypeDefinitionHandle)handle),
        HandleKind.TypeReference => RefName(md, (TypeReferenceHandle)handle),
        HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(SigProvider.I, null).Def,
        _ => null
    };

    private void Index(Asm asm)
    {
        foreach (var handle in asm.Md.TypeDefinitions)
        {
            var name = DefName(asm.Md, handle);
            var key = new TypeKey(asm, handle);
            Add(Types, name, key);
            var type = asm.Md.GetTypeDefinition(handle);
            if (!type.BaseType.IsNil && TypeName(asm.Md, type.BaseType) is { } baseName)
                Add(_subtypes, baseName, key);
            foreach (var implementation in type.GetInterfaceImplementations())
            {
                if (TypeName(asm.Md, asm.Md.GetInterfaceImplementation(implementation).Interface) is { } interfaceName)
                    Add(_subtypes, interfaceName, key);
            }
        }
    }

    private static void Add(Dictionary<string, List<TypeKey>> map, string name, TypeKey key)
    {
        if (!map.TryGetValue(name, out var list))
            map[name] = list = [];
        list.Add(key);
    }

    public TypeKey? FindType(string name, Asm prefer = null)
    {
        if (name is null || !Types.TryGetValue(name, out var list))
            return null;
        foreach (var key in list)
        {
            if (key.Asm == prefer)
                return key;
        }

        return list[0];
    }

    public bool IsDelegate(string typeName)
    {
        if (typeName is null)
            return false;
        if (_delegates.TryGetValue(typeName, out var known))
            return known;
        var result = false;
        var current = typeName;
        for (var depth = 0; depth < 6 && current is not null; depth++)
        {
            if (current is "System.MulticastDelegate" or "System.Delegate")
            {
                result = depth > 0;
                break;
            }

            if (FindType(current) is not { } key)
                break;
            var type = key.Asm.Md.GetTypeDefinition(key.Handle);
            current = type.BaseType.IsNil ? null : TypeName(key.Asm.Md, type.BaseType);
        }

        _delegates[typeName] = result;
        return result;
    }

    public string MethodDocId(MethodKey method)
    {
        var md = method.Asm.Md;
        var definition = md.GetMethodDefinition(method.Handle);
        var type = DefName(md, definition.GetDeclaringType());
        var name = md.GetString(definition.Name).Replace('.', '#');
        var signature = definition.DecodeSignature(SigProvider.I, null);
        var id = "M:" + type + "." + name;
        if (signature.GenericParameterCount > 0)
            id += "``" + signature.GenericParameterCount;
        if (signature.ParameterTypes.Length > 0)
            id += "(" + string.Join(",", signature.ParameterTypes.Select(p => p.Doc)) + ")";
        return id;
    }

    public string Display(MethodKey method)
    {
        var md = method.Asm.Md;
        var definition = md.GetMethodDefinition(method.Handle);
        return DefName(md, definition.GetDeclaringType()) + "." + md.GetString(definition.Name);
    }

    public MethodKey? FindByDocId(string id)
    {
        if (id.StartsWith("P:", StringComparison.Ordinal))
        {
            var dot = id.LastIndexOf('.');
            var typeName = id[2..dot];
            var property = id[(dot + 1)..];
            if (FindType(typeName) is not { } propertyType)
                return null;
            foreach (var handle in propertyType.Asm.Md.GetTypeDefinition(propertyType.Handle).GetMethods())
            {
                if (propertyType.Asm.Md.GetString(propertyType.Asm.Md.GetMethodDefinition(handle).Name) == "set_" + property)
                    return new MethodKey(propertyType.Asm, handle);
            }

            return null;
        }

        var paren = id.IndexOf('(');
        var head = paren < 0 ? id : id[..paren];
        var nameStart = head.LastIndexOf('.');
        var owner = head[2..nameStart];
        if (FindType(owner) is not { } key)
            return null;
        foreach (var handle in key.Asm.Md.GetTypeDefinition(key.Handle).GetMethods())
        {
            var candidate = new MethodKey(key.Asm, handle);
            if (MethodDocId(candidate) == id)
                return candidate;
        }

        return null;
    }

    public int ArgumentIndex(MethodKey method, string parameterName)
    {
        var md = method.Asm.Md;
        var definition = md.GetMethodDefinition(method.Handle);
        var isStatic = definition.Attributes.HasFlag(MethodAttributes.Static);
        foreach (var handle in definition.GetParameters())
        {
            var parameter = md.GetParameter(handle);
            if (parameter.SequenceNumber > 0 && md.GetString(parameter.Name) == parameterName)
                return parameter.SequenceNumber - 1 + (isStatic ? 0 : 1);
        }

        return -1;
    }

    public bool ReturnsSequence(MethodKey method)
    {
        var doc = method.Asm.Md.GetMethodDefinition(method.Handle).DecodeSignature(SigProvider.I, null).ReturnType.Doc;
        return doc.Contains("IEnumerable{") || doc.Contains("IOrderedEnumerable{");
    }

    public Callee DecodeCall(Asm asm, EntityHandle handle)
    {
        var md = asm.Md;
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                var definition = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                return new Callee(DefName(md, definition.GetDeclaringType()), md.GetString(definition.Name),
                                  definition.DecodeSignature(SigProvider.I, null), new MethodKey(asm, (MethodDefinitionHandle)handle));
            }
            case HandleKind.MemberReference:
            {
                var reference = md.GetMemberReference((MemberReferenceHandle)handle);
                var name = md.GetString(reference.Name);
                var signature = reference.DecodeMethodSignature(SigProvider.I, null);
                var owner = reference.Parent.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification
                    ? TypeName(md, (EntityHandle)reference.Parent)
                    : null;
                return new Callee(owner, name, signature, FindMethod(owner, name, signature, asm));
            }
            case HandleKind.MethodSpecification:
                return DecodeCall(asm, md.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
            default:
                throw new InvalidOperationException(handle.Kind.ToString());
        }
    }

    private MethodKey? FindMethod(string owner, string name, MethodSignature<Sig> signature, Asm prefer)
    {
        if (FindType(owner, prefer) is not { } key)
            return null;
        var wanted = signature.ParameterTypes.Select(p => p.Doc).ToArray();
        MethodKey? fallback = null;
        foreach (var handle in key.Asm.Md.GetTypeDefinition(key.Handle).GetMethods())
        {
            var definition = key.Asm.Md.GetMethodDefinition(handle);
            if (key.Asm.Md.GetString(definition.Name) != name)
                continue;
            var candidate = definition.DecodeSignature(SigProvider.I, null);
            if (candidate.ParameterTypes.Length != wanted.Length || candidate.GenericParameterCount != signature.GenericParameterCount)
                continue;
            if (candidate.ParameterTypes.Select(p => p.Doc).SequenceEqual(wanted))
                return new MethodKey(key.Asm, handle);
            fallback ??= new MethodKey(key.Asm, handle);
        }

        return fallback;
    }

    /// <summary>The bodies a call may run: the method itself, or for a virtual call every implementation the loaded assemblies
    /// declare (class-hierarchy analysis), or null when there is none or too many.</summary>
    public List<MethodKey> Targets(MethodKey method, bool isVirtualCall)
    {
        var md = method.Asm.Md;
        var definition = md.GetMethodDefinition(method.Handle);
        var owner = md.GetTypeDefinition(definition.GetDeclaringType());
        var attributes = definition.Attributes;
        var dispatches = isVirtualCall && attributes.HasFlag(MethodAttributes.Virtual) && !attributes.HasFlag(MethodAttributes.Final) &&
                         !owner.Attributes.HasFlag(TypeAttributes.Sealed);
        var result = new List<MethodKey>();
        if (definition.RelativeVirtualAddress != 0)
            result.Add(method);
        if (!dispatches)
            return result;
        var ownerName = DefName(md, definition.GetDeclaringType());
        var name = md.GetString(definition.Name);
        var parameterCount = definition.DecodeSignature(SigProvider.I, null).ParameterTypes.Length;
        var isInterface = owner.Attributes.HasFlag(TypeAttributes.Interface);
        foreach (var subtype in Subtypes(ownerName))
        {
            var subMd = subtype.Asm.Md;
            var type = subMd.GetTypeDefinition(subtype.Handle);
            MethodKey? found = null;
            if (isInterface)
            {
                foreach (var implementationHandle in type.GetMethodImplementations())
                {
                    var implementation = subMd.GetMethodImplementation(implementationHandle);
                    var declaration = DecodeCall(subtype.Asm, implementation.MethodDeclaration);
                    if (declaration.Name == name && declaration.DeclType == ownerName && implementation.MethodBody.Kind == HandleKind.MethodDefinition)
                        found = new MethodKey(subtype.Asm, (MethodDefinitionHandle)implementation.MethodBody);
                }
            }

            if (found is null)
            {
                foreach (var handle in type.GetMethods())
                {
                    var candidate = subMd.GetMethodDefinition(handle);
                    if (subMd.GetString(candidate.Name) == name && candidate.Attributes.HasFlag(MethodAttributes.Virtual) &&
                        candidate.DecodeSignature(SigProvider.I, null).ParameterTypes.Length == parameterCount &&
                        (isInterface || !candidate.Attributes.HasFlag(MethodAttributes.NewSlot)))
                    {
                        found = new MethodKey(subtype.Asm, handle);
                        break;
                    }
                }
            }

            if (found is { } body && body.Asm.Md.GetMethodDefinition(body.Handle).RelativeVirtualAddress != 0)
                result.Add(body);
            if (result.Count > 8)
                return null;
        }

        return result;
    }

    private IEnumerable<TypeKey> Subtypes(string typeName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { typeName };
        var queue = new Queue<string>([typeName]);
        var count = 0;
        while (queue.Count > 0 && count < 400)
        {
            if (!_subtypes.TryGetValue(queue.Dequeue(), out var list))
                continue;
            foreach (var key in list)
            {
                count++;
                yield return key;
                var name = DefName(key.Asm.Md, key.Handle);
                if (seen.Add(name))
                    queue.Enqueue(name);
            }
        }
    }
}

record struct V(bool R, byte Kind, int Index)
{
    // Kind: 0 none, 1 argument, 2 local, 3 site (result of a call or newobj at an IL offset), 4 static-rooted
    public static readonly V None = new(false, 0, 0);
}

sealed record Instr(int Offset, OpCode Op, int Token, int Number, int[] Targets, int Next);

sealed class Analyzer(World world)
{
    private const int MAX_DEPTH = 14;
    private static readonly Dictionary<short, OpCode> Ops = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                                                                            .Select(f => (OpCode)f.GetValue(null))
                                                                            .ToDictionary(o => o.Value);
    private readonly Dictionary<(Asm, MethodDefinitionHandle, int), HashSet<Event>> _cache = new();
    private readonly HashSet<(Asm, MethodDefinitionHandle, int)> _inProgress = [];

    public HashSet<Event> Summary(MethodKey method, int argument, int depth)
    {
        var key = (method.Asm, method.Handle, argument);
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        if (depth > MAX_DEPTH)
            return [new Event(Ev.DepthLimit, world.Display(method))];
        if (!_inProgress.Add(key))
            return [];
        var result = new Run(this, world, method, argument, depth).Execute();
        _inProgress.Remove(key);
        _cache[key] = result;
        return result;
    }

    internal static List<Instr> Decode(byte[] il)
    {
        var list = new List<Instr>();
        var i = 0;
        while (i < il.Length)
        {
            var start = i;
            short value = il[i++];
            if (value == 0xFE)
                value = unchecked((short)(0xFE00 | il[i++]));
            var op = Ops[value];
            int token = 0, number = 0;
            int[] targets = null;
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                {
                    var delta = (sbyte)il[i];
                    i += 1;
                    targets = [i + delta];
                    break;
                }
                case OperandType.ShortInlineI:
                    number = (sbyte)il[i];
                    i += 1;
                    break;
                case OperandType.ShortInlineVar:
                    number = il[i];
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    number = BitConverter.ToUInt16(il, i);
                    i += 2;
                    break;
                case OperandType.InlineBrTarget:
                {
                    var delta = BitConverter.ToInt32(il, i);
                    i += 4;
                    targets = [i + delta];
                    break;
                }
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                {
                    var count = BitConverter.ToInt32(il, i);
                    i += 4;
                    var baseOffset = i + 4 * count;
                    targets = new int[count];
                    for (var k = 0; k < count; k++)
                        targets[k] = baseOffset + BitConverter.ToInt32(il, i + 4 * k);
                    i += 4 * count;
                    break;
                }
                default:
                    token = BitConverter.ToInt32(il, i);
                    number = token;
                    i += 4;
                    break;
            }

            list.Add(new Instr(start, op, token, number, targets, i));
        }

        return list;
    }

    private sealed class Run
    {
        private readonly Analyzer _analyzer;
        private readonly World _world;
        private readonly MethodKey _method;
        private readonly int _depth;
        private readonly bool _isStatic;
        private readonly bool[] _argR;
        private readonly HashSet<int> _localR = [];
        private readonly Dictionary<int, HashSet<V>> _localOrigins = new();
        private readonly HashSet<int> _siteR = [];
        private readonly HashSet<Event> _events = [];
        private int _version;

        public Run(Analyzer analyzer, World world, MethodKey method, int argument, int depth)
        {
            _analyzer = analyzer;
            _world = world;
            _method = method;
            _depth = depth;
            var definition = method.Asm.Md.GetMethodDefinition(method.Handle);
            _isStatic = definition.Attributes.HasFlag(MethodAttributes.Static);
            var count = definition.DecodeSignature(SigProvider.I, null).ParameterTypes.Length + (_isStatic ? 0 : 1);
            _argR = new bool[Math.Max(count, argument + 1)];
            _argR[argument] = true;
        }

        public HashSet<Event> Execute()
        {
            var definition = _method.Asm.Md.GetMethodDefinition(_method.Handle);
            if (definition.RelativeVirtualAddress == 0)
                return [new Event(Ev.Escaped, "no body: " + _world.Display(_method))];
            var body = _method.Asm.Pe.GetMethodBody(definition.RelativeVirtualAddress);
            var code = Decode(body.GetILBytes());
            var index = new Dictionary<int, int>();
            for (var k = 0; k < code.Count; k++)
                index[code[k].Offset] = k;
            var returnsValue = definition.DecodeSignature(SigProvider.I, null).ReturnType.Doc != "System.Void";
            for (var round = 0; round < 20; round++)
            {
                var before = _version;
                var entry = new Dictionary<int, V[]> { [0] = [] };
                foreach (var region in body.ExceptionRegions)
                {
                    entry[region.HandlerOffset] = region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter ? [V.None] : [];
                    if (region.Kind == ExceptionRegionKind.Filter)
                        entry[region.FilterOffset] = [V.None];
                }

                var work = new Stack<int>(entry.Keys);
                var guard = 0;
                while (work.Count > 0 && guard++ < 200_000)
                {
                    var offset = work.Pop();
                    if (!index.TryGetValue(offset, out var at))
                        continue;
                    var stack = new List<V>(entry[offset]);
                    var instruction = code[at];
                    foreach (var (next, nextStack) in Step(instruction, stack, returnsValue))
                    {
                        if (!entry.TryGetValue(next, out var existing))
                        {
                            entry[next] = nextStack;
                            work.Push(next);
                        }
                        else if (Join(existing, nextStack) is { } joined)
                        {
                            entry[next] = joined;
                            work.Push(next);
                        }
                    }
                }

                if (_version == before)
                    break;
            }

            return _events;
        }

        private V[] Join(V[] existing, V[] incoming)
        {
            if (existing.Length != incoming.Length)
                return null;
            V[] result = null;
            for (var k = 0; k < existing.Length; k++)
            {
                var a = existing[k];
                var b = incoming[k];
                var r = IsR(a) || IsR(b);
                var merged = a.Kind == b.Kind && a.Index == b.Index ? a with { R = r } : new V(r, 0, 0);
                if (merged != a)
                {
                    result ??= (V[])existing.Clone();
                    result[k] = merged;
                }
            }

            return result;
        }

        private void AddEvent(Ev kind, string detail = "")
        {
            if (_events.Add(new Event(kind, detail)))
                _version++;
        }

        private bool IsR(V v) => v.R || v.Kind switch
        {
            1 => v.Index < _argR.Length && _argR[v.Index],
            2 => LocalIsR(v.Index, []),
            3 => _siteR.Contains(v.Index),
            _ => false
        };

        private bool LocalIsR(int local, HashSet<int> visited)
        {
            if (_localR.Contains(local))
                return true;
            if (!visited.Add(local) || !_localOrigins.TryGetValue(local, out var origins))
                return false;
            return origins.Any(o => o.Kind switch
            {
                1 => o.Index < _argR.Length && _argR[o.Index],
                2 => LocalIsR(o.Index, visited),
                3 => _siteR.Contains(o.Index),
                _ => false
            });
        }

        private void MarkHolder(V target, HashSet<int> visitedLocals = null)
        {
            switch (target.Kind)
            {
                case 1:
                    if (!_isStatic && target.Index == 0)
                        AddEvent(Ev.StoredInReceiver);
                    else
                        AddEvent(Ev.StoredInArg, target.Index.ToString());
                    break;
                case 2:
                    if (_localR.Add(target.Index))
                        _version++;
                    visitedLocals ??= [];
                    if (visitedLocals.Add(target.Index) && _localOrigins.TryGetValue(target.Index, out var origins))
                    {
                        foreach (var origin in origins.ToArray())
                            MarkHolder(origin, visitedLocals);
                    }

                    break;
                case 3:
                    if (_siteR.Add(target.Index))
                        _version++;
                    break;
                case 4:
                    AddEvent(Ev.StoredInStatic, "static-rooted object");
                    break;
                default:
                    AddEvent(Ev.Escaped, "store into untracked object in " + _world.Display(_method));
                    break;
            }
        }

        private static int PopCount(OpCode op)
        {
            var name = op.StackBehaviourPop.ToString();
            return name switch { "Pop0" => 0, "Varpop" => -1, _ => name.Split('_').Length };
        }

        private static int PushCount(OpCode op)
        {
            var name = op.StackBehaviourPush.ToString();
            return name switch { "Push0" => 0, "Varpush" => -1, "Push1_push1" => 2, _ => 1 };
        }

        private static V Pop(List<V> stack)
        {
            if (stack.Count == 0)
                return V.None;
            var value = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        private IEnumerable<(int, V[])> Step(Instr instruction, List<V> stack, bool returnsValue)
        {
            var op = instruction.Op;
            var name = op.Name;
            var emptiesStack = false;
            switch (name)
            {
                case "ldarg.0" or "ldarg.1" or "ldarg.2" or "ldarg.3":
                    stack.Add(new V(false, 1, name[^1] - '0'));
                    break;
                case "ldarg.s" or "ldarg" or "ldarga.s" or "ldarga":
                    stack.Add(new V(false, 1, instruction.Number));
                    break;
                case "starg.s" or "starg":
                {
                    var value = Pop(stack);
                    if (IsR(value) && instruction.Number < _argR.Length && !_argR[instruction.Number])
                    {
                        _argR[instruction.Number] = true;
                        _version++;
                    }

                    break;
                }
                case "ldloc.0" or "ldloc.1" or "ldloc.2" or "ldloc.3":
                    stack.Add(new V(false, 2, name[^1] - '0'));
                    break;
                case "ldloc.s" or "ldloc" or "ldloca.s" or "ldloca":
                    stack.Add(new V(false, 2, instruction.Number));
                    break;
                case "stloc.0" or "stloc.1" or "stloc.2" or "stloc.3" or "stloc.s" or "stloc":
                {
                    var local = name.StartsWith("stloc.", StringComparison.Ordinal) && name.Length == 7 ? name[^1] - '0' : instruction.Number;
                    var value = Pop(stack);
                    if (IsR(value) && _localR.Add(local))
                        _version++;
                    if (value.Kind != 0)
                    {
                        if (!_localOrigins.TryGetValue(local, out var origins))
                            _localOrigins[local] = origins = [];
                        if (origins.Add(value with { R = false }))
                            _version++;
                    }

                    break;
                }
                case "dup":
                {
                    var value = Pop(stack);
                    stack.Add(value);
                    stack.Add(value);
                    break;
                }
                case "ldfld" or "ldflda":
                {
                    var target = Pop(stack);
                    stack.Add(target with { R = IsR(target) });
                    break;
                }
                case "ldsfld" or "ldsflda":
                    stack.Add(new V(false, 4, 0));
                    break;
                case "stfld":
                {
                    var value = Pop(stack);
                    var target = Pop(stack);
                    if (IsR(value))
                        MarkHolder(target);
                    break;
                }
                case "stsfld":
                    if (IsR(Pop(stack)))
                        AddEvent(Ev.StoredInStatic, _world.Display(_method));
                    break;
                case "stobj" or "stind.ref":
                {
                    var value = Pop(stack);
                    var address = Pop(stack);
                    if (IsR(value))
                        MarkHolder(address);
                    break;
                }
                case "ldobj" or "ldind.ref" or "castclass" or "isinst" or "box" or "unbox.any" or "unbox":
                {
                    var value = Pop(stack);
                    stack.Add(value with { R = IsR(value) });
                    break;
                }
                case "ldelem.ref" or "ldelem" or "ldelema":
                {
                    Pop(stack);
                    var array = Pop(stack);
                    stack.Add(array with { R = IsR(array) });
                    break;
                }
                case "stelem.ref" or "stelem":
                {
                    var value = Pop(stack);
                    Pop(stack);
                    var array = Pop(stack);
                    if (IsR(value))
                        MarkHolder(array);
                    break;
                }
                case "call" or "callvirt" or "newobj":
                    Call(instruction, stack, name == "newobj", name == "callvirt");
                    break;
                case "calli":
                {
                    var signature = _method.Asm.Md.GetStandaloneSignature((StandaloneSignatureHandle)MetadataTokens.EntityHandle(instruction.Token))
                                           .DecodeMethodSignature(SigProvider.I, null);
                    Pop(stack);
                    var count = signature.ParameterTypes.Length + (signature.Header.IsInstance ? 1 : 0);
                    var any = false;
                    for (var k = 0; k < count; k++)
                        any |= IsR(Pop(stack));
                    if (any)
                        AddEvent(Ev.Escaped, "calli");
                    if (signature.ReturnType.Doc != "System.Void")
                        stack.Add(V.None);
                    break;
                }
                case "ret":
                    if (returnsValue && IsR(Pop(stack)))
                        AddEvent(Ev.Returned);
                    break;
                case "leave" or "leave.s":
                    emptiesStack = true;
                    break;
                default:
                {
                    var pop = PopCount(op);
                    var push = PushCount(op);
                    for (var k = 0; k < pop; k++)
                        Pop(stack);
                    for (var k = 0; k < push; k++)
                        stack.Add(V.None);
                    break;
                }
            }

            var after = emptiesStack ? [] : stack.ToArray();
            switch (op.FlowControl)
            {
                case FlowControl.Branch:
                    yield return (instruction.Targets[0], after);
                    break;
                case FlowControl.Cond_Branch:
                    foreach (var target in instruction.Targets)
                        yield return (target, after);
                    yield return (instruction.Next, after);
                    break;
                case FlowControl.Return or FlowControl.Throw:
                    break;
                default:
                    yield return (instruction.Next, after);
                    break;
            }
        }

        private void Call(Instr instruction, List<V> stack, bool isNewObj, bool isVirtual)
        {
            Callee callee;
            try
            {
                callee = _world.DecodeCall(_method.Asm, MetadataTokens.EntityHandle(instruction.Token));
            }
            catch (Exception)
            {
                stack.Clear();
                AddEvent(Ev.Escaped, "undecodable call");
                return;
            }

            var hasThis = callee.Sig.Header.IsInstance && !isNewObj;
            var count = callee.Sig.ParameterTypes.Length + (hasThis ? 1 : 0);
            var arguments = new V[count];
            for (var k = count - 1; k >= 0; k--)
                arguments[k] = Pop(stack);
            var returns = isNewObj || callee.Sig.ReturnType.Doc != "System.Void";
            var site = instruction.Offset;
            var resultR = false;
            var tainted = arguments.Select(IsR).ToArray();
            if (tainted.Any(t => t))
            {
                var isDelegate = _world.IsDelegate(callee.DeclType);
                if (isDelegate && hasThis && callee.Name is "Invoke" or "BeginInvoke")
                {
                    if (tainted[0])
                        AddEvent(Ev.Invoked, _world.Display(_method));
                    if (tainted.Skip(1).Any(t => t))
                        AddEvent(Ev.Escaped, "argument of a delegate call in " + _world.Display(_method));
                }
                else if (isDelegate && isNewObj)
                    resultR = tainted[0];
                else if (callee.DeclType == "System.Delegate" && callee.Name is "Combine" or "Remove")
                    resultR = true;
                else if (callee.DeclType == "Microsoft.Extensions.DependencyInjection.ServiceDescriptor")
                    AddEvent(Ev.RegisteredDI, callee.Display);
                else
                {
                    var targets = callee.Def is { } definition ? _world.Targets(definition, isVirtual) : null;
                    for (var k = 0; k < count; k++)
                    {
                        if (!tainted[k])
                            continue;
                        if (targets is null or { Count: 0 })
                        {
                            AddEvent(Ev.Escaped, callee.Display + (targets is null && callee.Def is not null ? " (too many implementations)" : ""));
                            resultR = true;
                            continue;
                        }

                        var calleeArgument = isNewObj ? k + 1 : k;
                        foreach (var target in targets)
                        {
                            foreach (var ev in _analyzer.Summary(target, calleeArgument, _depth + 1))
                            {
                                switch (ev.Kind)
                                {
                                    case Ev.StoredInReceiver:
                                        if (isNewObj)
                                            resultR = true;
                                        else
                                            MarkHolder(arguments[0]);
                                        break;
                                    case Ev.StoredInArg:
                                    {
                                        var at = int.Parse(ev.Detail) - (isNewObj ? 1 : 0);
                                        if (at >= 0 && at < arguments.Length)
                                            MarkHolder(arguments[at]);
                                        break;
                                    }
                                    case Ev.Returned:
                                        resultR = true;
                                        break;
                                    default:
                                        AddEvent(ev.Kind, ev.Detail);
                                        break;
                                }
                            }
                        }
                    }
                }
            }

            if (returns)
            {
                stack.Add(new V(false, 3, site));
                if (resultR && _siteR.Add(site))
                    _version++;
            }
        }
    }
}
