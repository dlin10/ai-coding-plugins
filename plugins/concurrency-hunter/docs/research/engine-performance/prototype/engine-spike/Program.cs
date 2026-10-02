// CoreLib spike, engine side: concurrency-hunter's engine (as already built) over a corlib-only scope: the decompiled CoreLib as one
// compilation without references and a driver compiled against it. EngineFixture.AnalyzeScope finds roots only through the
// ASP.NET Core and hosting providers, so this replicates EngineFixture.ReachScope with one hand-made root instead.
//   engine-spike <decompiled dir> all                                  lower every CoreLib method once
//   engine-spike <decompiled dir> study <root> <exclusion> [solve]     root: run | setcount | register
//                                                                      exclusion: none | four | four-linkcut | top10 | top20
// An exclusion makes the CoreLib overrides/implementations of the excluded slots bodiless in the program index the reachable set
// and the heap are built over (HasSourceBody = false; override and interface links kept), so a call dispatching to them finds no
// source body and is opaque. four-linkcut instead cuts their override and interface links (dispatch only; reachability only).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

const string RootDirectory = @"C:\fixture";
const string ScopeId = "scope:Fixture";
const string CoreLibPrefix = "body:System.Private.CoreLib:";
string[] fourSlots =
[
    CoreLibPrefix + "M:System.Object.ToString",
    CoreLibPrefix + "M:System.Object.Equals(System.Object)",
    CoreLibPrefix + "M:System.Object.GetHashCode",
    CoreLibPrefix + "M:System.IDisposable.Dispose",
];

// Guard for this machine: stop this process (only this one) if its working set passes MAX_WS_GB (default 6 GB).
var maxGb = int.TryParse(Environment.GetEnvironmentVariable("MAX_WS_GB"), out var gb) ? gb : 6;
new Thread(() =>
{
    while (true)
    {
        Thread.Sleep(2000);
        if (Process.GetCurrentProcess().WorkingSet64 > maxGb * 1024L * 1024 * 1024)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] working set above {maxGb} GB: exiting");
            Environment.Exit(4);
        }
    }
}) { IsBackground = true }.Start();

var watch = Stopwatch.StartNew();
var parse = new CSharpParseOptions(LanguageVersion.Preview);
var trees = Directory.GetFiles(args[0], "*.cs").AsParallel()
                     .Select(f => CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(f)), parse,
                                                             Path.Combine(RootDirectory, "CoreLib", Path.GetFileName(f))))
                     .ToArray();
var coreLib = CSharpCompilation.Create("System.Private.CoreLib", trees, [],
                                       new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                                                                    nullableContextOptions: NullableContextOptions.Disable));
const string Source = """
    public sealed class Probe
    {
        public static int Hit;
        public string Name = "probe";

        public override string ToString() => Name;
    }

    public static class Driver
    {
        public static string Run() => string.Join(",", new object[] { new Probe() });

        public static void SetCountRun()
        {
            System.Collections.Generic.List<Probe> l = new();
            System.Runtime.InteropServices.CollectionsMarshal.SetCount(l, 3);
        }

        public static void RegisterRun() => new System.Threading.CancellationTokenSource().Token.Register(() => Probe.Hit = 1);
    }
    """;
