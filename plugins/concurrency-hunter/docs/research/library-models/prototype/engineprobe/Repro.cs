// Minimal reproduction of "No SSA definition was planned for ..." on user code: each case is a controller action calling one
// helper with the construct under test, analysed by the real engine the way EngineFixture does.
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

static class Repro
{
    private static readonly (string Name, string Helper)[] Cases =
    [
        ("R1 protobuf WriteAsciiStringToBuffer, verbatim", """
            public static void Write(Span<byte> buffer, ref int position, string value, int length)
            {
                ref char reference = ref MemoryMarshal.GetReference(value.AsSpan());
                ref byte reference2 = ref MemoryMarshal.GetReference(buffer.Slice(position));
                int i = 0;
                if (IntPtr.Size == 8 && length >= 4)
                {
                    ref byte source = ref Unsafe.As<char, byte>(ref reference);
                    int num = value.Length - 4;
                    do
                    {
                        Narrow(ref Unsafe.AddByteOffset(ref reference2, (IntPtr)i), Unsafe.ReadUnaligned<ulong>(in Unsafe.AddByteOffset(ref source, (IntPtr)(i * 2))));
                    }
                    while ((i += 4) <= num);
                }
                for (; i < length; i++)
                {
                    Unsafe.AddByteOffset(ref reference2, (IntPtr)i) = (byte)Unsafe.AddByteOffset(ref reference, (IntPtr)(i * 2));
                }
                position += length;
            }
            private static void Narrow(ref byte output, ulong value) => output = (byte)value;
            """),
        ("R2 protobuf WriteStringToBuffer, verbatim (nested fixed)", """
            public static unsafe int Write(Span<byte> buffer, ref int position, string value)
            {
                ReadOnlySpan<char> span = value.AsSpan();
                int bytes;
                fixed (char* reference = &MemoryMarshal.GetReference(span))
                {
                    fixed (byte* reference2 = &MemoryMarshal.GetReference(buffer))
                    {
                        bytes = System.Text.Encoding.UTF8.GetBytes(reference, span.Length, reference2 + position, buffer.Length - position);
                    }
                }
                position += bytes;
                return bytes;
            }
            """),
        ("R3 one ref local written through a ref-returning call in a loop", """
            public static void Write(Span<byte> buffer, int length)
            {
                ref byte first = ref MemoryMarshal.GetReference(buffer);
                for (int i = 0; i < length; i++)
                    Unsafe.Add(ref first, i) = 1;
            }
            """),
        ("R4 the same without a loop", """
            public static void Write(Span<byte> buffer)
            {
                ref byte first = ref MemoryMarshal.GetReference(buffer);
                Unsafe.Add(ref first, 1) = 1;
            }
            """),
        ("R5 ref local passed by ref to a user method in a loop", """
            public static void Write(Span<byte> buffer, int length)
            {
                ref byte first = ref MemoryMarshal.GetReference(buffer);
                for (int i = 0; i < length; i++)
                    Set(ref first);
            }
            private static void Set(ref byte target) => target = 1;
            """),
        ("R6 ref local declared before a do-while that uses it", """
            public static void Write(Span<byte> buffer, int length)
            {
                ref byte first = ref MemoryMarshal.GetReference(buffer);
                int i = 0;
                do
                {
                    Unsafe.Add(ref first, i) = 1;
                }
                while (++i < length);
            }
            """),
        ("R9 plain local by ref into a user ref-returning call, as target", """
            public static void Write()
            {
                int x = 0;
                At(ref x) = 1;
            }
            private static ref int At(ref int value) => ref value;
            """),
        ("R10 plain local by ref into a user ref-returning call, read", """
            public static int Write()
            {
                int x = 0;
                return At(ref x);
            }
            private static ref int At(ref int value) => ref value;
            """),
        ("R11 plain local by ref, compound assignment on the result", """
            public static void Write()
            {
                int x = 0;
                At(ref x) += 1;
            }
            private static ref int At(ref int value) => ref value;
            """),
        ("R12 field by ref into a user ref-returning call, as target", """
            private static int _field;
            public static void Write()
            {
                At(ref _field) = 1;
            }
            private static ref int At(ref int value) => ref value;
            """),
        ("R13 parameter by ref into a user ref-returning call, as target", """
            public static void Write(int x)
            {
                At(ref x) = 1;
            }
            private static ref int At(ref int value) => ref value;
            """),
        ("R7 single fixed pointer", """
            public static unsafe void Write(byte[] buffer)
            {
                fixed (byte* p = buffer)
                {
                    p[0] = 1;
                }
            }
            """),
        ("R8 two nested fixed pointers", """
            public static unsafe void Write(byte[] a, byte[] b)
            {
                fixed (byte* p = a)
                {
                    fixed (byte* q = b)
                    {
                        *q = *p;
                    }
                }
            }
            """),
    ];

