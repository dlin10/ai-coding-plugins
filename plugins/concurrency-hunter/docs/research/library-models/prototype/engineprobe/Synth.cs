// Driver synthesis from signatures: for one library member, build the driver the generator needs without a human.
// Everything below is decided from symbols of the library's decompiled source; nothing is written per member.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

static class Synth
{
    private const int MAX_TRIGGERS = 24;
    private static readonly SymbolDisplayFormat Q = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>One library loaded once: its solution (user document swapped per member, library compilation reused).</summary>
    private sealed class Library
    {
        public string Name;
        public Solution Solution;
        public DocumentId UserDocument;
    }

    public sealed record Outcome(string Member, string Library, bool Compiled, string CompileError, bool ReceiverNull, int NullFallbacks,
                                 int Triggers, Dictionary<string, string> Labels, double Seconds, string EngineError, string Driver);

    // ---- entry points ----

    public static void Run(string root, string mode)
    {
        var libraries = new Dictionary<string, Library>();
        Library Load(string name)
        {
            if (libraries.TryGetValue(name, out var existing))
                return existing;
            var source = File.ReadAllText(Path.Combine(root, "decompiled", name + ".cs"))
                             .Replace("[assembly: AssemblyVersion(\"8.0.0.0\")]", "[assembly: AssemblyVersion(\"10.0.0.0\")]");
            var (solution, user) = Build(name, source);
            return libraries[name] = new Library { Name = name, Solution = solution, UserDocument = user };
        }

        var results = new List<Outcome>();
        var total = Stopwatch.StartNew();
        if (mode == "gold")
        {
            var gold = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "gold.json"))).RootElement.EnumerateArray().ToArray();
            string[] keys = ["All", "FirstOrDefault", "SingleOrDefault", "Sum", "ToDictionary", "Where", "Select", "SelectMany", "OrderBy", "GroupBy",
                             "FindFirst", "PolicyExecute", "AsyncPolicyExecuteAsync", "WaitAndRetry", "WaitAndRetryAsync", "WaitAndRetryForeverAsync",
                             "MessageParserCtor", "ForMessage"];
            foreach (var key in keys)
            {
                var entry = gold.First(e => e.GetProperty("key").GetString() == key);
                var library = Load(entry.GetProperty("assembly").GetString());
                var outcome = Member(library, entry.GetProperty("id").GetString(),
                                     entry.GetProperty("delegateParams").EnumerateArray().Select(p => p.GetString()).ToArray());
                results.Add(outcome);
                Print(outcome, entry.GetProperty("label").GetString());
            }
        }
        else
        {
            var random = new Random(20260927);
            foreach (var (name, typeFilter, sample) in new[]
                     {
                         ("Google.Protobuf", (string)null, 1000), ("System.Security.Claims", null, 1000),
                         ("Polly", null, 60), ("System.Linq", "System.Linq.Enumerable", 60)
                     })
            {
                var library = Load(name);
                var compilation = library.Solution.GetProject(library.UserDocument.ProjectId)!.GetCompilationAsync().Result!;
                var assembly = compilation.References.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>().First(a => a.Name == name);
                var candidates = Types(assembly.GlobalNamespace)
                                 .Where(t => t.DeclaredAccessibility == Accessibility.Public && (typeFilter is null || t.ToDisplayString() == typeFilter))
                                 .SelectMany(t => t.GetMembers().OfType<IMethodSymbol>())
                                 .Where(m => m.DeclaredAccessibility == Accessibility.Public &&
                                             m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.PropertySet &&
                                             m.Parameters.Any(p => IsDelegate(p.Type)))
                                 .Select(m => m.MethodKind == MethodKind.PropertySet
                                             ? DocumentationCommentId.CreateDeclarationId(m.AssociatedSymbol!)
                                             : DocumentationCommentId.CreateDeclarationId(m))
                                 .Where(id => id is not null)
                                 .Distinct()
                                 .OrderBy(id => id, StringComparer.Ordinal)
                                 .ToList();
                var chosen = candidates.Count <= sample ? candidates : candidates.OrderBy(_ => random.Next()).Take(sample).OrderBy(id => id).ToList();
                Console.WriteLine($"== {name}: {candidates.Count} public members with a delegate parameter, running {chosen.Count}");
                foreach (var id in chosen)
                {
                    var outcome = Member(library, id, null);
                    results.Add(outcome);
                    Print(outcome, null);
                }
            }
        }

        File.WriteAllText(Path.Combine(root, $"synth-{mode}.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"total {total.Elapsed.TotalMinutes:F1} min");
    }

    private static void Print(Outcome o, string gold)
    {
        var status = !o.Compiled ? "NO-COMPILE " + o.CompileError : o.EngineError is not null ? "ENGINE " + o.EngineError
            : string.Join(", ", o.Labels.Select(l => $"{l.Key}: {l.Value}"));
        Console.WriteLine($"{Short(o.Member),-70} {(gold is null ? "" : "gold=" + gold + " ")}{o.Seconds,5:F1}s recvNull={o.ReceiverNull} nulls={o.NullFallbacks} trig={o.Triggers} | {status}");
    }

    private static string Short(string id) => id.Length > 70 ? id[..67] + "..." : id;

    // ---- one member ----

    private static Outcome Member(Library library, string id, string[] parameterNames)
    {
        var watch = Stopwatch.StartNew();
        string driver = null;
        var labels = new Dictionary<string, string>();
        try
        {
            // pass 1: placeholders for the stub classes, so that symbols constructed over them can be rendered
            var skeleton = Skeleton("", "");
            var compilation = Compile(library, skeleton + Placeholders(4));
            var plan = new Plan(compilation, library.Name);
            if (!plan.Resolve(id))
                return new Outcome(id, library.Name, false, "member not found", false, 0, 0, labels, watch.Elapsed.TotalSeconds, null, null);
            var probed = plan.Target.Parameters.Where(p => parameterNames?.Contains(p.Name) ?? IsDelegate(p.Type)).ToArray();
            driver = plan.Driver(probed);
            var final = Compile(library, driver);
            var errors = final.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
                return new Outcome(id, library.Name, false, errors[0].GetMessage(), plan.ReceiverNull, plan.NullFallbacks, plan.TriggerCount, labels,
                                   watch.Elapsed.TotalSeconds, null, driver);
            var solution = library.Solution.WithDocumentText(library.UserDocument, SourceText.From(driver));
            EngineRun run;
            try
            {
                run = EngineFixture.AnalyzeScope(solution, "scope:Fixture");
            }
            catch (Exception ex)
            {
                return new Outcome(id, library.Name, true, null, plan.ReceiverNull, plan.NullFallbacks, plan.TriggerCount, labels,
                                   watch.Elapsed.TotalSeconds, ex.GetType().Name + ": " + ex.Message.Split('\n')[0], driver);
            }

            var kept = Kept(run);
            foreach (var parameter in probed)
                labels[parameter.Name] = Classify(run, parameter.Ordinal, plan, kept);
            return new Outcome(id, library.Name, true, null, plan.ReceiverNull, plan.NullFallbacks, plan.TriggerCount, labels, watch.Elapsed.TotalSeconds,
                               null, driver);
        }
        catch (Exception ex)
        {
            return new Outcome(id, library.Name, false, "synthesis threw " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0], false, 0, 0, labels,
                               watch.Elapsed.TotalSeconds, null, driver);
        }
    }

    private static bool _shownRegions;

    /// <summary>The probes of the call variant whose delegate the heap still holds after the call: a delegate region created by the
    /// probe's factory and reachable from a static region (the driver keeps the result and the receiver in statics, and a library
    /// cache is static too) through fields, collection storage and closure captures.</summary>
    private static HashSet<int> Kept(EngineRun run)
    {
        var heap = run.Execution.Heap.Heap;
        if (!_shownRegions)
        {
            _shownRegions = true;
            Console.WriteLine("delegate regions: " + string.Join(" | ", heap.Regions.Values.Where(r => r.Kind.ToString() == "Delegate")
                                                                          .Select(r => $"{r.Display} @ {r.SiteBodyId}").Take(3)));
            Console.WriteLine("static regions: " + string.Join(" | ", heap.Regions.Values.Where(r => r.Kind.ToString() == "Static").Select(r => r.Display).Take(5)));
        }

        var seen = new HashSet<string>();
        var queue = new Queue<string>(heap.Regions.Values.Where(r => r.Kind.ToString() == "Static").Select(r => r.Identity));
        while (queue.Count > 0 && seen.Count < 20_000)
        {
            var region = queue.Dequeue();
            if (!seen.Add(region))
                continue;
            var next = heap.FieldsOf(region).SelectMany(field => heap.PointsTo(region, field));
            if (heap.Regions.TryGetValue(region, out var info) && info.Kind.ToString() == "Delegate")
                next = next.Concat(heap.DelegateCaptures(region));
            if (heap.Collections.TryGetValue(region, out var storages))
                next = next.Concat(storages);
            foreach (var target in next)
                queue.Enqueue(target);
        }

        var kept = new HashSet<int>();
        foreach (var region in seen)
        {
            if (heap.Regions.TryGetValue(region, out var info) && info.Kind.ToString() == "Delegate" && info.SiteBodyId is { } site &&
                System.Text.RegularExpressions.Regex.Match(site, @"L_P(\d+)_Call_") is { Success: true } match)
                kept.Add(int.Parse(match.Groups[1].Value));
        }

        return kept;
    }

    private static string Classify(EngineRun run, int ordinal, Plan plan, HashSet<int> kept)
    {
        // A probe counts for its variant only where it runs under that variant's own action: regions the heap merges across the
        // actions of one driver would otherwise let one action's trigger run another action's lambda.
        HashSet<string> Kinds(string variant) =>
            run.Of($"P{ordinal}_{variant}")
               .Select(a => run.Execution.Analysis.Execution(a.ExecutionId))
               .Where(e => OwnRoot(e.TreeRootId, variant))
               .Select(e => e.Kind.ToString())
               .ToHashSet();
        var nowKinds = Kinds("Call");
        var now = nowKinds.Contains("Root") || nowKinds.Contains("Startup");
        var isKept = kept.Contains(ordinal);
        // invoke-now only when the delegate ran during the call and nothing kept it: a delegate that also stays reachable may run again
        // later, anywhere, and the later runs decide its exposure
        if (now && !isKept)
            return "invoke-now" + (nowKinds.Contains("Spawn") ? "+spawn" : "") + (nowKinds.Contains("UnknownDelegateCall") ? "*" : "");
        if (plan.Enumerates && Kinds("Enum").Contains("Root"))
            return "iterator" + (now ? "(also now)" : "");
        var fired = plan.TriggerNames.Select((name, i) => (name, i)).Where(t => Kinds("T" + t.i).Contains("Root")).Select(t => t.name).ToArray();
        if (fired.Length > 0 && !now)
            return $"holder({string.Join("; ", fired.Take(2))}{(fired.Length > 2 ? $"; +{fired.Length - 2}" : "")})";
        if (!now && nowKinds.Count > 0)
            return "unknown(" + string.Join("/", nowKinds) + ")";
        // a delegate that ran during the call and is kept runs in every variant's own call, so a trigger variant cannot tell its later
        // runs apart from that one: it keeps the unknown execution
        return "framework-event(" + (now ? "kept, also now" : "open world") + ")";
    }

    private static bool OwnRoot(string display, string variant)
    {
        var action = variant switch { "Call" => "V_Call", "Enum" => "V_Enum", _ => variant };
        return System.Text.RegularExpressions.Regex.IsMatch(display, $@"\b{action}\b");
    }

    // ---- the plan of one driver ----

    private sealed class Plan(Compilation compilation, string libraryName)
    {
        public IMethodSymbol Target;
        public bool IsSetter;
        public bool ReceiverNull;
        public int NullFallbacks;
        public bool Enumerates;
        public int TriggerCount => TriggerNames.Count;
        public List<string> TriggerNames = [];
        private readonly Dictionary<ITypeParameterSymbol, ITypeSymbol> _choices = new(SymbolEqualityComparer.Default);
        private readonly List<string> _stubs = [];
        private readonly List<string> _factories = [];
        private bool _hasReceiver;
        private int _nextStub;
        private IAssemblySymbol _library;
        private List<IMethodSymbol> _producers;

        public bool Resolve(string id)
        {
            _library = compilation.References.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>().First(a => a.Name == libraryName);
            var symbol = DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation)
                                               .FirstOrDefault(s => SymbolEqualityComparer.Default.Equals(s.ContainingAssembly, _library));
            IMethodSymbol method = symbol switch
            {
                IMethodSymbol m => m,
                IPropertySymbol { SetMethod: { } setter } => setter,
                _ => null
            };
            if (method is null)
                return false;
            IsSetter = method.MethodKind == MethodKind.PropertySet;
            var type = method.ContainingType;
            if (type.IsGenericType)
            {
                var constructed = type.OriginalDefinition.Construct(type.OriginalDefinition.TypeParameters.Select(Choose).ToArray());
                method = constructed.GetMembers(method.Name).OfType<IMethodSymbol>()
                                    .First(m => SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, method.OriginalDefinition));
            }

            if (method.IsGenericMethod)
                method = method.Construct(method.TypeParameters.Select(Choose).ToArray());
            Target = method;
            return true;
        }

        /// <summary>A type argument that satisfies the parameter's constraints: a value type, object, the constraint class itself, or a
        /// stub class implementing the constraint interfaces over itself.</summary>
        private ITypeSymbol Choose(ITypeParameterSymbol parameter)
        {
            if (_choices.TryGetValue(parameter, out var chosen))
                return chosen;
            ITypeSymbol result;
            var constraints = parameter.ConstraintTypes;
            if (constraints.Length == 0)
                result = compilation.GetSpecialType(parameter.HasReferenceTypeConstraint ? SpecialType.System_Object : SpecialType.System_Int32);
            else if (constraints.All(c => c.TypeKind == TypeKind.Class) && constraints[0] is INamedTypeSymbol { IsAbstract: false } constraintClass &&
                     !constraintClass.IsGenericType)
                result = constraintClass;
            else if (constraints.All(c => c.TypeKind == TypeKind.Interface))
            {
                var stub = compilation.GetTypeByMetadataName($"Gen{_nextStub++}")!;
                _choices[parameter] = stub;
                var interfaces = constraints.Select(c => (INamedTypeSymbol)Substitute(c, parameter, stub)).ToArray();
                _stubs.Add(StubClass(stub.Name, interfaces));
                return stub;
            }
            else
                result = constraints[0];
            _choices[parameter] = result;
            return result;
        }

        private ITypeSymbol Substitute(ITypeSymbol type, ITypeParameterSymbol parameter, ITypeSymbol with) => type switch
        {
            ITypeParameterSymbol p when SymbolEqualityComparer.Default.Equals(p, parameter) => with,
            INamedTypeSymbol { IsGenericType: true } named => named.OriginalDefinition.Construct(
                named.TypeArguments.Select(a => Substitute(a, parameter, with)).ToArray()),
            _ => type
        };

        /// <summary>A class implementing every abstract member of the interfaces explicitly, with empty bodies.</summary>
        private static string StubClass(string name, INamedTypeSymbol[] interfaces)
        {
            var body = new StringBuilder();
            foreach (var @interface in interfaces.SelectMany(i => i.AllInterfaces.Prepend(i)).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
            {
                var owner = @interface.ToDisplayString(Q);
                foreach (var member in @interface.GetMembers().Where(m => m.IsAbstract && !m.IsStatic))
                {
                    switch (member)
                    {
                        case IMethodSymbol { MethodKind: MethodKind.Ordinary } method:
                        {
                            var typeParameters = method.TypeParameters.Length > 0 ? "<" + string.Join(", ", method.TypeParameters.Select(t => t.Name)) + ">" : "";
                            var parameters = string.Join(", ", method.Parameters.Select((p, i) => $"{RefPrefix(p.RefKind)}{p.Type.ToDisplayString(Q)} p{i}"));
                            var outs = string.Concat(method.Parameters.Select((p, i) => p.RefKind == RefKind.Out ? $" p{i} = default!;" : ""));
                            var ret = method.ReturnsVoid ? "" : " return default!;";
                            body.AppendLine($"    {(method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(Q))} {owner}.{method.Name}{typeParameters}({parameters}) {{{outs}{ret} }}");
                            break;
                        }
                        case IPropertySymbol property:
                        {
                            var accessors = (property.GetMethod is not null ? " get => default!;" : "") + (property.SetMethod is not null ? " set { }" : "");
                            var declared = property.IsIndexer
                                ? "this[" + string.Join(", ", property.Parameters.Select((p, i) => $"{p.Type.ToDisplayString(Q)} p{i}")) + "]"
                                : property.Name;
                            body.AppendLine($"    {property.Type.ToDisplayString(Q)} {owner}.{declared} {{{accessors} }}");
                            break;
                        }
                        case IEventSymbol @event:
                            body.AppendLine($"    event {@event.Type.ToDisplayString(Q)} {owner}.{@event.Name} {{ add {{ }} remove {{ }} }}");
                            break;
                    }
                }
            }

            return $"public sealed class {name} : {string.Join(", ", interfaces.Select(i => i.ToDisplayString(Q)))}\n{{\n{body}}}\n";
        }

        // ---- producing values ----

        /// <summary>An expression of <paramref name="type"/>: a probe lambda for a delegate, a canned BCL value, a constructor, a
        /// static factory of the library, or, failing all, <c>default!</c> (counted).</summary>
        public string Produce(ITypeSymbol type, int depth, string probe, List<string> prelude)
        {
            var display = type.ToDisplayString(Q);
            if (IsDelegate(type))
            {
                if (probe is null)
                    return Lambda((INamedTypeSymbol)type, null);
                // a probe lambda is made by a factory of its own, so that its delegate region names the probe (see Kept)
                var factory = $"L_{probe}_{_factories.Count}";
                _factories.Add($"    private static {display} {factory}() => {Lambda((INamedTypeSymbol)type, probe)};");
                return factory + "()";
            }
            switch (type.SpecialType)
            {
                case SpecialType.System_String:
                    return "\"s\"";
                case SpecialType.System_Object:
                    return "new object()";
            }

            if (type is IArrayTypeSymbol array)
                return $"new {array.ElementType.ToDisplayString(Q)}[0]";
            if (type.IsValueType || type.TypeKind == TypeKind.Enum)
                return $"default({display})";
            if (type is INamedTypeSymbol named)
            {
                var definition = named.OriginalDefinition.ToDisplayString();
                var args = named.TypeArguments.Select(a => a.ToDisplayString(Q)).ToArray();
                switch (definition)
                {
                    case "System.Collections.Generic.IEnumerable<T>" or "System.Collections.Generic.ICollection<T>" or "System.Collections.Generic.IList<T>"
                        or "System.Collections.Generic.IReadOnlyCollection<T>" or "System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.List<T>":
                        return $"new global::System.Collections.Generic.List<{args[0]}>()";
                    case "System.Collections.Generic.IDictionary<TKey, TValue>" or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                        or "System.Collections.Generic.Dictionary<TKey, TValue>":
                        return $"new global::System.Collections.Generic.Dictionary<{args[0]}, {args[1]}>()";
                    case "System.Collections.IEnumerable":
                        return "new global::System.Collections.Generic.List<object>()";
                    case "System.IO.Stream":
                        return "new global::System.IO.MemoryStream()";
                    case "System.Threading.Tasks.Task":
                        return "global::System.Threading.Tasks.Task.CompletedTask";
                    case "System.Type":
                        return "typeof(object)";
                    case "System.Exception":
                        return "new global::System.Exception()";
                }

                if (definition.StartsWith("System.Linq.Expressions.Expression", StringComparison.Ordinal))
                    return Null(display);
                if (depth > 0)
                {
                    if (Construct(named, depth, probe, prelude) is { } created)
                        return created;
                    if (Factory(named, depth, probe, prelude) is { } produced)
                        return produced;
                }
            }

            return Null(display);
        }

        private string Null(string display)
        {
            NullFallbacks++;
            return $"default({display})!";
        }

        private string Construct(INamedTypeSymbol type, int depth, string probe, List<string> prelude)
        {
            var candidates = type.TypeKind == TypeKind.Class && !type.IsAbstract ? [type] : Implementations(type);
            foreach (var candidate in candidates)
            {
                var constructor = candidate.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public &&
                                                                            c.Parameters.All(p => p.RefKind == RefKind.None))
                                           .OrderBy(c => probe is not null && c.Parameters.Any(p => IsDelegate(p.Type)) ? 0 : 1)
                                           .ThenBy(c => c.Parameters.Length)
                                           .FirstOrDefault();
                if (constructor is null)
                    continue;
                var arguments = constructor.Parameters.Select(p => Produce(p.Type, depth - 1, probe, prelude));
                return $"new {candidate.ToDisplayString(Q)}({string.Join(", ", arguments)})";
            }

            return null;
        }

        /// <summary>Non-abstract classes of the library that convert to the type, fewest constructor parameters first.</summary>
        private IEnumerable<INamedTypeSymbol> Implementations(INamedTypeSymbol type) =>
            Types(_library.GlobalNamespace).Where(t => t.DeclaredAccessibility == Accessibility.Public && t.TypeKind == TypeKind.Class && !t.IsAbstract &&
                                                       !t.IsGenericType && Converts(t, type))
                                           .OrderBy(t => t.InstanceConstructors.Select(c => c.Parameters.Length).DefaultIfEmpty(99).Min());

        /// <summary>A public static method or extension method of the library whose result converts to the type.</summary>
        private string Factory(INamedTypeSymbol type, int depth, string probe, List<string> prelude)
        {
            _producers ??= Types(_library.GlobalNamespace)
                           .Where(t => t.DeclaredAccessibility == Accessibility.Public)
                           .SelectMany(t => t.GetMembers().OfType<IMethodSymbol>())
                           .Where(m => m.IsStatic && m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public &&
                                       !m.ReturnsVoid && m.Parameters.All(p => p.RefKind == RefKind.None))
                           .OrderBy(m => m.Parameters.Length)
                           .ToList();
            foreach (var producer in _producers)
            {
                var method = producer;
                if (method.IsGenericMethod)
                {
                    if (method.TypeParameters.Any(t => t.ConstraintTypes.Any(c => c.TypeKind == TypeKind.Interface)))
                        continue;
                    method = method.Construct(method.TypeParameters.Select(Choose).ToArray());
                }

                if (!Converts(method.ReturnType, type) || method.Parameters.Any(p => SymbolEqualityComparer.Default.Equals(p.Type, type)))
                    continue;
                var arguments = method.Parameters.Select(p => Produce(p.Type, depth - 1, probe, prelude));
                var typeArguments = method.IsGenericMethod ? "<" + string.Join(", ", method.TypeArguments.Select(a => a.ToDisplayString(Q))) + ">" : "";
                return $"{method.ContainingType.ToDisplayString(Q)}.{method.Name}{typeArguments}({string.Join(", ", arguments)})";
            }

            return null;
        }

        private bool Converts(ITypeSymbol from, ITypeSymbol to)
        {
            var conversion = compilation.ClassifyConversion(from, to);
            return conversion.IsIdentity || conversion.IsImplicit && conversion.IsReference;
        }

        private static string Lambda(INamedTypeSymbol type, string probe)
        {
            var invoke = type.DelegateInvokeMethod!;
            var parameters = string.Join(", ", invoke.Parameters.Select((p, i) => $"{RefPrefix(p.RefKind)}{p.Type.ToDisplayString(Q)} a{i}"));
            var body = new StringBuilder();
            if (probe is not null)
                body.Append($" Probe.{probe} = 1;");
            foreach (var (p, i) in invoke.Parameters.Select((p, i) => (p, i)).Where(t => t.p.RefKind == RefKind.Out))
                body.Append($" a{i} = default!;");
            if (!invoke.ReturnsVoid)
                body.Append(invoke.ReturnType.ToDisplayString() == "System.Threading.Tasks.Task"
                    ? " return global::System.Threading.Tasks.Task.CompletedTask;"
                    : " return default!;");
            return $"(({type.ToDisplayString(Q)})(({parameters}) => {{{body} }}))";
        }

        // ---- the driver ----

        public string Driver(IParameterSymbol[] probed)
        {
            var result = Target.MethodKind == MethodKind.Constructor ? Target.ContainingType : Target.ReturnsVoid ? null : Target.ReturnType;
            var awaited = result?.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask";
            var awaitedValue = result is INamedTypeSymbol { IsGenericType: true } generic &&
                               generic.OriginalDefinition.ToDisplayString() is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>";
            var valueType = awaitedValue ? ((INamedTypeSymbol)result).TypeArguments[0] : awaited ? null : result;
            Enumerates = valueType is not null && valueType.SpecialType != SpecialType.System_String &&
                         (valueType.AllInterfaces.Any(i => i.ToDisplayString() == "System.Collections.IEnumerable") ||
                          valueType.ToDisplayString() == "System.Collections.IEnumerable");
            var holderType = Target.MethodKind == MethodKind.Constructor ? Target.ContainingType
                : valueType is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface } v && v.SpecialType == SpecialType.None &&
                  SymbolEqualityComparer.Default.Equals(v.ContainingAssembly, _library) ? v
                : !Target.IsStatic ? Target.ContainingType : null;
            var holderIsResult = holderType is not null && (Target.MethodKind == MethodKind.Constructor || SymbolEqualityComparer.Default.Equals(holderType, valueType));

            var actions = new StringBuilder();
            var callText = Call(probed, "Call", out var callPrelude);
            var keep = (result is not null && Target.MethodKind != MethodKind.PropertySet ? "Keep.R = r; " : "") + (_hasReceiver ? "Keep.Recv = recv;" : "");
            actions.AppendLine(Action("V_Call", callText, callPrelude, keep));
            if (Enumerates)
                actions.AppendLine(Action("V_Enum", Call(probed, "Enum", out var enumPrelude), enumPrelude, "foreach (var _ in r) { }"));
            if (holderType is not null)
            {
                var index = 0;
                foreach (var trigger in Triggers(holderType))
                {
                    var prelude = new List<string>();
                    var call = Call(probed, "T" + index, out var callStatements);
                    prelude.AddRange(callStatements);
                    var arguments = trigger.Parameters.Select(p => p.RefKind == RefKind.Out ? $"out {p.Type.ToDisplayString(Q)} _" : Produce(p.Type, 1, null, prelude));
                    var awaits = trigger.ReturnType.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask" ||
                                 trigger.ReturnType.OriginalDefinition.ToDisplayString() is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>";
                    var target = holderIsResult ? "r" : "recv";
                    var invocation = $"{target}.{trigger.Name}({string.Join(", ", arguments)})";
                    var statement = trigger.ReturnsVoid ? invocation + ";" : awaits ? $"await {invocation};" : $"_ = {invocation};";
                    actions.AppendLine(Action("T" + index, call, prelude, statement));
                    TriggerNames.Add(trigger.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                    index++;
                }
            }

            foreach (var factory in _factories)
                actions.AppendLine(factory);
            var stubs = string.Concat(_stubs) + Placeholders(4, from: _nextStub);
            return Skeleton(stubs, actions.ToString());

            string Action(string name, string call, List<string> prelude, string after)
            {
                var head = string.Concat(prelude.Select(s => "        " + s + "\n"));
                var invoke = Target.MethodKind == MethodKind.PropertySet ? call + ";"
                    : result is null ? call + ";"
                    : awaited ? $"await {call}; object r = null!;"
                    : awaitedValue ? $"var r = await {call};"
                    : $"var r = {call};";
                return $"    public async global::System.Threading.Tasks.Task<IActionResult> {name}()\n    {{\n{head}        {invoke}\n        {after}\n        await global::System.Threading.Tasks.Task.Yield();\n        return Ok();\n    }}";
            }
        }

        private string Call(IParameterSymbol[] probed, string variant, out List<string> prelude)
        {
            prelude = [];
            var local = prelude;
            string Argument(IParameterSymbol p)
            {
                var probe = probed.Contains(p, SymbolEqualityComparer.Default) ? $"P{p.Ordinal}_{variant}" : null;
                return p.RefKind switch
                {
                    RefKind.Out => $"out {p.Type.ToDisplayString(Q)} o{p.Ordinal}",
                    RefKind.Ref => Ref(p, probe, local),
                    _ => Produce(p.Type, 2, probe, local)
                };
            }

            var arguments = string.Join(", ", Target.Parameters.Select(Argument));
            if (Target.MethodKind == MethodKind.Constructor)
                return $"new {Target.ContainingType.ToDisplayString(Q)}({arguments})";
            var typeArguments = Target.IsGenericMethod ? "<" + string.Join(", ", Target.TypeArguments.Select(a => a.ToDisplayString(Q))) + ">" : "";
            if (Target.MethodKind == MethodKind.PropertySet && Target.IsStatic)
                return $"{Target.ContainingType.ToDisplayString(Q)}.{((IPropertySymbol)Target.AssociatedSymbol!).Name} = {arguments}";
            if (Target.IsStatic)
                return $"{Target.ContainingType.ToDisplayString(Q)}.{Target.Name}{typeArguments}({arguments})";
            var receiver = Produce(Target.ContainingType, 3, null, local);
            if (receiver.StartsWith("default(", StringComparison.Ordinal))
                ReceiverNull = true;
            _hasReceiver = true;
            local.Add($"var recv = {receiver};");
            if (Target.MethodKind == MethodKind.PropertySet)
                return $"recv.{((IPropertySymbol)Target.AssociatedSymbol!).Name} = {arguments}";
            return $"recv.{Target.Name}{typeArguments}({arguments})";
        }

        private string Ref(IParameterSymbol p, string probe, List<string> prelude)
        {
            prelude.Add($"var ref{p.Ordinal} = {Produce(p.Type, 2, probe, prelude)};");
            return $"ref ref{p.Ordinal}";
        }

        /// <summary>The public instance methods of the holder's type and of its bases and interfaces that the library declares.</summary>
        private IEnumerable<IMethodSymbol> Triggers(ITypeSymbol holder)
        {
            var seen = new HashSet<string>();
            var types = new List<ITypeSymbol>();
            for (var current = holder; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
                types.Add(current);
            types.AddRange(holder.AllInterfaces);
            foreach (var type in types.Where(t => SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, _library)))
            {
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.DeclaredAccessibility != Accessibility.Public ||
                        method.IsGenericMethod || method.Parameters.Any(p => p.RefKind is RefKind.Ref or RefKind.In || p.Type.IsRefLikeType) ||
                        SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, Target.OriginalDefinition) ||
                        !seen.Add(method.Name + "(" + string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString())) + ")"))
                        continue;
                    yield return method;
                    if (seen.Count >= MAX_TRIGGERS)
                        yield break;
                }
            }
        }
    }

    // ---- infrastructure ----

    private static string Placeholders(int count, int from = 0) =>
        string.Concat(Enumerable.Range(from, Math.Max(0, count - from)).Select(i => $"public sealed class Gen{i} {{ }}\n"));

    private static string RefPrefix(RefKind kind) => kind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" };

    public static bool IsDelegate(ITypeSymbol type) => type.TypeKind == TypeKind.Delegate;

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol @namespace) =>
        @namespace.GetTypeMembers().SelectMany(Nested).Concat(@namespace.GetNamespaceMembers().SelectMany(Types));

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) => type.GetTypeMembers().SelectMany(Nested).Prepend(type);

    private static string Skeleton(string types, string actions)
    {
        var fields = new StringBuilder();
        for (var p = 0; p < 8; p++)
        {
            foreach (var v in new[] { "Call", "Enum" }.Concat(Enumerable.Range(0, MAX_TRIGGERS).Select(t => "T" + t)))
                fields.Append($" public static int P{p}_{v};");
        }

        return EngineFixture.Usings + $$"""
            public static class Probe {{{fields}} }
            public static class Keep { public static object? R; public static object? Recv; }
            public sealed class Marker { }
            {{types}}
            public sealed class DriverController : ControllerBase
            {
            {{actions}}
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
    }

    private static Compilation Compile(Library library, string userSource) =>
        library.Solution.WithDocumentText(library.UserDocument, SourceText.From(userSource))
               .GetProject(library.UserDocument.ProjectId)!.GetCompilationAsync().Result!;

    private static (Solution, DocumentId) Build(string libraryName, string librarySource)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var platform = StubAssemblies.PlatformWithout([libraryName]).ToArray();
        var stubs = StubAssemblies.Names.Select(name => StubAssemblies.Get(name, StubAssemblies.DefaultVersion(name))).ToArray();
        var fixtureId = ProjectId.CreateNewId();
        var libraryId = ProjectId.CreateNewId();
        var userDocument = DocumentId.CreateNewId(fixtureId);
        solution = solution.AddProject(ProjectInfo.Create(fixtureId, VersionStamp.Default, "Fixture", "Fixture", LanguageNames.CSharp,
                                                          filePath: @"C:\fixture\Fixture.csproj",
                                                          compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                                                                                                           nullableContextOptions: NullableContextOptions.Enable),
                                                          parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                                                          metadataReferences: platform.Concat(stubs)));
        solution = solution.AddDocument(userDocument, "Case.cs", SourceText.From(""), filePath: @"C:\fixture\Case.cs");
        solution = solution.AddProject(ProjectInfo.Create(libraryId, VersionStamp.Default, libraryName, libraryName, LanguageNames.CSharp,
                                                          filePath: $@"C:\fixture\{libraryName}\{libraryName}.csproj",
                                                          compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                                                                                                           nullableContextOptions: NullableContextOptions.Enable),
                                                          parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
                                                          metadataReferences: platform));
        solution = solution.AddDocument(DocumentId.CreateNewId(libraryId), libraryName + ".cs", SourceText.From(librarySource),
                                        filePath: $@"C:\fixture\{libraryName}\{libraryName}.cs");
        return (solution.AddProjectReference(fixtureId, new ProjectReference(libraryId)), userDocument);
    }
}