var driverTree = CSharpSyntaxTree.ParseText(Source, parse, Path.Combine(RootDirectory, "Driver", "Driver.cs"));
var driver = CSharpCompilation.Create("Driver", [driverTree], [coreLib.ToMetadataReference()],
                                      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
Console.WriteLine($"compilations: CoreLib {coreLib.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error)} errors, " +
                  $"driver {driver.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error)} errors ({watch.Elapsed.TotalSeconds:F1}s)");
foreach (var error in driver.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
    Console.WriteLine($"    {error}");
Compilation[] compilations = [coreLib, driver];

var rootBodyId = typeof(IrLowering).GetMethod("RootBodyId", BindingFlags.NonPublic | BindingFlags.Static)!;
string BodyId(IMethodSymbol method) => (string)rootBodyId.Invoke(null, [method]);

watch.Restart();
var index = DiIndexBuilder.Build(ScopeId, compilations, RootDirectory, CancellationToken.None);
var bindings = InjectionBindings.Discover(compilations, index, RootDirectory, CancellationToken.None);
var fullProgram = ProgramIndexBuilder.Build(ScopeId, compilations, RootDirectory, CancellationToken.None);
Console.WriteLine($"DI index, {bindings.Count} injection bindings, program index: {watch.Elapsed.TotalSeconds:F1}s");

var methods = new Dictionary<string, (IMethodSymbol Method, Compilation Compilation)>(StringComparer.Ordinal);
foreach (var compilation in compilations)
{
    foreach (var method in Types(compilation.Assembly.GlobalNamespace).SelectMany(type => type.GetMembers().OfType<IMethodSymbol>()))
        methods.TryAdd(BodyId(method), (method, compilation));
}

Console.WriteLine($"member map: {methods.Count} methods");

if (args[1] == "all")
{
    LowerAll();
    return;
}

var rootMethod = args[2] switch
{
    "run" => "Run",
    "setcount" => "SetCountRun",
    "register" => "RegisterRun",
    _ => throw new ArgumentException(args[2])
};
var entry = driver.GetTypeByMetadataName("Driver")!.GetMembers(rootMethod).OfType<IMethodSymbol>().Single();
var exclusion = args[3];
var solve = args.Contains("solve");

// The slot ranking over the closure without any exclusion (step 3), and the closure itself.
var baseline = Reach($"Driver.{rootMethod}", entry, fullProgram);
var ranking = Rank(baseline.Result, fullProgram);
if (exclusion == "none")
{
    Console.WriteLine("  slots by override bodies they pull into the closure (inclusive: reached dispatch targets other than the slot itself; " +
                      "first: members whose recorded reason is a dispatch through the slot):");
    foreach (var (slot, inclusive, first) in ranking.Take(20))
        Console.WriteLine($"    {inclusive,5} {first,5}  {slot}");
    Console.WriteLine($"  the four slots' ranks: {string.Join(", ", fourSlots.Select(s => $"{s[CoreLibPrefix.Length..]} #{ranking.FindIndex(r => r.Slot == s) + 1}"))}");
}

string[] slots = exclusion switch
{
    "none" => [],
    "four" or "four-linkcut" => fourSlots,
    "top10" => ranking.Take(10).Select(r => r.Slot).ToArray(),
    "top20" => ranking.Take(20).Select(r => r.Slot).ToArray(),
    "all" => ranking.Select(r => r.Slot).ToArray(),
    _ => throw new ArgumentException(exclusion)
};
var program = exclusion == "none" ? fullProgram : Exclude(fullProgram, slots, exclusion == "four-linkcut");
var run = exclusion == "none" ? baseline : Reach($"Driver.{rootMethod} excluding {exclusion}", entry, program);
if (!solve)
    return;

watch.Restart();
var heap = Capped("heap solve", () => EngineFixture.Solve(run));
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] heap solve: {watch.Elapsed.TotalSeconds:F1}s, {heap.Heap.Regions.Count} regions; peak working set {PeakMb()} MB");
watch.Restart();
var execution = Capped("executions", () => EngineFixture.Execute(heap));
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] executions: {watch.Elapsed.TotalSeconds:F1}s; peak working set {PeakMb()} MB");
watch.Restart();
var analyze = typeof(EngineFixture).GetMethod("Analyze", BindingFlags.NonPublic | BindingFlags.Static, [typeof(ExecutionRun)])!;
var engineRun = Capped("accesses and pairs", () => (EngineRun)analyze.Invoke(null, [execution]));
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] accesses and pairs: {watch.Elapsed.TotalSeconds:F1}s; {engineRun.Collection.Accesses.Count} accesses, " +
                  $"{engineRun.Pairs.Pairs.Count} pairs; peak working set {PeakMb()} MB");