    public static void Run()
    {
        foreach (var (name, helper) in Cases)
        {
            var source = EngineFixture.Usings + $$"""
                using System.Runtime.CompilerServices;
                using System.Runtime.InteropServices;
                public sealed class Marker { }
                public static class Helper
                {
                {{helper}}
                }
                public sealed class DriverController : ControllerBase
                {
                    private readonly byte[] _buffer = new byte[64];
                    public IActionResult Get()
                    {
                        {{Call(name)}}
                        return Ok();
                    }
                }
                public static class Startup
                {
                    public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                    {
                        services.AddControllers();
                        app.MapControllers();
                        services.AddSingleton<Marker>(_ => new Marker());
                    }
                }
                """;
            var solution = Build(source);
            var errors = solution.Projects.Single().GetCompilationAsync().Result!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
            {
                Console.WriteLine($"{name,-62} DOES NOT COMPILE: {errors[0].GetMessage()}");
                continue;
            }

            try
            {
                var run = EngineFixture.AnalyzeScope(solution, "scope:Fixture");
                Console.WriteLine($"{name,-62} ok ({run.Collection.Accesses.Count} accesses)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{name,-62} {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                Console.WriteLine("        at " + string.Join(" <- ", (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("at ConcurrencyHunter"))
                                                                        .Take(4).Select(l => l[3..].Split('(')[0])));
            }
        }
    }

    /// <summary>The whole analyzer, as the CLI runs it, on an action that races on a singleton with and without the construct.</summary>
    public static void EndToEnd()
    {
        foreach (var (name, action) in new[]
                 {
                     ("E1 control: _state.Count++ only", "_state.Count++;"),
                     ("E2 same action also does At(ref x) = 1", "int x = 0; At(ref x) = 1; _state.Count++;"),
                     ("E3 At(ref x) = 1 inside a helper the action calls", "_state.Count++; Touch();"),
                 })
        {
            var source = EngineFixture.Usings + $$"""
                public sealed class Marker { }
                public sealed class State { public int Count; }
                public sealed class StateController : ControllerBase
                {
                    private readonly State _state;
                    public StateController(State state) => _state = state;
                    public IActionResult Post()
                    {
                        {{action}}
                        return Ok();
                    }
                    private static ref int At(ref int value) => ref value;
                    private static void Touch() { int y = 0; At(ref y) = 1; }
                }
                public static class Startup
                {
                    public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                    {
                        services.AddControllers();
                        app.MapControllers();
                        services.AddSingleton<State>();
                        services.AddSingleton<Marker>(_ => new Marker());
                    }
                }
                """;
            var result = ConcurrencyHunter.Analysis.PhaseOneAnalyzer.AnalyzeAsync(Build(source), EngineFixture.ROOT_DIRECTORY, CancellationToken.None).Result;
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            var lowering = System.Text.RegularExpressions.Regex.Matches(json, "lowering: [^\"]*").Select(m => m.Value).Distinct().ToArray();
            Console.WriteLine($"{name,-50} findings: {(result.Findings.Count == 0 ? "none" : string.Join(", ", result.Findings.Select(f => f.RuleId + " on " + string.Join(".", f.Resource.AccessPath))))}; " +
                              $"accesses {result.Accesses.Count}; diagnostics: {(lowering.Length == 0 ? "none" : string.Join(" | ", lowering))}");
        }
    }

    private static string Call(string name) => name.Split(' ')[0] switch
    {
        "R1" => "int position = 0; Helper.Write(_buffer, ref position, \"abcdefgh\", 8);",
        "R2" => "int position = 0; Helper.Write(_buffer, ref position, \"abc\");",
        "R3" or "R5" or "R6" => "Helper.Write(_buffer, 8);",
        "R4" or "R7" => "Helper.Write(_buffer);",
        "R9" or "R10" or "R11" or "R12" => "Helper.Write();",
        "R13" => "Helper.Write(1);",
        _ => "Helper.Write(_buffer, _buffer);"
    };

    private static Solution Build(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var references = StubAssemblies.PlatformWithout([]).Concat(StubAssemblies.Names.Select(n => StubAssemblies.Get(n, StubAssemblies.DefaultVersion(n))));
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "Fixture", "Fixture", LanguageNames.CSharp,
                                                                               filePath: @"C:\fixture\Fixture.csproj",
                                                                               compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                                                                                                                                allowUnsafe: true,
                                                                                                                                nullableContextOptions: NullableContextOptions.Enable),
                                                                               parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                                                                               metadataReferences: references));
        return solution.AddDocument(DocumentId.CreateNewId(projectId), "Case.cs", SourceText.From(source), filePath: @"C:\fixture\Case.cs");
    }
}
