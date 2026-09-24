using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

/// <summary>In-memory stand-ins for the framework assemblies the built-in providers match. Each stub has the
/// real assembly name and a <c>major.0.0.0</c> version, and mirrors the real public signatures of the types
/// and methods providers resolve, so overload resolution in a fixture picks what it would pick against the
/// shared framework. Bodies are empty: nothing here ever runs.</summary>
public static class StubAssemblies
{
    public const string DEPENDENCY_INJECTION_ABSTRACTIONS = "Microsoft.Extensions.DependencyInjection.Abstractions";
    public const string HOSTING_ABSTRACTIONS = "Microsoft.Extensions.Hosting.Abstractions";
    public const string HTTP_ABSTRACTIONS = "Microsoft.AspNetCore.Http.Abstractions";
    public const string ROUTING = "Microsoft.AspNetCore.Routing";
    public const string MVC_CORE = "Microsoft.AspNetCore.Mvc.Core";
    public const string MVC = "Microsoft.AspNetCore.Mvc";
    public const string GRPC_CORE_API = "Grpc.Core.Api";
    public const string GRPC_ASPNETCORE_SERVER = "Grpc.AspNetCore.Server";

    /// <summary>A stand-in for a library-table package at a version its range excludes. It is not one of <see cref="Names"/>: no
    /// fixture references it unless a case asks for it by name.</summary>
    public const string NEWTONSOFT_JSON = "Newtonsoft.Json";

    /// <summary>The gRPC packages are versioned on their own: their stubs default to <see cref="GRPC_DEFAULT_VERSION"/> and compile
    /// against the framework stubs at <see cref="FixtureOptions.DEFAULT_STUB_VERSION"/>.</summary>
    public const int GRPC_DEFAULT_VERSION = 2;