foreach (var access in engineRun.Collection.Accesses.Where(a => a.Resource.Member.Name is "Name" or "Hit"))
    Console.WriteLine($"    access {access.Operation} {access.Resource.Member.Name} in {access.BodyId} op {access.OperationId}; root {access.Root.RootId}; " +
                      $"execution {access.ExecutionId} ({execution.Analysis.Execution(access.ExecutionId).Kind}); construction-local {access.IsConstructionLocal}");
if (!engineRun.Collection.Accesses.Any(a => a.Resource.Member.Name is "Name" or "Hit"))
    Console.WriteLine("    no access to Probe.Name or Probe.Hit");
Console.WriteLine($"    accesses by member (top 10): {string.Join(", ", engineRun.Collection.Accesses.GroupBy(a => a.Resource.Member.Name).OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Key} x{g.Count()}"))}");

T Capped<T>(string stage, Func<T> work)
{
    var task = Task.Run(work);
    if (task.Wait(TimeSpan.FromMinutes(10)))
        return task.Result;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {stage}: did not finish within 10 minutes; peak working set {PeakMb()} MB; exiting");
    Environment.Exit(3);
    return default;
}

static long PeakMb() => Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024;

WholeProgramRun Reach(string label, IMethodSymbol root, ProgramIndex programIndex)
{
    var loweredIds = new List<string>();
    var failures = 0;
    IReadOnlyList<IrBody> Lower(string memberId)
    {
        loweredIds.Add(memberId);
        if (!methods.TryGetValue(memberId, out var member))
            return [];
        try
        {
            var result = IrLowering.Lower(member.Method, member.Compilation, RootDirectory, CancellationToken.None);
            return result.NestedBodies.Prepend(result.Body).ToArray();
        }
        catch (ArgumentException)
        {
            return [];
        }
        catch (Exception)
        {
            failures++;
            return [];
        }
    }

    var descriptor = new ExecutionRootDescriptor($"spike:{label}", "spike", "spike",
                                                 new RootEntry(BodyId(root), root.ToDisplayString(), label,
                                                               new SourceSpan(Path.Combine(RootDirectory, "Driver", "Driver.cs"), 1, 1, 1, 1)),
                                                 new InstanceBindings(ReceiverKind.None, []),
                                                 new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, ScopeId), [], [], [], []);
    var reachWatch = Stopwatch.StartNew();
    var input = new ReachabilityInput(programIndex, [descriptor], index, bindings, Lower);
    var reach = ReachableSet.Build(input);
    var opaque = reach.OpaqueCalls.SelectMany(pair => pair.Value).ToList();
    Console.WriteLine($"==== {label}: reach {reachWatch.Elapsed.TotalSeconds:F1}s; reached members {reach.Members.Count}, reached bodies {reach.ReachedBodies.Count}, " +
                      $"IR bodies {reach.Bodies.Count}, lowering threw {failures}; opaque calls {opaque.Count} ({opaque.Select(o => o.Callee).Distinct().Count()} distinct callees)");
    Console.WriteLine($"  from the driver: {string.Join(", ", reach.Members.Where(m => m.MemberId.StartsWith("body:Driver:", StringComparison.Ordinal)).Select(m => $"{m.MemberId["body:Driver:M:".Length..]} [{m.Reason}]"))}");
    Console.WriteLine($"  Probe.ToString reached: {reach.Members.Any(m => m.MemberId == "body:Driver:M:Probe.ToString")}; driver bodies reached: " +
                      string.Join(", ", reach.ReachedBodies.Where(b => b.Key.StartsWith("body:Driver:", StringComparison.Ordinal)).Select(b => $"{b.Key["body:Driver:M:".Length..]} [{b.Value}]")));
    Console.WriteLine($"  by namespace: {string.Join(", ", reach.Members.Where(m => m.MemberId.StartsWith(CoreLibPrefix, StringComparison.Ordinal)).GroupBy(m => string.Join('.', m.MemberId.Split(':')[3].Split('.').Take(2))).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} x{g.Count()}"))}");
    return new WholeProgramRun(ScopeId, input, reach, loweredIds);
}

