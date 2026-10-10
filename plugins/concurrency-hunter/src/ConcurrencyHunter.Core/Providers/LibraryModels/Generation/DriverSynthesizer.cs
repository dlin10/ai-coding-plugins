using System.Text;
using ConcurrencyHunter.Frontend;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>One probe lambda of the driver: made by a factory method of its own, so that its delegate region's site names it.</summary>
/// <param name="Parameter">The member's parameter the probe is handed over for.</param>
/// <param name="Variant">The variant whose action hands it over: <c>Call</c>, <c>Enum</c> or <c>T&lt;i&gt;</c>.</param>
/// <param name="Index">The probe's place within its parameter's value; probes of two variants with the same index are
/// counterparts.</param>
/// <param name="Factory">The factory method's name.</param>
/// <param name="FactoryId">The factory method's documentation id in the driver compilation.</param>
/// <param name="FiredField">The static field the probe writes first when it runs.</param>
/// <param name="InputFields">The static fields the probe stores its parameters in, <c>out</c> parameters aside.</param>
public sealed record DriverProbe(string Parameter, string Variant, int Index, string Factory, string FactoryId, string FiredField,
                                 IReadOnlyList<string> InputFields);

/// <summary>A parameter the classifier gives a fate: a delegate passed by value, <c>in</c> or <c>ref</c>, or a holding parameter
/// whose value carries probes.</summary>
/// <param name="Name">The parameter's name.</param>
/// <param name="Ordinal">The parameter's ordinal.</param>
/// <param name="RefKind">How the parameter is passed.</param>
/// <param name="Kind">The parameter's kind.</param>
/// <param name="OwnFields">By variant, the driver field that holds the value handed over; a delegate passed by value or
/// <c>in</c> has none.</param>
/// <param name="Probes">The probes handed over for the parameter, in every variant.</param>
public sealed record DriverParameter(string Name, int Ordinal, RefKind RefKind, ParameterKind Kind, IReadOnlyDictionary<string, string> OwnFields,
                                     IReadOnlyList<DriverProbe> Probes);

/// <summary>A trigger action of the driver.</summary>
/// <param name="Action">The action's name, <c>T&lt;i&gt;</c>.</param>
/// <param name="Member">The documentation id of the holder's member the action calls after the call.</param>
/// <param name="Holder">The holder whose member it calls: <c>result</c> (the object returned or constructed) or <c>this</c> (the
/// receiver).</param>
public sealed record DriverTrigger(string Action, string Member, string Holder);

/// <summary>One setup statement that places a seed at a library field path: an assignment to a field or auto-property, or a
/// subscription to a field-like event.</summary>
/// <param name="Path">The path from <c>this</c>, <c>arg:&lt;parameter&gt;</c> or <c>static</c>.</param>
/// <param name="Operation">The operation that stores the seed: an <see cref="ISimpleAssignmentOperation"/> for a field or
/// property seed, an <see cref="IEventAssignmentOperation"/> for an event seed.</param>
public sealed record DriverSeedStatement(string Path, IOperation Operation);

/// <summary>A synthesized driver, compiled against its library.</summary>
/// <param name="Member">The member the driver calls, in the library compilation.</param>
/// <param name="Compilation">The driver compilation; it references the library compilation.</param>
/// <param name="Source">The driver's source.</param>
/// <param name="Actions">The actions the fate run roots, in order: <c>V_Setup</c>, <c>V_Call</c>, and <c>V_Enum</c> when the
/// result is enumerable.</param>
/// <param name="Parameters">The parameters the classifier gives a fate.</param>
/// <param name="Triggers">The trigger actions: in the driver's source, but no root of the fate run.</param>
/// <param name="Uncovered">By holder, <c>result</c> or <c>this</c>, the members a caller could invoke on its static type that no
/// trigger calls, each with why: an empty list means every such member has a trigger; a holder absent has none.</param>
/// <param name="DefaultedValues">The owners of the non-holding values the driver could fill only with a default, by parameter
/// name (<c>this</c> for the receiver), sorted.</param>
/// <param name="Unseeded">The sorted field paths that could hold a user object but setup could not seed.</param>
/// <param name="UnseededMembers">The last field or auto-property of every unseeded path.</param>
/// <param name="SeedStatements">Every setup assignment that stores a seed, with its field path.</param>
/// <param name="Enumerates">Whether the result is enumerable, so <c>V_Enum</c> exists.</param>
public sealed record Driver(IMethodSymbol Member, CSharpCompilation Compilation, string Source, IReadOnlyList<string> Actions,
                            IReadOnlyList<DriverParameter> Parameters, IReadOnlyList<DriverTrigger> Triggers,
                            IReadOnlyDictionary<string, IReadOnlyList<string>> Uncovered, IReadOnlyList<string> DefaultedValues,
                            IReadOnlyList<string> Unseeded, IReadOnlyList<ISymbol> UnseededMembers,
                            IReadOnlyList<DriverSeedStatement> SeedStatements, bool Enumerates)
{
    /// <summary>The factory names of delegate seeds whose bodies are witnesses.</summary>
    public IReadOnlyList<string> SeedFactories { get; init; } = [];

    /// <summary>Whether the triggers cover a holder (A5): it has at least one trigger, and every member a caller could invoke on its
    /// static type has one.</summary>
    /// <param name="holder"><c>result</c> or <c>this</c>.</param>
    public bool Covers(string holder) =>
        Uncovered.TryGetValue(holder, out var uncovered) && uncovered.Count == 0 && Triggers.Any(trigger => trigger.Holder == holder);
}

/// <summary>The driver synthesized for a member, or the reason there is none.</summary>
/// <param name="Driver">The driver, or <c>null</c>.</param>
/// <param name="Reason">A reason of <see cref="GenerationReasons"/>, or <c>null</c> when there is a driver.</param>
/// <param name="Detail">Why, in words; empty when there is a driver.</param>
public sealed record DriverSynthesis(Driver? Driver, string? Reason, string Detail);

/// <summary>Synthesizes the <b>Driver</b> of one library member from its signature (SPEC TD-034b, G-3): <c>V_Setup</c> builds
/// every receiver, argument and intermediate into static fields; <c>V_Call</c>, <c>V_Enum</c> and each trigger <c>T&lt;i&gt;</c>
/// read their own fields, make their own probe lambdas and call the member. Every expressible member the driver writes for a user
/// type is a witness that records its receiver and arguments.</summary>
public static class DriverSynthesizer
{
    /// <summary>The driver compilation's assembly name.</summary>
    public const string ASSEMBLY = "ConcurrencyHunter.ModelDriver";

    /// <summary>The static class holding the actions, the fields and the probe factories.</summary>
    public const string DRIVER_TYPE = "ModelDriver";

    /// <summary>The static class holding the result and the intermediates.</summary>
    public const string KEEP_TYPE = "Keep";

    /// <summary>The static class whose object fields record what witness bodies receive.</summary>
    public const string WITNESSED_TYPE = "Witnessed";

    /// <summary>The prefix of the <see cref="WITNESSED_TYPE"/> fields a witness stores what it receives into when the member it stands
    /// for is code the analysis does not see: any member but those of <see cref="READ_WITNESS_FIELD"/>.</summary>
    public const string WITNESS_FIELD = "W";

    /// <summary>The prefix of the <see cref="WITNESSED_TYPE"/> fields a witness stores what it receives into when the analysis takes the
    /// member it stands for to read what it gets, as deep read takes user code it does not run (SPEC TD-034b): a getter, <c>ToString</c>,
    /// <c>Equals</c>, <c>GetHashCode</c> or a member of comparison.</summary>
    public const string READ_WITNESS_FIELD = "R";

    /// <summary>The generic static class whose field supplies witness bodies' by-reference returns.</summary>
    public const string WITNESSED_REF_TYPE = "WitnessedRef";

    /// <summary>The action that builds every receiver, argument and intermediate.</summary>
    public const string SETUP = "V_Setup";

    /// <summary>The action that makes the call and keeps the result.</summary>
    public const string CALL = "V_Call";

    /// <summary>The action that makes the call and enumerates the result.</summary>
    public const string ENUMERATE = "V_Enum";

    /// <summary>The variant of <see cref="CALL"/>.</summary>
    public const string CALL_VARIANT = "Call";

    /// <summary>The variant of <see cref="ENUMERATE"/>.</summary>
    public const string ENUMERATE_VARIANT = "Enum";

    private const int MAX_TRIGGERS = 24;
    private const int RECEIVER_DEPTH = 3;
    private const int ARGUMENT_DEPTH = 2;
    private const int STATIC_DEPTH = 2;
    private const int INLINE_DEPTH = 1;
    private const int TYPE_NESTING = 16;
    private const int DETAILS_SHOWN = 3;

    private static readonly Dictionary<string, string> BinaryOperators = new(StringComparer.Ordinal)
    {
        ["op_Addition"] = "+", ["op_Subtraction"] = "-", ["op_Multiply"] = "*", ["op_Division"] = "/", ["op_Modulus"] = "%",
        ["op_BitwiseAnd"] = "&", ["op_BitwiseOr"] = "|", ["op_ExclusiveOr"] = "^", ["op_LeftShift"] = "<<", ["op_RightShift"] = ">>",
        ["op_Equality"] = "==", ["op_Inequality"] = "!=", ["op_LessThan"] = "<", ["op_GreaterThan"] = ">", ["op_LessThanOrEqual"] = "<=",
        ["op_GreaterThanOrEqual"] = ">="
    };

    private static readonly Dictionary<string, string> UnaryOperators = new(StringComparer.Ordinal)
    {
        ["op_UnaryPlus"] = "+", ["op_UnaryNegation"] = "-", ["op_LogicalNot"] = "!", ["op_OnesComplement"] = "~"
    };

