using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>R2: every lowering route uses the receiver expression's static type for the whole call, including arguments and outputs.</summary>
public sealed class ReceiverRecordLoweringTests
{
    private static readonly Lazy<MetadataReference> Library = new(CompileLibrary);
    private static readonly string[] Routes =
    [
        "local", "field", "parameter", "implicit-this", "conditional", "foreach", "getter", "setter", "index-getter", "index-setter",
        "compound-property", "increment-property", "compound-indexer", "event-add", "event-remove", "out", "ref", "ref-property",
        "ref-getter", "ref-method", "deconstruct", "list-pattern", "range-pattern", "captured-indexer"
    ];

    public static IEnumerable<object[]> InstanceRoutes() => Routes.SelectMany(route => new[] { "K", "D" }.Select(receiver => new object[] { route, receiver }));

    [Fact]
    public void There_are_48_instance_route_cells_with_both_receiver_types()
    {
        var cells = InstanceRoutes().ToArray();
        Assert.Equal(48, cells.Length);
        Assert.All(Routes, route => Assert.Equal(2, cells.Count(cell => (string)cell[0] == route)));
        Assert.All(new[] { "K", "D" }, receiver => Assert.Equal(24, cells.Count(cell => (string)cell[1] == receiver)));
    }

    [Theory]
    [MemberData(nameof(InstanceRoutes))]
    public async Task Every_instance_route_uses_one_record_for_the_call(string route, string receiver)
    {
        var (work, members) = Work(route, receiver);
        var solution = Solution(receiver, work);
        var compilation = (await solution.Projects.Single().GetCompilationAsync())!;
        var models = Models(compilation);
        var run = AnalyzeScope(solution, "scope:Fixture", models);
        var operations = run.Execution.Heap.Program.Result.Bodies.Values.SelectMany(body => body.Blocks).SelectMany(block => block.Operations).ToArray();
        var owner = route == "range-pattern" ? "RangeD" : "D";
        var calls = operations.OfType<IrCallOperation>().Where(call => call.TargetMethodId?.Contains(":M:ReceiverLibrary." + owner + ".", StringComparison.Ordinal) == true &&
            members.Any(member => call.TargetMethodId.Contains("." + member + "(", StringComparison.Ordinal) ||
                                  call.TargetMethodId.Contains("." + member + "~", StringComparison.Ordinal) ||
                                  call.TargetMethodId.EndsWith("." + member, StringComparison.Ordinal))).ToArray();
        Assert.Equal(members.Length, calls.Length);
        Assert.All(calls, call =>
        {
            var library = Assert.IsType<IrLibraryCall>(call.Library);
            Assert.Equal(IrModelLayer.Project, library.Layer);
            Assert.Equal(receiver == "K", library.InRange);
            Assert.Equal(receiver == "D", library.DeclaredOpaque);
        });
        // Every curated call counted by the engine agrees with the model selected in IR, including both halves of a compound write.
        var all = operations.OfType<IrCallOperation>().Where(call => call.Library?.Layer == IrModelLayer.Project).ToArray();
        Assert.Equal(all.Count(call => call.Library!.InRange), run.Counter(CoverageCounters.KNOWN_CALL_PROJECT));
        Assert.Equal(all.Count(call => call.Library!.DeclaredOpaque), run.Counter(CoverageCounters.OPAQUE_BY_PROJECT));

        if (route is "out" or "ref")
        {
            var call = Assert.Single(calls);
            Assert.Equal(receiver == "K", call.RefResults.ContainsKey(0));
            if (receiver == "K")
                Assert.Contains(operations.OfType<IrStoreReferenceOperation>(), store => store.Value == call.RefResults[0] && store.IsUnseenOutput);
        }
        if (route == "foreach")
        {
            var enumeratorCalls = operations.OfType<IrCallOperation>().Where(call => call.TargetMethodId?.Contains(":M:ReceiverLibrary.Enumerator.", StringComparison.Ordinal) == true).ToArray();
            Assert.Equal(2, enumeratorCalls.Length);
            Assert.All(enumeratorCalls, call => Assert.True(call.Library?.InRange));
            Assert.Contains(operations.OfType<IrCallOperation>(), call => call.TargetMethodId == "body:System.Private.CoreLib:M:System.IDisposable.Dispose");
        }
    }

