using ConcurrencyHunter.Di;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Providers;

/// <summary>HTTP roots: controller actions as ASP.NET Core's controller feature and application model decide,
/// minimal-API handlers mapped on an endpoint route builder, and the methods of gRPC services mapped with
/// <c>MapGrpcService&lt;T&gt;()</c>.</summary>
public sealed class AspNetCoreRootProvider : IExecutionRootProvider
{
    public const string PROVIDER_ID = "aspnetcore";
    public const string GRPC_METHOD = "grpc-method";

    private const string MVC_CORE = "Microsoft.AspNetCore.Mvc.Core";
    private const string MVC = "Microsoft.AspNetCore.Mvc";
    private const string ROUTING = "Microsoft.AspNetCore.Routing";
    private const string HTTP_ABSTRACTIONS = "Microsoft.AspNetCore.Http.Abstractions";
    private const string BIND_SERVICE_METHOD = "Grpc.Core.BindServiceMethodAttribute";

    private static readonly SupportedAssemblyVersion MvcCore = SupportedAssemblyVersion.Framework(MVC_CORE);
    private static readonly SupportedAssemblyVersion Mvc = SupportedAssemblyVersion.Framework(MVC);
    private static readonly SupportedAssemblyVersion Routing = SupportedAssemblyVersion.Framework(ROUTING);
    private static readonly SupportedAssemblyVersion HttpAbstractions = SupportedAssemblyVersion.Framework(HTTP_ABSTRACTIONS);
    private static readonly SupportedAssemblyVersion DependencyInjection =
        SupportedAssemblyVersion.Framework("Microsoft.Extensions.DependencyInjection.Abstractions");
    private static readonly SupportedAssemblyVersion GrpcServer = new("Grpc.AspNetCore.Server", new Version(2, 0, 0, 0), new Version(3, 0, 0, 0));
    private static readonly SupportedAssemblyVersion GrpcCore = new("Grpc.Core.Api", new Version(2, 0, 0, 0), new Version(3, 0, 0, 0));

    private static readonly HashSet<string> ControllerRegistrations = new(StringComparer.Ordinal)
    {
        "AddControllers", "AddControllersWithViews", "AddMvc", "AddMvcCore"
    };

    private static readonly HashSet<string> ControllerMappings = new(StringComparer.Ordinal)
    {
        "MapControllers", "MapControllerRoute", "MapDefaultControllerRoute", "MapAreaControllerRoute", "MapFallbackToController",
        "MapFallbackToAreaController"
    };

    private static readonly HashSet<string> EndpointMappings = new(StringComparer.Ordinal)
    {
        "Map", "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods", "MapFallback"
    };

    private static readonly HashSet<string> FrameworkProvidedTypes = new(StringComparer.Ordinal)
    {
        "Microsoft.AspNetCore.Http.HttpContext", "Microsoft.AspNetCore.Http.HttpRequest", "Microsoft.AspNetCore.Http.HttpResponse",
        "System.Threading.CancellationToken", "System.Security.Claims.ClaimsPrincipal", "Microsoft.AspNetCore.Http.IFormFile",
        "Microsoft.AspNetCore.Http.IFormFileCollection", "Microsoft.AspNetCore.Http.IFormCollection", "System.IO.Stream",
        "System.IO.Pipelines.PipeReader"
    };

    private static readonly HashSet<string> SimpleTypes = new(StringComparer.Ordinal)
    {
        "System.Guid", "System.TimeSpan", "System.DateTimeOffset", "System.Uri", "System.DateOnly", "System.TimeOnly", "System.Version"
    };

    public string ProviderId => PROVIDER_ID;

    public IReadOnlyList<SupportedAssemblyVersion> SupportedAssemblyVersions =>
        [MvcCore, Mvc, Routing, HttpAbstractions, DependencyInjection, GrpcServer, GrpcCore];

    public RootDiscoveryResult Discover(RootDiscoveryContext context)
    {
        var (diagnostics, skipped) = ProviderSupport.VersionDiagnostics(context, ProviderId, SupportedAssemblyVersions);
        if (ProviderSupport.Scanned(context, SupportedAssemblyVersions, skipped) is not { } scanned)
            return new RootDiscoveryResult(RootDiscoveryStatus.NotChecked, [], diagnostics);
        context = scanned;

        var roots = new List<ExecutionRootDescriptor>();
        if (ProviderSupport.References(context, MvcCore))
            DiscoverControllers(context, roots, diagnostics);
        if (ProviderSupport.References(context, Routing))
            DiscoverMinimalApis(context, roots, diagnostics);
        if (ProviderSupport.References(context, GrpcServer))
            DiscoverGrpcServices(context, roots, diagnostics);
        return new RootDiscoveryResult(RootDiscoveryStatus.Checked, roots, diagnostics);
    }