// The dispatch targets of every slot, as ReachableSet's walker builds them: a method is a target of each method up its override
// chain and of each interface method it or an ancestor implements.
static Dictionary<string, List<ProgramMethod>> DispatchTargets(ProgramIndex program)
{
    var dispatch = new Dictionary<string, List<ProgramMethod>>(StringComparer.Ordinal);
    foreach (var method in program.Methods)
    {
        var ancestors = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var overridden = method.OverriddenMethodId; overridden is not null && visited.Add(overridden);
             overridden = program.Method(overridden)?.OverriddenMethodId)
        {
            ancestors.Add(overridden);
        }

        var interfaceMethods = method.ImplementedInterfaceMethodIds.Concat(ancestors.SelectMany(a => program.Method(a)?.ImplementedInterfaceMethodIds ?? []));
        foreach (var slot in ancestors.Concat(interfaceMethods).Distinct(StringComparer.Ordinal))
        {
            if (!dispatch.TryGetValue(slot, out var targets))
                dispatch.Add(slot, targets = []);
            targets.Add(method);
        }
    }

    return dispatch;
}

// Every slot a reached body calls by virtual or interface dispatch, with the reached source targets it dispatches to (inclusive)
// and the members whose recorded first reason is a call through it (first).
static List<(string Slot, int Inclusive, int First)> Rank(ReachableSetResult reach, ProgramIndex program)
{
    var dispatch = DispatchTargets(program);
    var reached = reach.Members.Select(m => m.MemberId).ToHashSet(StringComparer.Ordinal);
    var calls = new Dictionary<(string Body, int Op), string>();
    foreach (var (bodyId, body) in reach.Bodies)
    {
        foreach (var operation in body.Blocks.SelectMany(block => block.Operations))
        {
            if (operation is IrCallOperation { CallKind: IrCallKind.Virtual or IrCallKind.Interface, TargetMethodId: { } target })
                calls[(bodyId, operation.Id)] = target;
        }
    }

    var first = new Dictionary<string, int>(StringComparer.Ordinal);
    var how = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var member in reach.Members)
    {
        var category = member.Reason.Split(':')[0];
        if (category == "call")
        {
            var colon = member.Reason.LastIndexOf(':');
            category = "call (direct)";
            if (int.TryParse(member.Reason[(colon + 1)..], out var op) && calls.TryGetValue((member.Reason[5..colon], op), out var slot) && slot != member.MemberId)
            {
                first[slot] = first.GetValueOrDefault(slot) + 1;
                category = "call (dispatch to an override)";
            }
        }

        how[category] = how.GetValueOrDefault(category) + 1;
    }

    Console.WriteLine($"  members by how they were first reached: {string.Join(", ", how.OrderByDescending(h => h.Value).Select(h => $"{h.Key} {h.Value}"))}");

    // The spanning tree of first reasons (a call's or a delegate's body is the parent, nested bodies folded into their member):
    // the members whose subtrees are largest are the gateways the closure comes through.
    var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var member in reach.Members)
    {
        var parts = member.Reason.Split(':');
        if (parts[0] is "call" or "delegate")
        {
            var body = member.Reason[(parts[0].Length + 1)..member.Reason.LastIndexOf(':')];
            // IrLowering's nested-body suffix; a '#' alone also appears in explicit interface member ids
            parentOf[member.MemberId] = System.Text.RegularExpressions.Regex.Replace(body, @"(?:#lambda\d+|#local:[^#~]+(?:~\d+)?)+$", "");
        }
    }

    var subtree = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var member in reach.Members)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var current = member.MemberId; parentOf.TryGetValue(current, out var parent) && visited.Add(parent); current = parent)
            subtree[parent] = subtree.GetValueOrDefault(parent) + 1;
    }

    Console.WriteLine("  gateways (members by the size of their first-reason subtree):");
    foreach (var (member, size) in subtree.OrderByDescending(s => s.Value).Take(25))
        Console.WriteLine($"    {size,6}  {member}");

    return calls.Values.Distinct(StringComparer.Ordinal)
                .Select(slot => (Slot: slot,
                                 Inclusive: (dispatch.GetValueOrDefault(slot) ?? []).Count(m => m.HasSourceBody && m.MethodId != slot && reached.Contains(m.MethodId)),
                                 First: first.GetValueOrDefault(slot)))
                .OrderByDescending(r => r.Inclusive).ThenByDescending(r => r.First).ThenBy(r => r.Slot, StringComparer.Ordinal)
                .ToList();
}

