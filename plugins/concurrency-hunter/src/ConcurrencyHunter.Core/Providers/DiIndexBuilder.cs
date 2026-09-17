using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Providers;

/// <summary>Indexes every service registration call in a scope's source, matched by exact symbol and supported
/// version, whether or not anything calls the method that contains it.</summary>
public static class DiIndexBuilder
{
    private const string DI_ABSTRACTIONS = "Microsoft.Extensions.DependencyInjection.Abstractions";
    private const string HOSTING_ABSTRACTIONS = "Microsoft.Extensions.Hosting.Abstractions";
    private const string SERVICE_EXTENSIONS = "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions";
    private const string DESCRIPTOR_EXTENSIONS = "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions";
    private const string HOSTED_EXTENSIONS = "Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions";
    private const string HOSTED_SERVICE = "Microsoft.Extensions.Hosting.IHostedService";

    private const string FACTORY = "factory";
    private const string INSTANCE = "instance";
    private const string KEYED = "keyed";
    private const string OPEN_GENERIC = "open generic Type";
    private const string NON_TYPEOF = "non-typeof Type";
    private const string TYPE_PARAMETER = "type parameter";

    public static readonly SupportedAssemblyVersion DependencyInjection = SupportedAssemblyVersion.Framework(DI_ABSTRACTIONS);
    public static readonly SupportedAssemblyVersion Hosting = SupportedAssemblyVersion.Framework(HOSTING_ABSTRACTIONS);

    private static readonly Dictionary<string, (string ContainingType, DiLifetime Lifetime)> Methods = new(StringComparer.Ordinal)
    {
        ["AddSingleton"] = (SERVICE_EXTENSIONS, DiLifetime.Singleton),
        ["AddScoped"] = (SERVICE_EXTENSIONS, DiLifetime.Scoped),
        ["AddTransient"] = (SERVICE_EXTENSIONS, DiLifetime.Transient),
        ["AddKeyedSingleton"] = (SERVICE_EXTENSIONS, DiLifetime.Singleton),
        ["AddKeyedScoped"] = (SERVICE_EXTENSIONS, DiLifetime.Scoped),
        ["AddKeyedTransient"] = (SERVICE_EXTENSIONS, DiLifetime.Transient),
        ["TryAddSingleton"] = (DESCRIPTOR_EXTENSIONS, DiLifetime.Singleton),
        ["TryAddScoped"] = (DESCRIPTOR_EXTENSIONS, DiLifetime.Scoped),
        ["TryAddTransient"] = (DESCRIPTOR_EXTENSIONS, DiLifetime.Transient),
        [DiIndex.HOSTED_SERVICE_METHOD] = (HOSTED_EXTENSIONS, DiLifetime.Singleton)
    };

    public static DiIndex Build(string scopeId, IEnumerable<Compilation> compilations, string rootDirectory,
                                CancellationToken cancellationToken) =>
        Build(scopeId, compilations.Select(compilation => (compilation, (string?)null)), rootDirectory, cancellationToken);

