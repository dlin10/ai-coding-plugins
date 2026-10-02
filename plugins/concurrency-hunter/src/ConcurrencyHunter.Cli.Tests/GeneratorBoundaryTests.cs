using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

/// <summary>
/// What a directory listing and a proxy cannot prove about <c>generate</c> (R1), read from the metadata of
/// <c>ConcurrencyHunter.Core.dll</c> and the CLI assembly as built: no networking or P/Invoke at all, and no file-system write
/// reachable from <c>GenerateCommand.Run</c> but its one <c>File.WriteAllBytes</c> to the output path.
/// </summary>
public sealed class GeneratorBoundaryTests
{
    private const string GENERATE = "ConcurrencyHunter.GenerateCommand.Run";
    private const string WRITE_ALL_BYTES = "System.IO.File.WriteAllBytes";

    /// <summary>The writers of the two assemblies at the run's start commit ce22c8c, each by its declaring type and the source
    /// method its body comes from (a lambda's, a local function's and a state machine's body is its enclosing method's).</summary>
    private static readonly string[] START_WRITERS =
    [
        "ConcurrencyHunter.MetricsCommand.RunAsync",
        "ConcurrencyHunter.Providers.LibraryModels.ModelLock.Write",
        "ConcurrencyHunter.Runs.RunRegistry.Render"
    ];

    private static readonly string[] FILE_SYSTEM_TYPES = ["System.IO.File", "System.IO.FileInfo", "System.IO.Directory", "System.IO.DirectoryInfo",
                                                          "System.IO.FileSystemInfo"];

    private static readonly string[] WRITE_VERBS = ["Create", "Open", "Write", "Append", "Copy", "Move", "Replace", "Delete"];

    private static readonly string[] READ_OPENS = ["OpenRead", "OpenText"];