    private static (string Work, string[] Members) Work(string route, string receiver) => route switch
    {
        "local" => ("x.M(_output);", ["M"]),
        "field" => ("_field.M(_output);", ["M"]),
        "parameter" => ("p.M(_output);", ["M"]),
        "implicit-this" => ("new Child().Work();", ["M"]),
        "conditional" => ("x?.M(_output);", ["M"]),
        "foreach" => ("foreach (var item in x) { _ = item; }", ["GetEnumerator"]),
        "getter" => ("_ = x.P;", ["get_P"]),
        "setter" => ("x.P = 1;", ["set_P"]),
        "index-getter" => ("_ = x[0];", ["get_Item"]),
        "index-setter" => ("x[0] = 1;", ["set_Item"]),
        "compound-property" => ("x.P += 1;", ["get_P", "set_P"]),
        "increment-property" => ("x.P++;", ["get_P", "set_P"]),
        "compound-indexer" => ("x[0] += 1;", ["get_Item", "set_Item"]),
        "event-add" => ("x.E += Handle;", ["add_E"]),
        "event-remove" => ("x.E -= Handle;", ["remove_E"]),
        "out" => ("x.Out(out _output);", ["Out"]),
        "ref" => ("x.Ref(ref _output);", ["Ref"]),
        // A library ref indexer reaches LowerAddress. An ordinary library ref property's storage is not followed.
        "ref-property" => ("x[\"ref\"] = 1;", ["get_Item"]),
        "ref-getter" => ("_ = x.RefP;", ["get_RefP"]),
        "ref-method" => ("x.RefMethod() = 1;", ["RefMethod"]),
        "deconstruct" => ("_ = x is (var a, var b);", ["Deconstruct"]),
        "list-pattern" => ("_ = x is [1, .. var tail];", ["get_Length", "get_Item", "Slice"]),
        "range-pattern" => ($"ReceiverLibrary.Range{receiver} r = new ReceiverLibrary.RangeK(); _ = r is [1, .. var tail];", ["get_Length", "get_Item", "get_Item"]),
        "captured-indexer" => ("(x[0], x[1]) = (1, 2);", ["set_Item", "set_Item"]),
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    [Theory]
    [InlineData("object-creation")]
    [InlineData("pair-creation")]
    [InlineData("synthesized-base")]
    [InlineData("pattern-range")]
    public async Task Routes_without_a_receiver_keep_keyless_records(string route)
    {
        var work = route switch
        {
            "object-creation" => "_ = new ReceiverLibrary.D();",
            "pair-creation" => "_ = System.Collections.Generic.KeyValuePair.Create(1, 2);",
            "synthesized-base" => "_ = new Child();",
            "pattern-range" => "ReceiverLibrary.RangeK r = new(); _ = r is [1, .. var tail];",
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        var solution = Solution("K", work);
        var compilation = (await solution.Projects.Single().GetCompilationAsync())!;
        var models = Models(compilation);
        var run = AnalyzeScope(solution, "scope:Fixture", models);
        var calls = run.Execution.Heap.Program.Result.Bodies.Values.SelectMany(body => body.Blocks).SelectMany(block => block.Operations).OfType<IrCallOperation>().ToArray();
        var member = route switch
        {
            "object-creation" => "M:ReceiverLibrary.D.#ctor",
            "pair-creation" => "M:System.Collections.Generic.KeyValuePair`2.#ctor",
            "synthesized-base" => "M:ReceiverLibrary.K.#ctor",
            _ => "M:System.Range.#ctor"
        };
        var matching = calls.Where(call => call.Library?.MemberId.StartsWith(member, StringComparison.Ordinal) == true).ToArray();
        Assert.NotEmpty(matching);
        Assert.All(matching, call => Assert.True(call.Library!.InRange));
        Assert.True(run.Counter(CoverageCounters.KNOWN_CALL_PROJECT) >= matching.Length);
    }

    [Theory]
    [InlineData("monitor", "System.Threading.Monitor")]
    [InlineData("semaphore", "System.Threading.SemaphoreSlim")]
    [InlineData("atomic", "System.Threading.Interlocked")]
    [InlineData("atomic-element", "System.Threading.Interlocked")]
    [InlineData("combination", "System.Delegate")]
    public async Task Recognizers_keep_their_operations_primitives_and_effects(string route, string metadataType)
    {
        var work = route switch
        {
            "monitor" => "Monitor.Enter(_output);",
            "semaphore" => "_ = new SemaphoreSlim(1).WaitAsync();",
            "atomic" => "Interlocked.Increment(ref _number);",
            "atomic-element" => "Interlocked.Increment(ref _numbers[0]);",
            _ => "_ = Delegate.Combine((Action)Handle, (Action)Handle);"
        };
        var solution = Solution("K", work);
        var compilation = (await solution.Projects.Single().GetCompilationAsync())!;
        var method = compilation.GetTypeByMetadataName("RouteController")!.GetMembers("Run").OfType<IMethodSymbol>().Single();
        var records = compilation.GetTypeByMetadataName(metadataType)!.GetMembers().OfType<IMethodSymbol>()
            .Select(member => new System.Text.Json.Nodes.JsonObject { ["member"] = DocumentationCommentId.CreateDeclarationId(member), ["opaque"] = true }).ToArray();
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"assemblies\":[\"System.Private.CoreLib\"],\"models\":[" +
            string.Join(',', records.Select(record => record.ToJsonString())) + "]}");
        var files = ProjectModelFiles.Read("recognized.json", bytes);
        var resolved = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(ROOT_DIRECTORY, files));
        if (route == "combination")
            Assert.Empty(resolved.Rejections); // Combine lowers to its own IR operation even when an ordinary record is accepted.
        else
            Assert.NotEmpty(resolved.Rejections);
        var before = IrLowering.Lower(method, compilation, ROOT_DIRECTORY, CancellationToken.None);
        var after = IrLowering.Lower(method, compilation, ROOT_DIRECTORY, CancellationToken.None, resolved.Models);
        Assert.Equal(before.Body.Blocks.SelectMany(block => block.Operations).Select(operation => operation.ToString()),
                     after.Body.Blocks.SelectMany(block => block.Operations).Select(operation => operation.ToString()));
        var operations = after.Body.Blocks.SelectMany(block => block.Operations).ToArray();
        Assert.Contains(operations, operation => route switch
        {
            "monitor" => operation is IrAcquireOperation { Primitive: IrSynchronizationPrimitive.Monitor },
            "semaphore" => operation is IrCallOperation { IsRecognized: true, Method: "System.Threading.SemaphoreSlim.WaitAsync()" },
            "atomic" or "atomic-element" => operation is IrAtomicOperation,
            _ => operation is IrCombineDelegatesOperation
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Array_projection_checks_the_source_static_type(bool arrayReceiver)
    {
        var solution = Solution("K", "((System.Collections.ICollection)new object[1]).CopyTo(new object[1], 0);");
        var compilation = (await solution.Projects.Single().GetCompilationAsync())!;
        var array = compilation.GetSpecialType(SpecialType.System_Array);
        var method = compilation.GetTypeByMetadataName("System.Collections.ICollection")!.GetMembers("CopyTo").OfType<IMethodSymbol>().Single();
        var direct = array.GetMembers("CopyTo").OfType<IMethodSymbol>().Single(candidate => candidate.Parameters[1].Type.SpecialType == SpecialType.System_Int32);
        var record = Record(direct, "T:System.Array");
        var models = new LibraryModels(LibraryModels.BuiltIn, [record, Record(direct, null, opaque: true)]);
        var receiver = arrayReceiver ? compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Object)) : (ITypeSymbol)method.ContainingType;
        var implementations = IrLowering.Collections.ImplementationsOf(method, compilation, models, receiver);
        var projection = Assert.Single(implementations, implementation => implementation.Kind == CollectionObjects.ARRAY);
        Assert.Equal(arrayReceiver, projection.Library!.InRange);
        Assert.Equal(!arrayReceiver, projection.Library.DeclaredOpaque);
        // C# exposes the projection through an interface-typed receiver; its array allocation must not select an array key.
        var run = AnalyzeScope(solution, "scope:Fixture", models);
        Assert.Equal(0, run.Counter(CoverageCounters.KNOWN_CALL_PROJECT));
        Assert.Equal(1, run.Counter(CoverageCounters.OPAQUE_BY_PROJECT));
    }

    private static Solution Solution(string receiver, string work) => FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", Usings + $$"""
        public class Child : ReceiverLibrary.{{receiver}} { public void Work() { M(new object()); } }
        [ApiController]
        public class RouteController : ControllerBase
        {
            private ReceiverLibrary.{{receiver}} _field = new ReceiverLibrary.K();
            private object _output = new object();
            private int _number;
            private int[] _numbers = new int[1];
            private static void Handle() { }
            [HttpGet("/route")]
            public void Run(ReceiverLibrary.{{receiver}} p)
            {
                ReceiverLibrary.{{receiver}} x = new ReceiverLibrary.K();
                {{work}}
            }
        }
        """ + Startup()));

    private static LibraryModels Models(Compilation compilation)
    {
        var records = new List<LibraryModel>();
        foreach (var name in new[] { "D", "RangeD", "Enumerator", "K", "RangeK" })
        {
            var type = compilation.GetTypeByMetadataName("ReceiverLibrary." + name)!;
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.MethodKind == MethodKind.Constructor)
                    records.Add(Record(method, null));
                else
                {
                    var key = name == "Enumerator" ? "T:ReceiverLibrary.Enumerator" : "T:ReceiverLibrary." + (name == "RangeD" ? "RangeK" : "K");
                    records.Add(Record(method, key));
                    records.Add(Record(method, null, opaque: true));
                }
            }
        }
        foreach (var name in new[] { "System.Index", "System.Range", "System.Collections.Generic.KeyValuePair`2" })
            records.AddRange(compilation.GetTypeByMetadataName(name)!.InstanceConstructors.Where(method => method.Parameters.Length != 0).Select(method => Record(method, null)));
        return new LibraryModels(LibraryModels.BuiltIn, records);
    }

    private static LibraryModel Record(IMethodSymbol method, string? receiver, bool opaque = false)
    {
        var model = new LibraryModel(DocumentationCommentId.CreateDeclarationId(method)!,
            [new(method.ContainingAssembly.Identity.Name, new Version(0, 0, 0, 0), new Version(99, 0, 0, 0))], [], ModelLayer.Project, opaque,
            method.ContainingAssembly.Identity.Version, receiver);
        return opaque ? model : model with
        {
            Outputs = method.Parameters.Where(parameter => parameter.RefKind is RefKind.Out or RefKind.Ref)
                .ToDictionary(parameter => parameter.Name, _ => new LibraryResult(LibraryResultKind.New, []))
        };
    }

    private static MetadataReference CompileLibrary()
    {
        var compilation = CSharpCompilation.Create("ReceiverLibrary", [CSharpSyntaxTree.ParseText("""
            namespace ReceiverLibrary;
            public class D
            {
                private int _number;
                public void M(object value) { }
                public int P { get; set; }
                public int this[int index] { get => 0; set { } }
                public event System.Action E { add { } remove { } }
                public void Out(out object value) { value = new object(); }
                public void Ref(ref object value) { }
                public ref int RefP => ref _number;
                public ref int this[string name] => ref _number;
                public ref int RefMethod() => ref _number;
                public void Deconstruct(out object a, out object b) { a = b = new object(); }
                public int Length => 1;
                public D Slice(int start, int length) => this;
                public Enumerator GetEnumerator() => new();
            }
            public class K : D { }
            public class RangeD
            {
                public int Length => 1;
                public int this[int index] => 0;
                public RangeD this[System.Range range] => this;
            }
            public class RangeK : RangeD { }
            public class Enumerator : System.IDisposable
            {
                public object Current => new object();
                public bool MoveNext() => false;
                public void Dispose() { }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var result = compilation.Emit(bytes);
        Assert.True(result.Success, string.Join('\n', result.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    }
}