    /// <summary>Indexes the registrations of compilations paired with their project file paths, which order the registrations of
    /// two projects sharing an assembly name.</summary>
    public static DiIndex Build(string scopeId, IEnumerable<(Compilation Compilation, string? ProjectFilePath)> projects,
                                string rootDirectory, CancellationToken cancellationToken)
    {
        var registrations = new List<DiRegistration>();
        var diagnostics = new List<DiDiagnostic>();
        var reportedVersions = new HashSet<(string, Version)>();
        foreach (var (compilation, projectFilePath) in projects)
        {
            var projectPath = projectFilePath is null ? "" : SourceSpans.PathOf(projectFilePath, rootDirectory);
            var lowered = new Dictionary<IMethodSymbol, IReadOnlyList<IrBody>>(SymbolEqualityComparer.Default);
            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SemanticModel? model = null;
                foreach (var invocation in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!Methods.ContainsKey(InvokedName(invocation) ?? ""))
                        continue;

                    model ??= compilation.GetSemanticModel(tree);
                    if (model.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation)
                        continue;

                    var method = operation.TargetMethod;
                    if (!Methods.TryGetValue(method.Name, out var expected) || !IsDeclaredIn(method, expected.ContainingType))
                        continue;

                    var assembly = method.ContainingAssembly;
                    var range = method.Name == DiIndex.HOSTED_SERVICE_METHOD ? Hosting : DependencyInjection;
                    if (!ExactSymbols.IsInRange(assembly, range))
                    {
                        if (reportedVersions.Add((assembly.Identity.Name, assembly.Identity.Version)))
                        {
                            diagnostics.Add(new DiDiagnostic(
                                RootDiscoveryDiagnosticCode.UnsupportedAssemblyVersion,
                                assembly.Identity.Name,
                                $"{assembly.Identity.Name} {assembly.Identity.Version} is outside the supported range " +
                                $"{range.Minimum} up to {range.MaximumExclusive}; its registrations are not indexed.",
                                SourceSpans.From(invocation, rootDirectory)));
                        }
                        continue;
                    }

                    var source = SourceSpans.From(invocation, rootDirectory);
                    var (bodyId, operationId) = RegistrationCall(invocation, method, source, model, compilation, rootDirectory, lowered,
                                                                 cancellationToken);
                    registrations.Add(Classify(operation, expected.Lifetime, source) with
                    {
                        Assembly = compilation.AssemblyName ?? "",
                        ProjectPath = projectPath,
                        BodyId = bodyId,
                        OperationId = operationId
                    });
                }
            }
        }

        return new DiIndex(scopeId, registrations, diagnostics);
    }

    /// <summary>The lowered body holding a registration call and the call's operation in it: the member containing the invocation
    /// (a lambda or local function being one of its nested bodies) is lowered once, and the call is the operation with the
    /// registration method's id at the invocation's span.</summary>
    private static (string? BodyId, int? OperationId) RegistrationCall(InvocationExpressionSyntax invocation, IMethodSymbol method,
                                                                       SourceSpan source, SemanticModel model, Compilation compilation,
                                                                       string rootDirectory,
                                                                       Dictionary<IMethodSymbol, IReadOnlyList<IrBody>> lowered,
                                                                       CancellationToken cancellationToken)
    {
        var member = model.GetEnclosingSymbol(invocation.SpanStart, cancellationToken);
        while (member is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
            member = member.ContainingSymbol;
        if (member is not IMethodSymbol containing)
            return (null, null);

        if (!lowered.TryGetValue(containing, out var bodies))
        {
            try
            {
                var result = IrLowering.Lower(containing, compilation, rootDirectory, cancellationToken);
                bodies = result.NestedBodies.Prepend(result.Body).ToArray();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                bodies = [];
            }

            lowered.Add(containing, bodies);
        }

        var methodId = IrLowering.RootBodyId(method.OriginalDefinition);
        foreach (var body in bodies)
        {
            var call = body.Blocks.SelectMany(block => block.Operations)
                           .OfType<IrCallOperation>()
                           .FirstOrDefault(call => call.TargetMethodId == methodId && call.Provenance.Span == source);
            if (call is not null)
                return (body.BodyId, call.Id);
        }

        return (null, null);
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null
    };

    private static bool IsDeclaredIn(IMethodSymbol method, string containingType) =>
        method.ContainingType is { } type &&
        string.Equals(SymbolNames.Type(type.OriginalDefinition), containingType, StringComparison.Ordinal) &&
        string.Equals(type.ContainingAssembly.Identity.Name,
                      containingType == HOSTED_EXTENSIONS ? HOSTING_ABSTRACTIONS : DI_ABSTRACTIONS,
                      StringComparison.Ordinal);

    private static DiRegistration Classify(IInvocationOperation operation, DiLifetime lifetime, SourceSpan source)
    {
        var method = operation.TargetMethod;
        var isHostedMethod = method.Name == DiIndex.HOSTED_SERVICE_METHOD;
        var arguments = operation.Arguments.Where(argument => argument.Parameter is { Ordinal: > 0 })
                                           .OrderBy(argument => argument.Parameter!.Ordinal)
                                           .ToArray();

        ITypeSymbol? service;
        ITypeSymbol? implementation;
        string? unsupported = null;
        if (method.IsGenericMethod)
        {
            service = isHostedMethod ? null : method.TypeArguments[0];
            implementation = method.TypeArguments[^1];
            if (method.TypeArguments.Any(ContainsTypeParameter))
                unsupported = TYPE_PARAMETER;
        }
        else
        {
            var typeOfs = arguments.Where(argument => IsSystemType(argument.Parameter!.Type))
                                   .Select(argument => TypeOf(argument.Value))
                                   .ToArray();
            service = typeOfs.Length > 0 ? typeOfs[0].Type : null;
            implementation = typeOfs.Length > 0 ? typeOfs[^1].Type : null;
            unsupported = typeOfs.Select(typeOf => typeOf.Unsupported).FirstOrDefault(reason => reason is not null);
        }

        // A generic factory or instance form names its service and implementation in its type arguments, so it binds like
        // the parameterless generic form; a non-generic one, or a hosted-service factory, does not.
        var namesItsTypes = method.IsGenericMethod && !isHostedMethod;
        var form = arguments.Any(argument => IsFactory(argument.Parameter!.Type)) ? DiRegistrationForm.Factory
            : arguments.Any(argument => !IsSystemType(argument.Parameter!.Type)) ? DiRegistrationForm.Instance
            : DiRegistrationForm.Type;
        if (method.Name.StartsWith("AddKeyed", StringComparison.Ordinal))
            unsupported = KEYED;
        else if (!namesItsTypes && form == DiRegistrationForm.Factory)
            unsupported = FACTORY;
        else if (!namesItsTypes && form == DiRegistrationForm.Instance)
            unsupported = INSTANCE;

        var isHosted = isHostedMethod ||
                       lifetime == DiLifetime.Singleton && ExactSymbols.IsType(service, Hosting, HOSTED_SERVICE);
        var serviceName = isHostedMethod ? HOSTED_SERVICE : service is null ? null : TypeName(service);
        var serviceKey = isHostedMethod ? DiIndex.HOSTED_SERVICE_KEY : service is null ? null : SymbolNames.TypeKey(service);
        return new DiRegistration(serviceName, implementation is null ? null : TypeName(implementation), lifetime, isHosted,
                                  method.Name, source, unsupported, serviceKey, implementation is null ? null : SymbolNames.TypeKey(implementation),
                                  form);
    }

    internal static (ITypeSymbol? Type, string? Unsupported) TypeOf(IOperation value)
    {
        while (value is IConversionOperation conversion)
            value = conversion.Operand;
        if (value is not ITypeOfOperation typeOf)
            return (null, NON_TYPEOF);
        if (typeOf.TypeOperand is INamedTypeSymbol { IsUnboundGenericType: true })
            return (typeOf.TypeOperand, OPEN_GENERIC);
        if (ContainsTypeParameter(typeOf.TypeOperand))
            return (typeOf.TypeOperand, TYPE_PARAMETER);
        return (typeOf.TypeOperand, null);
    }

    internal static bool IsSystemType(ITypeSymbol type) =>
        type.Name == "Type" && type.ContainingNamespace?.ToDisplayString() == "System";

    private static bool IsFactory(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named &&
        named.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System" &&
        named.Name == "Func";

    internal static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
        _ => false
    };

    private static string TypeName(ITypeSymbol type) => SymbolNames.Type(type);
}