    [Fact]
    public void Generator_uses_no_network_api()
    {
        using var program = BuiltImages();
        foreach (var image in program.Images)
        {
            Assert.Empty(NetworkUse(image));
            Assert.Empty(PlatformInvokes(image));
        }

        var solution = RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "src", "ConcurrencyHunter.slnx");
        var projects = XDocument.Load(solution).Descendants().Where(element => element.Name.LocalName == "Project")
                                .Select(element => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(solution)!, element.Attribute("Path")!.Value)))
                                .ToArray();
        Assert.Contains(projects, project => project.EndsWith("ConcurrencyHunter.Core.csproj", StringComparison.Ordinal));
        var nugetClients = projects.SelectMany(project => XDocument.Load(project).Descendants()
                                                                   .Where(element => element.Name.LocalName == "PackageReference")
                                                                   .Select(element => (string?)element.Attribute("Include") ?? "")
                                                                   .Where(id => id.StartsWith("NuGet.", StringComparison.OrdinalIgnoreCase))
                                                                   .Select(id => $"{Path.GetFileName(project)}: {id}"))
                                   .ToArray();
        Assert.Empty(nugetClients);

        // The checks can fail: an assembly built on HttpClient references System.Net, and the corelib imports native methods.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        using var http = new Image(Path.Combine(runtime, "System.Net.Http.Json.dll"));
        Assert.NotEmpty(NetworkUse(http));
        using var corelib = new Image(typeof(object).Assembly.Location);
        Assert.NotEmpty(PlatformInvokes(corelib));
    }

    [Fact]
    public void Generator_writes_only_its_output_file()
    {
        using var program = BuiltImages();
        var writers = program.Writers();

        Assert.Equal(START_WRITERS.Append(GENERATE).Order(StringComparer.Ordinal),
                     writers.Select(writer => writer.Source).Distinct().Order(StringComparer.Ordinal));

        var reached = program.Reachable(program.Methods("ConcurrencyHunter.GenerateCommand", "Run"));
        var reachedWriters = writers.Where(writer => reached.ContainsKey(writer.Method)).ToArray();
        Assert.True(reachedWriters.Length == 1 && reachedWriters[0].Source == GENERATE,
                    "Writers reachable from generate:\n" + string.Join("\n", reachedWriters.Select(writer => PathTo(reached, writer.Method))));
        Assert.Equal([WRITE_ALL_BYTES], reachedWriters[0].Writes);
        // generate reaches the engine, not only its own command.
        Assert.Contains(reached.Keys, node => node.Image.SourceName(node.Handle) == "ConcurrencyHunter.Analysis.ScopePipeline.Run");

        // The reachability can find a writer: metrics writes its measurement from an async method, through its state machine.
        var metrics = program.Reachable(program.Methods("ConcurrencyHunter.MetricsCommand", "RunAsync"));
        Assert.Contains(writers, writer => writer.Source == "ConcurrencyHunter.MetricsCommand.RunAsync" && metrics.ContainsKey(writer.Method));
    }

    /// <summary>How a method was reached, from it back to an entry.</summary>
    /// <param name="reached">The reachable methods, each with the one it was reached from.</param>
    /// <param name="node">The method.</param>
    private static string PathTo(IReadOnlyDictionary<Node, Node?> reached, Node node)
    {
        var steps = new List<string>();
        for (Node? current = node; current is not null; current = reached[current])
            steps.Add(current.Image.SourceName(current.Handle));
        return string.Join("\n  <- ", steps);
    }

    private static ProgramImages BuiltImages() =>
        new([typeof(ModelGenerator).Assembly.Location, typeof(GenerateCommand).Assembly.Location]);

    /// <summary>Every reference into <c>System.Net</c>, and every assembly reference named <c>System.Net.*</c> or <c>NuGet.*</c>.</summary>
    /// <param name="image">The assembly.</param>
    private static List<string> NetworkUse(Image image)
    {
        var reader = image.Reader;
        var found = new List<string>();
        foreach (var handle in reader.AssemblyReferences)
        {
            var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
            if (IsNetwork(name) || name.StartsWith("NuGet.", StringComparison.OrdinalIgnoreCase))
                found.Add($"assembly {name}");
        }

        foreach (var handle in reader.TypeReferences)
        {
            if (image.TypeName(handle) is { } name && IsNetwork(name))
                found.Add($"type {name}");
        }

        foreach (var handle in reader.MemberReferences)
        {
            var reference = reader.GetMemberReference(handle);
            if (image.ParentTypeName(reference.Parent) is { } type && IsNetwork(type))
                found.Add($"member {type}.{reader.GetString(reference.Name)}");
        }

        return found;
    }

    private static bool IsNetwork(string name) => name == "System.Net" || name.StartsWith("System.Net.", StringComparison.Ordinal);

    /// <summary>Every method imported from native code: <c>DllImport</c>, or <c>LibraryImport</c> on its declaration.</summary>
    /// <param name="image">The assembly.</param>
    private static List<string> PlatformInvokes(Image image)
    {
        var reader = image.Reader;
        var found = new List<string>();
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            var libraryImport = method.GetCustomAttributes().Any(attribute => image.AttributeTypeName(reader.GetCustomAttribute(attribute)) ==
                                                                                "System.Runtime.InteropServices.LibraryImportAttribute");
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0 || !method.GetImport().Module.IsNil || libraryImport)
                found.Add(reader.GetString(method.Name));
        }

        return found;
    }

    /// <summary>A method of one of the images.</summary>
    /// <param name="Image">The image that defines it.</param>
    /// <param name="Handle">Its definition.</param>
    private sealed record Node(Image Image, MethodDefinitionHandle Handle);

    /// <summary>A method whose body references a file-system write.</summary>
    /// <param name="Method">The method.</param>
    /// <param name="Source">Its declaring type and source method.</param>
    /// <param name="Writes">The write members it references, as <c>type.member</c>.</param>
    private sealed record Writer(Node Method, string Source, IReadOnlyList<string> Writes);

    /// <summary>The images read together: a call into the other image is resolved by type name, method name and parameter count —
    /// overloads with as many parameters are all taken, which only adds edges.</summary>
    private sealed class ProgramImages : IDisposable
    {
        private readonly Dictionary<string, List<(Image Image, TypeDefinitionHandle Type)>> _types = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Node>> _virtuals = new(StringComparer.Ordinal);
        private readonly Dictionary<(Image, TypeDefinitionHandle), HashSet<string>?> _ancestors = [];

        public ProgramImages(IEnumerable<string> paths)
        {
            Images = paths.Select(path => new Image(path)).ToArray();
            foreach (var image in Images)
            {
                foreach (var type in image.Reader.TypeDefinitions)
                {
                    var name = image.TypeName(type);
                    if (!_types.TryGetValue(name, out var list))
                        _types[name] = list = [];
                    list.Add((image, type));
                    foreach (var handle in image.Reader.GetTypeDefinition(type).GetMethods())
                    {
                        var method = image.Reader.GetMethodDefinition(handle);
                        if ((method.Attributes & MethodAttributes.Virtual) == 0)
                            continue;
                        // An explicit implementation is named after its interface member: System.IDisposable.Dispose.
                        var simple = image.Reader.GetString(method.Name);
                        simple = simple[(simple.LastIndexOf('.') + 1)..];
                        if (!_virtuals.TryGetValue(simple, out var overrides))
                            _virtuals[simple] = overrides = [];
                        overrides.Add(new Node(image, handle));
                    }
                }
            }
        }

        public IReadOnlyList<Image> Images { get; }

        public void Dispose()
        {
            foreach (var image in Images)
                image.Dispose();
        }

        /// <summary>The methods of a type with a name.</summary>
        /// <param name="typeName">The type's full name.</param>
        /// <param name="methodName">The method name.</param>
        public IReadOnlyList<Node> Methods(string typeName, string methodName)
        {
            var found = Types(typeName).SelectMany(type => type.Image.Reader.GetTypeDefinition(type.Type).GetMethods()
                                                                .Where(handle => type.Image.MethodName(handle) == methodName)
                                                                .Select(handle => new Node(type.Image, handle)))
                                       .ToArray();
            Assert.NotEmpty(found);
            return found;
        }

        /// <summary>Every method of the images whose body references a file-system write.</summary>
        public IReadOnlyList<Writer> Writers()
        {
            var writers = new List<Writer>();
            foreach (var image in Images)
            {
                foreach (var handle in image.Reader.MethodDefinitions)
                {
                    var writes = image.Instructions(handle).Where(instruction => instruction.Kind == OperandType.InlineMethod)
                                      .Select(instruction => image.MemberName(instruction.Token))
                                      .Where(member => member is { } known && IsWrite(known.Type, known.Name))
                                      .Select(member => $"{member!.Value.Type}.{member.Value.Name}")
                                      .ToArray();
                    if (writes.Length > 0)
                        writers.Add(new Writer(new Node(image, handle), image.SourceName(handle), writes));
                }
            }

            return writers;
        }

        /// <summary>The methods reachable from the entries through <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c> and
        /// <c>ldvirtftn</c>: a virtual or interface method reaches every override and implementation in the images with its name and
        /// parameter count; a created type reaches every virtual method of its own and its bases' (code outside the images may
        /// call them back); a method reaches its state machine's methods and its type's static constructor, a static field its
        /// type's. Each method maps to the one it was first reached from, an entry to <c>null</c>.</summary>
        /// <param name="entries">The methods the search starts from.</param>
        public Dictionary<Node, Node?> Reachable(IEnumerable<Node> entries)
        {
            var reached = new Dictionary<Node, Node?>();
            var pending = new Stack<Node>();
            Node? from = null;
            void Reach(Node node)
            {
                if (reached.TryAdd(node, from))
                    pending.Push(node);
            }

            foreach (var entry in entries)
                Reach(entry);
            while (pending.TryPop(out var node))
            {
                from = node;
                var image = node.Image;
                var reader = image.Reader;
                var declaring = reader.GetMethodDefinition(node.Handle).GetDeclaringType();
                foreach (var initializer in StaticConstructors(image, declaring))
                    Reach(initializer);
                var name = image.MethodName(node.Handle);
                foreach (var nested in reader.GetTypeDefinition(declaring).GetNestedTypes())
                {
                    if (reader.GetString(reader.GetTypeDefinition(nested).Name).StartsWith($"<{name}>d__", StringComparison.Ordinal))
                    {
                        foreach (var method in reader.GetTypeDefinition(nested).GetMethods())
                            Reach(new Node(image, method));
                    }
                }

                foreach (var instruction in image.Instructions(node.Handle))
                {
                    if (instruction.Kind == OperandType.InlineField)
                    {
                        if (instruction.OpCode == OpCodes.Ldsfld || instruction.OpCode == OpCodes.Stsfld || instruction.OpCode == OpCodes.Ldsflda)
                        {
                            foreach (var type in FieldTypes(image, instruction.Token))
                                foreach (var initializer in StaticConstructors(type.Image, type.Type))
                                    Reach(initializer);
                        }

                        continue;
                    }

                    if (instruction.Kind != OperandType.InlineMethod)
                        continue;
                    var (targets, member) = Targets(image, instruction.Token);
                    foreach (var target in targets)
                        Reach(target);
                    if (member is null)
                        continue;
                    if (instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Ldvirtftn)
                    {
                        foreach (var target in _virtuals.GetValueOrDefault(member.Value.Name) ?? [])
                        {
                            var ancestors = Ancestors(target.Image, target.Image.Reader.GetMethodDefinition(target.Handle).GetDeclaringType());
                            if (target.Image.ParameterCount(target.Handle) == member.Value.Parameters &&
                                (ancestors is null || ancestors.Contains(member.Value.Type)))
                                Reach(target);
                        }
                    }

                    if (instruction.OpCode == OpCodes.Newobj)
                    {
                        foreach (var created in Types(member.Value.Type).SelectMany(type => WithBases(type.Image, type.Type)))
                        {
                            foreach (var method in created.Image.Reader.GetTypeDefinition(created.Type).GetMethods())
                                if ((created.Image.Reader.GetMethodDefinition(method).Attributes & MethodAttributes.Virtual) != 0)
                                    Reach(new Node(created.Image, method));
                        }
                    }
                }
            }

            return reached;
        }

        private IReadOnlyList<(Image Image, TypeDefinitionHandle Type)> Types(string name) => _types.GetValueOrDefault(name) ?? [];

        /// <summary>A type of the images, its base types and its interfaces, by name — through the images' metadata as far as they
        /// define them, then through the loaded assembly that defines an outside one; <c>null</c> when an outside one cannot be
        /// loaded, so a virtual call of any type may dispatch to it.</summary>
        /// <param name="image">The image that defines the type.</param>
        /// <param name="type">The type.</param>
        private HashSet<string>? Ancestors(Image image, TypeDefinitionHandle type)
        {
            if (_ancestors.TryGetValue((image, type), out var known))
                return known;
            var names = new HashSet<string>(StringComparer.Ordinal) { image.TypeName(type) };
            var complete = AddAncestors(image, type, names);
            _ancestors[(image, type)] = complete ? names : null;
            return _ancestors[(image, type)];
        }

        private bool AddAncestors(Image image, TypeDefinitionHandle type, HashSet<string> names)
        {
            var definition = image.Reader.GetTypeDefinition(type);
            var parents = definition.GetInterfaceImplementations().Select(handle => image.Reader.GetInterfaceImplementation(handle).Interface).ToList();
            if (!definition.BaseType.IsNil)
                parents.Add(definition.BaseType);
            var complete = true;
            foreach (var parent in parents)
            {
                if (image.ParentTypeName(parent) is not { } name)
                {
                    complete = false;
                    continue;
                }

                if (!names.Add(name))
                    continue;
                var own = Types(name);
                if (own.Count > 0)
                {
                    foreach (var found in own)
                        complete &= AddAncestors(found.Image, found.Type, names);
                }
                else if (image.OutsideType(parent) is { } outside)
                {
                    foreach (var ancestor in Enumerable.Repeat(outside.BaseType, 1).Concat(outside.GetInterfaces()))
                    {
                        for (var current = ancestor; current is not null; current = current.BaseType)
                            names.Add(Image.ReflectionName(current));
                    }
                }
                else
                    complete = false;
            }

            return complete;
        }

        /// <summary>The images' methods a method token names, and what it names: its declaring type, name and parameter count.</summary>
        /// <param name="image">The image whose body holds the token.</param>
        /// <param name="token">A method definition, reference or specification.</param>
        private (IReadOnlyList<Node> Targets, (string Type, string Name, int Parameters)? Member) Targets(Image image, EntityHandle token)
        {
            var reader = image.Reader;
            switch (token.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var handle = (MethodDefinitionHandle)token;
                    var type = image.TypeName(reader.GetMethodDefinition(handle).GetDeclaringType());
                    return ([new Node(image, handle)], (type, image.MethodName(handle), image.ParameterCount(handle)));
                }
                case HandleKind.MemberReference:
                {
                    var reference = reader.GetMemberReference((MemberReferenceHandle)token);
                    if (reference.Parent.Kind == HandleKind.MethodDefinition)
                        return Targets(image, reference.Parent);
                    if (image.ParentTypeName(reference.Parent) is not { } type)
                        return ([], null);
                    var name = reader.GetString(reference.Name);
                    var parameters = Image.ParameterCount(reader.GetBlobReader(reference.Signature));
                    var targets = Types(type).SelectMany(found => found.Image.Reader.GetTypeDefinition(found.Type).GetMethods()
                                                                       .Where(handle => found.Image.MethodName(handle) == name &&
                                                                                        found.Image.ParameterCount(handle) == parameters)
                                                                       .Select(handle => new Node(found.Image, handle)))
                                             .ToArray();
                    return (targets, (type, name, parameters));
                }
                case HandleKind.MethodSpecification:
                    return Targets(image, reader.GetMethodSpecification((MethodSpecificationHandle)token).Method);
                default:
                    return ([], null);
            }
        }

        private IEnumerable<(Image Image, TypeDefinitionHandle Type)> FieldTypes(Image image, EntityHandle token)
        {
            var reader = image.Reader;
            if (token.Kind == HandleKind.FieldDefinition)
                return [(image, reader.GetFieldDefinition((FieldDefinitionHandle)token).GetDeclaringType())];
            if (token.Kind == HandleKind.MemberReference && image.ParentTypeName(reader.GetMemberReference((MemberReferenceHandle)token).Parent) is { } type)
                return Types(type);
            return [];
        }

        private static IEnumerable<Node> StaticConstructors(Image image, TypeDefinitionHandle type) =>
            image.Reader.GetTypeDefinition(type).GetMethods().Where(handle => image.MethodName(handle) == ".cctor").Select(handle => new Node(image, handle));

        /// <summary>A type and its base types, as far as the images define them.</summary>
        /// <param name="image">The image that defines the type.</param>
        /// <param name="type">The type.</param>
        private IEnumerable<(Image Image, TypeDefinitionHandle Type)> WithBases(Image image, TypeDefinitionHandle type)
        {
            var seen = new HashSet<(Image, TypeDefinitionHandle)>();
            var pending = new Queue<(Image Image, TypeDefinitionHandle Type)>([(image, type)]);
            while (pending.TryDequeue(out var current) && seen.Add(current))
            {
                yield return current;
                var baseType = current.Image.Reader.GetTypeDefinition(current.Type).BaseType;
                if (!baseType.IsNil && current.Image.ParentTypeName(baseType) is { } name)
                    foreach (var found in Types(name))
                        pending.Enqueue(found);
            }
        }

        private static bool IsWrite(string type, string name) =>
            FILE_SYSTEM_TYPES.Contains(type) && WRITE_VERBS.Any(verb => name.StartsWith(verb, StringComparison.Ordinal)) && !READ_OPENS.Contains(name) ||
            type is "System.IO.FileStream" or "System.IO.StreamWriter" && name == ".ctor" ||
            type == "System.IO.Path" && name == "GetTempFileName";
    }

    /// <summary>One instruction with a token operand.</summary>
    /// <param name="OpCode">The opcode.</param>
    /// <param name="Kind">Its operand type.</param>
    /// <param name="Token">The method or field token.</param>
    private sealed record Instruction(OpCode OpCode, OperandType Kind, EntityHandle Token);

    /// <summary>One assembly file, read with <see cref="System.Reflection.Metadata"/>.</summary>
    private sealed class Image : IDisposable
    {
        private static readonly Dictionary<ushort, OpCode> OPCODES =
            typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => (OpCode)field.GetValue(null)!)
                           .ToDictionary(opCode => unchecked((ushort)opCode.Value));

        private readonly PEReader _pe;

        public Image(string path)
        {
            _pe = new PEReader(File.OpenRead(path));
            Reader = _pe.GetMetadataReader();
        }

        public MetadataReader Reader { get; }

        public void Dispose() => _pe.Dispose();

        public string MethodName(MethodDefinitionHandle handle) => Reader.GetString(Reader.GetMethodDefinition(handle).Name);

        public int ParameterCount(MethodDefinitionHandle handle) => ParameterCount(Reader.GetBlobReader(Reader.GetMethodDefinition(handle).Signature));

        public static int ParameterCount(BlobReader signature)
        {
            var header = signature.ReadSignatureHeader();
            if (header.IsGeneric)
                signature.ReadCompressedInteger();
            return signature.ReadCompressedInteger();
        }

        public string TypeName(TypeDefinitionHandle handle)
        {
            var type = Reader.GetTypeDefinition(handle);
            var name = Reader.GetString(type.Name);
            if (type.GetDeclaringType() is { IsNil: false } outer)
                return TypeName(outer) + "/" + name;
            var space = Reader.GetString(type.Namespace);
            return space.Length == 0 ? name : space + "." + name;
        }

        public string? TypeName(TypeReferenceHandle handle)
        {
            var type = Reader.GetTypeReference(handle);
            var name = Reader.GetString(type.Name);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference)
                return TypeName((TypeReferenceHandle)type.ResolutionScope) + "/" + name;
            var space = Reader.GetString(type.Namespace);
            return space.Length == 0 ? name : space + "." + name;
        }

        /// <summary>The name of a member's parent type: a definition, a reference, or the generic type of an instantiation.</summary>
        /// <param name="parent">The parent handle.</param>
        public string? ParentTypeName(EntityHandle parent)
        {
            switch (parent.Kind)
            {
                case HandleKind.TypeDefinition:
                    return TypeName((TypeDefinitionHandle)parent);
                case HandleKind.TypeReference:
                    return TypeName((TypeReferenceHandle)parent);
                case HandleKind.TypeSpecification:
                    var blob = Reader.GetBlobReader(Reader.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                    if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                        return null;
                    blob.ReadSignatureTypeCode();
                    return ParentTypeName(blob.ReadTypeHandle());
                default:
                    return null;
            }
        }

        /// <summary>A type defined outside the images, from the loaded assembly its reference names; <c>null</c> when it cannot be
        /// loaded.</summary>
        /// <param name="handle">A type reference, or the instantiation of one.</param>
        public Type? OutsideType(EntityHandle handle)
        {
            if (handle.Kind == HandleKind.TypeSpecification)
            {
                var blob = Reader.GetBlobReader(Reader.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                    return null;
                blob.ReadSignatureTypeCode();
                return OutsideType(blob.ReadTypeHandle());
            }

            if (handle.Kind != HandleKind.TypeReference)
                return null;
            var name = TypeName((TypeReferenceHandle)handle)!.Replace('/', '+');
            var scope = Reader.GetTypeReference((TypeReferenceHandle)handle).ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference)
                scope = Reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            if (scope.Kind != HandleKind.AssemblyReference)
                return null;
            try
            {
                return Assembly.Load(Reader.GetAssemblyReference((AssemblyReferenceHandle)scope).GetAssemblyName()).GetType(name, throwOnError: false);
            }
            catch (Exception error) when (error is IOException or BadImageFormatException)
            {
                return null;
            }
        }

        /// <summary>A loaded type's name as <see cref="TypeName(TypeReferenceHandle)"/> writes it: a generic type's definition,
        /// nested types joined by <c>/</c>.</summary>
        /// <param name="type">The type.</param>
        public static string ReflectionName(Type type) =>
            (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName!.Replace('+', '/');

        public string? AttributeTypeName(CustomAttribute attribute) => attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => ParentTypeName(Reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
            HandleKind.MethodDefinition => TypeName(Reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
            _ => null
        };

        /// <summary>A method token's declaring type and name.</summary>
        /// <param name="token">A method definition, reference or specification.</param>
        public (string Type, string Name)? MemberName(EntityHandle token)
        {
            switch (token.Kind)
            {
                case HandleKind.MethodDefinition:
                    var definition = (MethodDefinitionHandle)token;
                    return (TypeName(Reader.GetMethodDefinition(definition).GetDeclaringType()), MethodName(definition));
                case HandleKind.MemberReference:
                    var reference = Reader.GetMemberReference((MemberReferenceHandle)token);
                    return reference.Parent.Kind == HandleKind.MethodDefinition
                        ? MemberName(reference.Parent)
                        : ParentTypeName(reference.Parent) is { } type ? (type, Reader.GetString(reference.Name)) : null;
                case HandleKind.MethodSpecification:
                    return MemberName(Reader.GetMethodSpecification((MethodSpecificationHandle)token).Method);
                default:
                    return null;
            }
        }

        /// <summary>The declaring type and source method of a method's body: a compiler-generated method or type stands for the
        /// method it was generated from.</summary>
        /// <param name="handle">The method.</param>
        public string SourceName(MethodDefinitionHandle handle)
        {
            var name = MethodName(handle);
            var type = Reader.GetMethodDefinition(handle).GetDeclaringType();
            string? owner = null;
            while (Reader.GetString(Reader.GetTypeDefinition(type).Name) is var typeName && typeName.StartsWith('<') &&
                   Reader.GetTypeDefinition(type).GetDeclaringType() is { IsNil: false } outer)
            {
                owner ??= Inner(typeName);
                type = outer;
            }

            var method = name.StartsWith('<') ? Inner(name) : owner is { Length: > 0 } ? owner : name;
            return TypeName(type).Replace('/', '.') + "." + method;
        }

        private static string Inner(string generated) => generated[1..generated.IndexOf('>')];

        /// <summary>The instructions of a method body that carry a method or a field token.</summary>
        /// <param name="handle">The method.</param>
        public IEnumerable<Instruction> Instructions(MethodDefinitionHandle handle)
        {
            var address = Reader.GetMethodDefinition(handle).RelativeVirtualAddress;
            if (address == 0)
                return [];
            var il = _pe.GetMethodBody(address).GetILReader();
            var found = new List<Instruction>();
            while (il.RemainingBytes > 0)
            {
                ushort code = il.ReadByte();
                if (code == 0xFE)
                    code = (ushort)(0xFE00 | il.ReadByte());
                var opCode = OPCODES[code];
                switch (opCode.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                        il.Offset += 1;
                        break;
                    case OperandType.InlineVar:
                        il.Offset += 2;
                        break;
                    case OperandType.InlineI8 or OperandType.InlineR:
                        il.Offset += 8;
                        break;
                    case OperandType.InlineSwitch:
                        var targets = il.ReadInt32();
                        il.Offset += 4 * targets;
                        break;
                    case OperandType.InlineMethod or OperandType.InlineField:
                        found.Add(new Instruction(opCode, opCode.OperandType, MetadataTokens.EntityHandle(il.ReadInt32())));
                        break;
                    default:
                        il.Offset += 4;
                        break;
                }
            }

            return found;
        }
    }
}