    /// <summary>The library's own member with a declaration id, or <c>null</c>.</summary>
    /// <param name="library">The library compilation.</param>
    /// <param name="memberId">The declaration id.</param>
    public static ISymbol? FindMember(CSharpCompilation library, string memberId) =>
        DocumentationCommentId.GetSymbolsForDeclarationId(memberId, library)
                              .FirstOrDefault(symbol => SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, library.Assembly));

    /// <summary>The driver of one member, or the first reason of G-6's order that applies from the member's kind on:
    /// <c>accessor</c> for a property or an event, <c>driver-not-synthesized</c> for any other kind the generator does not take (a
    /// field), <c>not-a-candidate</c>, <c>body-does-not-compile</c>, <c>driver-not-synthesized</c> for a
    /// driver that could not be built.</summary>
    /// <param name="library">The library compilation, its failing bodies made <c>extern</c>.</param>
    /// <param name="member">The member, from <see cref="FindMember"/>.</param>
    /// <param name="externMembers">The documentation ids of the library's members made <c>extern</c>.</param>
    /// <param name="cancellationToken">Cancels the driver's compilation.</param>
    public static DriverSynthesis Synthesize(CSharpCompilation library, ISymbol member, IReadOnlySet<string> externMembers,
                                             CancellationToken cancellationToken)
        => Synthesize(library, member, externMembers, new HashSet<string>(StringComparer.Ordinal), cancellationToken);

    /// <summary>The driver of one member, with the library fields that opening made reachable, or the first G-6 reason.</summary>
    /// <param name="library">The library compilation, its failing bodies made <c>extern</c>.</param>
    /// <param name="member">The member, from <see cref="FindMember"/>.</param>
    /// <param name="externMembers">The documentation ids of the library's members made <c>extern</c>.</param>
    /// <param name="openedFields">The documentation ids of fields and auto-properties opened for the driver.</param>
    /// <param name="cancellationToken">Cancels the driver's compilation.</param>
    public static DriverSynthesis Synthesize(CSharpCompilation library, ISymbol member, IReadOnlySet<string> externMembers,
                                             IReadOnlySet<string> openedFields, CancellationToken cancellationToken)
        => Synthesize(library, member, externMembers, openedFields, new HashSet<(string Type, string Name)>(), cancellationToken);

    /// <summary>The driver with seeds only for static storage loaded by its reached closure.</summary>
    /// <param name="library">The library compilation.</param>
    /// <param name="member">The member to call.</param>
    /// <param name="externMembers">The members whose bodies could not compile.</param>
    /// <param name="openedFields">The fields and auto-properties opened for setup.</param>
    /// <param name="loadedStatics">The static storage loaded by the closure, by declaring type definition and name.</param>
    /// <param name="cancellationToken">Cancels the driver's compilation.</param>
    public static DriverSynthesis Synthesize(CSharpCompilation library, ISymbol member, IReadOnlySet<string> externMembers,
                                             IReadOnlySet<string> openedFields, IReadOnlySet<(string Type, string Name)> loadedStatics,
                                             CancellationToken cancellationToken)
    {
        if (IsAccessor(member))
            return new DriverSynthesis(null, GenerationReasons.ACCESSOR, AccessorDetail(member));
        if (member is not IMethodSymbol
            {
                MethodKind: MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator or MethodKind.Conversion or
                            MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove
            } method)
        {
            var kind = member is IMethodSymbol other ? other.MethodKind.ToString() : member.Kind.ToString();
            return new DriverSynthesis(null, GenerationReasons.DRIVER_NOT_SYNTHESIZED,
                                       $"{Id(member)} is a {kind}, not a method, constructor, operator, conversion or accessor");
        }

        var definition = method.OriginalDefinition;
        var writer = new Writer(library, definition, openedFields, loadedStatics);
        writer.Build();
        if (!writer.Candidate)
        {
            return new DriverSynthesis(null, GenerationReasons.NOT_A_CANDIDATE,
                                       $"{Id(definition)} has no receiver or parameter that can hold a user object");
        }

        if (externMembers.Contains(Id(definition)))
        {
            return new DriverSynthesis(null, GenerationReasons.BODY_DOES_NOT_COMPILE,
                                       LibraryCompilation.HasNoBodies(definition.ContainingType)
                                           ? $"the body of {Id(definition)} is not the code the runtime runs and was made extern"
                                           : $"the body of {Id(definition)} did not compile and was made extern");
        }
        if (writer.Failures.Count > 0)
            return new DriverSynthesis(null, GenerationReasons.DRIVER_NOT_SYNTHESIZED, string.Join("; ", writer.Failures.Distinct().Take(DETAILS_SHOWN)));

        // A trigger whose call does not compile leaves its holder's member uncovered rather than the whole driver unbuilt: errors
        // only inside trigger actions, or inside the setup of their own arguments, drop those triggers once and recompile.
        string source;
        SyntaxTree tree;
        CSharpCompilation compilation;
        Diagnostic[] errors;
        for (var attempt = 0; ; attempt++)
        {
            source = writer.Source();
            tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), $"{ASSEMBLY}/{DRIVER_TYPE}.cs",
                                              cancellationToken: cancellationToken);
            compilation = CSharpCompilation.Create(ASSEMBLY, [tree], library.References.Append(library.ToMetadataReference()),
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                                                                                nullableContextOptions: NullableContextOptions.Disable));
            errors = compilation.GetDiagnostics(cancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length == 0 || attempt > 0 || TriggersIn(tree, errors, cancellationToken) is not { } failing)
                break;
            writer.DropTriggers(failing, errors.Select(error => $"{error.Id} {error.GetMessage()}").First());
        }

        if (errors.Length > 0)
        {
            return new DriverSynthesis(null, GenerationReasons.DRIVER_NOT_SYNTHESIZED,
                                       "the driver does not compile: " + string.Join("; ", errors.Take(DETAILS_SHOWN).Select(error => $"{error.Id} {error.GetMessage()}")));
        }

        if (!Binds(compilation, tree, Id(definition), cancellationToken))
            return new DriverSynthesis(null, GenerationReasons.DRIVER_NOT_SYNTHESIZED, $"the driver's call does not bind to {Id(definition)}");
        var seeds = SeedOperations(compilation, tree, writer.SeedStatements, cancellationToken);
        return new DriverSynthesis(new Driver(definition, compilation, source, writer.Actions, writer.Parameters, writer.Triggers, writer.Uncovered,
                                              writer.Defaulted.Distinct().Order(StringComparer.Ordinal).ToArray(), writer.Unseeded.Keys.ToArray(),
                                              writer.Unseeded.Values.ToArray(), seeds, writer.Enumerates) { SeedFactories = writer.SeedFactories },
                                   null, "");
    }

    /// <summary>Whether a member answers <c>accessor</c>: a property or an event, named by its <c>P:</c> or <c>E:</c> id. Its
    /// accessor methods, named by their <c>M:</c> ids, are generated as methods are.</summary>
    /// <param name="member">The library member.</param>
    public static bool IsAccessor(ISymbol member) => member is IPropertySymbol or IEventSymbol;

    /// <summary>The detail of an <c>accessor</c> answer: the property or event, and the <c>M:</c> ids of its accessors to generate
    /// instead.</summary>
    /// <param name="member">The property or event.</param>
    public static string AccessorDetail(ISymbol member)
    {
        IEnumerable<IMethodSymbol?> accessors = member switch
        {
            IPropertySymbol property => [property.GetMethod, property.SetMethod],
            IEventSymbol @event => [@event.AddMethod, @event.RemoveMethod],
            _ => []
        };
        return $"{Id(member)} is a property or event; generate its accessors: {string.Join(", ", accessors.OfType<IMethodSymbol>().Select(Id))}";
    }

    /// <summary>Whether a caller could call a member before <see cref="LibraryFieldOpening"/> opened the copy: the member, of any
    /// kind, and each of its containing types are public as the unopened compilation declares them. The opening makes the private
    /// members and nested types it opens <c>internal</c>, and strips the modifier of a seed auto-property's accessor, which makes that
    /// accessor public in the copy.</summary>
    /// <param name="library">The library compilation, opened or not.</param>
    /// <param name="member">The member or type, in that compilation or another assembly.</param>
    internal static bool OriginalAccessibility(CSharpCompilation library, ISymbol member)
    {
        for (var current = Original(library, member); current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;
        }

        return true;
    }

    /// <summary>The symbol with the same declaration id in the unopened compilation; a symbol of another assembly, or one the
    /// unopened compilation does not resolve, is its own.</summary>
    /// <param name="library">The library compilation, opened or not.</param>
    /// <param name="symbol">The symbol.</param>
    private static ISymbol Original(CSharpCompilation library, ISymbol symbol)
    {
        var definition = symbol.OriginalDefinition;
        if (!SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, library.Assembly) || definition.GetDocumentationCommentId() is not { } id)
            return definition;
        var unopened = LibraryFieldOpening.Unopened(library);
        return ReferenceEquals(unopened, library) ? definition : FindMember(unopened, id) ?? definition;
    }

    private static IReadOnlyList<DriverSeedStatement> SeedOperations(CSharpCompilation compilation, SyntaxTree tree,
                                                                     IReadOnlyList<(string Path, string Statement)> statements,
                                                                     CancellationToken cancellationToken)
    {
        var setup = tree.GetRoot(cancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>()
                        .Single(method => method.Identifier.Text == SETUP);
        var assignments = compilation.GetSemanticModel(tree).GetOperation(setup, cancellationToken)!.Descendants()
                                     .Where(operation => operation is ISimpleAssignmentOperation or IEventAssignmentOperation).ToList();
        var result = new List<DriverSeedStatement>();
        foreach (var (path, statement) in statements)
        {
            var text = statement.TrimEnd(';');
            var index = assignments.FindIndex(operation => operation.Syntax.ToString() == text);
            if (index < 0)
                continue;
            result.Add(new DriverSeedStatement(path, assignments[index]));
            assignments.RemoveAt(index);
        }

        return result;
    }

    /// <summary>The trigger variants every error lies in — a trigger action, or a setup statement of a trigger's own argument — or
    /// <c>null</c> when an error lies anywhere else.</summary>
    /// <param name="tree">The driver's tree.</param>
    /// <param name="errors">The driver's errors.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    private static IReadOnlySet<string>? TriggersIn(SyntaxTree tree, IReadOnlyList<Diagnostic> errors, CancellationToken cancellationToken)
    {
        var root = tree.GetRoot(cancellationToken);
        var variants = new HashSet<string>(StringComparer.Ordinal);
        foreach (var error in errors)
        {
            if (error.Location.SourceTree != tree)
                return null;
            var node = root.FindNode(error.Location.SourceSpan, getInnermostNodeForTie: true);
            var method = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (method?.Identifier.Text is { } name && IsTriggerName(name))
            {
                variants.Add(name);
                continue;
            }

            var statement = node.AncestorsAndSelf().OfType<ExpressionStatementSyntax>().FirstOrDefault()?.ToString() ?? "";
            if (method?.Identifier.Text != SETUP || !statement.StartsWith("TArg_", StringComparison.Ordinal))
                return null;
            var variant = statement["TArg_".Length..].Split('_')[0];
            if (!IsTriggerName(variant))
                return null;
            variants.Add(variant);
        }

        return variants;
    }

    private static bool IsTriggerName(string name) => name is ['T', _, ..] && name[1..].All(char.IsAsciiDigit);

    private static string Id(ISymbol symbol) => symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString();

    /// <summary>Whether <c>V_Call</c> calls the member itself, not an overload or an extension the arguments bound to.</summary>
    /// <param name="compilation">The driver compilation.</param>
    /// <param name="tree">The driver's tree.</param>
    /// <param name="memberId">The member's documentation id.</param>
    /// <param name="cancellationToken">Cancels the binding.</param>
    private static bool Binds(CSharpCompilation compilation, SyntaxTree tree, string memberId, CancellationToken cancellationToken)
    {
        var call = tree.GetRoot(cancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>().First(method => method.Identifier.Text == CALL);
        var operation = compilation.GetSemanticModel(tree).GetOperation(call, cancellationToken);
        return operation is not null && operation.Descendants().Any(child => child switch
        {
            IInvocationOperation invocation => Id(invocation.TargetMethod.OriginalDefinition) == memberId,
            IPropertyReferenceOperation reference =>
                (reference.Parent is ISimpleAssignmentOperation assignment && assignment.Target == reference
                    ? reference.Property.SetMethod
                    : reference.Property.GetMethod) is { } accessor && Id(accessor.OriginalDefinition) == memberId,
            IEventAssignmentOperation { EventReference: IEventReferenceOperation { Event: var @event } } subscription =>
                (subscription.Adds ? @event.AddMethod : @event.RemoveMethod) is { } eventAccessor && Id(eventAccessor.OriginalDefinition) == memberId,
            IObjectCreationOperation creation => creation.Constructor is { } constructor && Id(constructor.OriginalDefinition) == memberId,
            IBinaryOperation binary => binary.OperatorMethod is { } binaryOperator && Id(binaryOperator.OriginalDefinition) == memberId,
            IUnaryOperation unary => unary.OperatorMethod is { } unaryOperator && Id(unaryOperator.OriginalDefinition) == memberId,
            IConversionOperation conversion => conversion.OperatorMethod is { } conversionOperator && Id(conversionOperator.OriginalDefinition) == memberId,
            _ => false
        });
    }

    /// <summary>What a value belongs to — a parameter in one variant, the receiver, a trigger's argument — and the probes it
    /// carries. A value with no variant gets plain lambdas, not probes.</summary>
    /// <param name="name">The parameter's name, <c>Recv</c>, or <c>T&lt;i&gt;_&lt;parameter&gt;</c>.</param>
    /// <param name="variant">The variant whose probes the value carries, or <c>null</c>.</param>
    private sealed class Owner(string name, string? variant)
    {
        public string Name { get; } = name;

        public string? Variant { get; } = variant;

        public List<DriverProbe> Probes { get; } = [];

        public int Inputs { get; set; }

        /// <summary>The same owner without probes, for values made inside a lambda or a driver class.</summary>
        public Owner Inline() => new(Name, null);
    }

    /// <summary>The driver of one member, written as text.</summary>
    /// <param name="library">The library compilation.</param>
    /// <param name="member">The member's definition.</param>
    /// <param name="openedFields">The fields and auto-properties opened for setup.</param>
    /// <param name="loadedStatics">The static storage loaded by the reached closure.</param>
    private sealed class Writer(CSharpCompilation library, IMethodSymbol member, IReadOnlySet<string> openedFields,
                                IReadOnlySet<(string Type, string Name)> loadedStatics)
    {
        /// <summary>The types whose members compare what they get, which the analysis takes to read it, as it takes <c>Equals</c>.</summary>
        private static readonly HashSet<string> ComparisonTypes = new(
        [
            "System.IComparable", "System.IComparable`1", "System.IEquatable`1", "System.Collections.IComparer", "System.Collections.IEqualityComparer",
            "System.Collections.Generic.IComparer`1", "System.Collections.Generic.IEqualityComparer`1", "System.Collections.Generic.Comparer`1",
            "System.Collections.Generic.EqualityComparer`1"
        ], StringComparer.Ordinal);

        private readonly IReadOnlySet<string> _openedFields = openedFields;
        private readonly Dictionary<ITypeParameterSymbol, string> _probeTypes = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeParameterSymbol, ITypeSymbol> _choices = new(SymbolEqualityComparer.Default);
        private readonly List<ITypeParameterSymbol> _chosen = [];
        private readonly HashSet<string> _names = new(StringComparer.Ordinal) { DRIVER_TYPE, KEEP_TYPE, WITNESSED_TYPE, WITNESSED_REF_TYPE };
        private readonly List<string> _types = [];
        private readonly List<string> _fields = [];
        private readonly List<string> _keep = [];
        private readonly List<string> _methods = [];
        private readonly List<string> _actions = [];
        private readonly List<string> _setup = [];
        private readonly List<(string Path, string Statement)> _seedStatements = [];
        private readonly SortedDictionary<string, ISymbol> _unseeded = new(StringComparer.Ordinal);

        /// <summary>The types the unseeded-path walk is below: entered by <see cref="RecordBelow"/> or <see cref="RecordStruct"/> and
        /// not yet left.</summary>
        private readonly List<ITypeSymbol> _below = [];
        private readonly Dictionary<ITypeSymbol, string> _seedTypes = new(SymbolEqualityComparer.Default);
        private readonly List<ITypeSymbol> _seedTypeKeys = [];
        private readonly Dictionary<string, string> _classes = new(StringComparer.Ordinal);
        private readonly List<string> _classKeys = [];
        private readonly Dictionary<(string Parameter, string Variant), Owner> _owners = [];
        private List<IMethodSymbol>? _producers;
        private int _intermediates;
        private int _lambdaParameters;
        private readonly List<string> _witnessFields = [];
        private int _seedContainers;
        private bool _staticsSeeded;
        private INamedTypeSymbol? _receiver;
        private ITypeSymbol? _result;

        /// <summary>How many times the call is awaited: once per task level around the member's result.</summary>
        private int _awaits;

        public List<string> Failures { get; } = [];

        public List<string> Defaulted { get; } = [];

        public List<DriverParameter> Parameters { get; } = [];

        public List<DriverTrigger> Triggers { get; } = [];

        public List<string> Actions { get; } = [SETUP];

        public IReadOnlyList<(string Path, string Statement)> SeedStatements => _seedStatements;

        public IReadOnlyList<string> SeedFactories => _seedTypes.Where(seed => TypeShape.Of(seed.Key) == TypeShapeKind.Delegate)
                                                               .Select(seed => seed.Value).ToArray();

        public SortedDictionary<string, ISymbol> Unseeded => _unseeded;

        public bool Candidate { get; private set; }

        public bool Enumerates { get; private set; }

        /// <summary>Builds every part of the driver; what could not be built is in <see cref="Failures"/>.</summary>
        public void Build()
        {
            ChooseTypeArguments();
            CheckCallable();
            var constructs = member.MethodKind == MethodKind.Constructor;
            var containing = (INamedTypeSymbol)Substitute(member.ContainingType);
            _receiver = !member.IsStatic && !constructs ? containing : null;
            Candidate = _receiver is not null && ParameterKinds.IsCandidate(_receiver) ||
                        member.Parameters.Any(parameter => parameter.RefKind != RefKind.Out &&
                                                           ParameterKinds.IsCandidate(Substitute(parameter.Type)));
            if (constructs)
                _result = containing;
            else if (!member.ReturnsVoid)
            {
                // A task is awaited once per level, down to what its innermost task completes with, or to nothing for Task and ValueTask.
                var returned = Substitute(member.ReturnType);
                var innermost = TaskTypes.Innermost(returned);
                var valueless = TaskTypes.IsValueless(innermost);
                _awaits = TaskTypes.Depth(returned) + (valueless ? 1 : 0);
                _result = valueless ? null : innermost;
            }

            Enumerates = _result is { SpecialType: not SpecialType.System_String } result &&
                         (result.SpecialType == SpecialType.System_Collections_IEnumerable ||
                          result.AllInterfaces.Any(@interface => @interface.SpecialType == SpecialType.System_Collections_IEnumerable));

            Setup(CALL_VARIANT);
            if (Enumerates)
                Setup(ENUMERATE_VARIANT);

            // The triggers (A5): for each holder the applicability rules could admit — the result of a constructor or of a reference
            // type, the receiver of an instance method — one per member a caller could invoke on its static type, T0…T23 for the whole
            // driver: the holders share the 24, and a member past them leaves its holder uncovered.
            var triggers = new List<(string Variant, string Holder, string Statement)>();
            var holders = new List<(string Kind, ITypeSymbol Type)>();
            if (constructs || _result is { IsReferenceType: true })
                holders.Add((FateClassifier.RESULT, _result!));
            if (_receiver is not null)
                holders.Add((FateClassifier.THIS, _receiver));
            var number = 0;
            foreach (var (kind, type) in holders)
            {
                var uncovered = new List<string>();
                Uncovered[kind] = uncovered;
                if (type is not INamedTypeSymbol holder)
                {
                    uncovered.Add($"{type.ToDisplayString()}: the driver names no members of a type that is not a named type");
                    continue;
                }

                foreach (var trigger in HolderMembers(holder))
                {
                    if (number == MAX_TRIGGERS)
                    {
                        uncovered.Add($"{Id(trigger)}: past the driver's {MAX_TRIGGERS} triggers");
                        continue;
                    }

                    var variant = "T" + number;
                    var snapshot = Snapshot();
                    Setup(variant);
                    var statement = TriggerStatement(trigger, holder, kind == FateClassifier.RESULT ? "r" : $"Recv_{variant}", variant);
                    if (Failures.Count > snapshot.Failures)
                    {
                        uncovered.Add($"{Id(trigger)}: {Failures[snapshot.Failures]}");
                        Rollback(snapshot, variant);
                        continue;
                    }

                    number++;
                    Triggers.Add(new DriverTrigger(variant, Id(trigger.OriginalDefinition), kind));
                    triggers.Add((variant, kind, statement));
                }
            }

            var keepsResult = _result is not null && TypeShape.Of(_result) != TypeShapeKind.RefLikeOrPointer;
            if (keepsResult)
                _keep.Add($"public static {R(_result!)} R;");
            Actions.Add(CALL);
            _actions.Add(Action(CALL, CALL_VARIANT, keepsResult ? $"{KEEP_TYPE}.R = r;" : null));
            if (Enumerates)
            {
                Actions.Add(ENUMERATE);
                _actions.Add(Action(ENUMERATE, ENUMERATE_VARIANT, "foreach (var e in r) { }"));
            }

            foreach (var (variant, _, statement) in triggers)
                _actions.Add(Action(variant, variant, statement));

            SeedStatics();

            Classify();
        }

        /// <summary>By holder, the members no trigger calls, with why (<see cref="Driver.Uncovered"/>).</summary>
        public Dictionary<string, IReadOnlyList<string>> Uncovered { get; } = new(StringComparer.Ordinal);

        /// <summary>Drops trigger actions whose code does not compile: their members become uncovered, their probes and the setup of
        /// their own arguments go.</summary>
        /// <param name="variants">The trigger variants to drop.</param>
        /// <param name="why">The first compiler error, for the record.</param>
        public void DropTriggers(IReadOnlySet<string> variants, string why)
        {
            foreach (var trigger in Triggers.Where(trigger => variants.Contains(trigger.Action)).ToArray())
            {
                ((List<string>)Uncovered[trigger.Holder]).Add($"{trigger.Member}: the driver's call does not compile: {why}");
                Triggers.Remove(trigger);
            }

            _actions.RemoveAll(action => variants.Any(variant => action.StartsWith($"    public static void {variant}()\n", StringComparison.Ordinal) ||
                                                                 action.StartsWith($"    public static async global::System.Threading.Tasks.Task {variant}()\n", StringComparison.Ordinal)));
            _setup.RemoveAll(statement => variants.Any(variant => statement.StartsWith($"TArg_{variant}_", StringComparison.Ordinal)));
            for (var index = 0; index < Parameters.Count; index++)
            {
                var parameter = Parameters[index];
                Parameters[index] = parameter with
                {
                    Probes = parameter.Probes.Where(probe => !variants.Contains(probe.Variant)).ToArray(),
                    OwnFields = parameter.OwnFields.Where(pair => !variants.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal)
                };
            }
        }

        /// <summary>The driver's source: the driver class, the keep class, then the driver's own types.</summary>
        public string Source()
        {
            var text = new StringBuilder();
            text.Append("// The model generator's driver for ").Append(Id(member)).Append('\n');
            text.Append("#pragma warning disable\n");
            text.Append("public static class ").Append(DRIVER_TYPE).Append("\n{\n");
            foreach (var field in _fields)
                text.Append("    ").Append(field).Append('\n');
            text.Append('\n').Append("    public static void ").Append(SETUP).Append("()\n    {\n");
            foreach (var statement in _setup)
                text.Append("        ").Append(statement).Append('\n');
            text.Append("    }\n");
            foreach (var action in _actions)
                text.Append('\n').Append(action);
            foreach (var factory in _methods)
                text.Append('\n').Append("    ").Append(factory).Append('\n');
            text.Append("}\n\npublic static class ").Append(KEEP_TYPE).Append("\n{\n");
            foreach (var field in _keep)
                text.Append("    ").Append(field).Append('\n');
            text.Append("}\n\npublic static class ").Append(WITNESSED_TYPE).Append("\n{\n");
            foreach (var field in _witnessFields)
                text.Append("    public static object ").Append(field).Append(";\n");
            text.Append("}\n\npublic static class ").Append(WITNESSED_REF_TYPE).Append("<T>\n{\n    public static T Value;\n}\n");
            foreach (var type in _types)
                text.Append('\n').Append(type);
            return text.ToString();
        }

        // ---- the member ----

        /// <summary>The type arguments of the member and of its containing types.</summary>
        private void ChooseTypeArguments()
        {
            var parameters = new List<ITypeParameterSymbol>();
            for (var type = member.ContainingType; type is not null; type = type.ContainingType)
                parameters.InsertRange(0, type.TypeParameters);
            parameters.AddRange(member.TypeParameters);
            ChooseTypeArguments(parameters);
        }

        /// <summary>A probe class for every type parameter whose constraints allow one, the recipe's choice for the others.</summary>
        /// <param name="parameters">The type parameters: the member's and its containing types', or a trigger's own.</param>
        private void ChooseTypeArguments(IReadOnlyList<ITypeParameterSymbol> parameters)
        {
            var probed = new List<ITypeParameterSymbol>();
            foreach (var parameter in parameters)
                Choose(parameter, probed);
            foreach (var parameter in probed)
            {
                var classes = parameter.ConstraintTypes.OfType<INamedTypeSymbol>().Where(type => type.TypeKind == TypeKind.Class).ToArray();
                var interfaces = parameter.ConstraintTypes.OfType<INamedTypeSymbol>().Where(type => type.TypeKind == TypeKind.Interface).ToArray();
                _types.Add(ClassDeclaration(_probeTypes[parameter], classes.FirstOrDefault(), interfaces, fields: true, null, parameter.Name));
            }
        }

        /// <summary>Chooses one type parameter's argument: its probe class, the argument of the one type parameter it is constrained
        /// to, or the recipe's choice.</summary>
        /// <param name="parameter">The type parameter.</param>
        /// <param name="probed">Receives the type parameters given a probe class of their own.</param>
        private void Choose(ITypeParameterSymbol parameter, List<ITypeParameterSymbol> probed)
        {
            if (_probeTypes.ContainsKey(parameter) || _choices.ContainsKey(parameter))
                return;
            _chosen.Add(parameter);
            if (CanProbe(parameter))
            {
                _probeTypes[parameter] = Unique("Probe_" + parameter.Name);
                probed.Add(parameter);
                return;
            }

            if (!parameter.HasValueTypeConstraint && !parameter.HasUnmanagedTypeConstraint && parameter.ConstraintTypes is [ITypeParameterSymbol other])
            {
                Choose(other, probed);
                if (_probeTypes.TryGetValue(other, out var probe))
                    _probeTypes[parameter] = probe;
                else if (_choices.TryGetValue(other, out var chosen))
                    _choices[parameter] = chosen;
                return;
            }

            if (RecipeChoice(parameter) is { } choice)
                _choices[parameter] = choice;
            else
                Fail($"no type argument meets the constraints of {parameter.Name}");
        }

        /// <summary>Whether one sealed class can meet the constraints: no <c>struct</c> or <c>unmanaged</c>, no type parameter among
        /// them, at most one base class, one a subclass can derive from, and interfaces it can implement.</summary>
        /// <param name="parameter">The type parameter.</param>
        private static bool CanProbe(ITypeParameterSymbol parameter)
        {
            if (parameter.HasValueTypeConstraint || parameter.HasUnmanagedTypeConstraint ||
                parameter.ConstraintTypes.Any(type => type is not INamedTypeSymbol))
            {
                return false;
            }

            var classes = parameter.ConstraintTypes.Where(type => type.TypeKind == TypeKind.Class).Cast<INamedTypeSymbol>().ToArray();
            return classes.Length <= 1 && classes.All(Derivation.CanDerive) &&
                   parameter.ConstraintTypes.Where(type => type.TypeKind == TypeKind.Interface).Cast<INamedTypeSymbol>().All(Implementable);
        }

        /// <summary>The recipe's type argument (step 2): <c>int</c> for <c>struct</c> or <c>unmanaged</c> when it meets the
        /// interfaces; <c>int</c>, or <c>object</c> under <c>class</c>, with no constraint type; the constraint class itself when it is
        /// concrete and not generic.</summary>
        /// <param name="parameter">The type parameter.</param>
        private ITypeSymbol? RecipeChoice(ITypeParameterSymbol parameter)
        {
            var integer = library.GetSpecialType(SpecialType.System_Int32);
            if (parameter.HasValueTypeConstraint || parameter.HasUnmanagedTypeConstraint)
            {
                return parameter.ConstraintTypes.All(constraint => constraint.TypeKind == TypeKind.Interface &&
                                                                   library.ClassifyConversion(integer, Replace(constraint, parameter, integer)).IsImplicit)
                    ? integer
                    : null;
            }

            if (parameter.ConstraintTypes.Length == 0)
                return parameter.HasReferenceTypeConstraint ? library.GetSpecialType(SpecialType.System_Object) : integer;
            if (parameter.ConstraintTypes is [INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false, IsGenericType: false } constraint] &&
                (!parameter.HasConstructorConstraint ||
                 constraint.InstanceConstructors.Any(constructor => constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 0)))
            {
                return constraint;
            }

            return null;
        }

        private static ITypeSymbol Replace(ITypeSymbol type, ITypeParameterSymbol parameter, ITypeSymbol with) => type switch
        {
            ITypeParameterSymbol other when SymbolEqualityComparer.Default.Equals(other, parameter) => with,
            INamedTypeSymbol { IsGenericType: true } named => named.ConstructedFrom.Construct(named.TypeArguments.Select(argument => Replace(argument, parameter, with)).ToArray()),
            _ => type
        };

        /// <summary>Records why the recipe cannot call the member. Whether it is public, and whether it is an init accessor, is read
        /// on the unopened compilation: the opening strips a seed auto-property accessor's modifier and turns its <c>init</c> into
        /// <c>set</c>.</summary>
        private void CheckCallable()
        {
            if (!OriginalAccessibility(library, member))
                Fail($"{Id(member)} is not public");
            if (Original(library, member) is IMethodSymbol { IsInitOnly: true })
                Fail($"{Id(member)} is an init accessor, which no driver form calls on a receiver whose state setup chose");
            if (member.IsAbstract)
                Fail($"{Id(member)} is abstract");
            if (member.ContainingType.TypeKind == TypeKind.Interface && !member.IsStatic)
                Fail($"{Id(member)} is an interface member");
            if (member.MethodKind == MethodKind.Constructor && (member.ContainingType.IsAbstract || member.ContainingType.IsStatic))
                Fail($"{Id(member)} constructs an abstract type");
            if (member.MethodKind == MethodKind.UserDefinedOperator &&
                !(member.Parameters.Length == 2 && BinaryOperators.ContainsKey(member.Name) || member.Parameters.Length == 1 && UnaryOperators.ContainsKey(member.Name)))
            {
                Fail($"{Id(member)} is an operator the driver cannot write");
            }

            if (member.IsVararg || member.ReturnsByRef || member.ReturnsByRefReadonly)
                Fail($"{Id(member)} returns by reference or takes a variable argument list");
        }

        /// <summary>Builds the receiver and the arguments of one variant's call into its static fields.</summary>
        /// <param name="variant">The variant: <c>Call</c>, <c>Enum</c> or <c>T&lt;i&gt;</c>.</param>
        private void Setup(string variant)
        {
            if (_receiver is not null)
            {
                _fields.Add($"public static {R(_receiver)} Recv_{variant};");
                _setup.Add($"Recv_{variant} = {ReceiverValue(_receiver)};");
                SeedObject(_receiver, $"Recv_{variant}", "this", RECEIVER_DEPTH, _setup);
            }

            foreach (var parameter in member.Parameters)
            {
                var type = Substitute(parameter.Type);
                var isDelegate = Kind(type) == ParameterKind.Delegate;
                var id = parameter.Name;
                switch (parameter.RefKind)
                {
                    case RefKind.Out:
                        _fields.Add($"public static {R(type)} Out_{id}_{variant};");
                        break;
                    case RefKind.Ref:
                        _fields.Add($"public static {R(type)} Out_{id}_{variant};");
                        if (!isDelegate)
                        {
                            _setup.Add($"Out_{id}_{variant} = {Value(type, OwnerOf(parameter, variant), ARGUMENT_DEPTH, _setup)};");
                            SeedObject(type, $"Out_{id}_{variant}", $"arg:{parameter.Name}", ARGUMENT_DEPTH, _setup);
                        }
                        break;
                    default:
                        if (isDelegate)
                            break;
                        _fields.Add($"public static {R(type)} Arg_{id}_{variant};");
                        _setup.Add($"Arg_{id}_{variant} = {Value(type, OwnerOf(parameter, variant), ARGUMENT_DEPTH, _setup)};");
                        SeedObject(type, $"Arg_{id}_{variant}", $"arg:{parameter.Name}", ARGUMENT_DEPTH, _setup);
                        break;
                }
            }
        }

        // ---- seeds ----

        private void SeedObject(ITypeSymbol type, string value, string path, int depth, List<string> statements)
        {
            type = Substitute(type);
            if (SeedableFields.Struct(type))
            {
                RecordStruct(type, path, null);
                return;
            }
            if (type is IArrayTypeSymbol array)
            {
                if (SeedableFields.Path(array.ElementType))
                    SeedElements(value, array.ElementType, path, depth, statements);
                return;
            }

            if (type is INamedTypeSymbol collectionType && SeedableFields.Collection(collectionType) is { } rootCollection)
            {
                SeedCollectionElements(value, rootCollection, path, depth, statements);
                return;
            }

            if (type is not INamedTypeSymbol named)
                return;
            foreach (var field in SeedMembers(named, isStatic: false))
            {
                var fieldPath = path + "/" + Id(field);
                var access = $"(({R(field.ContainingType)}){value}).{Escape(field.Name)}";
                SeedMember(field, access, fieldPath, depth, statements);
            }
        }

        private void SeedMember(ISymbol field, string access, string path, int depth, List<string> statements)
        {
            var type = Substitute(MemberType(field));
            // A field-like event's storage is seeded by subscribing a witness: a subscription reads nothing and goes no deeper.
            if (field is IEventSymbol)
            {
                if (depth == 0 || !(Accessible(field) || IsOpened(field)) || !SeedEvent(access, type, path, statements))
                    RecordUnseeded(path, field, type);
                return;
            }
            if (!CanRead(field) || depth == 0)
            {
                RecordUnseeded(path, field, type);
                return;
            }
            if (SeedableFields.Struct(type))
            {
                RecordStruct(type, path, field);
                return;
            }
            if (type is INamedTypeSymbol unknown && SeedableFields.UnknownCollection(unknown))
            {
                AddUnseeded(path, field);
                return;
            }

            var seed = SeedableFields.Seed(type);
            var container = type is IArrayTypeSymbol || type is INamedTypeSymbol named && SeedableFields.Collection(named) is not null;
            string? seededValue = null;
            if (seed || container && CanWrite(field))
            {
                if (!CanWrite(field) || !SeedField(access, type, path, statements, out seededValue))
                    RecordUnseeded(path, field, type);
            }
            else if (container && SeedableFields.Path(type) && !SeedElement(access, type, statements))
            {
                // A container the driver cannot replace may be empty: the seeds below it count only where an element it adds carries them.
                RecordUnseeded(path, field, type);
            }
            if (SeedableFields.Path(type))
            {
                SeedObject(type, access, path, depth - 1, statements);
                if (seededValue is not null && seededValue != access)
                    SeedObject(type, seededValue, path, depth - 1, statements);
            }
        }

        private bool SeedField(string target, ITypeSymbol type, string path, List<string> statements, out string? seededValue)
        {
            var snapshot = Snapshot();
            if (TrySeedField(target, type, path, statements, out seededValue) && Failures.Count == snapshot.Failures)
                return true;
            Rollback(snapshot, "Seed");
            seededValue = null;
            return false;
        }

        /// <summary>Subscribes the witness a delegate-typed field would get to a field-like event, or rolls back and answers
        /// <c>false</c>.</summary>
        /// <param name="target">The expression naming the event.</param>
        /// <param name="type">The event's delegate type.</param>
        /// <param name="path">The event's field path.</param>
        /// <param name="statements">The statements the subscription is added in.</param>
        private bool SeedEvent(string target, ITypeSymbol type, string path, List<string> statements)
        {
            var snapshot = Snapshot();
            if (SeedClassValue(type) is { } seed && Failures.Count == snapshot.Failures)
            {
                AddSeed(path, $"{target} += {seed};", statements);
                return true;
            }
            Rollback(snapshot, "Seed");
            return false;
        }

        private bool TrySeedField(string target, ITypeSymbol type, string path, List<string> statements, out string? seededValue)
        {
            seededValue = null;
            if (type is IArrayTypeSymbol array)
            {
                if (!SeedableFields.Seed(array.ElementType) && !SeedableFields.Path(array.ElementType))
                    return false;
                if (SeedArray(array) is not { } created)
                    return false;
                AddSeed(path, $"{target} = {created};", statements);
                seededValue = target;
                return true;
            }

            if (type is INamedTypeSymbol named && SeedableFields.Collection(named) is { } collection)
            {
                var local = SeedCollection(collection, statements);
                if (local is null && named.TypeKind != TypeKind.Interface && !named.IsAbstract)
                    return false;
                if (local is not null)
                {
                    AddSeed(path, $"{target} = {local};", statements);
                    seededValue = named.TypeKind == TypeKind.Interface || named.IsAbstract ? local : target;
                }
                if (named.TypeKind == TypeKind.Interface || named.IsAbstract)
                {
                    if (SeedClassValue(named) is not { } implementation)
                        return false;
                    AddSeed(path, $"{target} = {implementation};", statements);
                }
                return true;
            }

            if (SeedClassValue(type) is not { } seed)
                return false;
            AddSeed(path, $"{target} = {seed};", statements);
            seededValue = target;
            return true;
        }

        private string? SeedCollection(SeedableFields.CollectionSeed collection, List<string> statements)
        {
            var values = collection.Axes.Select(SeedAxis).ToArray();
            if (values.Any(value => value is null))
                return null;
            var type = CollectionType(collection);
            var local = $"SeedContainer_{_seedContainers++}";
            statements.Add($"var {local} = new {type}();");
            statements.Add(Insertion(collection, local, values));
            return local;
        }

        /// <summary>Adds one recipe-built element to a container the driver reaches but cannot replace, before its elements are seeded:
        /// a concrete ADR 0010 collection through its insertion member, an array into its first cell when it has one (R2).</summary>
        /// <param name="target">The expression reading the container.</param>
        /// <param name="type">The container's type.</param>
        /// <param name="statements">The statements the element is added in.</param>
        private bool SeedElement(string target, ITypeSymbol type, List<string> statements)
        {
            var collection = type is INamedTypeSymbol { TypeKind: not TypeKind.Interface, IsAbstract: false } named ? SeedableFields.Collection(named) : null;
            if (type is not IArrayTypeSymbol && collection is null)
                return false;
            var snapshot = Snapshot();
            var values = type is IArrayTypeSymbol array ? new[] { SeedAxis(array.ElementType) } : collection!.Axes.Select(SeedAxis).ToArray();
            // A defaulted element is a null: it carries no seed.
            if (values.Any(value => value is null) || Failures.Count != snapshot.Failures || Defaulted.Count != snapshot.Defaulted)
            {
                Rollback(snapshot, "Seed");
                return false;
            }
            if (type is IArrayTypeSymbol cells)
                statements.Add($"if ({target}.Length != 0) {target}[{string.Join(", ", Enumerable.Repeat("0", cells.Rank))}] = {values[0]};");
            else
                statements.Add(Insertion(collection!, target, values));
            return true;
        }

        private static string Insertion(SeedableFields.CollectionSeed collection, string target, IReadOnlyList<string?> values) =>
            !collection.Dictionary ? $"{target}.{collection.InsertionMember}({values[0]});"
            : collection.InsertionMember == "TryAdd" ? $"{target}.TryAdd({values[0]}, {values[1]});"
            : $"{target}[{values[0]}] = {values[1]};";

        /// <summary>A new array holding one seed: of length 1 in every dimension of its own rank, whose one cell holds the seed of its
        /// element type, itself an array of its own rank when the element is one (R2).</summary>
        /// <param name="array">The array type.</param>
        private string? SeedArray(IArrayTypeSymbol array)
        {
            var value = SeedAxis(array.ElementType);
            if (value is null)
                return null;
            var initializer = "{ " + value + " }";
            for (var rank = 1; rank < array.Rank; rank++)
                initializer = "{ " + initializer + " }";
            return $"new {R(array)} {initializer}";
        }

        private string? SeedAxis(ITypeSymbol type)
        {
            type = Substitute(type);
            if (SeedableFields.Seed(type))
            {
                if (type is IArrayTypeSymbol array)
                    return SeedArray(array);
                if (type is INamedTypeSymbol collectionType && SeedableFields.Collection(collectionType) is not null)
                    return SeedReturnValue(type);
                return SeedClassValue(type);
            }
            if (SeedableFields.Path(type))
                return Value(type, new Owner("Seed", null), INLINE_DEPTH, null);
            return Canned(type, new Owner("Seed", null));
        }

        private string? SeedClassValue(ITypeSymbol type)
        {
            type = Substitute(type);
            if (type is ITypeParameterSymbol parameter)
                return _probeTypes.TryGetValue(parameter, out var probe) ? ProbeInstance(probe) : null;
            if (type is IDynamicTypeSymbol)
                type = library.GetSpecialType(SpecialType.System_Object);
            if (type is INamedTypeSymbol delegateType && TypeShape.Of(type) == TypeShapeKind.Delegate)
            {
                if (_seedTypes.TryGetValue(type, out var factory))
                    return $"{factory}()";
                factory = Unique($"Seed_{_seedTypes.Count}");
                _seedTypes[type] = factory;
                _seedTypeKeys.Add(type);
                if (delegateType.DelegateInvokeMethod is not { } invoke)
                    return null;
                _types.Add($"public unsafe sealed class {factory} {{ {MethodWitness("public ", invoke)} }}");
                _methods.Add($"internal static {R(type)} {factory}() => new {factory}().Invoke;");
                return $"{factory}()";
            }
            if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Interface && !Implementable(named) ||
                named.TypeKind == TypeKind.Class && named.SpecialType != SpecialType.System_Object && !Derivation.CanDerive(named))
            {
                return null;
            }

            if (_seedTypes.TryGetValue(type, out var existing))
                return $"new {existing}()";
            var name = Unique($"Seed_{_seedTypes.Count}");
            _seedTypes[type] = name;
            _seedTypeKeys.Add(type);
            var (baseClass, interfaces) = named.TypeKind == TypeKind.Interface ? (null, new[] { named })
                : named.SpecialType == SpecialType.System_Object ? (null, Array.Empty<INamedTypeSymbol>())
                : (named, Array.Empty<INamedTypeSymbol>());
            _types.Add(ClassDeclaration(name, baseClass, interfaces, fields: false, null, name));
            return $"new {name}()";
        }

        private void SeedElements(string value, ITypeSymbol element, string path, int depth, List<string> statements)
        {
            var local = $"SeedElement_{_seedContainers++}";
            statements.Add($"foreach ({R(element)} {local} in {value}) {{");
            var nested = new List<string>();
            SeedObject(element, local, path, depth, nested);
            statements.AddRange(nested.Select(statement => "    " + statement));
            statements.Add("}");
        }

        private void SeedCollectionElements(string value, SeedableFields.CollectionSeed collection, string path, int depth,
                                            List<string> statements)
        {
            if (collection.Dictionary)
            {
                if (SeedableFields.Path(collection.Axes[0]))
                    SeedElements(value + ".Keys", collection.Axes[0], path, depth, statements);
                if (SeedableFields.Path(collection.Axes[1]))
                    SeedElements(value + ".Values", collection.Axes[1], path, depth, statements);
            }
            else if (SeedableFields.Path(collection.Axes[0]))
                SeedElements(value, collection.Axes[0], path, depth, statements);
        }

        private void SeedStatics()
        {
            if (_staticsSeeded)
                return;
            _staticsSeeded = true;
            foreach (var definition in AllLibraryTypes())
            {
                var staticFields = SeedMembers(definition, isStatic: true)
                                      .Where(field => loadedStatics.Contains(SeedableFields.StaticStorage(field))).ToArray();
                if (staticFields.Length == 0)
                    continue;
                if (!TryConstructStaticType(definition, out var type, out var typeName))
                {
                    foreach (var field in staticFields)
                        RecordUnseeded("static/" + Id(field), field, MemberType(field));
                    continue;
                }

                foreach (var field in SeedMembers(type, isStatic: true))
                {
                    if (!loadedStatics.Contains(SeedableFields.StaticStorage(field)))
                        continue;
                    var path = "static/" + Id(field.OriginalDefinition);
                    var access = $"{typeName}.{Escape(field.Name)}";
                    SeedMember(field, access, path, STATIC_DEPTH + 1, _setup);
                }
            }
        }

        private bool TryConstructStaticType(INamedTypeSymbol definition, out INamedTypeSymbol type, out string text)
        {
            type = definition;
            if (definition.Arity == 0)
            {
                text = R(definition);
                return true;
            }

            var arguments = new List<ITypeSymbol>();
            foreach (var parameter in definition.TypeParameters)
            {
                if (CanProbe(parameter))
                {
                    if (!_probeTypes.TryGetValue(parameter, out var probe))
                    {
                        probe = Unique("Probe_" + parameter.Name);
                        _probeTypes[parameter] = probe;
                        var classes = parameter.ConstraintTypes.OfType<INamedTypeSymbol>().Where(candidate => candidate.TypeKind == TypeKind.Class).ToArray();
                        var interfaces = parameter.ConstraintTypes.OfType<INamedTypeSymbol>().Where(candidate => candidate.TypeKind == TypeKind.Interface).ToArray();
                        _types.Add(ClassDeclaration(probe, classes.FirstOrDefault(), interfaces, fields: true, null, parameter.Name));
                    }
                    arguments.Add(parameter);
                }
                else if (RecipeChoice(parameter) is { } choice)
                {
                    _choices[parameter] = choice;
                    arguments.Add(choice);
                }
                else
                {
                    text = "";
                    return false;
                }
            }

            type = definition.Construct(arguments.ToArray());
            text = R(type);
            return true;
        }

        private IEnumerable<ISymbol> SeedMembers(INamedTypeSymbol type, bool isStatic)
        {
            for (var current = type; current is not null; current = isStatic ? null : current.BaseType)
            {
                foreach (var candidate in current.GetMembers())
                {
                    if (candidate.IsStatic != isStatic || candidate.IsImplicitlyDeclared)
                        continue;
                    if (candidate is IFieldSymbol { IsConst: false } || candidate is IPropertySymbol { IsIndexer: false } property && IsAutoProperty(property) ||
                        candidate is IEventSymbol @event && FieldLikeEvents.Is(@event) && SeedableFields.Seed(@event.Type))
                    {
                        yield return candidate;
                    }
                }
            }
        }

        private static bool IsAutoProperty(IPropertySymbol property) => property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is
            PropertyDeclarationSyntax { AccessorList.Accessors: var accessors } &&
            accessors.Count > 0 && accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null);

        private bool CanRead(ISymbol field) => (Accessible(field) || IsOpened(field)) && field switch
        {
            IPropertySymbol property => property.GetMethod is { } getter && (Accessible(getter) || IsOpened(property)),
            _ => true
        };

        private bool CanWrite(ISymbol field) => (Accessible(field) || IsOpened(field)) && field switch
        {
            IFieldSymbol { IsReadOnly: false } => true,
            IPropertySymbol property => property.SetMethod is { } setter && (Accessible(setter) || IsOpened(property)),
            _ => false
        };

        private bool IsOpened(ISymbol symbol) => symbol.DeclaredAccessibility is not (Accessibility.Private or Accessibility.Protected or
                                                                                      Accessibility.ProtectedAndInternal) &&
                                                 symbol.DeclaringSyntaxReferences.Length > 0 &&
                                                 SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, library.Assembly) &&
                                                 _openedFields.Contains(Id(symbol));

        private bool Accessible(ISymbol symbol)
        {
            for (var current = symbol; current is not null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
                    return false;
                if (current.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal &&
                    !SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, library.Assembly))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Records the unseeded reference storage inside a struct, and below it.</summary>
        /// <param name="type">The struct's type.</param>
        /// <param name="path">The path of the storage that holds the struct.</param>
        /// <param name="field">The field that leads to the struct, or <c>null</c> for an argument or the receiver.</param>
        private void RecordStruct(ITypeSymbol type, string path, ISymbol? field)
        {
            if (type is not INamedTypeSymbol named || Bounded(type, path, field))
                return;
            _below.Add(type);
            try
            {
                foreach (var member in named.GetMembers().OfType<IFieldSymbol>().Where(member => !member.IsStatic))
                {
                    var storage = member.AssociatedSymbol ?? member;
                    var fieldPath = path + "/" + Id(storage);
                    if (SeedableFields.Struct(member.Type))
                        RecordStruct(member.Type, fieldPath, storage);
                    else if (SeedableFields.Seed(member.Type) || SeedableFields.Path(member.Type) ||
                             member.Type is INamedTypeSymbol unknown && SeedableFields.UnknownCollection(unknown))
                        RecordUnseeded(fieldPath, storage, member.Type);
                }

                // No backing field of a field-like event is listed among the struct's members: the event names its storage.
                foreach (var @event in named.GetMembers().OfType<IEventSymbol>().Where(@event => !@event.IsStatic && FieldLikeEvents.Is(@event)))
                    RecordUnseeded(path + "/" + Id(@event), @event, @event.Type);
            }
            finally
            {
                _below.RemoveAt(_below.Count - 1);
            }
        }

        private void RecordUnseeded(string path, ISymbol field, ITypeSymbol type)
        {
            if (SeedableFields.Seed(type) || type is INamedTypeSymbol unknown && SeedableFields.UnknownCollection(unknown))
                AddUnseeded(path, field);
            if (SeedableFields.Struct(type))
                RecordStruct(type, path, field);
            else if (SeedableFields.Path(type))
                RecordBelow(type, path, field, new HashSet<ITypeSymbol>(_below, SymbolEqualityComparer.Default));
        }

        /// <summary>Records the unseeded storage below a field of a path type; a path stops at a type it is already below.</summary>
        /// <param name="type">The type the field holds: a class, an array or a collection.</param>
        /// <param name="path">The field's path.</param>
        /// <param name="field">The field that leads to <paramref name="type"/>.</param>
        /// <param name="seen">The types this walk stops at: the types it started below, and those its branches entered.</param>
        private void RecordBelow(ITypeSymbol type, string path, ISymbol field, HashSet<ITypeSymbol> seen)
        {
            type = Substitute(type);
            if (Bounded(type, path, field) || !seen.Add(type))
                return;
            _below.Add(type);
            try
            {
                if (type is IArrayTypeSymbol array)
                {
                    RecordBelow(array.ElementType, path, field, seen);
                    return;
                }
                if (type is INamedTypeSymbol collectionType && SeedableFields.Collection(collectionType) is { } collection)
                {
                    foreach (var axis in collection.Axes.Where(SeedableFields.Path))
                        RecordBelow(axis, path, field, seen);
                    return;
                }
                if (type is not INamedTypeSymbol named)
                    return;
                foreach (var member in SeedMembers(named, isStatic: false))
                {
                    var nested = path + "/" + Id(member);
                    if (SeedableFields.Seed(MemberType(member)) || MemberType(member) is INamedTypeSymbol unknown && SeedableFields.UnknownCollection(unknown))
                        AddUnseeded(nested, member);
                    else if (SeedableFields.Struct(MemberType(member)))
                        RecordStruct(MemberType(member), nested, member);
                    else if (SeedableFields.Path(MemberType(member)))
                        RecordBelow(MemberType(member), nested, member, seen);
                }
                seen.Remove(type);
            }
            finally
            {
                _below.RemoveAt(_below.Count - 1);
            }
        }

        /// <summary>Whether the unseeded-path walk stops at <paramref name="type"/> because its type arguments nest more than
        /// <see cref="TYPE_NESTING"/> levels deep, an array counting as a level; the field that leads to it is then recorded and
        /// nothing below.</summary>
        /// <param name="type">The type the walk is given.</param>
        /// <param name="path">The path of the field that leads to it.</param>
        /// <param name="field">The field that leads to it, or <c>null</c> for an argument or the receiver.</param>
        private bool Bounded(ITypeSymbol type, string path, ISymbol? field)
        {
            if (!NestsDeeper(type, TYPE_NESTING))
                return false;
            if (field is not null)
                AddUnseeded(path, field);
            return true;

            static bool NestsDeeper(ITypeSymbol type, int levels) => levels < 0 || type switch
            {
                IArrayTypeSymbol array => NestsDeeper(array.ElementType, levels - 1),
                INamedTypeSymbol named => named.TypeArguments.Any(argument => NestsDeeper(argument, levels - 1)) ||
                                          named.ContainingType is { } outer && NestsDeeper(outer, levels),
                _ => false
            };
        }

        private void AddUnseeded(string path, ISymbol member) => _unseeded.TryAdd(path, member);

        private void AddSeed(string path, string statement, List<string> statements)
        {
            statements.Add(statement);
            _seedStatements.Add((path, statement));
        }

        private static ITypeSymbol MemberType(ISymbol member) => member switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            IEventSymbol @event => @event.Type,
            _ => throw new ArgumentOutOfRangeException(nameof(member))
        };

        private string CollectionType(SeedableFields.CollectionSeed collection)
        {
            var tick = collection.ConcreteMetadataName.IndexOf('`');
            var name = tick < 0 ? collection.ConcreteMetadataName : collection.ConcreteMetadataName[..tick];
            return "global::" + name + "<" + string.Join(", ", collection.Axes.Select(R)) + ">";
        }

        /// <summary>The receiver: the driver's subclass when a user could subclass its type, overriding everything but the member and
        /// what it overrides, and for an accessor the property or event it belongs to and what that overrides; else the recipe's
        /// value.</summary>
        /// <param name="receiver">The receiver's type.</param>
        private string ReceiverValue(INamedTypeSymbol receiver)
        {
            var owner = new Owner("Recv", null);
            var kind = Kind(receiver);
            if (kind is ParameterKind.Subclass or ParameterKind.ProbeObject && receiver.TypeKind == TypeKind.Class)
            {
                var excluded = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                for (var overridden = member; overridden is not null; overridden = overridden.OverriddenMethod)
                    excluded.Add(overridden.OriginalDefinition);
                for (var associated = member.AssociatedSymbol; associated is not null;
                     associated = associated switch { IPropertySymbol property => property.OverriddenProperty, IEventSymbol @event => @event.OverriddenEvent, _ => null })
                {
                    excluded.Add(associated.OriginalDefinition);
                }
                if (receiver.IsAbstract || Derivation.Overridable(receiver, out _).Any(candidate => !excluded.Contains(candidate.OriginalDefinition)))
                    return $"new {DriverClass("Sub", owner.Name, receiver, excluded)}()";
            }

            return kind switch
            {
                ParameterKind.Canned => Canned(receiver, owner),
                ParameterKind.NotSynthesized => Fail($"the receiver is of {receiver.ToDisplayString()}, a ref-like type"),
                _ => Recipe(receiver, owner, RECEIVER_DEPTH, _setup)
            };
        }

        /// <summary>One variant's action: the call, then <paramref name="after"/>.</summary>
        /// <param name="name">The action's name.</param>
        /// <param name="variant">The variant whose fields and probes it uses.</param>
        /// <param name="after">The statement after the call, or <c>null</c>.</param>
        private string Action(string name, string variant, string? after)
        {
            var lines = new List<string>();
            var call = Call(variant, lines);
            if (_awaits != 0)
            {
                var awaited = string.Concat(Enumerable.Repeat("await ", _awaits)) + call;
                lines.Add(_result is null ? $"{awaited};" : $"var r = {awaited};");
            }
            else if (_result is not null)
                lines.Add($"var r = {call};");
            else
                lines.Add($"{call};");
            if (after is not null)
                lines.Add(after);
            var isAsync = lines.Any(line => line.Contains("await ", StringComparison.Ordinal));
            var text = new StringBuilder();
            text.Append(isAsync ? $"    public static async global::System.Threading.Tasks.Task {name}()\n" : $"    public static void {name}()\n");
            text.Append("    {\n");
            foreach (var line in lines)
                text.Append("        ").Append(line).Append('\n');
            text.Append("    }\n");
            return text.ToString();
        }

        /// <summary>The call of one variant, reading its fields and making its probes; a <c>ref</c> delegate's field is set to its
        /// probe in <paramref name="lines"/> right before.</summary>
        /// <param name="variant">The variant.</param>
        /// <param name="lines">Receives the statements before the call.</param>
        private string Call(string variant, List<string> lines)
        {
            var arguments = new List<string>();
            foreach (var parameter in member.Parameters)
            {
                var type = Substitute(parameter.Type);
                var isDelegate = Kind(type) == ParameterKind.Delegate;
                var id = parameter.Name;
                switch (parameter.RefKind)
                {
                    case RefKind.Out:
                        arguments.Add($"out Out_{id}_{variant}");
                        break;
                    case RefKind.Ref:
                        if (isDelegate)
                            lines.Add($"Out_{id}_{variant} = {ProbeFactory((INamedTypeSymbol)type, OwnerOf(parameter, variant))};");
                        arguments.Add($"ref Out_{id}_{variant}");
                        break;
                    default:
                        arguments.Add(isDelegate ? ProbeFactory((INamedTypeSymbol)type, OwnerOf(parameter, variant))
                                          : parameter.RefKind == RefKind.None ? $"Arg_{id}_{variant}" : $"in Arg_{id}_{variant}");
                        break;
                }
            }

            var list = string.Join(", ", arguments);
            var containing = R(Substitute(member.ContainingType));
            var typeArguments = member.IsGenericMethod ? "<" + string.Join(", ", member.TypeParameters.Select(R)) + ">" : "";
            var target = member.IsStatic ? containing : $"Recv_{variant}";
            return member.MethodKind switch
            {
                MethodKind.PropertyGet when member.AssociatedSymbol is IPropertySymbol { IsIndexer: true } => $"{target}[{list}]",
                MethodKind.PropertyGet => $"{target}.{Escape(member.AssociatedSymbol!.Name)}",
                MethodKind.PropertySet when member.AssociatedSymbol is IPropertySymbol { IsIndexer: true } =>
                    $"{target}[{string.Join(", ", arguments.SkipLast(1))}] = {arguments[^1]}",
                MethodKind.PropertySet => $"{target}.{Escape(member.AssociatedSymbol!.Name)} = {arguments[^1]}",
                MethodKind.EventAdd => $"{target}.{Escape(member.AssociatedSymbol!.Name)} += {arguments[^1]}",
                MethodKind.EventRemove => $"{target}.{Escape(member.AssociatedSymbol!.Name)} -= {arguments[^1]}",
                MethodKind.Constructor => $"new {containing}({list})",
                MethodKind.Conversion => $"(({R(Substitute(member.ReturnType))})({list}))",
                MethodKind.UserDefinedOperator when arguments.Count == 2 && BinaryOperators.TryGetValue(member.Name, out var binary) =>
                    $"({arguments[0]} {binary} {arguments[1]})",
                MethodKind.UserDefinedOperator when arguments.Count == 1 && UnaryOperators.TryGetValue(member.Name, out var unary) => $"({unary}{arguments[0]})",
                _ when member.IsStatic => $"{containing}.{Escape(member.Name)}{typeArguments}({list})",
                _ => $"Recv_{variant}.{Escape(member.Name)}{typeArguments}({list})"
            };
        }

        /// <summary>Every member a caller could invoke on a holder's static type (A5, the open-world rule): the public instance
        /// methods, generic ones included, property, indexer and event accessors, and delegate <c>Invoke</c> of the type, of its base
        /// types and of every interface it implements, wherever they are declared, then <c>object</c>'s <c>ToString</c>, <c>Equals</c>
        /// and <c>GetHashCode</c>; the member itself included. One per member a call reaches: an override stands for what it overrides,
        /// and an interface member a public method of a class holder implements stands for itself only when that method is not
        /// already in.</summary>
        /// <param name="holder">The holder's static type.</param>
        private IEnumerable<IMethodSymbol> HolderMembers(INamedTypeSymbol holder)
        {
            var types = new List<INamedTypeSymbol>();
            for (var type = holder; type is not null && type.SpecialType != SpecialType.System_Object; type = type.BaseType)
                types.Add(type);
            if (holder.TypeKind == TypeKind.Interface)
                types.Insert(0, holder);
            types.AddRange(holder.AllInterfaces);
            types = types.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToList();
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var type in types)
            {
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(IsCallable))
                {
                    if (type.TypeKind == TypeKind.Interface && holder.TypeKind != TypeKind.Interface &&
                        holder.FindImplementationForInterfaceMember(method) is IMethodSymbol { DeclaredAccessibility: Accessibility.Public } implementation &&
                        implementation.MethodKind != MethodKind.ExplicitInterfaceImplementation && seen.Contains(Root(implementation)))
                    {
                        continue;
                    }

                    if (seen.Add(Root(method)))
                        yield return method;
                }
            }

            foreach (var method in library.GetSpecialType(SpecialType.System_Object).GetMembers().OfType<IMethodSymbol>().Where(method => method.IsVirtual && IsCallable(method)))
            {
                if (seen.Add(Root(method)))
                    yield return method;
            }

            // A field-like event's accessors are the compiler's: they combine delegates and run none, so there is nothing to observe.
            // Whether a member is public or init is read as the library declares it, before the opening (OriginalAccessibility); the
            // containing type a caller must name is TriggerStatement's to judge, so a member on one it cannot name stays uncovered.
            bool IsCallable(IMethodSymbol method) =>
                method is { IsStatic: false } && Original(library, method) is IMethodSymbol { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false } &&
                method.MethodKind is MethodKind.Ordinary or MethodKind.DelegateInvoke or MethodKind.PropertyGet or MethodKind.PropertySet or
                                     MethodKind.EventAdd or MethodKind.EventRemove &&
                !IsFieldLikeEventAccessor(method);

            static ISymbol Root(IMethodSymbol method)
            {
                while (method.OverriddenMethod is { } overridden)
                    method = overridden;
                return method.OriginalDefinition;
            }
        }

        /// <summary>The statement of a trigger action after the call: one call of the holder's member, through a cast to the type
        /// declaring it when that is not the holder's static type, its arguments built by setup into fields of their own; a task it
        /// returns awaited; an event accessor is a subscription or an unsubscription. A member the driver cannot call is a
        /// failure.</summary>
        /// <param name="trigger">The member: a method, a property or indexer accessor, an event accessor or a delegate's <c>Invoke</c>.</param>
        /// <param name="holder">The holder's static type.</param>
        /// <param name="target">The holder in the action: <c>r</c> or <c>Recv_&lt;variant&gt;</c>.</param>
        /// <param name="variant">The trigger's variant.</param>
        private string TriggerStatement(IMethodSymbol trigger, INamedTypeSymbol holder, string target, string variant)
        {
            var declaring = trigger.ContainingType;
            if (!OriginalAccessibility(library, declaring))
                return Fail($"{Id(trigger)} is declared on {declaring.ToDisplayString()}, which a caller cannot name");
            if (trigger.IsVararg)
                return Fail($"{Id(trigger)} takes a variable argument list");
            if (trigger.GetAttributes().Concat(trigger.AssociatedSymbol?.GetAttributes() ?? []).Any(IsObsoleteError))
                return Fail($"{Id(trigger)} is obsolete as an error");
            var returned = trigger.MethodKind == MethodKind.PropertyGet ? ((IPropertySymbol)trigger.AssociatedSymbol!).Type : trigger.ReturnType;
            if (returned is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
                return Fail($"{Id(trigger)} returns a pointer");
            if (trigger.IsGenericMethod)
                ChooseTypeArguments(trigger.TypeParameters);

            var arguments = new List<string>();
            foreach (var parameter in trigger.Parameters)
            {
                if (parameter.RefKind == RefKind.Out)
                {
                    arguments.Add("out _");
                    continue;
                }

                var type = Substitute(parameter.Type);
                var field = $"TArg_{variant}_{parameter.Ordinal}";
                _fields.Add($"public static {R(type)} {field};");
                _setup.Add($"{field} = {Value(type, new Owner($"{variant}_{parameter.Name}", null), ARGUMENT_DEPTH, _setup)};");
                arguments.Add(parameter.RefKind switch
                {
                    RefKind.Ref => $"ref {field}",
                    RefKind.In or RefKind.RefReadOnlyParameter => $"in {field}",
                    _ => field
                });
            }

            var receiver = SymbolEqualityComparer.Default.Equals(declaring, holder) ? target : $"(({R(declaring)}){target})";
            var awaits = TypeShape.Of(returned) is TypeShapeKind.Task or TypeShapeKind.TaskOfT && !trigger.ReturnsVoid;
            switch (trigger.MethodKind)
            {
                case MethodKind.PropertyGet:
                {
                    var property = (IPropertySymbol)trigger.AssociatedSymbol!;
                    var access = property.IsIndexer ? $"{receiver}[{string.Join(", ", arguments)}]" : $"{receiver}.{Escape(property.Name)}";
                    return awaits ? $"await {access};" : $"_ = {access};";
                }

                case MethodKind.PropertySet:
                {
                    var property = (IPropertySymbol)trigger.AssociatedSymbol!;
                    var access = property.IsIndexer ? $"{receiver}[{string.Join(", ", arguments.SkipLast(1))}]" : $"{receiver}.{Escape(property.Name)}";
                    return $"{access} = {arguments[^1]};";
                }

                case MethodKind.EventAdd or MethodKind.EventRemove:
                {
                    var operation = trigger.MethodKind == MethodKind.EventAdd ? "+=" : "-=";
                    return $"{receiver}.{Escape(trigger.AssociatedSymbol!.Name)} {operation} {arguments[^1]};";
                }

                default:
                {
                    var typeArguments = trigger.IsGenericMethod ? "<" + string.Join(", ", trigger.TypeParameters.Select(R)) + ">" : "";
                    var call = $"{receiver}.{Escape(trigger.Name)}{typeArguments}({string.Join(", ", arguments)})";
                    return awaits ? $"await {call};" : $"{call};";
                }
            }
        }

        /// <summary>Whether an accessor is one the compiler wrote for a field-like event (<see cref="FieldLikeEvents.Is"/>) that no
        /// subclass can replace: neither virtual nor an override (R8). An <c>extern</c> event is not field-like, so its accessors are
        /// triggers.</summary>
        /// <param name="accessor">The accessor.</param>
        private static bool IsFieldLikeEventAccessor(IMethodSymbol accessor) =>
            accessor.AssociatedSymbol is IEventSymbol { IsVirtual: false, IsOverride: false } @event && FieldLikeEvents.Is(@event);

        private static bool IsObsoleteError(AttributeData attribute) =>
            attribute.AttributeClass is { Name: "ObsoleteAttribute", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } &&
            attribute.ConstructorArguments is [_, { Value: true }];

        /// <summary>The parameters the classifier gives a fate, with their probes and their own fields.</summary>
        private void Classify()
        {
            var variants = Actions.Skip(1).Select(action => action == CALL ? CALL_VARIANT : ENUMERATE_VARIANT)
                                  .Concat(Triggers.Select(trigger => trigger.Action)).ToArray();
            foreach (var parameter in member.Parameters.Where(parameter => parameter.RefKind != RefKind.Out))
            {
                var kind = Kind(Substitute(parameter.Type));
                var probes = variants.SelectMany(variant => _owners.GetValueOrDefault((parameter.Name, variant))?.Probes ?? []).ToArray();
                var carries = ParameterKinds.IsHolding(kind) && probes.Any(probe => probe.Variant == CALL_VARIANT);
                if (kind != ParameterKind.Delegate && !carries)
                    continue;
                var id = parameter.Name;
                var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var variant in variants)
                {
                    if (parameter.RefKind == RefKind.Ref)
                        fields[variant] = $"Out_{id}_{variant}";
                    else if (kind != ParameterKind.Delegate)
                        fields[variant] = $"Arg_{id}_{variant}";
                }

                Parameters.Add(new DriverParameter(parameter.Name, parameter.Ordinal, parameter.RefKind, kind, fields, probes));
            }
        }

        // ---- values ----

        /// <summary>A value of a type for its owner, by the type's kind (G-3's axes): a probe lambda, a canned value, a container, a
        /// probe object, the driver's subclass, or the recipe's value.</summary>
        /// <param name="type">The type.</param>
        /// <param name="owner">What the value belongs to.</param>
        /// <param name="depth">How many more constructors or factories the recipe may nest.</param>
        /// <param name="hoist">Setup's statements, which intermediates are added to; <c>null</c> inside a lambda or a driver class.</param>
        private string Value(ITypeSymbol type, Owner owner, int depth, List<string>? hoist)
        {
            type = Substitute(type);
            switch (Kind(type))
            {
                case ParameterKind.NotSynthesized:
                    return Fail($"{owner.Name} needs a value of {type.ToDisplayString()}, a ref-like or pointer type");
                case ParameterKind.Delegate:
                    return owner.Variant is not null ? ProbeFactory((INamedTypeSymbol)type, owner) : Lambda((INamedTypeSymbol)type, owner, INLINE_DEPTH, null);
                case ParameterKind.Canned:
                    return Canned(type, owner);
                case ParameterKind.Container:
                    return Container(type, owner, depth, hoist);
                case ParameterKind.ProbeObject:
                    if (type is ITypeParameterSymbol parameter)
                        return _probeTypes.TryGetValue(parameter, out var probe) ? ProbeInstance(probe) : Fail($"{parameter.Name} has no type argument");
                    return ProbeInstance(DriverClass("Probe", owner.Name, type as INamedTypeSymbol ?? library.GetSpecialType(SpecialType.System_Object), null));
                case ParameterKind.Subclass:
                    return $"new {DriverClass("Sub", owner.Name, (INamedTypeSymbol)type, null)}()";
                default:
                    return Recipe(type, owner, depth, hoist);
            }
        }

        private static string ProbeInstance(string name) => $"new {name}() {{ Ref0 = new {name}() }}";

        /// <summary>A canned value: a string, a value type's default, <c>typeof(object)</c>, a public constructor of canned values, a
        /// public static member of the type itself; else a default, named in <see cref="Defaulted"/>.</summary>
        /// <param name="type">The immutable type or plain struct.</param>
        /// <param name="owner">What the value belongs to.</param>
        private string Canned(ITypeSymbol type, Owner owner)
        {
            if (type.SpecialType == SpecialType.System_String)
                return "\"s\"";
            if (type.IsValueType)
                return $"default({R(type)})";
            if (type is INamedTypeSymbol { MetadataName: "Type", ContainingNamespace: { } ns } && ns.ToDisplayString() == "System")
                return "typeof(object)";
            if (type is INamedTypeSymbol named)
            {
                var constructor = named.IsAbstract
                    ? null
                    : named.InstanceConstructors.Where(candidate => candidate.DeclaredAccessibility == Accessibility.Public &&
                                                                    candidate.Parameters.All(parameter => parameter.RefKind == RefKind.None &&
                                                                                                          Kind(Substitute(parameter.Type)) == ParameterKind.Canned))
                           .OrderBy(candidate => candidate.Parameters.Length)
                           .FirstOrDefault();
                if (constructor is not null)
                    return $"new {R(named)}({string.Join(", ", constructor.Parameters.Select(parameter => Canned(Substitute(parameter.Type), owner)))})";
                var shared = named.GetMembers().FirstOrDefault(candidate => candidate is { IsStatic: true, DeclaredAccessibility: Accessibility.Public } &&
                                                                            candidate is IFieldSymbol { Type: var fieldType } && SymbolEqualityComparer.Default.Equals(fieldType, named) ||
                                                                            candidate is IPropertySymbol { IsIndexer: false, GetMethod.DeclaredAccessibility: Accessibility.Public, Type: var propertyType } &&
                                                                            SymbolEqualityComparer.Default.Equals(propertyType, named));
                if (shared is { IsStatic: true, DeclaredAccessibility: Accessibility.Public })
                    return $"{R(named)}.{Escape(shared.Name)}";
            }

            Defaulted.Add(owner.Name);
            return $"default({R(type)})";
        }

        /// <summary>An array or a list of two values of the element's kind; an array of rank r has every dimension of length 1 but
        /// the last, of length 2.</summary>
        /// <param name="type">The array or sequence interface.</param>
        /// <param name="owner">What the value belongs to.</param>
        /// <param name="depth">How many more constructors or factories the recipe may nest.</param>
        /// <param name="hoist">Setup's statements, or <c>null</c>.</param>
        private string Container(ITypeSymbol type, Owner owner, int depth, List<string>? hoist)
        {
            if (type is IArrayTypeSymbol array)
            {
                var initializer = $"{{ {Value(array.ElementType, owner, depth, hoist)}, {Value(array.ElementType, owner, depth, hoist)} }}";
                for (var rank = 1; rank < array.Rank; rank++)
                    initializer = $"{{ {initializer} }}";
                return $"new {R(array)} {initializer}";
            }

            var element = ((INamedTypeSymbol)type).TypeArguments[0];
            return $"new global::System.Collections.Generic.List<{R(element)}> {{ {Value(element, owner, depth, hoist)}, {Value(element, owner, depth, hoist)} }}";
        }

        /// <summary>The recipe's value (steps 3–4): a task's canned value; a public constructor, the one taking a delegate first when
        /// probes are to be placed, then the fewest parameters; a public static factory of the library; a constructor of a library
        /// class that converts to an abstract type. A value only a default could fill makes the member <c>driver-not-synthesized</c>.</summary>
        /// <param name="type">The type.</param>
        /// <param name="owner">What the value belongs to.</param>
        /// <param name="depth">How many more constructors or factories the recipe may nest.</param>
        /// <param name="hoist">Setup's statements, or <c>null</c>.</param>
        private string Recipe(ITypeSymbol type, Owner owner, int depth, List<string>? hoist)
        {
            if (TypeShape.Of(type) == TypeShapeKind.StructWithReferences && type.IsValueType)
                return $"default({R(type)})";
            if (type is INamedTypeSymbol task && TypeShape.Of(type) is TypeShapeKind.Task or TypeShapeKind.TaskOfT)
            {
                if (task.Arity == 0)
                    return task.Name == "Task" ? "global::System.Threading.Tasks.Task.CompletedTask" : $"default({R(task)})";
                var inner = Value(task.TypeArguments[0], owner, depth, hoist);
                return task.Name == "Task"
                    ? $"global::System.Threading.Tasks.Task.FromResult<{R(task.TypeArguments[0])}>({inner})"
                    : $"new {R(task)}({inner})";
            }

            if (depth > 0 && type is INamedTypeSymbol named)
            {
                if (named is { IsAbstract: false, IsStatic: false, TypeKind: TypeKind.Class or TypeKind.Struct } && Constructor(named, owner, depth, hoist) is { } constructed)
                    return constructed;
                if (Factory(named, owner, depth, hoist) is { } produced)
                    return produced;
                if (named.TypeKind == TypeKind.Interface || named.IsAbstract)
                {
                    foreach (var implementation in Implementations(named))
                    {
                        if (Constructor(implementation, owner, depth, hoist) is { } implemented)
                            return implemented;
                    }
                }
            }

            return Fail($"only a default could fill the {type.ToDisplayString()} of {owner.Name}");
        }

        private string? Constructor(INamedTypeSymbol type, Owner owner, int depth, List<string>? hoist)
        {
            var placing = owner.Variant is not null;
            var constructor = type.InstanceConstructors
                                  .Where(candidate => candidate.DeclaredAccessibility == Accessibility.Public &&
                                                      !(type.IsValueType && candidate.IsImplicitlyDeclared) &&
                                                      candidate.Parameters.All(parameter => parameter.RefKind is RefKind.None or RefKind.In &&
                                                                                            TypeShape.Of(parameter.Type) != TypeShapeKind.RefLikeOrPointer))
                                  .OrderBy(candidate => placing && candidate.Parameters.Any(parameter => Kind(Substitute(parameter.Type)) == ParameterKind.Delegate) ? 0 : 1)
                                  .ThenBy(candidate => candidate.Parameters.Length)
                                  .FirstOrDefault();
            return constructor is null
                ? null
                : $"new {R(type)}({string.Join(", ", constructor.Parameters.Select(parameter => Argument(parameter.Type, owner, depth - 1, hoist)))})";
        }

        /// <summary>A public static method of the library whose result converts to the type, fewest parameters first; generic
        /// factories with interface constraints and factories taking the very type they produce are skipped.</summary>
        /// <param name="type">The type to produce.</param>
        /// <param name="owner">What the value belongs to.</param>
        /// <param name="depth">How many more constructors or factories the recipe may nest.</param>
        /// <param name="hoist">Setup's statements, or <c>null</c>.</param>
        private string? Factory(INamedTypeSymbol type, Owner owner, int depth, List<string>? hoist)
        {
            if (ContainsProbe(type))
                return null;
            _producers ??= LibraryTypes().Where(candidate => !candidate.IsGenericType)
                                         .SelectMany(candidate => candidate.GetMembers().OfType<IMethodSymbol>())
                                         .Where(method => method is { IsStatic: true, MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public, ReturnsVoid: false } &&
                                                          method.Parameters.All(parameter => parameter.RefKind == RefKind.None &&
                                                                                             TypeShape.Of(parameter.Type) != TypeShapeKind.RefLikeOrPointer))
                                         .OrderBy(method => method.Parameters.Length)
                                         .ToList();
            foreach (var producer in _producers)
            {
                var method = producer;
                if (method.IsGenericMethod)
                {
                    if (method.TypeParameters.Any(parameter => parameter.ConstraintTypes.Any(constraint => constraint.TypeKind == TypeKind.Interface)))
                        continue;
                    var arguments = method.TypeParameters.Select(RecipeChoice).ToArray();
                    if (arguments.Any(argument => argument is null))
                        continue;
                    method = method.Construct(arguments!);
                }

                if (!Converts(method.ReturnType, type) || method.Parameters.Any(parameter => SymbolEqualityComparer.Default.Equals(parameter.Type, type)))
                    continue;
                var typeArguments = method.IsGenericMethod ? "<" + string.Join(", ", method.TypeArguments.Select(R)) + ">" : "";
                return $"{R(method.ContainingType)}.{Escape(method.Name)}{typeArguments}" +
                       $"({string.Join(", ", method.Parameters.Select(parameter => Argument(parameter.Type, owner, depth - 1, hoist)))})";
            }

            return null;
        }

        /// <summary>The library's public, non-generic, non-abstract classes that convert to the type, fewest constructor parameters
        /// first.</summary>
        /// <param name="type">The abstract class or interface.</param>
        private IEnumerable<INamedTypeSymbol> Implementations(INamedTypeSymbol type) =>
            LibraryTypes().Where(candidate => candidate is { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false, IsGenericType: false } &&
                                              Converts(candidate, type))
                          .OrderBy(candidate => candidate.InstanceConstructors.Select(constructor => constructor.Parameters.Length).DefaultIfEmpty(int.MaxValue).Min());

        /// <summary>A value nested in a constructor's or factory's arguments. In setup, a holding object that carries no probe is
        /// kept in a <c>Keep.K&lt;n&gt;</c> intermediate of its own.</summary>
        /// <param name="type">The argument's type.</param>
        /// <param name="owner">What the value belongs to.</param>
        /// <param name="depth">How many more constructors or factories the recipe may nest.</param>
        /// <param name="hoist">Setup's statements, or <c>null</c>.</param>
        private string Argument(ITypeSymbol type, Owner owner, int depth, List<string>? hoist)
        {
            var probes = owner.Probes.Count;
            var value = Value(type, owner, depth, hoist);
            type = Substitute(type);
            if (hoist is null || owner.Probes.Count != probes || type.IsValueType || !ParameterKinds.IsHolding(Kind(type)) ||
                TypeShape.Of(type) == TypeShapeKind.RefLikeOrPointer)
            {
                return value;
            }

            var field = $"K{_intermediates++}";
            _keep.Add($"public static {R(type)} {field};");
            hoist.Add($"{KEEP_TYPE}.{field} = {value};");
            return $"{KEEP_TYPE}.{field}";
        }

        // ---- lambdas ----

        /// <summary>A probe of the owner's variant, made by a factory method of its own.</summary>
        /// <param name="type">The delegate type.</param>
        /// <param name="owner">The parameter in its variant.</param>
        private string ProbeFactory(INamedTypeSymbol type, Owner owner)
        {
            var index = owner.Probes.Count;
            var id = owner.Name;
            var factory = $"L_{id}_{index}_{owner.Variant}";
            var fired = $"P_{id}_{index}_{owner.Variant}";
            _fields.Add($"public static int {fired};");
            var inputs = new List<string>();
            owner.Probes.Add(new DriverProbe(owner.Name, owner.Variant!, index, factory, $"M:{DRIVER_TYPE}.{factory}", fired, inputs));
            _methods.Add($"public static {R(type)} {factory}() => {Lambda(type, owner, ARGUMENT_DEPTH, (fired, inputs))};");
            return factory + "()";
        }

        /// <summary>A lambda with explicit parameter types, cast to its delegate type. A probe writes its fired field first and
        /// stores each parameter in an <c>In_</c> field; a plain lambda witnesses its inputs. Every lambda assigns its <c>out</c> parameters and returns a value made in
        /// its own body.</summary>
        /// <param name="type">The delegate type.</param>
        /// <param name="owner">What the lambda belongs to.</param>
        /// <param name="depth">How many more delegates a returned value may nest.</param>
        /// <param name="probe">The fired field and the list receiving the input fields, for a probe; <c>null</c> for a plain lambda.</param>
        private string Lambda(INamedTypeSymbol type, Owner owner, int depth, (string Fired, List<string> Inputs)? probe)
        {
            if (type.DelegateInvokeMethod is not { ReturnsByRef: false, ReturnsByRefReadonly: false } invoke)
                return Fail($"{owner.Name} needs a {type.ToDisplayString()}, which returns by reference");
            var parameters = new List<string>();
            var body = new List<string>();
            if (probe is { } written)
                body.Add($"{written.Fired} = 1;");
            var inline = owner.Inline();
            foreach (var parameter in invoke.Parameters)
            {
                var name = $"a{_lambdaParameters++}";
                var parameterType = Substitute(parameter.Type);
                parameters.Add($"{RefPrefix(parameter.RefKind)}{R(parameterType)} {name}");
                if (parameter.RefKind == RefKind.Out)
                    body.Add($"{name} = {ReturnValue(parameterType, inline, depth)};");
                else if (probe is { } stored && TypeShape.Of(parameterType) != TypeShapeKind.RefLikeOrPointer)
                {
                    var field = $"In_{owner.Name}_{owner.Inputs++}_{owner.Variant}";
                    _fields.Add($"public static {R(parameterType)} {field};");
                    body.Add($"{field} = {name};");
                    stored.Inputs.Add(field);
                }
                else if (probe is null && TypeShape.Of(parameterType) != TypeShapeKind.RefLikeOrPointer)
                    body.Add(WitnessStore(name));
            }

            if (!invoke.ReturnsVoid)
                body.Add($"return {ReturnValue(Substitute(invoke.ReturnType), inline, depth)};");
            return $"(({R(type)})(({string.Join(", ", parameters)}) => {{ {string.Join(" ", body)} }}))";
        }

        /// <summary>What a lambda returns, made in its own body: a completed task of a value made by this rule, a canned value, a
        /// probe class instance for a type parameter, an <c>R_</c> marker for <c>object</c>, an interface or an abstract class, the
        /// recipe's constructor or factory for a concrete class or a struct with references; a holding value no rule makes is
        /// <c>driver-not-synthesized</c>, never a default.</summary>
        /// <param name="type">The returned type.</param>
        /// <param name="owner">What the lambda belongs to.</param>
        /// <param name="depth">How many more delegates or constructors a returned value may nest.</param>
        private string ReturnValue(ITypeSymbol type, Owner owner, int depth)
        {
            type = Substitute(type);
            switch (TypeShape.Of(type))
            {
                case TypeShapeKind.RefLikeOrPointer:
                    return Fail($"a lambda of {owner.Name} returns {type.ToDisplayString()}, a ref-like or pointer type");
                case TypeShapeKind.Delegate:
                    return depth > 0 ? Lambda((INamedTypeSymbol)type, owner, depth - 1, null) : Fail($"a lambda of {owner.Name} returns delegates too deep");
                case TypeShapeKind.Task:
                    return type.Name == "Task" ? "global::System.Threading.Tasks.Task.CompletedTask" : $"default({R(type)})";
                case TypeShapeKind.TaskOfT:
                {
                    var result = ((INamedTypeSymbol)type).TypeArguments[0];
                    var inner = ReturnValue(result, owner, depth);
                    return type.Name == "Task" ? $"global::System.Threading.Tasks.Task.FromResult<{R(result)}>({inner})" : $"new {R(type)}({inner})";
                }
                case TypeShapeKind.Immutable:
                    return Canned(type, owner);
                case TypeShapeKind.PlainStruct:
                    return $"default({R(type)})";
            }

            // A struct with references holds as a class does: the recipe's constructor or factory below, never a default.

            switch (type)
            {
                case ITypeParameterSymbol parameter:
                    return _probeTypes.TryGetValue(parameter, out var probe) ? ProbeInstance(probe) : Fail($"{parameter.Name} has no type argument");
                case IArrayTypeSymbol:
                    return $"new {R(type)} {{ }}";
                case IDynamicTypeSymbol:
                    return $"new {DriverClass("R", owner.Name, library.GetSpecialType(SpecialType.System_Object), null)}()";
                case INamedTypeSymbol { SpecialType: SpecialType.System_Object } or INamedTypeSymbol { TypeKind: TypeKind.Interface }:
                    return Implementable((INamedTypeSymbol)type) || type.SpecialType == SpecialType.System_Object
                        ? $"new {DriverClass("R", owner.Name, (INamedTypeSymbol)type, null)}()"
                        : Fail($"no class can implement the {type.ToDisplayString()} a lambda of {owner.Name} returns");
                case INamedTypeSymbol { IsAbstract: true } abstractClass when Derivation.CanDerive(abstractClass):
                    return $"new {DriverClass("R", owner.Name, abstractClass, null)}()";
                case INamedTypeSymbol named:
                    return (named.IsAbstract || named.IsStatic ? null : Constructor(named, owner, Math.Max(depth, INLINE_DEPTH), null)) ??
                           Factory(named, owner, Math.Max(depth, INLINE_DEPTH), null) ??
                           Fail($"only a default could fill the {type.ToDisplayString()} a lambda of {owner.Name} returns");
                default:
                    return Fail($"a lambda of {owner.Name} returns {type.ToDisplayString()}");
            }
        }

        // ---- driver classes ----

        /// <summary>The driver's sealed class of one role for one owner and type, built once: <c>Probe_</c> with two reference fields
        /// and an <c>int</c> field, <c>Sub_</c> a subclass, <c>R_</c> a returned marker. Its one constructor calls the base
        /// constructor the recipe would pick; every expressible member a user type could override or implement is a witness.</summary>
        /// <param name="role"><c>Probe</c>, <c>Sub</c> or <c>R</c>.</param>
        /// <param name="owner">The parameter, <c>Recv</c>, or a trigger argument the class is for.</param>
        /// <param name="type">The class to derive from, the interface to implement, or <c>object</c>.</param>
        /// <param name="excluded">The members left alone, or <c>null</c>.</param>
        private string DriverClass(string role, string owner, INamedTypeSymbol type, ISet<ISymbol>? excluded)
        {
            var key = $"{role}|{owner}|{R(type)}";
            if (_classes.TryGetValue(key, out var existing))
                return existing;
            var name = Unique($"{role}_{owner}");
            _classes[key] = name;
            _classKeys.Add(key);
            var (baseClass, interfaces) = type.TypeKind == TypeKind.Interface ? (null, new[] { type })
                : type.SpecialType == SpecialType.System_Object ? (null, [])
                : (type, Array.Empty<INamedTypeSymbol>());
            _types.Add(ClassDeclaration(name, baseClass, interfaces, role == "Probe", excluded, owner));
            return name;
        }

        private string ClassDeclaration(string name, INamedTypeSymbol? baseClass, IReadOnlyList<INamedTypeSymbol> interfaces, bool fields,
                                        ISet<ISymbol>? excluded, string owner)
        {
            var bases = (baseClass is null ? [] : new[] { R(baseClass) }).Concat(interfaces.Select(R)).ToArray();
            var text = new StringBuilder();
            text.Append("public sealed class ").Append(name).Append(bases.Length > 0 ? " : " + string.Join(", ", bases) : "").Append("\n{\n");
            if (fields)
                text.Append("    public object Ref0;\n    public object Ref1;\n    public int Int0;\n");
            var root = baseClass ?? library.GetSpecialType(SpecialType.System_Object);
            if (Derivation.Constructors(root).FirstOrDefault() is not { } constructor)
                Fail($"{root.ToDisplayString()} has no constructor a subclass can call");
            else
            {
                var arguments = constructor.Parameters.Select(parameter => Value(parameter.Type, new Owner(owner, null), INLINE_DEPTH, null));
                text.Append("    public ").Append(name).Append("() : base(").Append(string.Join(", ", arguments)).Append(") { }\n");
            }

            if (baseClass is not null && !Derivation.CanDerive(baseClass))
                Fail($"no class can derive from {baseClass.ToDisplayString()}");
            foreach (var overridable in Derivation.Overridable(root, out _).Where(candidate => excluded?.Contains(candidate.OriginalDefinition) != true))
                text.Append("    ").Append(Override(overridable)).Append('\n');
            foreach (var @interface in interfaces.SelectMany(@interface => @interface.AllInterfaces.Prepend(@interface))
                                                 .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
            {
                if (!Implementable(@interface))
                    Fail($"no class can implement {@interface.ToDisplayString()}");
                foreach (var implemented in @interface.GetMembers().Where(candidate => !candidate.IsStatic && (candidate.IsAbstract || candidate.IsVirtual) &&
                                                                                       !candidate.IsSealed &&
                                                                                       candidate is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol))
                {
                    text.Append("    ").Append(Explicit(@interface, implemented)).Append('\n');
                }
            }

            return text.Append("}\n").ToString();
        }

        /// <summary>Whether a class in another assembly can implement the interface: it is visible, every member it must
        /// implement is public, and none is static.</summary>
        /// <param name="interface">The interface.</param>
        private static bool Implementable(INamedTypeSymbol @interface) =>
            Derivation.IsVisible(@interface) &&
            @interface.AllInterfaces.Prepend(@interface).SelectMany(type => type.GetMembers())
                      .All(candidate => !candidate.IsAbstract || !candidate.IsStatic && candidate.DeclaredAccessibility == Accessibility.Public);

        private string Override(ISymbol overridden)
        {
            var access = overridden.DeclaredAccessibility == Accessibility.Public ? "public" : "protected";
            return overridden switch
            {
                IMethodSymbol method => MethodWitness($"{access} override ", method),
                IPropertySymbol property => PropertyWitness($"{access} override ", property),
                IEventSymbol @event => $"{access} override event {R(@event.Type)} {Escape(@event.Name)} {{ " +
                                        $"add {{ {WitnessStore("this")} {WitnessStore("value")} }} remove {{ {WitnessStore("this")} {WitnessStore("value")} }} }}",
                _ => ""
            };
        }

        /// <summary>An explicit witness implementation of an interface member.</summary>
        /// <param name="interface">The interface.</param>
        /// <param name="implemented">Its method, property, indexer or event.</param>
        private string Explicit(INamedTypeSymbol @interface, ISymbol implemented)
        {
            var owner = R(@interface);
            return implemented switch
            {
                IMethodSymbol method => MethodWitness("", method, owner + "."),
                IPropertySymbol property => PropertyWitness("", property, owner + "."),
                IEventSymbol @event => $"event {R(@event.Type)} {owner}.{Escape(@event.Name)} {{ " +
                                        $"add {{ {WitnessStore("this")} {WitnessStore("value")} }} remove {{ {WitnessStore("this")} {WitnessStore("value")} }} }}",
                _ => ""
            };
        }

        private string PropertyWitness(string modifier, IPropertySymbol property, string explicitOwner = "")
        {
            var accessors = new List<(string Declaration, string? Body)>();
            var snapshot = Snapshot();
            var explicitly = explicitOwner.Length > 0;
            foreach (var accessor in new[] { property.GetMethod, property.SetMethod })
            {
                if (accessor is null || !explicitly && !Derivation.IsVisibleToSubclass(accessor.DeclaredAccessibility))
                    continue;
                var accessorModifier = explicitly || accessor.DeclaredAccessibility == property.DeclaredAccessibility ? ""
                    : accessor.DeclaredAccessibility == Accessibility.Public ? "public " : "protected ";
                var keyword = accessor.MethodKind == MethodKind.PropertyGet ? "get" : accessor.IsInitOnly ? "init" : "set";
                var body = WitnessBody(accessor, accessor.ReturnType, accessor.ReturnsByRef, accessor.ReturnsByRefReadonly,
                                       index => accessor.MethodKind == MethodKind.PropertySet && index == accessor.Parameters.Length - 1 ? "value" : $"p{index}");
                accessors.Add(($"{accessorModifier}{keyword}", body));
            }

            var isExtern = accessors.Any(accessor => accessor.Body is null);
            if (isExtern)
                Rollback(snapshot, "Witness");
            var bodies = string.Join(" ", accessors.Select(accessor => accessor.Declaration + (isExtern ? ";" : " " + accessor.Body)));
            return $"{modifier}{(isExtern ? "extern " : "")}{RefReturn(property.ReturnsByRef, property.ReturnsByRefReadonly)}{R(property.Type)} " +
                   $"{explicitOwner}{(property.IsIndexer ? $"this[{Signature(property.Parameters)}]" : Escape(property.Name))} {{ {bodies} }}";
        }

        private string MethodWitness(string modifier, IMethodSymbol method, string explicitOwner = "")
        {
            var body = WitnessBody(method, method.ReturnType, method.ReturnsByRef, method.ReturnsByRefReadonly, index => $"p{index}");
            var externModifier = body is null ? "extern " : "";
            return $"{modifier}{externModifier}{RefReturn(method.ReturnsByRef, method.ReturnsByRefReadonly)}" +
                   $"{(method.ReturnsVoid ? "void" : R(method.ReturnType))} {explicitOwner}{Escape(method.Name)}{TypeParameters(method)}" +
                   $"({Signature(method.Parameters)}){(body is null ? ";" : " " + body)}";
        }

        private string? WitnessBody(IMethodSymbol method, ITypeSymbol returnType, bool returnsByRef, bool returnsByRefReadonly,
                                    Func<int, string> parameterName)
        {
            if (method.Parameters.Any(parameter => TypeShape.Of(Substitute(parameter.Type)) == TypeShapeKind.RefLikeOrPointer) ||
                TypeShape.Of(Substitute(returnType)) == TypeShapeKind.RefLikeOrPointer)
            {
                return null;
            }

            var snapshot = Snapshot();
            var result = WitnessReturn(method.ReturnsVoid, Substitute(returnType), returnsByRef, returnsByRefReadonly);
            if (!method.ReturnsVoid && result is null)
                return null;
            var field = ReadsByConvention(method) ? READ_WITNESS_FIELD : WITNESS_FIELD;
            var statements = new List<string> { WitnessStore("this", field) };
            foreach (var (parameter, index) in method.Parameters.Select((parameter, index) => (parameter, index)))
            {
                var name = parameterName(index);
                if (parameter.RefKind != RefKind.Out)
                    statements.Add(WitnessStore(name, field));
                // What the member gives back through out is the witness's, as what it returns is (TD-034b).
                else if (WitnessValue(parameter.Type) is { } value)
                    statements.Add($"{name} = {value};");
                else
                {
                    Rollback(snapshot, "Witness");
                    return null;
                }
            }

            if (result is not null)
                statements.Add(result);
            return "{ " + string.Join(" ", statements) + " }";
        }

        private string? WitnessReturn(bool returnsVoid, ITypeSymbol type, bool byRef, bool byRefReadonly)
        {
            if (returnsVoid)
                return null;
            if (byRef || byRefReadonly)
                return $"return ref {WITNESSED_REF_TYPE}<{R(type)}>.Value;";
            var shape = TypeShape.Of(type);
            if (shape == TypeShapeKind.Task)
                return type.Name == "Task" ? "return global::System.Threading.Tasks.Task.CompletedTask;" : $"return default({R(type)});";
            if (shape == TypeShapeKind.TaskOfT)
            {
                var inner = ((INamedTypeSymbol)type).TypeArguments[0];
                var value = WitnessValue(inner);
                if (value is null)
                    return null;
                return type.Name == "Task"
                    ? $"return global::System.Threading.Tasks.Task.FromResult<{R(inner)}>({value});"
                    : $"return new {R(type)}({value});";
            }

            var direct = WitnessValue(type);
            return direct is null ? null : $"return {direct};";
        }

        private string? WitnessValue(ITypeSymbol type)
        {
            type = Substitute(type);
            if (type is ITypeParameterSymbol parameter)
                return _probeTypes.TryGetValue(parameter, out var probe) ? ProbeInstance(probe) : null;
            if (TypeShape.Of(type) == TypeShapeKind.RefLikeOrPointer)
                return null;
            if (TypeShape.Of(type) is TypeShapeKind.Immutable or TypeShapeKind.PlainStruct)
                return Canned(type, new Owner("Witness", null));
            var snapshot = Snapshot();
            var value = SeedableFields.Seed(type) ||
                        SeedableFields.Path(type) && (type is IArrayTypeSymbol ||
                                                     type is INamedTypeSymbol collection && SeedableFields.Collection(collection) is not null)
                ? SeedReturnValue(type)
                : type is INamedTypeSymbol namedType
                    ? Constructor(namedType, new Owner("Witness", null), INLINE_DEPTH, null) ??
                      Factory(namedType, new Owner("Witness", null), INLINE_DEPTH, null)
                    : null;
            if (value is not null && Failures.Count == snapshot.Failures)
                return value;
            Rollback(snapshot, "Witness");
            return null;
        }

        private string? SeedReturnValue(ITypeSymbol type)
        {
            type = Substitute(type);
            if (type is IArrayTypeSymbol array)
                return SeedArray(array);
            if (type is INamedTypeSymbol named && SeedableFields.Collection(named) is { } collection)
            {
                var values = collection.Axes.Select(SeedAxis).ToArray();
                if (values.Any(value => value is null))
                    return named.TypeKind == TypeKind.Interface || named.IsAbstract ? SeedClassValue(named) : null;
                var concrete = CollectionType(collection);
                var factory = Unique($"SeedContainer_{_seedContainers++}");
                var insert = collection.Dictionary
                    ? collection.InsertionMember == "TryAdd" ? $"value.TryAdd({values[0]}, {values[1]});" : $"value[{values[0]}] = {values[1]};"
                    : $"value.{collection.InsertionMember}({values[0]});";
                _methods.Add($"internal static {R(type)} {factory}() {{ var value = new {concrete}(); {insert} return value; }}");
                return $"{DRIVER_TYPE}.{factory}()";
            }
            return SeedClassValue(type);
        }

        /// <summary>A witness's store of what it received into a new field of <see cref="WITNESSED_TYPE"/>.</summary>
        /// <param name="value">What it received.</param>
        /// <param name="prefix"><see cref="WITNESS_FIELD"/>, or <see cref="READ_WITNESS_FIELD"/> for a member the analysis takes to read.</param>
        private string WitnessStore(string value, string prefix = WITNESS_FIELD)
        {
            var field = prefix + _witnessFields.Count;
            _witnessFields.Add(field);
            return $"{WITNESSED_TYPE}.{field} = {value};";
        }

        /// <summary>Whether the analysis takes the member a witness stands for to read what it gets, as deep read takes user code it does
        /// not run (TD-034b): a getter, <c>ToString</c>, <c>Equals</c>, <c>GetHashCode</c>, or a member of comparison.</summary>
        /// <param name="method">The member, or its accessor, as the base or the interface declares it.</param>
        private static bool ReadsByConvention(IMethodSymbol method) =>
            method.MethodKind == MethodKind.PropertyGet ||
            method is { Name: nameof(ToString) or nameof(GetHashCode), Parameters.Length: 0 } ||
            method is { Name: nameof(Equals), Parameters: [{ Type.SpecialType: SpecialType.System_Object }] } ||
            ComparisonTypes.Contains($"{method.ContainingType.ContainingNamespace.ToDisplayString()}.{method.ContainingType.MetadataName}");

        private static string TypeParameters(IMethodSymbol method) =>
            method.IsGenericMethod ? "<" + string.Join(", ", method.TypeParameters.Select(parameter => Escape(parameter.Name))) + ">" : "";

        private string Signature(IEnumerable<IParameterSymbol> parameters) =>
            string.Join(", ", parameters.Select((parameter, index) => $"{RefPrefix(parameter.RefKind)}{R(parameter.Type)} p{index}"));

        private static string RefReturn(bool byRef, bool byRefReadonly) => byRefReadonly ? "ref readonly " : byRef ? "ref " : "";

        private static string RefPrefix(RefKind kind) => kind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            RefKind.RefReadOnlyParameter => "ref readonly ",
            _ => ""
        };

        // ---- types ----

        /// <summary>The kind of a substituted type; a type parameter with a probe class is a probe object.</summary>
        /// <param name="type">The substituted type.</param>
        private ParameterKind Kind(ITypeSymbol type) =>
            type is ITypeParameterSymbol parameter && _probeTypes.ContainsKey(parameter) ? ParameterKind.ProbeObject : ParameterKinds.Of(type);

        /// <summary>The type with every type parameter the recipe chose a type for replaced by it; type parameters with a probe
        /// class stay, and render as their class.</summary>
        /// <param name="type">The type.</param>
        private ITypeSymbol Substitute(ITypeSymbol type)
        {
            if (_choices.Count == 0)
                return type;
            switch (type)
            {
                case ITypeParameterSymbol parameter:
                    return _choices.TryGetValue(parameter, out var chosen) ? chosen : parameter;
                case IArrayTypeSymbol array:
                    return library.CreateArrayTypeSymbol(Substitute(array.ElementType), array.Rank);
                case IPointerTypeSymbol pointer:
                    return library.CreatePointerTypeSymbol(Substitute(pointer.PointedAtType));
                case INamedTypeSymbol named when named.TypeArguments.Length > 0 || named.ContainingType is not null:
                {
                    var containing = named.ContainingType is { } outer ? (INamedTypeSymbol)Substitute(outer) : null;
                    var definition = containing is not null && !SymbolEqualityComparer.Default.Equals(containing, named.ContainingType)
                        ? containing.GetTypeMembers(named.Name, named.Arity).First()
                        : named.ConstructedFrom;
                    return named.Arity > 0 ? definition.Construct(named.TypeArguments.Select(Substitute).ToArray()) : definition;
                }

                default:
                    return type;
            }
        }

        private bool ContainsProbe(ITypeSymbol type) => type switch
        {
            ITypeParameterSymbol parameter => _probeTypes.ContainsKey(parameter),
            IArrayTypeSymbol array => ContainsProbe(array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(ContainsProbe) || named.ContainingType is { } outer && ContainsProbe(outer),
            _ => false
        };

        /// <summary>The type as source text, fully qualified, type parameters replaced by their probe class or choice.</summary>
        /// <param name="type">The type.</param>
        private string R(ITypeSymbol type)
        {
            switch (type)
            {
                case ITypeParameterSymbol parameter:
                    return _probeTypes.TryGetValue(parameter, out var probe) ? probe
                        : _choices.TryGetValue(parameter, out var chosen) ? R(chosen)
                        : Escape(parameter.Name);
                case IArrayTypeSymbol array:
                {
                    var ranks = new List<int>();
                    ITypeSymbol element = array;
                    for (; element is IArrayTypeSymbol nested; element = nested.ElementType)
                        ranks.Add(nested.Rank);
                    return R(element) + string.Concat(ranks.Select(rank => "[" + new string(',', rank - 1) + "]"));
                }

                case IPointerTypeSymbol pointer:
                    return R(pointer.PointedAtType) + "*";
                case IDynamicTypeSymbol:
                    return "dynamic";
                case INamedTypeSymbol named:
                {
                    if (named.IsTupleType && named.TupleUnderlyingType is { } underlying)
                        named = underlying;
                    var prefix = named.ContainingType is { } outer ? R(outer) + "."
                        : named.ContainingNamespace is { IsGlobalNamespace: false } ns ? "global::" + string.Join(".", ns.ToDisplayParts().Where(part => part.Kind == SymbolDisplayPartKind.NamespaceName).Select(part => Escape(part.ToString()))) + "."
                        : "global::";
                    return prefix + Escape(named.Name) + (named.Arity > 0 ? "<" + string.Join(", ", named.TypeArguments.Select(R)) + ">" : "");
                }

                default:
                    Fail($"the driver cannot name {type.ToDisplayString()}");
                    return "object";
            }
        }

        private IEnumerable<INamedTypeSymbol> AllLibraryTypes() => Types(library.Assembly.GlobalNamespace);

        private IEnumerable<INamedTypeSymbol> LibraryTypes() => AllLibraryTypes().Where(PubliclyNamed);

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol @namespace) =>
            @namespace.GetTypeMembers().SelectMany(Nested).Concat(@namespace.GetNamespaceMembers().SelectMany(Types));

        private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) => type.GetTypeMembers().SelectMany(Nested).Prepend(type);

        private static bool PubliclyNamed(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility != Accessibility.Public)
                    return false;
            }

            return true;
        }

        private bool Converts(ITypeSymbol from, ITypeSymbol to)
        {
            var conversion = library.ClassifyConversion(from, to);
            return conversion.IsIdentity || conversion.IsImplicit && conversion.IsReference;
        }

        // ---- bookkeeping ----

        private Owner OwnerOf(IParameterSymbol parameter, string variant)
        {
            if (!_owners.TryGetValue((parameter.Name, variant), out var owner))
                _owners[(parameter.Name, variant)] = owner = new Owner(parameter.Name, variant);
            return owner;
        }

        private string Fail(string reason)
        {
            Failures.Add(reason);
            return "default";
        }

        private string Unique(string name)
        {
            var unique = name;
            for (var suffix = 2; !_names.Add(unique); suffix++)
                unique = $"{name}_{suffix}";
            return unique;
        }

        private static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

        private (int Failures, int Defaulted, int Setup, int Fields, int Keep, int Types, int Methods, int SeedTypes, int ClassKeys, int Chosen) Snapshot() =>
            (Failures.Count, Defaulted.Count, _setup.Count, _fields.Count, _keep.Count, _types.Count, _methods.Count, _seedTypeKeys.Count,
             _classKeys.Count, _chosen.Count);

        /// <summary>Drops everything a trigger variant added, when one of its values could not be built.</summary>
        /// <param name="snapshot">The sizes before the variant was built.</param>
        /// <param name="variant">The trigger's variant.</param>
        private void Rollback((int Failures, int Defaulted, int Setup, int Fields, int Keep, int Types, int Methods, int SeedTypes, int ClassKeys, int Chosen) snapshot,
                              string variant)
        {
            foreach (var parameter in _chosen.Skip(snapshot.Chosen))
            {
                _probeTypes.Remove(parameter);
                _choices.Remove(parameter);
            }

            _chosen.RemoveRange(snapshot.Chosen, _chosen.Count - snapshot.Chosen);
            Failures.RemoveRange(snapshot.Failures, Failures.Count - snapshot.Failures);
            Defaulted.RemoveRange(snapshot.Defaulted, Defaulted.Count - snapshot.Defaulted);
            _setup.RemoveRange(snapshot.Setup, _setup.Count - snapshot.Setup);
            _fields.RemoveRange(snapshot.Fields, _fields.Count - snapshot.Fields);
            _keep.RemoveRange(snapshot.Keep, _keep.Count - snapshot.Keep);
            _types.RemoveRange(snapshot.Types, _types.Count - snapshot.Types);
            _methods.RemoveRange(snapshot.Methods, _methods.Count - snapshot.Methods);
            foreach (var key in _seedTypeKeys.Skip(snapshot.SeedTypes))
                _seedTypes.Remove(key);
            _seedTypeKeys.RemoveRange(snapshot.SeedTypes, _seedTypeKeys.Count - snapshot.SeedTypes);
            foreach (var key in _classKeys.Skip(snapshot.ClassKeys))
                _classes.Remove(key);
            _classKeys.RemoveRange(snapshot.ClassKeys, _classKeys.Count - snapshot.ClassKeys);
            foreach (var key in _owners.Keys.Where(key => key.Variant == variant).ToArray())
                _owners.Remove(key);
        }
    }
}
