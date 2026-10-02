using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>Who made an object of a driver run, and when.</summary>
public enum AllocationKind
{
    /// <summary>For a constructor member, the object the driver's <c>new</c> in <c>V_Call</c> creates.</summary>
    MemberObject,

    /// <summary>The driver made it, or setup did through the library.</summary>
    Driver,

    /// <summary>The library made it in own(X), X being <c>V_Call</c> or <c>V_Enum</c>.</summary>
    LibraryDuring,

    /// <summary>The library made it in tree(X) but not in own(X).</summary>
    LibraryInChild,

    /// <summary>A static, or an object a type initializer made.</summary>
    LibraryBefore,

    /// <summary>Anything else.</summary>
    Unknown
}

/// <summary>What a driver-made object is to the driver.</summary>
public enum DriverRole
{
    /// <summary>Not a driver-made object.</summary>
    None,

    /// <summary>A receiver or argument value, or something setup built into one.</summary>
    ArgumentValue,

    /// <summary>An instance of a driver probe class.</summary>
    ProbeObject,

    /// <summary>A probe delegate, made by its factory.</summary>
    ProbeDelegate,

    /// <summary>An object a probe lambda returns, made in the lambda's own body.</summary>
    ProbeLambdaReturn,

    /// <summary>A <c>Keep.&lt;n&gt;</c> intermediate.</summary>
    Intermediate,

    /// <summary>The initial value of an <c>Out_</c> field.</summary>
    OutInitialValue,

    /// <summary>An object a library factory or constructor setup called made.</summary>
    Setup
}

/// <summary>The origin of one object.</summary>
/// <param name="Kind">Who made it and when.</param>
/// <param name="Role">For <see cref="AllocationKind.Driver"/>, its role; else <see cref="DriverRole.None"/>.</param>
/// <param name="Action">For <see cref="AllocationKind.LibraryDuring"/> and <see cref="AllocationKind.LibraryInChild"/>, the action;
/// else <c>null</c>.</param>
public sealed record Allocation(AllocationKind Kind, DriverRole Role, string? Action);

/// <summary>Who made a region of a driver run and when (SPEC TD-034b, G-0), the one owner of the question, checked in this order:
/// the member's object, the driver, the library during X, the library in a child of X, the library before, unknown.</summary>
public sealed class Allocations
{
    private const string LAMBDA_SEPARATOR = "#";

    private readonly HeapSolution _heap;
    private readonly ExecutionAnalysis _executions;
    private readonly Dictionary<string, ExecutionKind> _kinds;
    private readonly DriverExecutions _driverExecutions;
    private readonly string _driverBodies = $"body:{DriverSynthesizer.ASSEMBLY}:";
    private readonly HashSet<string> _factories;
    private readonly HashSet<string> _intermediates;
    private readonly HashSet<string> _outValues;
    private readonly Dictionary<string, IteratorObject> _iterators;
    private readonly string? _constructed;