    private void DiscoverControllers(RootDiscoveryContext context, List<ExecutionRootDescriptor> roots,
                                     List<RootDiscoveryDiagnostic> diagnostics)
    {
        var controllers = context.Compilations
                                 .Where(compilation => ExactSymbols.FindReferencedAssembly(compilation, MVC_CORE) is not null)
                                 .SelectMany(compilation => compilation.Assembly.GlobalNamespace.GetNamespaceMembers()
                                                                       .SelectMany(TopLevelTypes)
                                                                       .Concat(compilation.Assembly.GlobalNamespace.GetTypeMembers())
                                                                       .Where(IsController)
                                                                       .Select(type => (compilation, type)))
                                 .ToArray();
        if (controllers.Length == 0)
            return;

        var calls = Invocations(context, ControllerRegistrations.Concat(ControllerMappings)).ToArray();
        var registered = calls.Any(call => ControllerRegistrations.Contains(call.Name) &&
                                           (IsDeclaredIn(call, Mvc, "Microsoft.Extensions.DependencyInjection.MvcServiceCollectionExtensions") ||
                                            IsDeclaredIn(call, MvcCore, "Microsoft.Extensions.DependencyInjection.MvcCoreServiceCollectionExtensions")));
        var mapped = calls.Any(call => ControllerMappings.Contains(call.Name) &&
                                       IsDeclaredIn(call, MvcCore, "Microsoft.AspNetCore.Builder.ControllerEndpointRouteBuilderExtensions"));
        if (!registered || !mapped)
        {
            diagnostics.Add(new RootDiscoveryDiagnostic(ProviderId, RootDiscoveryDiagnosticCode.UnsupportedPattern, context.ScopeId,
                                                        $"controllers are not both registered and mapped in scope {context.ScopeId}", []));
            return;
        }

        foreach (var (compilation, controller) in controllers)
        {
            var apiController = HasApiController(controller);
            foreach (var action in Actions(compilation, controller))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var symbol = SymbolNames.Method(action);
                var parameters = action.Parameters.Select(parameter => new ParameterBinding(
                                                              parameter.Name, SymbolNames.Type(parameter.Type),
                                                              ControllerParameterKind(context.DiIndex, compilation, parameter, apiController),
                                                              parameter.Type.IsValueType, SymbolNames.TypeKey(parameter.Type)))
                                       .ToArray();
                roots.Add(new ExecutionRootDescriptor(
                    $"aspnetcore:controller-action:{controller.ContainingAssembly.Name}:{controller.GetDocumentationCommentId()}:{action.GetDocumentationCommentId()}",
                    "controller-action",
                    ProviderId,
                    new RootEntry(IrLowering.RootBodyId(action), symbol, $"action {symbol} of controller {SymbolNames.Type(controller)}",
                                  ProviderSupport.Source(action, context.RootDirectory)),
                    new InstanceBindings(ReceiverKind.PerInvocation, parameters, SymbolNames.Type(controller), SymbolNames.TypeKey(controller)),
                    new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, context.ScopeId),
                    [],
                    [],
                    [new DiscoveryEvidence("E1", "controller", $"{SymbolNames.Type(controller)} is a controller",
                                           ProviderSupport.Source(controller, context.RootDirectory))],
                    []));
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> TopLevelTypes(INamespaceSymbol @namespace) =>
        @namespace.GetTypeMembers().Concat(@namespace.GetNamespaceMembers().SelectMany(TopLevelTypes));

    private static bool IsController(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.ContainingType is not null || type.DeclaredAccessibility != Accessibility.Public ||
            type.IsAbstract || type.IsGenericType || type.DeclaringSyntaxReferences.Length == 0)
        {
            return false;
        }

        var chain = BaseChain(type).ToArray();
        if (chain.Any(current => current.GetAttributes().Any(attribute => IsAttribute(attribute, "Microsoft.AspNetCore.Mvc.NonControllerAttribute"))))
            return false;

        return type.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) ||
               chain.Any(current => current.GetAttributes().Any(attribute => IsAttribute(attribute, "Microsoft.AspNetCore.Mvc.ControllerAttribute")));
    }

    private static bool HasApiController(INamedTypeSymbol controller) =>
        BaseChain(controller).Any(current => current.GetAttributes().Any(attribute => IsAttribute(attribute, "Microsoft.AspNetCore.Mvc.ApiControllerAttribute"))) ||
        controller.ContainingAssembly.GetAttributes().Any(attribute => IsAttribute(attribute, "Microsoft.AspNetCore.Mvc.ApiControllerAttribute"));

    /// <summary>Whether the attribute's class is <paramref name="metadataName"/> from Mvc.Core in range, or derives from it.</summary>
    private static bool IsAttribute(AttributeData attribute, string metadataName) =>
        BaseChain(attribute.AttributeClass).Any(current => ExactSymbols.IsType(current, MvcCore, metadataName));

    private static IEnumerable<INamedTypeSymbol> BaseChain(INamedTypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            yield return current;
    }

    private static IEnumerable<IMethodSymbol> Actions(Compilation compilation, INamedTypeSymbol controller)
    {
        var effective = new List<IMethodSymbol>();
        foreach (var type in BaseChain(controller))
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.DeclaredAccessibility != Accessibility.Public ||
                    effective.Any(chosen => SameSignature(chosen, method)))
                {
                    continue;
                }

                effective.Add(method);
            }
        }

        var dispose = compilation.GetSpecialType(SpecialType.System_IDisposable).GetMembers("Dispose").OfType<IMethodSymbol>().FirstOrDefault();
        var disposeImplementation = dispose is null ? null : controller.FindImplementationForInterfaceMember(dispose) as IMethodSymbol;
        return effective.Where(method =>
            !method.IsGenericMethod &&
            !method.IsAbstract &&
            !Overridden(method).Any(declaration => declaration.GetAttributes().Any(attribute => IsAttribute(attribute, "Microsoft.AspNetCore.Mvc.NonActionAttribute"))) &&
            Overridden(method).Last().ContainingType.SpecialType != SpecialType.System_Object &&
            !(disposeImplementation is not null && ProviderSupport.Overrides(method, disposeImplementation)) &&
            ProviderSupport.HasSourceBody(method));
    }

    private static IEnumerable<IMethodSymbol> Overridden(IMethodSymbol method)
    {
        for (IMethodSymbol? current = method; current is not null; current = current.OverriddenMethod)
            yield return current;
    }

    private static bool SameSignature(IMethodSymbol first, IMethodSymbol second) =>
        first.Name == second.Name &&
        first.TypeParameters.Length == second.TypeParameters.Length &&
        first.Parameters.Length == second.Parameters.Length &&
        first.Parameters.Zip(second.Parameters).All(pair => pair.First.RefKind == pair.Second.RefKind &&
                                                             SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type));

    /// <summary>
    /// One root per public override, in a mapped service, of a virtual method of the service's base class marked
    /// <c>BindServiceMethod</c> (the generated <c>*Base</c>), whatever its streaming kind. The service is created per call, unless it is
    /// registered in DI, in which case the registration decides.
    /// </summary>
    private void DiscoverGrpcServices(RootDiscoveryContext context, List<ExecutionRootDescriptor> roots,
                                      List<RootDiscoveryDiagnostic> diagnostics)
    {
        var services = Invocations(context, ["MapGrpcService"])
                       .Where(call => IsDeclaredIn(call, GrpcServer, "Microsoft.AspNetCore.Builder.GrpcEndpointRouteBuilderExtensions") &&
                                      call.Operation.TargetMethod.TypeArguments is [INamedTypeSymbol])
                       .GroupBy(call => (INamedTypeSymbol)call.Operation.TargetMethod.TypeArguments[0], SymbolEqualityComparer.Default)
                       .OrderBy(group => group.Key!.GetDocumentationCommentId(), StringComparer.Ordinal);
        foreach (var group in services)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var service = (INamedTypeSymbol)group.Key!;
            var display = SymbolNames.Type(service);
            var bindBase = BaseChain(service.BaseType).FirstOrDefault(type => type.GetAttributes().Any(attribute =>
                               ExactSymbols.IsType(attribute.AttributeClass, GrpcCore, BIND_SERVICE_METHOD)));
            if (bindBase is null || service.IsGenericType || service.DeclaringSyntaxReferences.Length == 0)
            {
                diagnostics.Add(new RootDiscoveryDiagnostic(ProviderId, RootDiscoveryDiagnosticCode.UnsupportedPattern, display,
                                                            $"MapGrpcService maps {display}, which is no source service deriving from a class marked BindServiceMethod",
                                                            []));
                continue;
            }

            var typeKey = SymbolNames.TypeKey(service);
            var receiver = context.DiIndex.Resolve(typeKey).Kind == DiResolutionKind.Unregistered ? ReceiverKind.PerInvocation : ReceiverKind.DiService;
            var mapping = group.OrderBy(call => call.Syntax.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(call => call.Syntax.SpanStart).First();
            foreach (var method in ServiceMethods(service, bindBase))
            {
                var symbol = SymbolNames.Method(method);
                roots.Add(new ExecutionRootDescriptor(
                    $"aspnetcore:{GRPC_METHOD}:{service.ContainingAssembly.Name}:{service.GetDocumentationCommentId()}:{method.GetDocumentationCommentId()}",
                    GRPC_METHOD,
                    ProviderId,
                    new RootEntry(IrLowering.RootBodyId(method), symbol, $"{GRPC_METHOD} {symbol} of gRPC service {display}",
                                  ProviderSupport.Source(method, context.RootDirectory)),
                    new InstanceBindings(receiver,
                        method.Parameters.Select(parameter => new ParameterBinding(
                                                     parameter.Name, SymbolNames.Type(parameter.Type), ParameterBindingKind.RequestData,
                                                     parameter.Type.IsValueType, SymbolNames.TypeKey(parameter.Type)))
                              .ToArray(),
                        display, typeKey),
                    new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, context.ScopeId),
                    [],
                    [],
                    [new DiscoveryEvidence("E1", "grpc-mapping", $"MapGrpcService maps {display}", SourceSpans.From(mapping.Syntax, context.RootDirectory))],
                    []));
            }
        }
    }

    /// <summary>The declaration the runtime dispatches to on <paramref name="service"/> for each public virtual method of the bind base,
    /// when it is a public override with a source body.</summary>
    private static IEnumerable<IMethodSymbol> ServiceMethods(INamedTypeSymbol service, INamedTypeSymbol bindBase)
    {
        foreach (var virtualMethod in bindBase.GetMembers().OfType<IMethodSymbol>()
                                              .Where(method => method is { MethodKind: MethodKind.Ordinary, IsVirtual: true, IsStatic: false,
                                                                           DeclaredAccessibility: Accessibility.Public }))
        {
            var dispatched = BaseChain(service).TakeWhile(type => !SymbolEqualityComparer.Default.Equals(type, bindBase))
                                               .SelectMany(type => type.GetMembers(virtualMethod.Name).OfType<IMethodSymbol>())
                                               .FirstOrDefault(candidate => candidate.IsOverride && ProviderSupport.Overrides(candidate, virtualMethod));
            if (dispatched is { DeclaredAccessibility: Accessibility.Public } && ProviderSupport.HasSourceBody(dispatched))
                yield return dispatched;
        }
    }

    private void DiscoverMinimalApis(RootDiscoveryContext context, List<ExecutionRootDescriptor> roots,
                                     List<RootDiscoveryDiagnostic> diagnostics)
    {
        var mappings = Invocations(context, EndpointMappings)
                       .Where(call => IsDeclaredIn(call, Routing, "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions") ||
                                      call.Name == "MapFallback" &&
                                      IsDeclaredIn(call, Routing, "Microsoft.AspNetCore.Builder.FallbackEndpointRouteBuilderExtensions"))
                       .Select(call => (Call: call, Member: ContainingMember(call)))
                       .Where(mapping => mapping.Member is not null)
                       .GroupBy(mapping => mapping.Member!, SymbolEqualityComparer.Default);

        foreach (var group in mappings)
        {
            var member = (IMethodSymbol)group.Key!;
            var ordered = group.OrderBy(mapping => mapping.Call.Syntax.SyntaxTree.FilePath, StringComparer.Ordinal)
                               .ThenBy(mapping => mapping.Call.Syntax.SpanStart)
                               .ToArray();
            IReadOnlyDictionary<(SyntaxTree, Microsoft.CodeAnalysis.Text.TextSpan), string>? nestedIds = null;
            for (var index = 0; index < ordered.Length; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var (call, _) = ordered[index];
                var number = index + 1;
                var anchor = $"{SymbolNames.Method(member)}#map{number}";
                var handler = Handler(call.Operation);
                if (handler is null)
                {
                    diagnostics.Add(new RootDiscoveryDiagnostic(ProviderId, RootDiscoveryDiagnosticCode.UnresolvedBinding, anchor,
                                                                $"the handler of {call.Name} in {anchor} is not a source method group, lambda or local function",
                                                                []));
                    continue;
                }

                var (method, syntax, isMethodGroup) = handler.Value;
                if (isMethodGroup && IsGenericHandler(method))
                {
                    // A closed generic handler has statics of its own, which the open definition's body would merge.
                    var display = SymbolNames.Method(method);
                    diagnostics.Add(new RootDiscoveryDiagnostic(ProviderId, RootDiscoveryDiagnosticCode.UnsupportedPattern, display,
                                                                $"generic handler {display}", []));
                    continue;
                }

                if (isMethodGroup)
                    method = method.OriginalDefinition;
                var receiver = (isMethodGroup ? method.IsStatic : member.IsStatic) ? ReceiverKind.None : ReceiverKind.Unbound;
                string bodyKey;
                string symbol;
                if (isMethodGroup)
                {
                    bodyKey = IrLowering.RootBodyId(method);
                    symbol = SymbolNames.Method(method);
                }
                else
                {
                    nestedIds ??= NestedIds(member, call.Compilation, context.CancellationToken);
                    bodyKey = nestedIds.TryGetValue((syntax.SyntaxTree, syntax.Span), out var nestedId)
                        ? nestedId
                        : $"{IrLowering.RootBodyId(member)}#map{number}";
                    symbol = method.MethodKind == MethodKind.LocalFunction ? SymbolNames.Method(method) : anchor;
                }

                var parameters = method.Parameters.Select(parameter => new ParameterBinding(
                                                              parameter.Name, SymbolNames.Type(parameter.Type),
                                                              MinimalApiParameterKind(context.DiIndex, parameter),
                                                              parameter.Type.IsValueType, SymbolNames.TypeKey(parameter.Type)))
                                       .ToArray();
                var receiverType = receiver == ReceiverKind.None ? null : (isMethodGroup ? method : member).ContainingType;
                roots.Add(new ExecutionRootDescriptor(
                    $"aspnetcore:minimal-api:{member.ContainingAssembly.Name}:{member.GetDocumentationCommentId()}#map{number}",
                    "minimal-api",
                    ProviderId,
                    new RootEntry(bodyKey, symbol, $"{call.Name} handler {anchor}", SourceSpans.From(syntax, context.RootDirectory)),
                    new InstanceBindings(receiver, parameters, receiverType is null ? null : SymbolNames.Type(receiverType),
                                         receiverType is null ? null : SymbolNames.TypeKey(receiverType)),
                    new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, context.ScopeId),
                    [],
                    [],
                    [new DiscoveryEvidence("E1", "endpoint-mapping", $"{call.Name} maps {anchor}",
                                           SourceSpans.From(call.Syntax, context.RootDirectory))],
                    []));
            }
        }
    }

    private static IReadOnlyDictionary<(SyntaxTree, Microsoft.CodeAnalysis.Text.TextSpan), string> NestedIds(
        IMethodSymbol member, Compilation compilation, CancellationToken cancellationToken)
    {
        try
        {
            return IrLowering.NestedBodyIds(member, compilation, cancellationToken);
        }
        catch (ArgumentException)
        {
            return new Dictionary<(SyntaxTree, Microsoft.CodeAnalysis.Text.TextSpan), string>();
        }
    }

    /// <summary>The handler a mapping call registers, when it is a source method group, a lambda or a local function.</summary>
    private static (IMethodSymbol Method, SyntaxNode Syntax, bool IsMethodGroup)? Handler(IInvocationOperation operation)
    {
        var argument = operation.Arguments.LastOrDefault(candidate =>
            candidate.Parameter?.Type is { Name: "Delegate" or "RequestDelegate" });
        if (argument is null)
            return null;

        var value = argument.Value;
        while (value is IConversionOperation conversion)
            value = conversion.Operand;
        if (value is not IDelegateCreationOperation creation)
            return null;

        switch (creation.Target)
        {
            case IAnonymousFunctionOperation lambda:
                return (lambda.Symbol, lambda.Syntax, false);
            case IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction } local
                when local.Method.DeclaringSyntaxReferences.FirstOrDefault() is { } reference:
                return (local.Method, reference.GetSyntax(), false);
            case IMethodReferenceOperation reference when ProviderSupport.HasSourceBody(reference.Method.OriginalDefinition):
                return (reference.Method, reference.Method.OriginalDefinition.DeclaringSyntaxReferences[0].GetSyntax(), true);
            default:
                return null;
        }
    }

    private static bool IsGenericHandler(IMethodSymbol method)
    {
        if (method.IsGenericMethod)
            return true;
        for (var type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.IsGenericType)
                return true;
        }

        return false;
    }

    private static IMethodSymbol? ContainingMember(Invocation call)
    {
        var symbol = call.Model.GetEnclosingSymbol(call.Syntax.SpanStart);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
            symbol = symbol.ContainingSymbol;
        return symbol as IMethodSymbol;
    }

    private static ParameterBindingKind? AttributeKind(IParameterSymbol parameter)
    {
        foreach (var attribute in parameter.GetAttributes())
        {
            if (IsAttribute(attribute, "Microsoft.AspNetCore.Mvc.FromServicesAttribute"))
                return ParameterBindingKind.DiService;
            if (BaseChain(attribute.AttributeClass).Any(current =>
                    ExactSymbols.IsType(current, DependencyInjection, "Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute")))
            {
                return ParameterBindingKind.Unsupported;
            }
        }

        foreach (var attribute in parameter.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeClass && attributeClass.Name.StartsWith("From", StringComparison.Ordinal) &&
                attributeClass.ContainingNamespace?.ToDisplayString() == "Microsoft.AspNetCore.Mvc" &&
                ExactSymbols.IsInRange(attributeClass.ContainingAssembly, MvcCore))
            {
                return ParameterBindingKind.RequestData;
            }
        }

        return null;
    }

    private static ParameterBindingKind MinimalApiParameterKind(DiIndex index, IParameterSymbol parameter)
    {
        if (AttributeKind(parameter) is { } kind)
            return kind;
        if (parameter.GetAttributes().Any(attribute => attribute.AttributeClass is { } attributeClass &&
                                                        ExactSymbols.IsType(attributeClass, HttpAbstractions, "Microsoft.AspNetCore.Http.AsParametersAttribute")))
        {
            return ParameterBindingKind.Unsupported;
        }

        var type = Unwrapped(parameter.Type);
        if (FrameworkProvidedTypes.Contains(SymbolNames.Type(type)) || HasBindingMethod(type))
            return ParameterBindingKind.RequestData;
        return index.Resolve(SymbolNames.TypeKey(parameter.Type)).Kind == DiResolutionKind.Unregistered
            ? ParameterBindingKind.RequestData
            : ParameterBindingKind.DiService;
    }

    private static ParameterBindingKind ControllerParameterKind(DiIndex index, Compilation compilation, IParameterSymbol parameter,
                                                                bool apiController)
    {
        if (AttributeKind(parameter) is { } kind)
            return kind;
        return apiController && !IsSimple(compilation, parameter.Type) &&
               index.Resolve(SymbolNames.TypeKey(parameter.Type)).Kind != DiResolutionKind.Unregistered
            ? ParameterBindingKind.DiService
            : ParameterBindingKind.RequestData;
    }

    private static ITypeSymbol Unwrapped(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;

    /// <summary>A public static <c>BindAsync</c> or <c>TryParse</c> on the type, a base class or an implemented interface.</summary>
    private static bool HasBindingMethod(ITypeSymbol type) =>
        BaseChain(type as INamedTypeSymbol).Cast<ITypeSymbol>().Concat(type.AllInterfaces)
            .SelectMany(candidate => candidate.GetMembers())
            .OfType<IMethodSymbol>()
            .Any(method => method is { IsStatic: true, DeclaredAccessibility: Accessibility.Public, Name: "BindAsync" or "TryParse" });

    /// <summary>Not complex to MVC: a primitive or known simple type, a type a <c>TypeConverter</c> attribute (on it or a
    /// base type) converts from string, or a parseable type.</summary>
    private static bool IsSimple(Compilation compilation, ITypeSymbol type)
    {
        type = Unwrapped(type);
        return type.TypeKind == TypeKind.Enum ||
               type.SpecialType is not (SpecialType.None or SpecialType.System_Object) ||
               SimpleTypes.Contains(SymbolNames.Type(type)) ||
               BaseChain(type as INamedTypeSymbol).Any(current => current.GetAttributes().Any(attribute =>
                   attribute.AttributeClass is { } attributeClass && SymbolNames.Type(attributeClass) == "System.ComponentModel.TypeConverterAttribute" &&
                   ConvertsFromString(compilation, attribute))) ||
               IsParseable(type);
    }

    /// <summary>
    /// The static stand-in for <c>TypeDescriptor.GetConverter(type).CanConvertFrom(typeof(string))</c>: the attribute's converter
    /// (a <c>typeof</c> argument or a type name the compilation resolves) overrides <c>CanConvertFrom(ITypeDescriptorContext, Type)</c>
    /// below <c>System.ComponentModel.TypeConverter</c>, whose own override accepts only <c>InstanceDescriptor</c>. A converter
    /// that does not resolve counts as converting.
    /// </summary>
    private static bool ConvertsFromString(Compilation compilation, AttributeData attribute)
    {
        if (attribute.ConstructorArguments is not [var argument])
            return false;

        var converter = argument.Value switch
        {
            INamedTypeSymbol named => named,
            string name => compilation.GetTypeByMetadataName(name.Split(',')[0].Trim()),
            _ => null
        };
        if (converter is null || converter.TypeKind == TypeKind.Error)
            return true;

        return BaseChain(converter).TakeWhile(current => SymbolNames.Type(current) != "System.ComponentModel.TypeConverter")
                                   .Any(current => current.GetMembers("CanConvertFrom").OfType<IMethodSymbol>().Any(method =>
                                       method is { IsOverride: true, Parameters: [_, { Type: var sourceType }] } &&
                                       SymbolNames.Type(sourceType) == "System.Type"));
    }

    /// <summary>A public static <c>bool TryParse(string, out T)</c> or <c>bool TryParse(string, IFormatProvider, out T)</c> on
    /// the type or a base class, or an <c>IParsable&lt;T&gt;</c> implementation, where <c>T</c> is the type itself.</summary>
    private static bool IsParseable(ITypeSymbol type) =>
        type.AllInterfaces.Any(candidate => SymbolNames.Type(candidate.OriginalDefinition) == "System.IParsable<TSelf>" &&
                                            SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[0], type)) ||
        BaseChain(type as INamedTypeSymbol).SelectMany(current => current.GetMembers("TryParse")).OfType<IMethodSymbol>().Any(method =>
            method is { IsStatic: true, DeclaredAccessibility: Accessibility.Public, ReturnType.SpecialType: SpecialType.System_Boolean } &&
            method.Parameters is [{ Type.SpecialType: SpecialType.System_String }, .., { RefKind: RefKind.Out } result] &&
            (method.Parameters.Length == 2 ||
             method.Parameters.Length == 3 && SymbolNames.Type(method.Parameters[1].Type) == "System.IFormatProvider") &&
            SymbolEqualityComparer.Default.Equals(result.Type, type));

    private static bool IsDeclaredIn(Invocation call, SupportedAssemblyVersion range, string containingType) =>
        ExactSymbols.IsType(call.Operation.TargetMethod.ContainingType, range, containingType);

    private sealed record Invocation(Compilation Compilation, SemanticModel Model, InvocationExpressionSyntax Syntax,
                                     IInvocationOperation Operation)
    {
        public string Name => Operation.TargetMethod.Name;
    }

    private static IEnumerable<Invocation> Invocations(RootDiscoveryContext context, IEnumerable<string> names)
    {
        var nameSet = names.ToHashSet(StringComparer.Ordinal);
        foreach (var compilation in context.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                SemanticModel? model = null;
                foreach (var syntax in tree.GetRoot(context.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = syntax.Expression switch
                    {
                        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                        SimpleNameSyntax simple => simple.Identifier.ValueText,
                        _ => null
                    };
                    if (name is null || !nameSet.Contains(name))
                        continue;

                    model ??= compilation.GetSemanticModel(tree);
                    if (model.GetOperation(syntax, context.CancellationToken) is IInvocationOperation operation)
                        yield return new Invocation(compilation, model, syntax, operation);
                }
            }
        }
    }
}