// The program index with the CoreLib dispatch targets of the slots made bodiless (or, with linkCut, unlinked from the slots).
static ProgramIndex Exclude(ProgramIndex program, IReadOnlyList<string> slots, bool linkCut)
{
    var dispatch = DispatchTargets(program);
    var excluded = slots.SelectMany(slot => dispatch.GetValueOrDefault(slot) ?? [])
                        .Where(m => m.MethodId.StartsWith(CoreLibPrefix, StringComparison.Ordinal) && m.HasSourceBody)
                        .Select(m => m.MethodId).ToHashSet(StringComparer.Ordinal);
    var slotSet = slots.ToHashSet(StringComparer.Ordinal);
    var methodsOut = program.Methods.Select(m => !excluded.Contains(m.MethodId) ? m
                                                 : linkCut ? m with { OverriddenMethodId = null, ImplementedInterfaceMethodIds = m.ImplementedInterfaceMethodIds.Where(i => !slotSet.Contains(i)).ToArray() }
                                                 : m with { HasSourceBody = false, NestedBodyIds = [] })
                                    .ToList();
    var variantTypes = ((System.Collections.IDictionary)typeof(ProgramIndex).GetField("_variantTypes", BindingFlags.NonPublic | BindingFlags.Instance)!
                                                                            .GetValue(program)!).Values.Cast<ProgramVariantType>().ToList();
    Console.WriteLine($"exclusion: {excluded.Count} CoreLib methods {(linkCut ? "unlinked from" : "made bodiless for")} {slots.Count} slots: " +
                      string.Join(", ", slots.Select(s => s.StartsWith(CoreLibPrefix, StringComparison.Ordinal) ? s[CoreLibPrefix.Length..] : s)));
    return new ProgramIndex(program.ScopeId, program.Types, methodsOut, program.Fields, program.ClosedGenericTypes, program.InterfaceMappings, variantTypes)
    {
        ImmutableTypeKeys = program.ImmutableTypeKeys
    };
}

// Every CoreLib method lowered once, in parallel, capped at three minutes: how much of CoreLib the frontend can lower at all.
void LowerAll()
{
    var candidates = methods.Values.Where(m => m.Compilation == coreLib && !m.Method.IsAbstract && !m.Method.IsExtern).ToList();
    var ok = 0;
    var noBody = 0;
    var failed = new ConcurrentDictionary<string, int>();
    var cap = Stopwatch.StartNew();
    var attempted = 0;
    Parallel.ForEach(candidates, (member, state) =>
    {
        if (cap.Elapsed > TimeSpan.FromMinutes(3))
        {
            state.Stop();
            return;
        }

        Interlocked.Increment(ref attempted);
        try
        {
            IrLowering.Lower(member.Method, member.Compilation, RootDirectory, CancellationToken.None);
            Interlocked.Increment(ref ok);
        }
        catch (ArgumentException)
        {
            Interlocked.Increment(ref noBody);
        }
        catch (Exception ex)
        {
            failed.AddOrUpdate($"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}", 1, (_, n) => n + 1);
        }
    });
    Console.WriteLine($"==== lower all CoreLib methods: {attempted}/{candidates.Count} attempted in {cap.Elapsed.TotalSeconds:F1}s; lowered {ok}, " +
                      $"no source body {noBody}, threw {failed.Values.Sum()}");
    foreach (var (failure, count) in failed.OrderByDescending(f => f.Value).Take(12))
        Console.WriteLine($"    threw x{count}: {failure}");
}

static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns)
{
    foreach (var type in ns.GetTypeMembers().SelectMany(Nested))
        yield return type;
    foreach (var child in ns.GetNamespaceMembers())
    {
        foreach (var type in Types(child))
            yield return type;
    }
}

static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) => type.GetTypeMembers().SelectMany(Nested).Prepend(type);