    // Only the shared runtime's own assemblies: the trusted platform list also holds the test host's dependencies
    // (xunit.core, the test platform, Microsoft.Extensions.Hosting), which would make every fixture project look
    // like a test project and put real framework assemblies beside the stubs.
    private static readonly IReadOnlyList<(string Name, MetadataReference Reference)> PlatformReferences =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? throw new InvalidOperationException(
            "TRUSTED_PLATFORM_ASSEMBLIES is unavailable."))
        .Split(Path.PathSeparator)
        .Where(path => string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(typeof(object).Assembly.Location),
                                     StringComparison.OrdinalIgnoreCase))
        .Select(path => (Path.GetFileNameWithoutExtension(path), (MetadataReference)MetadataReference.CreateFromFile(path)))
        .ToArray();

    private static readonly IReadOnlyDictionary<string, (string[] Dependencies, string Source)> Stubs =
        new Dictionary<string, (string[], string)>(StringComparer.Ordinal)
        {
            [DEPENDENCY_INJECTION_ABSTRACTIONS] = ([], DependencyInjectionSource),
            [HOSTING_ABSTRACTIONS] = ([DEPENDENCY_INJECTION_ABSTRACTIONS], HostingSource),
            [HTTP_ABSTRACTIONS] = ([], HttpSource),
            [ROUTING] = ([HTTP_ABSTRACTIONS], RoutingSource),
            [MVC_CORE] = ([DEPENDENCY_INJECTION_ABSTRACTIONS, HTTP_ABSTRACTIONS, ROUTING], MvcCoreSource),
            [MVC] = ([DEPENDENCY_INJECTION_ABSTRACTIONS, MVC_CORE], MvcSource),
            [GRPC_CORE_API] = ([], GrpcCoreSource),
            [GRPC_ASPNETCORE_SERVER] = ([DEPENDENCY_INJECTION_ABSTRACTIONS, HTTP_ABSTRACTIONS, ROUTING, GRPC_CORE_API], GrpcServerSource),
            [NEWTONSOFT_JSON] = ([], NewtonsoftJsonSource)
        };

    private static readonly ConcurrentDictionary<(string Name, int Major, bool Empty), Lazy<MetadataReference>> Cache = new();

    public static IReadOnlyList<string> Names { get; } = Stubs.Keys.Where(name => name != NEWTONSOFT_JSON).ToArray();

    /// <summary>Whether a stub follows the target framework's major version; the gRPC stubs do not.</summary>
    public static bool IsFramework(string name) => name is not (GRPC_CORE_API or GRPC_ASPNETCORE_SERVER);

    /// <summary>The major version a stub is referenced at when a case names none.</summary>
    public static int DefaultVersion(string name) => IsFramework(name) ? FixtureOptions.DEFAULT_STUB_VERSION : GRPC_DEFAULT_VERSION;

    /// <summary>The runtime's platform assemblies without any that share a name with a stub or with <paramref name="replaced"/>.</summary>
    public static IEnumerable<MetadataReference> PlatformWithout(IEnumerable<string> replaced)
    {
        var names = new HashSet<string>(Names.Concat(replaced), StringComparer.OrdinalIgnoreCase);
        return PlatformReferences.Where(platform => !names.Contains(platform.Name)).Select(platform => platform.Reference);
    }

    /// <summary>The stub for one of <see cref="Names"/>; its stub dependencies are compiled at the same major version.</summary>
    public static MetadataReference Get(string name, int majorVersion)
    {
        if (!Stubs.ContainsKey(name))
            throw new ArgumentException($"No stub is defined for assembly '{name}'.", nameof(name));
        return Cache.GetOrAdd((name, majorVersion, false),
                              key => new Lazy<MetadataReference>(() => Compile(key.Name, key.Major, false))).Value;
    }

    /// <summary>An assembly with this exact name and version and no types, for identity-only cases.</summary>
    public static MetadataReference GetEmpty(string name, int majorVersion) =>
        Cache.GetOrAdd((name, majorVersion, true),
                       key => new Lazy<MetadataReference>(() => Compile(key.Name, key.Major, true))).Value;

    private static MetadataReference Compile(string name, int majorVersion, bool empty)
    {
        var (dependencies, source) = empty ? ([], "") : Stubs[name];
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            name,
            [
                CSharpSyntaxTree.ParseText(source, parseOptions),
                CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{majorVersion}.0.0.0\")]", parseOptions)
            ],
            PlatformWithout([name]).Concat(dependencies.Select(dependency => Get(dependency, IsFramework(dependency) == IsFramework(name)
                                                                                                  ? majorVersion
                                                                                                  : DefaultVersion(dependency)))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        if (!emitted.Success)
        {
            throw new InvalidOperationException($"Stub {name} {majorVersion} does not compile:" + Environment.NewLine +
                string.Join(Environment.NewLine, emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        }

        return AssemblyMetadata.CreateFromImage(stream.ToArray()).GetReference(display: name + ".dll");
    }

    private const string DependencyInjectionSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace Microsoft.Extensions.DependencyInjection
        {
            public enum ServiceLifetime { Singleton, Scoped, Transient }

            public class ServiceDescriptor
            {
                public ServiceDescriptor(Type serviceType, Type implementationType, ServiceLifetime lifetime) { }
                public ServiceDescriptor(Type serviceType, object? serviceKey, Type implementationType, ServiceLifetime lifetime) { }
                public ServiceDescriptor(Type serviceType, object instance) { }
                public ServiceDescriptor(Type serviceType, object? serviceKey, object instance) { }
                public ServiceDescriptor(Type serviceType, Func<IServiceProvider, object> factory, ServiceLifetime lifetime) { }
                public ServiceDescriptor(Type serviceType, object? serviceKey, Func<IServiceProvider, object?, object> factory, ServiceLifetime lifetime) { }
                public ServiceLifetime Lifetime => throw null!;
                public object? ServiceKey => throw null!;
                public Type ServiceType => throw null!;
                public Type? ImplementationType => throw null!;
                public object? ImplementationInstance => throw null!;
                public Func<IServiceProvider, object>? ImplementationFactory => throw null!;
                public bool IsKeyedService => throw null!;
                public static ServiceDescriptor Describe(Type serviceType, Type implementationType, ServiceLifetime lifetime) => throw null!;
                public static ServiceDescriptor Describe(Type serviceType, Func<IServiceProvider, object> implementationFactory, ServiceLifetime lifetime) => throw null!;
                public static ServiceDescriptor Singleton(Type service, Type implementationType) => throw null!;
                public static ServiceDescriptor Singleton<TService, TImplementation>() where TService : class where TImplementation : class, TService => throw null!;
                public static ServiceDescriptor Scoped(Type service, Type implementationType) => throw null!;
                public static ServiceDescriptor Scoped<TService, TImplementation>() where TService : class where TImplementation : class, TService => throw null!;
                public static ServiceDescriptor Transient(Type service, Type implementationType) => throw null!;
                public static ServiceDescriptor Transient<TService, TImplementation>() where TService : class where TImplementation : class, TService => throw null!;
            }

            public interface IServiceCollection : IList<ServiceDescriptor>, ICollection<ServiceDescriptor>, IEnumerable<ServiceDescriptor>, System.Collections.IEnumerable { }

            public interface IServiceScope : IDisposable
            {
                IServiceProvider ServiceProvider { get; }
            }

            public interface IServiceScopeFactory
            {
                IServiceScope CreateScope();
            }

            public readonly struct AsyncServiceScope : IServiceScope, IAsyncDisposable
            {
                public AsyncServiceScope(IServiceScope serviceScope) { }
                public IServiceProvider ServiceProvider => throw null!;
                public void Dispose() { }
                public ValueTask DisposeAsync() => throw null!;
            }

            public interface IKeyedServiceProvider : IServiceProvider
            {
                object? GetKeyedService(Type serviceType, object? serviceKey);
                object GetRequiredKeyedService(Type serviceType, object? serviceKey);
            }

            [AttributeUsage(AttributeTargets.Parameter)]
            public class FromKeyedServicesAttribute : Attribute
            {
                public FromKeyedServicesAttribute(object key) { }
                public object? Key => throw null!;
            }

            [AttributeUsage(AttributeTargets.Parameter)]
            public class ServiceKeyAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.All)]
            public class ActivatorUtilitiesConstructorAttribute : Attribute { }

            public static class ActivatorUtilities
            {
                public static T CreateInstance<T>(IServiceProvider provider, params object[] parameters) => throw null!;
                public static object CreateInstance(IServiceProvider provider, Type instanceType, params object[] parameters) => throw null!;
                public static T GetServiceOrCreateInstance<T>(IServiceProvider provider) => throw null!;
                public static object GetServiceOrCreateInstance(IServiceProvider provider, Type type) => throw null!;
            }

            public static class ServiceProviderServiceExtensions
            {
                public static T? GetService<T>(this IServiceProvider provider) => throw null!;
                public static object GetRequiredService(this IServiceProvider provider, Type serviceType) => throw null!;
                public static T GetRequiredService<T>(this IServiceProvider provider) where T : notnull => throw null!;
                public static IEnumerable<T> GetServices<T>(this IServiceProvider provider) => throw null!;
                public static IEnumerable<object?> GetServices(this IServiceProvider provider, Type serviceType) => throw null!;
                public static IServiceScope CreateScope(this IServiceProvider provider) => throw null!;
                public static AsyncServiceScope CreateAsyncScope(this IServiceProvider provider) => throw null!;
                public static AsyncServiceScope CreateAsyncScope(this IServiceScopeFactory serviceScopeFactory) => throw null!;
            }

            public static class ServiceProviderKeyedServiceExtensions
            {
                public static T? GetKeyedService<T>(this IServiceProvider provider, object? serviceKey) => throw null!;
                public static T GetRequiredKeyedService<T>(this IServiceProvider provider, object? serviceKey) where T : notnull => throw null!;
            }

            public static class ServiceCollectionServiceExtensions
            {
                public static IServiceCollection AddTransient(this IServiceCollection services, Type serviceType, Type implementationType) => throw null!;
                public static IServiceCollection AddTransient(this IServiceCollection services, Type serviceType, Func<IServiceProvider, object> implementationFactory) => throw null!;
                public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection services) where TService : class where TImplementation : class, TService => throw null!;
                public static IServiceCollection AddTransient(this IServiceCollection services, Type serviceType) => throw null!;
                public static IServiceCollection AddTransient<TService>(this IServiceCollection services) where TService : class => throw null!;
                public static IServiceCollection AddTransient<TService>(this IServiceCollection services, Func<IServiceProvider, TService> implementationFactory) where TService : class => throw null!;
                public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection services, Func<IServiceProvider, TImplementation> implementationFactory) where TService : class where TImplementation : class, TService => throw null!;

                public static IServiceCollection AddScoped(this IServiceCollection services, Type serviceType, Type implementationType) => throw null!;
                public static IServiceCollection AddScoped(this IServiceCollection services, Type serviceType, Func<IServiceProvider, object> implementationFactory) => throw null!;
                public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services) where TService : class where TImplementation : class, TService => throw null!;
                public static IServiceCollection AddScoped(this IServiceCollection services, Type serviceType) => throw null!;
                public static IServiceCollection AddScoped<TService>(this IServiceCollection services) where TService : class => throw null!;
                public static IServiceCollection AddScoped<TService>(this IServiceCollection services, Func<IServiceProvider, TService> implementationFactory) where TService : class => throw null!;
                public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services, Func<IServiceProvider, TImplementation> implementationFactory) where TService : class where TImplementation : class, TService => throw null!;

                public static IServiceCollection AddSingleton(this IServiceCollection services, Type serviceType, Type implementationType) => throw null!;
                public static IServiceCollection AddSingleton(this IServiceCollection services, Type serviceType, Func<IServiceProvider, object> implementationFactory) => throw null!;
                public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services) where TService : class where TImplementation : class, TService => throw null!;
                public static IServiceCollection AddSingleton(this IServiceCollection services, Type serviceType) => throw null!;
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection services) where TService : class => throw null!;
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, Func<IServiceProvider, TService> implementationFactory) where TService : class => throw null!;
                public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services, Func<IServiceProvider, TImplementation> implementationFactory) where TService : class where TImplementation : class, TService => throw null!;
                public static IServiceCollection AddSingleton(this IServiceCollection services, Type serviceType, object implementationInstance) => throw null!;
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, TService implementationInstance) where TService : class => throw null!;

                public static IServiceCollection AddKeyedTransient(this IServiceCollection services, Type serviceType, object? serviceKey, Type implementationType) => throw null!;
                public static IServiceCollection AddKeyedTransient(this IServiceCollection services, Type serviceType, object? serviceKey, Func<IServiceProvider, object?, object> implementationFactory) => throw null!;
                public static IServiceCollection AddKeyedTransient<TService, TImplementation>(this IServiceCollection services, object? serviceKey) where TService : class where TImplementation : class, TService => throw null!;
                public static IServiceCollection AddKeyedTransient(this IServiceCollection services, Type serviceType, object? serviceKey) => throw null!;
                public static IServiceCollection AddKeyedTransient<TService>(this IServiceCollection services, object? serviceKey) where TService : class => throw null!;
                public static IServiceCollection AddKeyedTransient<TService>(this IServiceCollection services, object? serviceKey, Func<IServiceProvider, object?, TService> implementationFactory) where TService : class => throw null!;
                public static IServiceCollection AddKeyedTransient<TService, TImplementation>(this IServiceCollection services, object? serviceKey, Func<IServiceProvider, object?, TImplementation> implementationFactory) where TService : class where TImplementation : class, TService => throw null!;

                public static IServiceCollection AddKeyedScoped(this IServiceCollection services, Type serviceType, object? serviceKey, Type implementationType) => throw null!;
                public static IServiceCollection AddKeyedScoped(this IServiceCollection services, Type serviceType, object? serviceKey, Func<IServiceProvider, object?, object> implementationFactory) => throw null!;
                public static IServiceCollection AddKeyedScoped<TService, TImplementation>(this IServiceCollection services, object? serviceKey) where TService : class where TImplementation : class, TService => throw null!;
                public static IServiceCollection AddKeyedScoped(this IServiceCollection services, Type serviceType, object? serviceKey) => throw null!;
                public static IServiceCollection AddKeyedScoped<TService>(this IServiceCollection services, object? serviceKey) where TService : class => throw null!;
                public static IServiceCollection AddKeyedScoped<TService>(this IServiceCollection services, object? serviceKey, Func<IServiceProvider, object?, TService> implementationFactory) where TService : class => throw null!;
                public static IServiceCollection AddKeyedScoped<TService, TImplementation>(this IServiceCollection services, object? serviceKey, Func<IServiceProvider, object?, TImplementation> implementationFactory) where TService : class where TImplementation : class, TService => throw null!;

                public static IServiceCollection AddKeyedSingleton(this IServiceCollection services, Type serviceType, object? serviceKey, Type implementationType) => throw null!;
                public static IServiceCollection AddKeyedSingleton(this IServiceCollection services, Type serviceType, object? serviceKey, Func<IServiceProvider, object?, object> implementationFactory) => throw null!;
                public static IServiceCollection AddKeyedSingleton<TService, TImplementation>(this IServiceCollection services, object? serviceKey) where TService : class where TImplementation : class, TService => throw null!;
                public static IServiceCollection AddKeyedSingleton(this IServiceCollection services, Type serviceType, object? serviceKey) => throw null!;
                public static IServiceCollection AddKeyedSingleton<TService>(this IServiceCollection services, object? serviceKey) where TService : class => throw null!;
                public static IServiceCollection AddKeyedSingleton<TService>(this IServiceCollection services, object? serviceKey, Func<IServiceProvider, object?, TService> implementationFactory) where TService : class => throw null!;
                public static IServiceCollection AddKeyedSingleton<TService, TImplementation>(this IServiceCollection services, object? serviceKey, Func<IServiceProvider, object?, TImplementation> implementationFactory) where TService : class where TImplementation : class, TService => throw null!;
                public static IServiceCollection AddKeyedSingleton(this IServiceCollection services, Type serviceType, object? serviceKey, object implementationInstance) => throw null!;
                public static IServiceCollection AddKeyedSingleton<TService>(this IServiceCollection services, object? serviceKey, TService implementationInstance) where TService : class => throw null!;
            }
        }

        namespace Microsoft.Extensions.DependencyInjection.Extensions
        {
            public static class ServiceCollectionDescriptorExtensions
            {
                public static IServiceCollection Add(this IServiceCollection collection, ServiceDescriptor descriptor) => throw null!;
                public static IServiceCollection Add(this IServiceCollection collection, IEnumerable<ServiceDescriptor> descriptors) => throw null!;
                public static void TryAdd(this IServiceCollection collection, ServiceDescriptor descriptor) { }
                public static void TryAdd(this IServiceCollection collection, IEnumerable<ServiceDescriptor> descriptors) { }

                public static void TryAddTransient(this IServiceCollection collection, Type service) { }
                public static void TryAddTransient(this IServiceCollection collection, Type service, Type implementationType) { }
                public static void TryAddTransient(this IServiceCollection collection, Type service, Func<IServiceProvider, object> implementationFactory) { }
                public static void TryAddTransient<TService>(this IServiceCollection collection) where TService : class { }
                public static void TryAddTransient<TService, TImplementation>(this IServiceCollection collection) where TService : class where TImplementation : class, TService { }
                public static void TryAddTransient<TService>(this IServiceCollection services, Func<IServiceProvider, TService> implementationFactory) where TService : class { }

                public static void TryAddScoped(this IServiceCollection collection, Type service) { }
                public static void TryAddScoped(this IServiceCollection collection, Type service, Type implementationType) { }
                public static void TryAddScoped(this IServiceCollection collection, Type service, Func<IServiceProvider, object> implementationFactory) { }
                public static void TryAddScoped<TService>(this IServiceCollection collection) where TService : class { }
                public static void TryAddScoped<TService, TImplementation>(this IServiceCollection collection) where TService : class where TImplementation : class, TService { }
                public static void TryAddScoped<TService>(this IServiceCollection services, Func<IServiceProvider, TService> implementationFactory) where TService : class { }

                public static void TryAddSingleton(this IServiceCollection collection, Type service) { }
                public static void TryAddSingleton(this IServiceCollection collection, Type service, Type implementationType) { }
                public static void TryAddSingleton(this IServiceCollection collection, Type service, Func<IServiceProvider, object> implementationFactory) { }
                public static void TryAddSingleton<TService>(this IServiceCollection collection) where TService : class { }
                public static void TryAddSingleton<TService, TImplementation>(this IServiceCollection collection) where TService : class where TImplementation : class, TService { }
                public static void TryAddSingleton<TService>(this IServiceCollection collection, TService instance) where TService : class { }
                public static void TryAddSingleton<TService>(this IServiceCollection services, Func<IServiceProvider, TService> implementationFactory) where TService : class { }

                public static void TryAddKeyedTransient(this IServiceCollection collection, Type service, object? serviceKey) { }
                public static void TryAddKeyedTransient(this IServiceCollection collection, Type service, object? serviceKey, Type implementationType) { }
                public static void TryAddKeyedTransient(this IServiceCollection collection, Type service, object? serviceKey, Func<IServiceProvider, object?, object> implementationFactory) { }
                public static void TryAddKeyedTransient<TService>(this IServiceCollection collection, object? serviceKey) where TService : class { }
                public static void TryAddKeyedTransient<TService, TImplementation>(this IServiceCollection collection, object? serviceKey) where TService : class where TImplementation : class, TService { }
                public static void TryAddKeyedTransient<TService>(this IServiceCollection services, object? serviceKey, Func<IServiceProvider, object?, TService> implementationFactory) where TService : class { }

                public static void TryAddKeyedScoped(this IServiceCollection collection, Type service, object? serviceKey) { }
                public static void TryAddKeyedScoped(this IServiceCollection collection, Type service, object? serviceKey, Type implementationType) { }
                public static void TryAddKeyedScoped(this IServiceCollection collection, Type service, object? serviceKey, Func<IServiceProvider, object?, object> implementationFactory) { }
                public static void TryAddKeyedScoped<TService>(this IServiceCollection collection, object? serviceKey) where TService : class { }
                public static void TryAddKeyedScoped<TService, TImplementation>(this IServiceCollection collection, object? serviceKey) where TService : class where TImplementation : class, TService { }
                public static void TryAddKeyedScoped<TService>(this IServiceCollection services, object? serviceKey, Func<IServiceProvider, object?, TService> implementationFactory) where TService : class { }

                public static void TryAddKeyedSingleton(this IServiceCollection collection, Type service, object? serviceKey) { }
                public static void TryAddKeyedSingleton(this IServiceCollection collection, Type service, object? serviceKey, Type implementationType) { }
                public static void TryAddKeyedSingleton(this IServiceCollection collection, Type service, object? serviceKey, Func<IServiceProvider, object?, object> implementationFactory) { }
                public static void TryAddKeyedSingleton<TService>(this IServiceCollection collection, object? serviceKey) where TService : class { }
                public static void TryAddKeyedSingleton<TService, TImplementation>(this IServiceCollection collection, object? serviceKey) where TService : class where TImplementation : class, TService { }
                public static void TryAddKeyedSingleton<TService>(this IServiceCollection collection, object? serviceKey, TService instance) where TService : class { }
                public static void TryAddKeyedSingleton<TService>(this IServiceCollection services, object? serviceKey, Func<IServiceProvider, object?, TService> implementationFactory) where TService : class { }

                public static void TryAddEnumerable(this IServiceCollection services, ServiceDescriptor descriptor) { }
                public static void TryAddEnumerable(this IServiceCollection services, IEnumerable<ServiceDescriptor> descriptors) { }
                public static IServiceCollection Replace(this IServiceCollection collection, ServiceDescriptor descriptor) => throw null!;
                public static IServiceCollection RemoveAll<T>(this IServiceCollection collection) => throw null!;
                public static IServiceCollection RemoveAll(this IServiceCollection collection, Type serviceType) => throw null!;
            }
        }
        """;

    private const string HostingSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.Hosting;

        namespace Microsoft.Extensions.Hosting
        {
            public interface IHostedService
            {
                Task StartAsync(CancellationToken cancellationToken);
                Task StopAsync(CancellationToken cancellationToken);
            }

            public interface IHostedLifecycleService : IHostedService
            {
                Task StartingAsync(CancellationToken cancellationToken);
                Task StartedAsync(CancellationToken cancellationToken);
                Task StoppingAsync(CancellationToken cancellationToken);
                Task StoppedAsync(CancellationToken cancellationToken);
            }

            public abstract class BackgroundService : IHostedService, IDisposable
            {
                public virtual Task? ExecuteTask => throw null!;
                protected abstract Task ExecuteAsync(CancellationToken stoppingToken);
                public virtual Task StartAsync(CancellationToken cancellationToken) => throw null!;
                public virtual Task StopAsync(CancellationToken cancellationToken) => throw null!;
                public virtual void Dispose() { }
            }

            public interface IHostApplicationLifetime
            {
                CancellationToken ApplicationStarted { get; }
                CancellationToken ApplicationStopping { get; }
                CancellationToken ApplicationStopped { get; }
                void StopApplication();
            }

            public interface IHost : IDisposable
            {
                IServiceProvider Services { get; }
                Task StartAsync(CancellationToken cancellationToken = default);
                Task StopAsync(CancellationToken cancellationToken = default);
            }

            public static class HostingAbstractionsHostExtensions
            {
                public static void Start(this IHost host) { }
                public static void Run(this IHost host) { }
                public static Task RunAsync(this IHost host, CancellationToken token = default) => throw null!;
                public static Task StopAsync(this IHost host, TimeSpan timeout) => throw null!;
            }
        }

        namespace Microsoft.Extensions.DependencyInjection
        {
            public static class ServiceCollectionHostedServiceExtensions
            {
                public static IServiceCollection AddHostedService<THostedService>(this IServiceCollection services) where THostedService : class, IHostedService => throw null!;
                public static IServiceCollection AddHostedService<THostedService>(this IServiceCollection services, Func<IServiceProvider, THostedService> implementationFactory) where THostedService : class, IHostedService => throw null!;
            }
        }
        """;

    private const string HttpSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Http;

        namespace Microsoft.AspNetCore.Http
        {
            public abstract class HttpContext
            {
                public abstract IServiceProvider RequestServices { get; set; }
                public abstract CancellationToken RequestAborted { get; set; }
                public abstract void Abort();
            }

            public delegate Task RequestDelegate(HttpContext context);

            public abstract class HttpRequest
            {
                public abstract HttpContext HttpContext { get; }
                public abstract string Method { get; set; }
            }

            public abstract class HttpResponse
            {
                public abstract HttpContext HttpContext { get; }
                public abstract int StatusCode { get; set; }
            }

            public interface IFormFile
            {
                string Name { get; }
                long Length { get; }
            }

            public interface IFormFileCollection : IReadOnlyList<IFormFile>
            {
                IFormFile? GetFile(string name);
            }

            public interface IFormCollection : IEnumerable<KeyValuePair<string, string?>>
            {
                int Count { get; }
                IFormFileCollection Files { get; }
            }

            [AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
            public sealed class AsParametersAttribute : Attribute { }

            public interface IResult
            {
                Task ExecuteAsync(HttpContext httpContext);
            }
        }

        namespace Microsoft.AspNetCore.Http.Metadata
        {
            public interface IFromServiceMetadata { }
            public interface IFromBodyMetadata { bool AllowEmpty => false; }
            public interface IFromQueryMetadata { string? Name { get; } }
            public interface IFromRouteMetadata { string? Name { get; } }
            public interface IFromHeaderMetadata { string? Name { get; } }
            public interface IFromFormMetadata { string? Name { get; } }
        }

        namespace Microsoft.AspNetCore.Builder
        {
            public abstract class EndpointBuilder
            {
                public RequestDelegate? RequestDelegate { get; set; }
                public string? DisplayName { get; set; }
                public IList<object> Metadata => throw null!;
                public IServiceProvider ApplicationServices { get; init; } = null!;
                public abstract Microsoft.AspNetCore.Http.Endpoint Build();
            }

            public interface IEndpointConventionBuilder
            {
                void Add(Action<EndpointBuilder> convention);
                void Finally(Action<EndpointBuilder> finallyConvention) { }
            }

            public interface IApplicationBuilder
            {
                IServiceProvider ApplicationServices { get; set; }
                IDictionary<string, object?> Properties { get; }
                IApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware);
                IApplicationBuilder New();
                RequestDelegate Build();
            }
        }

        namespace Microsoft.AspNetCore.Http
        {
            public class Endpoint
            {
                public Endpoint(RequestDelegate? requestDelegate, EndpointMetadataCollection? metadata, string? displayName) { }
                public string? DisplayName => throw null!;
                public RequestDelegate? RequestDelegate => throw null!;
            }

            public sealed class EndpointMetadataCollection
            {
                public EndpointMetadataCollection(params object[] items) { }
            }
        }
        """;

    private const string RoutingSource = """
        using System;
        using System.Collections.Generic;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Routing;
        using Microsoft.AspNetCore.Routing.Patterns;

        namespace Microsoft.AspNetCore.Routing
        {
            public abstract class EndpointDataSource
            {
                public abstract IReadOnlyList<Endpoint> Endpoints { get; }
            }

            public interface IEndpointRouteBuilder
            {
                IApplicationBuilder CreateApplicationBuilder();
                IServiceProvider ServiceProvider { get; }
                ICollection<EndpointDataSource> DataSources { get; }
            }

            public sealed class RouteGroupBuilder : IEndpointRouteBuilder, IEndpointConventionBuilder
            {
                private RouteGroupBuilder() { }
                IServiceProvider IEndpointRouteBuilder.ServiceProvider => throw null!;
                ICollection<EndpointDataSource> IEndpointRouteBuilder.DataSources => throw null!;
                IApplicationBuilder IEndpointRouteBuilder.CreateApplicationBuilder() => throw null!;
                void IEndpointConventionBuilder.Add(Action<EndpointBuilder> convention) { }
                void IEndpointConventionBuilder.Finally(Action<EndpointBuilder> finallyConvention) { }
            }
        }

        namespace Microsoft.AspNetCore.Routing.Patterns
        {
            public sealed class RoutePattern
            {
                private RoutePattern() { }
                public string? RawText => throw null!;
            }

            public static class RoutePatternFactory
            {
                public static RoutePattern Parse(string pattern) => throw null!;
            }
        }

        namespace Microsoft.AspNetCore.Builder
        {
            public sealed class RouteHandlerBuilder : IEndpointConventionBuilder
            {
                public RouteHandlerBuilder(IEnumerable<IEndpointConventionBuilder> endpointConventionBuilders) { }
                public void Add(Action<EndpointBuilder> convention) { }
                public void Finally(Action<EndpointBuilder> finalConvention) { }
            }

            public static class EndpointRouteBuilderExtensions
            {
                public static RouteGroupBuilder MapGroup(this IEndpointRouteBuilder endpoints, string prefix) => throw null!;
                public static RouteGroupBuilder MapGroup(this IEndpointRouteBuilder endpoints, RoutePattern prefix) => throw null!;

                public static IEndpointConventionBuilder MapGet(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate requestDelegate) => throw null!;
                public static IEndpointConventionBuilder MapPost(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate requestDelegate) => throw null!;
                public static IEndpointConventionBuilder MapPut(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate requestDelegate) => throw null!;
                public static IEndpointConventionBuilder MapDelete(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate requestDelegate) => throw null!;
                public static IEndpointConventionBuilder MapPatch(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate requestDelegate) => throw null!;
                public static IEndpointConventionBuilder MapMethods(this IEndpointRouteBuilder endpoints, string pattern, IEnumerable<string> httpMethods, RequestDelegate requestDelegate) => throw null!;
                public static IEndpointConventionBuilder Map(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate requestDelegate) => throw null!;
                public static IEndpointConventionBuilder Map(this IEndpointRouteBuilder endpoints, RoutePattern pattern, RequestDelegate requestDelegate) => throw null!;

                public static RouteHandlerBuilder MapGet(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => throw null!;
                public static RouteHandlerBuilder MapPost(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => throw null!;
                public static RouteHandlerBuilder MapPut(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => throw null!;
                public static RouteHandlerBuilder MapDelete(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => throw null!;
                public static RouteHandlerBuilder MapPatch(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => throw null!;
                public static RouteHandlerBuilder MapMethods(this IEndpointRouteBuilder endpoints, string pattern, IEnumerable<string> httpMethods, Delegate handler) => throw null!;
                public static RouteHandlerBuilder Map(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => throw null!;
                public static RouteHandlerBuilder Map(this IEndpointRouteBuilder endpoints, RoutePattern pattern, Delegate handler) => throw null!;
                public static RouteHandlerBuilder MapFallback(this IEndpointRouteBuilder endpoints, Delegate handler) => throw null!;
                public static RouteHandlerBuilder MapFallback(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => throw null!;
            }

            public static class FallbackEndpointRouteBuilderExtensions
            {
                public static readonly string DefaultPattern = "{*path:nonfile}";
                public static IEndpointConventionBuilder MapFallback(this IEndpointRouteBuilder endpoints, RequestDelegate requestDelegate) => throw null!;
                public static IEndpointConventionBuilder MapFallback(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate requestDelegate) => throw null!;
            }
        }
        """;

    private const string MvcCoreSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Http.Metadata;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Mvc.Routing;
        using Microsoft.AspNetCore.Routing;

        namespace Microsoft.AspNetCore.Mvc
        {
            [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
            public class ControllerAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
            public sealed class NonControllerAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
            public sealed class NonActionAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
            public class ApiControllerAttribute : ControllerAttribute { }

            [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
            public class FromServicesAttribute : Attribute, IFromServiceMetadata { }

            [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
            public class FromBodyAttribute : Attribute, IFromBodyMetadata { }

            [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
            public class FromQueryAttribute : Attribute, IFromQueryMetadata { public string? Name { get; set; } }

            [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
            public class FromRouteAttribute : Attribute, IFromRouteMetadata { public string? Name { get; set; } }

            [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
            public class FromHeaderAttribute : Attribute, IFromHeaderMetadata { public string? Name { get; set; } }

            [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
            public class FromFormAttribute : Attribute, IFromFormMetadata { public string? Name { get; set; } }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
            public class RouteAttribute : Attribute, IRouteTemplateProvider
            {
                public RouteAttribute(string template) { }
                public string Template => throw null!;
                public int Order { get; set; }
                int? IRouteTemplateProvider.Order => throw null!;
                public string? Name { get; set; }
            }

            public class HttpGetAttribute : HttpMethodAttribute
            {
                public HttpGetAttribute() : base(Array.Empty<string>()) { }
                public HttpGetAttribute(string template) : base(Array.Empty<string>(), template) { }
            }

            public class HttpPostAttribute : HttpMethodAttribute
            {
                public HttpPostAttribute() : base(Array.Empty<string>()) { }
                public HttpPostAttribute(string template) : base(Array.Empty<string>(), template) { }
            }

            public class HttpPutAttribute : HttpMethodAttribute
            {
                public HttpPutAttribute() : base(Array.Empty<string>()) { }
                public HttpPutAttribute(string template) : base(Array.Empty<string>(), template) { }
            }

            public class HttpDeleteAttribute : HttpMethodAttribute
            {
                public HttpDeleteAttribute() : base(Array.Empty<string>()) { }
                public HttpDeleteAttribute(string template) : base(Array.Empty<string>(), template) { }
            }

            public class HttpPatchAttribute : HttpMethodAttribute
            {
                public HttpPatchAttribute() : base(Array.Empty<string>()) { }
                public HttpPatchAttribute(string template) : base(Array.Empty<string>(), template) { }
            }

            public class HttpHeadAttribute : HttpMethodAttribute
            {
                public HttpHeadAttribute() : base(Array.Empty<string>()) { }
                public HttpHeadAttribute(string template) : base(Array.Empty<string>(), template) { }
            }

            public class HttpOptionsAttribute : HttpMethodAttribute
            {
                public HttpOptionsAttribute() : base(Array.Empty<string>()) { }
                public HttpOptionsAttribute(string template) : base(Array.Empty<string>(), template) { }
            }

            [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
            public sealed class AcceptVerbsAttribute : Attribute, IActionHttpMethodProvider, IRouteTemplateProvider
            {
                public AcceptVerbsAttribute(string method) { }
                public AcceptVerbsAttribute(params string[] methods) { }
                public IEnumerable<string> HttpMethods => throw null!;
                public string? Route { get; set; }
                string? IRouteTemplateProvider.Template => throw null!;
                public int Order { get; set; }
                int? IRouteTemplateProvider.Order => throw null!;
                public string? Name { get; set; }
            }

            public class ActionContext { }

            public interface IActionResult
            {
                Task ExecuteResultAsync(ActionContext context);
            }

            public abstract class ActionResult : IActionResult
            {
                public virtual Task ExecuteResultAsync(ActionContext context) => throw null!;
            }

            public class StatusCodeResult : ActionResult
            {
                public StatusCodeResult(int statusCode) { }
                public int StatusCode => throw null!;
            }

            public class OkResult : StatusCodeResult { public OkResult() : base(200) { } }
            public class NoContentResult : StatusCodeResult { public NoContentResult() : base(204) { } }
            public class BadRequestResult : StatusCodeResult { public BadRequestResult() : base(400) { } }
            public class NotFoundResult : StatusCodeResult { public NotFoundResult() : base(404) { } }

            public class ObjectResult : ActionResult
            {
                public ObjectResult(object? value) { }
                public object? Value { get; set; }
            }

            public class OkObjectResult : ObjectResult { public OkObjectResult(object? value) : base(value) { } }

            public sealed class ActionResult<TValue>
            {
                public ActionResult(TValue value) { }
                public ActionResult(ActionResult result) { }
                public ActionResult? Result => throw null!;
                public TValue? Value => throw null!;
                public static implicit operator ActionResult<TValue>(TValue value) => throw null!;
                public static implicit operator ActionResult<TValue>(ActionResult result) => throw null!;
            }

            public class ControllerContext : ActionContext { }

            public class MvcOptions { }

            [Controller]
            public abstract class ControllerBase
            {
                public HttpContext HttpContext => throw null!;
                public ControllerContext ControllerContext { get; set; } = null!;
                [NonAction] public virtual OkResult Ok() => throw null!;
                [NonAction] public virtual OkObjectResult Ok(object? value) => throw null!;
                [NonAction] public virtual NoContentResult NoContent() => throw null!;
                [NonAction] public virtual BadRequestResult BadRequest() => throw null!;
                [NonAction] public virtual NotFoundResult NotFound() => throw null!;
                [NonAction] public virtual StatusCodeResult StatusCode(int statusCode) => throw null!;
            }
        }

        namespace Microsoft.AspNetCore.Mvc.Routing
        {
            public interface IRouteTemplateProvider
            {
                string? Template { get; }
                int? Order { get; }
                string? Name { get; }
            }

            public interface IActionHttpMethodProvider
            {
                IEnumerable<string> HttpMethods { get; }
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
            public abstract class HttpMethodAttribute : Attribute, IActionHttpMethodProvider, IRouteTemplateProvider
            {
                public HttpMethodAttribute(IEnumerable<string> httpMethods) { }
                public HttpMethodAttribute(IEnumerable<string> httpMethods, string? template) { }
                public IEnumerable<string> HttpMethods => throw null!;
                public string? Template => throw null!;
                public int Order { get; set; }
                int? IRouteTemplateProvider.Order => throw null!;
                public string? Name { get; set; }
            }
        }

        namespace Microsoft.AspNetCore.Builder
        {
            public sealed class ControllerActionEndpointConventionBuilder : IEndpointConventionBuilder
            {
                private ControllerActionEndpointConventionBuilder() { }
                public void Add(Action<EndpointBuilder> convention) { }
                public void Finally(Action<EndpointBuilder> finallyConvention) { }
            }

            public static class ControllerEndpointRouteBuilderExtensions
            {
                public static ControllerActionEndpointConventionBuilder MapControllers(this IEndpointRouteBuilder endpoints) => throw null!;
                public static ControllerActionEndpointConventionBuilder MapDefaultControllerRoute(this IEndpointRouteBuilder endpoints) => throw null!;
                public static ControllerActionEndpointConventionBuilder MapControllerRoute(this IEndpointRouteBuilder endpoints, string name, string pattern, object? defaults = null, object? constraints = null, object? dataTokens = null) => throw null!;
                public static ControllerActionEndpointConventionBuilder MapAreaControllerRoute(this IEndpointRouteBuilder endpoints, string name, string areaName, string pattern, object? defaults = null, object? constraints = null, object? dataTokens = null) => throw null!;
                public static IEndpointConventionBuilder MapFallbackToController(this IEndpointRouteBuilder endpoints, string action, string controller) => throw null!;
                public static IEndpointConventionBuilder MapFallbackToController(this IEndpointRouteBuilder endpoints, string pattern, string action, string controller) => throw null!;
                public static IEndpointConventionBuilder MapFallbackToAreaController(this IEndpointRouteBuilder endpoints, string action, string controller, string area) => throw null!;
                public static IEndpointConventionBuilder MapFallbackToAreaController(this IEndpointRouteBuilder endpoints, string pattern, string action, string controller, string area) => throw null!;
            }
        }

        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IMvcCoreBuilder
            {
                IServiceCollection Services { get; }
            }

            public interface IMvcBuilder
            {
                IServiceCollection Services { get; }
            }

            public static class MvcCoreServiceCollectionExtensions
            {
                public static IMvcCoreBuilder AddMvcCore(this IServiceCollection services) => throw null!;
                public static IMvcCoreBuilder AddMvcCore(this IServiceCollection services, Action<MvcOptions> setupAction) => throw null!;
            }

            public static class MvcCoreMvcBuilderExtensions
            {
                public static IMvcBuilder AddControllersAsServices(this IMvcBuilder builder) => throw null!;
            }
        }
        """;

    private const string GrpcCoreSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Grpc.Core
        {
            [AttributeUsage(AttributeTargets.Class)]
            public class BindServiceMethodAttribute : Attribute
            {
                public BindServiceMethodAttribute(Type bindType, string bindMethodName) { }
                public Type BindType => throw null!;
                public string BindMethodName => throw null!;
            }

            public abstract class ServerCallContext
            {
                protected ServerCallContext() { }
                public CancellationToken CancellationToken => throw null!;
            }

            public interface IAsyncStreamReader<out T>
            {
                T Current { get; }
                Task<bool> MoveNext(CancellationToken cancellationToken);
            }

            public interface IAsyncStreamWriter<in T>
            {
                Task WriteAsync(T message);
            }

            public interface IServerStreamWriter<in T> : IAsyncStreamWriter<T>
            {
            }

            public class ServerServiceDefinition
            {
            }

            public abstract class ServiceBinderBase
            {
            }
        }
        """;

    private const string GrpcServerSource = """
        using System;
        using Microsoft.AspNetCore.Routing;

        namespace Microsoft.AspNetCore.Builder
        {
            public sealed class GrpcServiceEndpointConventionBuilder : IEndpointConventionBuilder
            {
                private GrpcServiceEndpointConventionBuilder() { }
                public void Add(Action<EndpointBuilder> convention) { }
                public void Finally(Action<EndpointBuilder> finallyConvention) { }
            }

            public static class GrpcEndpointRouteBuilderExtensions
            {
                public static GrpcServiceEndpointConventionBuilder MapGrpcService<TService>(this IEndpointRouteBuilder builder) where TService : class => throw null!;
            }
        }

        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IGrpcServerBuilder
            {
                IServiceCollection Services { get; }
            }

            public static class GrpcServicesExtensions
            {
                public static IGrpcServerBuilder AddGrpc(this IServiceCollection services) => throw null!;
            }
        }
        """;

    private const string NewtonsoftJsonSource = """
        namespace Newtonsoft.Json
        {
            public static class JsonConvert
            {
                public static string SerializeObject(object? value) => throw null!;
            }
        }
        """;

    private const string MvcSource = """
        using System;
        using Microsoft.AspNetCore.Mvc;

        namespace Microsoft.Extensions.DependencyInjection
        {
            public static class MvcServiceCollectionExtensions
            {
                public static IMvcBuilder AddMvc(this IServiceCollection services) => throw null!;
                public static IMvcBuilder AddMvc(this IServiceCollection services, Action<MvcOptions> setupAction) => throw null!;
                public static IMvcBuilder AddControllers(this IServiceCollection services) => throw null!;
                public static IMvcBuilder AddControllers(this IServiceCollection services, Action<MvcOptions>? configure) => throw null!;
                public static IMvcBuilder AddControllersWithViews(this IServiceCollection services) => throw null!;
                public static IMvcBuilder AddControllersWithViews(this IServiceCollection services, Action<MvcOptions>? configure) => throw null!;
            }
        }
        """;
}
