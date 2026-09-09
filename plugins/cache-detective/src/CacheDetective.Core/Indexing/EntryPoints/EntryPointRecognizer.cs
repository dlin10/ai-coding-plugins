namespace CacheDetective.Indexing.EntryPoints;

/// <summary>One uniform entry-point form: a shape a type implements or derives from, and the method on it
/// that runs.</summary>
internal sealed record EntryPointRecognizer(string ShapeName, int Arity, string MethodName, string Kind);

/// <summary>The uniform forms, grouped by where in the emission order they belong. Three lists rather than
/// one because the three groups emit at three different points and the order must not move: an entry point
/// that changes position changes the ids of the unresolved rows around it, and an id that moves between runs
/// binds an annotation to a different site.</summary>
internal sealed record EntryPointTables(IReadOnlyList<EntryPointRecognizer> RequestHandlers,
                                        IReadOnlyList<EntryPointRecognizer> HostedServices,
                                        IReadOnlyList<EntryPointRecognizer> Jobs)
{
    public static EntryPointTables Default { get; } = new(
        RequestHandlers:
        [
            new EntryPointRecognizer("IRequestHandler", 2, "Handle", "request_handler"),
            new EntryPointRecognizer("IRequestHandler", 1, "Handle", "request_handler")
        ],
        HostedServices: [new EntryPointRecognizer("IHostedService", 0, "StartAsync", "hosted_service")],
        Jobs: [new EntryPointRecognizer("IJob", 0, "Execute", "job")]);
}
