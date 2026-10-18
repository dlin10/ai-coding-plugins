using System.Text;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>R8 over both ways a member reaches the generator: a member whose signature carries a function-pointer type is no target in the
/// run and is answered <c>unnamed-member</c> by <c>generate</c>, wherever the type stands and whatever the member is. Every cell has a
/// control of the same shape with <c>Action</c> in the type's place, which is a target on both paths.</summary>
public sealed class FunctionPointerMatrixTests : IDisposable
{
    private const string LIBRARY = "Fp.Lib";
    private const string VERSION = "1.0.0";

    private static readonly IReadOnlyList<Cell> Cells = Enum.GetValues<Kind>()
        .SelectMany(kind => Enum.GetValues<Position>().Where(position => Exists(kind, position)).Select(position => new Cell(kind, position)))
        .ToArray();

    private static readonly Lazy<byte[]> Image = new(() =>
    {
        var source = Source();
        // C# allows the cells it compiles; a warning such as CS8500 for a pointer to Action is no error.
        var errors = CSharpCompilation.Create(LIBRARY, [CSharpSyntaxTree.ParseText(source)], EmittedAssemblies.RuntimeReferences,
                                              new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))
                                      .GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join('\n', errors.Take(10)));
        return EmittedAssemblies.Image(LIBRARY, source);
    });

    private readonly GenerationInstall _install = new();
    private readonly List<string> _nuspecReads = [];
    private readonly Lazy<Compilation> _scope;

    public FunctionPointerMatrixTests()
    {
        _install.SharedFramework("8.0.5", runtime: true);
        _scope = new Lazy<Compilation>(() =>
        {
            ReferenceScopes.AddAsset(_install, LIBRARY, VERSION, "net8.0", LIBRARY, Image.Value);
            return ReferenceScopes.Compile("public static class App { }", [.. ReferenceScopes.Runtime(_install, 8),
                                                               ReferenceScopes.Reference(Image.Value, ReferenceScopes.PackagePath(_install, LIBRARY, VERSION, "lib", "net8.0", LIBRARY))]);
        });
    }

    public void Dispose() => _install.Dispose();

    private enum Position
    {
        SoleParameter,
        OneOfSeveralParameters,
        InParameter,
        ReturnType,
        ParameterArrayElement,
        ParameterPointerTarget,
        ReturnArrayElement,
        ReturnPointerTarget
    }

    private enum Kind { Method, Constructor, PropertyGetter, IndexerGetter, BinaryOperator }

    private sealed record Cell(Kind Kind, Position Position)
    {
        public string Fixture => $"Cell_{Kind}_{Position}";

        public string Control => $"Control_{Kind}_{Position}";

        /// <summary>Whether the type stands in the signature's return type rather than in a parameter.</summary>
        public bool InReturn => Position is Position.ReturnType or Position.ReturnArrayElement or Position.ReturnPointerTarget;

        /// <summary>Whether the member's id ends in <c>(*)</c>: a sole parameter that is a pointer to the type. <c>generate</c> reads that
        /// text as the pattern "any parameters".</summary>
        public bool IdIsPattern => Position == Position.ParameterPointerTarget && Kind != Kind.BinaryOperator;

        public override string ToString() => $"{Kind} / {Position}";
    }

    [Fact]
    public void Every_cell_with_a_function_pointer_is_refused()
    {
        var wrong = new List<string>();
        foreach (var cell in Cells)
        {
            // The run: no target.
            if (RunPath(cell.Fixture, cell.Kind) is not NoGenerationTarget { Reason: NoTargetReason.FunctionPointer })
                wrong.Add($"{cell}: the run does not refuse the member");

            // The CLI: `generate` admits the id, except the pattern `(*)`, and the generator answers unnamed-member.
            var id = Symbol(cell.Fixture, cell.Kind).GetDocumentationCommentId()!;
            if (ModelGenerator.IsMemberId(id) == cell.IdIsPattern)
                wrong.Add($"{cell}: IsMemberId({id}) should be {!cell.IdIsPattern}");
            var reads = _nuspecReads.Count;
            var answer = Trace(id);
            if (answer.Reason != GenerationReasons.UNNAMED_MEMBER)
                wrong.Add($"{cell}: {id} is answered {answer.Reason ?? "a classification"}: {answer.Detail}");
            // A parameter's function pointer makes an empty type name in the id, which is refused before anything is resolved; a return type
            // leaves the id ordinary, so the member is looked up in the compiled package first.
            if ((answer.Generation.Implementation is not null) != cell.InReturn || (_nuspecReads.Count > reads) != cell.InReturn)
                wrong.Add($"{cell}: {id} {(cell.InReturn ? "was not looked up" : "reached the resolver")}");
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {Cells.Count} cells:\n" + string.Join('\n', wrong.Take(20)));
    }

    [Fact]
    public void No_control_is_refused()
    {
        var refused = new List<string>();
        foreach (var cell in Cells)
        {
            if (RunPath(cell.Control, cell.Kind) is not GenerationTarget)
                refused.Add($"{cell}: the run does not take the control as a target");
            var id = Symbol(cell.Control, cell.Kind).GetDocumentationCommentId()!;
            if (!ModelGenerator.IsMemberId(id))
                refused.Add($"{cell}: IsMemberId refuses {id}");
            else if (Trace(id).Reason == GenerationReasons.UNNAMED_MEMBER)
                refused.Add($"{cell}: {id} is answered unnamed-member");
        }

        Assert.True(refused.Count == 0, $"{refused.Count} of {Cells.Count} controls:\n" + string.Join('\n', refused.Take(20)));
    }

    [Fact]
    public void No_refused_cell_reaches_resolution_on_the_run_path()
    {
        foreach (var cell in Cells)
        {
            var reads = _nuspecReads.Count;

            RunPath(cell.Fixture, cell.Kind);

            Assert.True(_nuspecReads.Count == reads, $"{cell}: the resolver read {_nuspecReads.Count - reads} nuspec(s)");
        }

        // The control of a cell does reach it, so the count above can tell.
        var before = _nuspecReads.Count;
        RunPath(Cells[0].Control, Cells[0].Kind);
        Assert.True(_nuspecReads.Count > before);
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        // As C# allows: no constructor returns, a property getter has no parameter, a binary operator has two of them.
        Assert.Equal(Enum.GetValues<Kind>().Length * Enum.GetValues<Position>().Length - (3 + 5 + 1), Cells.Count);
        Assert.Equal(Cells.Count, Cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<Kind>().Order(), Cells.Select(cell => cell.Kind).Distinct().Order());
        Assert.Equal(Enum.GetValues<Position>().Order(), Cells.Select(cell => cell.Position).Distinct().Order());
        Assert.All(Enum.GetValues<Position>(), position => Assert.Contains(Cells, cell => cell.Position == position));
        Assert.Contains(Cells, cell => cell.InReturn);
        Assert.Contains(Cells, cell => !cell.InReturn);
        // `generate` reads (*) as the pattern, so the cells of that id are among those walked, and are not all of them.
        Assert.Equal(3, Cells.Count(cell => cell.IdIsPattern));
        Assert.All(Cells.Where(cell => cell.IdIsPattern), cell => Assert.EndsWith("(*)", Symbol(cell.Fixture, cell.Kind).GetDocumentationCommentId()));
        // Every cell sits beside a control, and the control's type is in the library under its own name.
        Assert.All(Cells, cell =>
        {
            Assert.NotNull(_scope.Value.GetTypeByMetadataName($"Fp.{cell.Fixture}"));
            Assert.NotNull(_scope.Value.GetTypeByMetadataName($"Fp.{cell.Control}"));
        });
    }

    /// <summary>Whether C# allows a member of a kind with the type at a position: a constructor has no return type, a property getter has
    /// no parameter, and a binary operator has two parameters, so a sole parameter cannot be.</summary>
    /// <param name="kind">The kind of member.</param>
    /// <param name="position">Where the type stands.</param>
    private static bool Exists(Kind kind, Position position)
    {
        var inReturn = position is Position.ReturnType or Position.ReturnArrayElement or Position.ReturnPointerTarget;
        return kind switch
        {
            Kind.Constructor => !inReturn,
            Kind.PropertyGetter => inReturn,
            Kind.BinaryOperator => position != Position.SoleParameter,
            _ => true
        };
    }

    /// <summary>The library: a type for each cell with the function-pointer member, and a control with <c>Action</c> in its place.</summary>
    private static string Source()
    {
        var source = new StringBuilder("using System;\n" + ReferenceScopes.VERSION_ATTRIBUTE + "namespace Fp\n{\n");
        foreach (var cell in Cells)
        {
            source.Append(Declare(cell.Fixture, Member(cell, cell.Fixture, "delegate*<void>")));
            source.Append(Declare(cell.Control, Member(cell, cell.Control, "Action")));
        }

        return source.Append("}\n").ToString();
    }

    private static string Declare(string type, string member) => $"    public sealed unsafe class {type}\n    {{\n        {member}\n    }}\n";

    /// <summary>The member of a cell, with a type at its position.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="owner">The declaring type's name, which a constructor and an operator carry.</param>
    /// <param name="type">The type the cell puts at the position: a function pointer or <c>Action</c>.</param>
    private static string Member(Cell cell, string owner, string type)
    {
        var element = cell.Position switch
        {
            Position.ParameterArrayElement or Position.ReturnArrayElement => type + "[]",
            Position.ParameterPointerTarget or Position.ReturnPointerTarget => type + "*",
            _ => type
        };
        string Parameter(string name) => cell.Position == Position.InParameter ? $"in {element} {name}" : $"{element} {name}";
        return cell.Kind switch
        {
            Kind.Method when cell.InReturn => $"public static {element} Run() => default;",
            Kind.Method => $"public static void Run({(cell.Position == Position.OneOfSeveralParameters ? $"int a, {Parameter("p")}, int b" : Parameter("p"))}) {{ }}",
            Kind.Constructor => $"public {owner}({(cell.Position == Position.OneOfSeveralParameters ? $"int a, {Parameter("p")}, int b" : Parameter("p"))}) {{ }}",
            Kind.PropertyGetter => $"public {element} P => default;",
            Kind.IndexerGetter when cell.InReturn => $"public {element} this[int i] => default;",
            Kind.IndexerGetter => $"public int this[{(cell.Position == Position.OneOfSeveralParameters ? $"int a, {Parameter("p")}, int b" : Parameter("p"))}] => 0;",
            Kind.BinaryOperator when cell.InReturn => $"public static {element} operator +({owner} left, int right) => default;",
            _ => $"public static {owner} operator +({owner} left, {Parameter("right")}) => left;"
        };
    }

    /// <summary>The method symbol of a cell's type, from the scope that references the library.</summary>
    /// <param name="type">The type's name.</param>
    /// <param name="kind">The kind of member it holds.</param>
    private IMethodSymbol Symbol(string type, Kind kind)
    {
        var name = kind switch
        {
            Kind.Method => "Run",
            Kind.Constructor => ".ctor",
            Kind.PropertyGetter => "get_P",
            Kind.IndexerGetter => "get_Item",
            _ => "op_Addition"
        };
        return _scope.Value.GetTypeByMetadataName($"Fp.{type}")!.GetMembers(name).OfType<IMethodSymbol>().Single();
    }

    /// <summary>What the run answers for a member.</summary>
    /// <param name="type">The type's name.</param>
    /// <param name="kind">The kind of member it holds.</param>
    private GenerationTargetAnswer RunPath(string type, Kind kind) =>
        GenerationTargets.Of(Symbol(type, kind), _scope.Value, _install.Resolver(nuspecRead: _nuspecReads.Add), CancellationToken.None);

    /// <summary>What the generator answers for an id, as <c>generate</c> asks it.</summary>
    /// <param name="id">The member's declaration id.</param>
    private GeneratedAnswer Trace(string id) =>
        ModelGenerator.Trace(new GenerationRequest(LIBRARY, VERSION, id, LIBRARY, "net8.0"), _install.Resolver(nuspecRead: _nuspecReads.Add), CancellationToken.None).Answer;
}