    /// <summary>The origins of the objects of one driver run.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="heap">The run's heap.</param>
    /// <param name="executions">The run's executions.</param>
    /// <param name="driverExecutions">The driver's executions.</param>
    /// <param name="reachability">The run's reachability, for the driver's static fields.</param>
    public Allocations(Driver driver, HeapSolution heap, ExecutionAnalysis executions, DriverExecutions driverExecutions,
                       HeapReachability reachability)
    {
        _heap = heap;
        _executions = executions;
        _kinds = executions.Executions.ToDictionary(execution => execution.Id, execution => execution.Kind, StringComparer.Ordinal);
        _driverExecutions = driverExecutions;
        _factories = driver.Parameters.SelectMany(parameter => parameter.Probes).Select(ProbeBody).ToHashSet(StringComparer.Ordinal);
        var statics = reachability.StaticFields();
        _intermediates = statics.Where(start => start.Slot.StartsWith(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "K"),
                                                                     StringComparison.Ordinal))
                                .SelectMany(reachability.Targets).ToHashSet(StringComparer.Ordinal);
        _outValues = statics.Where(start => start.Slot.StartsWith(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.DRIVER_TYPE, "Out_"),
                                                                 StringComparison.Ordinal))
                            .SelectMany(reachability.Targets).ToHashSet(StringComparer.Ordinal);
        _iterators = heap.IteratorObjects.GroupBy(iterator => iterator.RegionId, StringComparer.Ordinal)
                         .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _constructed = driver.Member.MethodKind == MethodKind.Constructor ? WithoutTypeArguments(SymbolNames.TypeKey(driver.Member.ContainingType)) : null;
    }

    /// <summary>The body id of a probe's factory, which a probe delegate's site names.</summary>
    /// <param name="probe">The probe.</param>
    public static string ProbeBody(DriverProbe probe) => $"body:{DriverSynthesizer.ASSEMBLY}:{probe.FactoryId}";

    /// <summary>Who made a region, and when.</summary>
    /// <param name="regionId">The region.</param>
    public Allocation Of(string regionId)
    {
        if (!_heap.Regions.TryGetValue(regionId, out var region))
            return new Allocation(AllocationKind.Unknown, DriverRole.None, null);
        if (region.Kind == HeapRegionKind.Static)
            return new Allocation(AllocationKind.LibraryBefore, DriverRole.None, null);

        var site = region.SiteBodyId;
        var creators = Creators(region, out var createdBy);
        if (_constructed is not null && region.Kind == HeapRegionKind.Allocation && site == CallBody &&
            region.TypeKey is { } typeKey && WithoutTypeArguments(typeKey) == _constructed)
        {
            return new Allocation(AllocationKind.MemberObject, DriverRole.None, null);
        }

        if (site is not null && site.StartsWith(_driverBodies, StringComparison.Ordinal))
            return new Allocation(AllocationKind.Driver, DriverRoleOf(region, site), null);
        var executions = creators.SelectMany(instance => _executions.InstanceExecutions.GetValueOrDefault(instance) ?? new HashSet<string>())
                                 .ToHashSet(StringComparer.Ordinal);
        if (executions.Count != 0 && executions.All(_driverExecutions.InSetup))
            return new Allocation(AllocationKind.Driver, DriverRole.Setup, null);
        if (createdBy is null)
            return new Allocation(AllocationKind.Unknown, DriverRole.None, null);

        foreach (var action in new[] { DriverSynthesizer.CALL, DriverSynthesizer.ENUMERATE })
        {
            if (executions.Any(execution => DriverExecutions.IsOwn(execution, action)))
                return new Allocation(AllocationKind.LibraryDuring, DriverRole.None, action);
        }

        foreach (var action in new[] { DriverSynthesizer.CALL, DriverSynthesizer.ENUMERATE })
        {
            if (executions.Any(execution => _driverExecutions.InTree(execution, action)))
                return new Allocation(AllocationKind.LibraryInChild, DriverRole.None, action);
        }

        var typeInitializer = creators.Count != 0 &&
                              (creators.All(instance => _heap.Instances.TryGetValue(instance, out var created) && created.BodyId.EndsWith(".#cctor", StringComparison.Ordinal)) ||
                               executions.Count != 0 && executions.All(execution => _kinds.GetValueOrDefault(execution) == ExecutionKind.TypeInitializer));
        return typeInitializer
            ? new Allocation(AllocationKind.LibraryBefore, DriverRole.None, null)
            : new Allocation(AllocationKind.Unknown, DriverRole.None, null);
    }

    private static string CallBody => $"body:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.{DriverSynthesizer.CALL}";

    /// <summary>The instances that made a region, and the body that did: the instances of the site's body in the region's context,
    /// or of every context when none matches; for an iterator object, the instance whose call created it, the site being the iterator
    /// method.</summary>
    /// <param name="region">The region.</param>
    /// <param name="site">The body that made it, or <c>null</c> when the heap names none.</param>
    private IReadOnlyCollection<string> Creators(HeapRegion region, out string? site)
    {
        if (_iterators.TryGetValue(region.Identity, out var iterator) && _heap.Instances.TryGetValue(iterator.Creation.CalleeInstance, out var callee))
        {
            site = callee.BodyId;
            return [iterator.Creation.CallerInstance];
        }

        site = region.SiteBodyId;
        if (site is null)
            return [];
        var siteBody = site;
        var instances = _heap.Instances.Values.Where(instance => instance.BodyId == siteBody).ToArray();
        var inContext = instances.Where(instance => instance.Context == region.Context).Select(instance => instance.Id).ToArray();
        return inContext.Length != 0 ? inContext : instances.Select(instance => instance.Id).ToArray();
    }

    /// <summary>A driver-made object's role: a probe object by its class, a probe delegate or a probe lambda's returned object by its
    /// site, an intermediate or an <c>Out_</c> initial value by the field holding it, else an argument value.</summary>
    /// <param name="region">The region.</param>
    /// <param name="site">Its site's body.</param>
    private DriverRole DriverRoleOf(HeapRegion region, string site)
    {
        if (region.TypeKey is { } typeKey && typeKey.StartsWith($"{DriverSynthesizer.ASSEMBLY}:Probe_", StringComparison.Ordinal))
            return DriverRole.ProbeObject;
        if (_factories.Contains(site))
            return region.Kind == HeapRegionKind.Delegate ? DriverRole.ProbeDelegate : DriverRole.ProbeLambdaReturn;
        if (_factories.Any(factory => site.StartsWith(factory + LAMBDA_SEPARATOR, StringComparison.Ordinal)))
            return DriverRole.ProbeLambdaReturn;
        if (_intermediates.Contains(region.Identity))
            return DriverRole.Intermediate;
        return _outValues.Contains(region.Identity) ? DriverRole.OutInitialValue : DriverRole.ArgumentValue;
    }

    private static string WithoutTypeArguments(string typeKey) => typeKey.IndexOf('<') is var open and >= 0 ? typeKey[..open] : typeKey;
}
