using System.Runtime.CompilerServices;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class AspNetCoreRootProviderTests
{
    private const string POLICY = "multiplicity=Repeated overlap=MayOverlap scope=scope:Fixture";

    private const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Routing;
        using Microsoft.Extensions.DependencyInjection;

        """;

    private const string Startup = """

        public static class Startup
        {
            public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
            {
                services.AddControllers();
                app.MapControllers();
            }
        }
        """;

    [Fact]
    public void AspNetCore_ControllerBaseDescendant_ActionRoot()
    {
        Run(Case("""
            public sealed class HomeController : ControllerBase
            {
                public string Index(int id) => "home";
            }
            """ + Startup) with
        {
            ExpectedRoots = [$"controller-action HomeController.Index(int) receiver=PerInvocation parameters=[id:RequestData] {POLICY}"]
        });
    }

    [Fact]
    public void AspNetCore_PocoBySuffix_ActionRoot()
    {
        Run(Case("""
            public class Shelfcontroller
            {
                public void Put(string label) { }
            }
            """ + Startup) with
        {
            ExpectedRoots = [$"controller-action Shelfcontroller.Put(string) receiver=PerInvocation parameters=[label:RequestData] {POLICY}"]
        });
    }

    [Fact]
    public void AspNetCore_ControllerAttributeWithoutSuffix_ActionRoot()
    {
        Run(Case("""
            [Controller]
            public class Shelf
            {
                public void Get() { }
            }
            [Controller]
            public abstract class Resource { }
            public class Things : Resource
            {
                public void List() { }
            }
            """ + Startup) with
        {
            ExpectedRoots =
            [
                $"controller-action Shelf.Get() receiver=PerInvocation parameters=[] {POLICY}",
                $"controller-action Things.List() receiver=PerInvocation parameters=[] {POLICY}"
            ]
        });
    }

    [Fact]
    public void AspNetCore_NonController_NoRoots()
    {
        Run(Case("""
            [NonController]
            public class HomeController : ControllerBase
            {
                public void Index() { }
            }
            public class ChildController : HomeController
            {
                public void Child() { }
            }
            """ + Startup));
    }

    [Fact]
    public void AspNetCore_PublicNestedController_NoRoots()
    {
        Run(Case("""
            public static class Features
            {
                public class InnerController : ControllerBase
                {
                    public void Get() { }
                }
            }
            """ + Startup));
    }

    [Fact]
    public void AspNetCore_AbstractGenericOrInternalController_NoRoots()
    {
        Run(Case("""
            public abstract class AbstractController : ControllerBase { public void Get() { } }
            public class GenericController<T> : ControllerBase { public void Get() { } }
            internal class InternalController : ControllerBase { public void Get() { } }
            """ + Startup));
    }

    [Fact]
    public void AspNetCore_IneligibleMethods_NotActions()
    {
        Run(Case("""
            public class HomeController : ControllerBase
            {
                public void Index() { }
                public static void Static() { }
                public void Generic<T>() { }
                [NonAction] public void Helper() { }
                public override string ToString() => "home";
                public int Count { get; set; }
                private void Private() { }
                protected void Protected() { }
                internal void Internal() { }
            }
            """ + Startup) with
        {
            ExpectedRoots = [$"controller-action HomeController.Index() receiver=PerInvocation parameters=[] {POLICY}"]
        });
    }

    [Fact]
    public void AspNetCore_UnrelatedDispose_IsAction()
    {
        Run(Case("""
            public class CleanupController : ControllerBase
            {
                public void Dispose() { }
            }
            public class DisposableController : ControllerBase, IDisposable
            {
                public void Get() { }
                public void Dispose() { }
            }
            """ + Startup) with
        {
            ExpectedRoots =
            [
                $"controller-action CleanupController.Dispose() receiver=PerInvocation parameters=[] {POLICY}",
                $"controller-action DisposableController.Get() receiver=PerInvocation parameters=[] {POLICY}"
            ]
        });
    }

    [Fact]
    public void AspNetCore_InheritedAction_RootOfEachController()
    {
        Run(Case("""
            public abstract class ApiBase : ControllerBase
            {
                public void Ping() { }
            }
            public class OrdersController : ApiBase { }
            public class ItemsController : ApiBase { }
            """ + Startup) with
        {
            ExpectedRoots =
            [
                $"controller-action ApiBase.Ping() receiver=PerInvocation parameters=[] {POLICY}",
                $"controller-action ApiBase.Ping() receiver=PerInvocation parameters=[] {POLICY}"
            ]
        });
    }

    [Fact]
    public void AspNetCore_OverrideWithoutBaseCall_OnlyOverrideRoot()
    {
        Run(Case("""
            public abstract class BaseController : ControllerBase
            {
                public virtual string Get() => "base";
            }
            public class DerivedController : BaseController
            {
                public override string Get() => "derived";
            }
            """ + Startup) with
        {
            ExpectedRoots = [$"controller-action DerivedController.Get() receiver=PerInvocation parameters=[] {POLICY}"]
        });
    }

    [Fact]
    public void AspNetCore_NewMethodHidingBase_OnlyNewRoot()
    {
        Run(Case("""
            public abstract class BaseController : ControllerBase
            {
                public string Get() => "base";
            }
            public class DerivedController : BaseController
            {
                public new string Get() => "derived";
            }
            """ + Startup) with
        {
            ExpectedRoots = [$"controller-action DerivedController.Get() receiver=PerInvocation parameters=[] {POLICY}"]
        });
    }

    [Fact]
    public void AspNetCore_RegisteredButUnmapped_UnsupportedPattern()
    {
        Run(Case("""
            public class HomeController : ControllerBase { public void Index() { } }
            public static class Startup
            {
                public static void Configure(IServiceCollection services) => services.AddControllers();
            }
            """) with
        {
            ExpectedDiagnostics = ["UnsupportedPattern scope:Fixture"]
        });
    }

    [Fact]
    public void AspNetCore_MappedButUnregistered_UnsupportedPattern()
    {
        Run(Case("""
            public class HomeController : ControllerBase { public void Index() { } }
            public static class Startup
            {
                public static void Configure(IEndpointRouteBuilder app) => app.MapDefaultControllerRoute();
            }
            """) with
        {
            ExpectedDiagnostics = ["UnsupportedPattern scope:Fixture"]
        });
    }

    [Fact]
    public void AspNetCore_MvcCoreAndFallbackToController_ActionRoot()
    {
        Run(Case("""
            public class HomeController : ControllerBase { public void Index() { } }
            public static class Startup
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                {
                    services.AddMvcCore();
                    app.MapFallbackToController("Index", "Home");
                }
            }
            """) with
        {
            ExpectedRoots = [$"controller-action HomeController.Index() receiver=PerInvocation parameters=[] {POLICY}"]
        });
    }

    [Fact]
    public void AspNetCore_ApiControllerInference_DiService()
    {
        Run(Case("""
            public sealed class Store { }
            public sealed class Clock { }
            public sealed class Payload { }
            [ApiController]
            public class OrdersController : ControllerBase
            {
                public void Post(Store store, [FromServices] Clock clock, [FromBody] Payload payload, int id, string name) { }
            }
            public class PlainController : ControllerBase
            {
                public void Post(Store store, [FromServices] Clock clock) { }
            }
            public static class Registrations
            {
                public static void Register(IServiceCollection services) => services.AddSingleton<Store>().AddSingleton<Payload>();
            }
            """ + Startup) with
        {
            ExpectedRoots =
            [
                $"controller-action OrdersController.Post(Store, Clock, Payload, int, string) receiver=PerInvocation parameters=[store:DiService,clock:DiService,payload:RequestData,id:RequestData,name:RequestData] {POLICY}",
                $"controller-action PlainController.Post(Store, Clock) receiver=PerInvocation parameters=[store:RequestData,clock:DiService] {POLICY}"
            ]
        });
    }

    [Fact]
    public void AspNetCore_ApiControllerTypeConverterParameter_IsRequestData()
    {
        Run(Case("""
            public class CodeConverter : System.ComponentModel.TypeConverter
            {
                public override bool CanConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);
            }
            public sealed class DerivedCodeConverter : CodeConverter { }
            [System.ComponentModel.TypeConverter(typeof(CodeConverter))]
            public sealed class Code { }
            [System.ComponentModel.TypeConverter(typeof(DerivedCodeConverter))]
            public class OrderIdBase { }
            public sealed class OrderId : OrderIdBase { }
            [System.ComponentModel.TypeConverter("CodeConverter")]
            public sealed class Named { }
            [System.ComponentModel.TypeConverter("Missing.Converter, Missing")]
            public sealed class Unresolved { }
            public sealed class Store { }
            [ApiController]
            public class OrdersController : ControllerBase
            {
                public void Post(Code code, OrderId id, Named named, Unresolved unresolved, Store store) { }
            }
            public static class Registrations
            {
                public static void Register(IServiceCollection services) =>
                    services.AddSingleton<Code>().AddSingleton<OrderId>().AddSingleton<Named>().AddSingleton<Unresolved>().AddSingleton<Store>();
            }
            """ + Startup) with
        {
            ExpectedRoots =
            [
                "controller-action OrdersController.Post(Code, OrderId, Named, Unresolved, Store) receiver=PerInvocation " +
                $"parameters=[code:RequestData,id:RequestData,named:RequestData,unresolved:RequestData,store:DiService] {POLICY}"
            ]
        });
    }

    [Fact]
    public void AspNetCore_ApiControllerTypeConverterWithoutStringConversion_IsDiService()
    {
        Run(Case("""
            public sealed class EmptyConverter : System.ComponentModel.TypeConverter { }
            [System.ComponentModel.TypeConverter(typeof(EmptyConverter))]
            public sealed class Code { }
            [System.ComponentModel.TypeConverter("EmptyConverter")]
            public sealed class Named { }
            [ApiController]
            public class OrdersController : ControllerBase
            {
                public void Post(Code code, Named named) { }
            }
            public static class Registrations
            {
                public static void Register(IServiceCollection services) => services.AddSingleton<Code>().AddSingleton<Named>();
            }
            """ + Startup) with
        {
            ExpectedRoots = [$"controller-action OrdersController.Post(Code, Named) receiver=PerInvocation parameters=[code:DiService,named:DiService] {POLICY}"]
        });
    }

    [Fact]
    public void AspNetCore_ApiControllerTryParseParameter_IsRequestData()
    {
        Run(Case("""
            public sealed class Sku { public static bool TryParse(string value, out Sku result) { result = new(); return true; } }
            public class TagBase
            {
                public static bool TryParse(string value, IFormatProvider? provider, out Tag result) { result = new(); return true; }
            }
            public sealed class Tag : TagBase { }
            public sealed class Slug : IParsable<Slug>
            {
                static Slug IParsable<Slug>.Parse(string s, IFormatProvider? provider) => new();
                static bool IParsable<Slug>.TryParse(string? s, IFormatProvider? provider, out Slug result) { result = new(); return true; }
            }
            public sealed class Store { public static bool TryParse(string value, out int result) { result = 0; return true; } }
            [ApiController]
            public class OrdersController : ControllerBase
            {
                public void Post(Sku sku, Tag tag, Slug slug, Store store) { }
            }
            public static class Registrations
            {
                public static void Register(IServiceCollection services) =>
                    services.AddSingleton<Sku>().AddSingleton<Tag>().AddSingleton<Slug>().AddSingleton<Store>();
            }
            """ + Startup) with
        {
            ExpectedRoots = [$"controller-action OrdersController.Post(Sku, Tag, Slug, Store) receiver=PerInvocation parameters=[sku:RequestData,tag:RequestData,slug:RequestData,store:DiService] {POLICY}"]
        });
    }

    [Fact]
    public void AspNetCore_AssemblyApiController_DiService()
    {
        Run(Case("""
            [assembly: ApiController]
            public sealed class Store { }
            public class OrdersController : ControllerBase
            {
                public void Post(Store store) { }
            }
            public static class Registrations
            {
                public static void Register(IServiceCollection services) => services.AddScoped<Store>();
            }
            """ + Startup) with
        {
            ExpectedRoots = [$"controller-action OrdersController.Post(Store) receiver=PerInvocation parameters=[store:DiService] {POLICY}"]
        });
    }

    [Fact]
    public void AspNetCore_FromKeyedServices_Unsupported()
    {
        Run(Case("""
            public sealed class Store { }
            [ApiController]
            public class OrdersController : ControllerBase
            {
                public void Post([FromKeyedServices("primary")] Store store) { }
            }
            public static class Endpoints
            {
                public static void Configure(IEndpointRouteBuilder app) => app.MapGet("/store", ([FromKeyedServices("primary")] Store store) => "ok");
            }
            """ + Startup) with
        {
            ExpectedRoots =
            [
                $"controller-action OrdersController.Post(Store) receiver=PerInvocation parameters=[store:Unsupported] {POLICY}",
                $"minimal-api Endpoints.Configure(IEndpointRouteBuilder)#map1 receiver=None parameters=[store:Unsupported] {POLICY}"
            ]
        });
    }

    [Fact]
    public void AspNetCore_BindAsyncType_RequestDataInHandlerDiServiceInApiController()
    {
        Run(Case("""
            public sealed class Session
            {
                public static ValueTask<Session?> BindAsync(HttpContext context) => default;
            }
            [ApiController]
            public class SessionController : ControllerBase
            {
                public void Post(Session session) { }
            }
            public static class Endpoints
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                {
                    services.AddSingleton<Session>();
                    app.MapPost("/session", (Session session) => "ok");
                }
            }
            """ + Startup) with
        {
            ExpectedRoots =
            [
                $"controller-action SessionController.Post(Session) receiver=PerInvocation parameters=[session:DiService] {POLICY}",
                $"minimal-api Endpoints.Configure(IServiceCollection, IEndpointRouteBuilder)#map1 receiver=None parameters=[session:RequestData] {POLICY}"
            ]
        });
    }

    [Fact]
    public void AspNetCore_InheritedBindAsync_RequestData()
    {
        Run(Case("""
            public class SessionBase
            {
                public static ValueTask<SessionBase?> BindAsync(HttpContext context) => default;
            }
            public sealed class Session : SessionBase { }
            public static class Endpoints
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                {
                    services.AddSingleton<Session>();
                    app.MapPost("/session", (Session session) => "ok");
                }
            }
            """) with
        {
            ExpectedRoots = [$"minimal-api Endpoints.Configure(IServiceCollection, IEndpointRouteBuilder)#map1 receiver=None parameters=[session:RequestData] {POLICY}"]
        });
    }

    [Fact]
    public void AspNetCore_HandlerParameters_FollowRequestDelegateFactory()
    {
        Run(Case("""
            public sealed class Store { }
            public sealed class Other { }
            public sealed class Query { public int Page { get; set; } }
            public sealed class Parsed { public static bool TryParse(string value, out Parsed result) { result = new(); return true; } }
            public interface ISelfBinding { static ValueTask<object?> BindAsync(HttpContext context) => default; }
            public sealed class Bound : ISelfBinding { }
            public static class Handlers
            {
                public static string Get(HttpContext context, CancellationToken token, int id, Parsed parsed, Bound bound, Store store,
                                         Other other, [AsParameters] Query query, [FromServices] Other forced, [FromQuery] Store fromQuery) => "ok";
            }
            public static class Endpoints
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                {
                    services.AddSingleton<Store>().AddSingleton<Bound>();
                    app.MapGet("/items", Handlers.Get);
                }
            }
            """) with
        {
            ExpectedRoots =
            [
                "minimal-api Handlers.Get(HttpContext, CancellationToken, int, Parsed, Bound, Store, Other, Query, Other, Store) receiver=None " +
                "parameters=[context:RequestData,token:RequestData,id:RequestData,parsed:RequestData,bound:RequestData,store:DiService," +
                $"other:RequestData,query:Unsupported,forced:DiService,fromQuery:RequestData] {POLICY}"
            ]
        });
    }

    [Fact]
    public void AspNetCore_StaticMethodGroup_ReceiverNone()
    {
        var result = Run(Case("""
            public static class Handlers { public static string Get() => "ok"; }
            public static class Endpoints
            {
                public static void Configure(IEndpointRouteBuilder app) => app.MapGet("/", Handlers.Get);
            }
            """) with
        {
            ExpectedRoots = [$"minimal-api Handlers.Get() receiver=None parameters=[] {POLICY}"]
        });

        var root = Assert.Single(result.Roots);
        Assert.Equal("aspnetcore:minimal-api:Fixture:M:Endpoints.Configure(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)#map1", root.StableRootId);
        Assert.Equal("body:Fixture:M:Handlers.Get", root.Entry.BodyKey);
    }

    [Fact]
    public void AspNetCore_ConstructedGenericMethodGroupHandler_IsUnsupported()
    {
        Run(Case("""
            public static class State<T>
            {
                public static T? Value;
                public static string Handle() { Value = default; return "ok"; }
            }
            public static class Endpoints
            {
                public static void Configure(IEndpointRouteBuilder app)
                {
                    app.MapGet("/int", State<int>.Handle);
                    app.MapGet("/string", State<string>.Handle);
                }
            }
            """) with
        {
            ExpectedDiagnostics = ["UnsupportedPattern State<int>.Handle()", "UnsupportedPattern State<string>.Handle()"]
        });
    }

    [Fact]
    public void AspNetCore_InstanceMethodGroup_ReceiverUnbound()
    {
        Run(Case("""
            public sealed class Handlers { public string Get() => "ok"; }
            public static class Endpoints
            {
                public static void Configure(IEndpointRouteBuilder app)
                {
                    var handlers = new Handlers();
                    app.MapGet("/", handlers.Get);
                }
            }
            """) with
        {
            ExpectedRoots = [$"minimal-api Handlers.Get() receiver=Unbound parameters=[] {POLICY}"]
        });
    }

    [Fact]
    public void AspNetCore_LambdaReceiver_FollowsEnclosingMember()
    {
        var result = Run(Case("""
            public sealed class Module
            {
                public void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/instance", () => "instance");
                    app.MapPost("/instance", () => "posted");
                }

                public static void MapStatic(IEndpointRouteBuilder app) => app.MapGet("/static", () => "static");
            }
            """) with
        {
            ExpectedRoots =
            [
                $"minimal-api Module.Map(IEndpointRouteBuilder)#map1 receiver=Unbound parameters=[] {POLICY}",
                $"minimal-api Module.Map(IEndpointRouteBuilder)#map2 receiver=Unbound parameters=[] {POLICY}",
                $"minimal-api Module.MapStatic(IEndpointRouteBuilder)#map1 receiver=None parameters=[] {POLICY}"
            ]
        });

        var second = Assert.Single(result.Roots, root => root.Entry.Symbol.EndsWith("Map(IEndpointRouteBuilder)#map2", StringComparison.Ordinal));
        Assert.Equal("aspnetcore:minimal-api:Fixture:M:Module.Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)#map2", second.StableRootId);
        Assert.Equal("body:Fixture:M:Module.Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)#lambda2", second.Entry.BodyKey);
    }

    [Fact]
    public void AspNetCore_LocalFunction_Root()
    {
        var result = Run(Case("""
            public static class Endpoints
            {
                public static void Configure(IEndpointRouteBuilder app)
                {
                    app.MapDelete("/items/{id}", Remove);

                    static string Remove(int id) => "removed";
                }
            }
            """) with
        {
            ExpectedRoots = [$"minimal-api Endpoints.Remove(int) receiver=None parameters=[id:RequestData] {POLICY}"]
        });

        Assert.Equal("body:Fixture:M:Endpoints.Configure(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)#local:Remove",
                     Assert.Single(result.Roots).Entry.BodyKey);
    }

    [Fact]
    public void AspNetCore_RouteGroup_Root()
    {
        Run(Case("""
            public static class Handlers { public static string Post(string name) => name; }
            public static class Endpoints
            {
                public static void Configure(IEndpointRouteBuilder app)
                {
                    var group = app.MapGroup("/api");
                    group.MapPost("/names", Handlers.Post);
                    group.MapMethods("/names", ["PUT"], Handlers.Post);
                }
            }
            """) with
        {
            ExpectedRoots =
            [
                $"minimal-api Handlers.Post(string) receiver=None parameters=[name:RequestData] {POLICY}",
                $"minimal-api Handlers.Post(string) receiver=None parameters=[name:RequestData] {POLICY}"
            ]
        });
    }

    [Fact]
    public void AspNetCore_HandlerFromVariable_UnresolvedBinding()
    {
        Run(Case("""
            public static class Handlers { public static string Get() => "ok"; }
            public static class Endpoints
            {
                public static void Configure(IEndpointRouteBuilder app)
                {
                    Delegate handler = Handlers.Get;
                    app.MapGet("/variable", handler);
                    app.MapGet("/direct", Handlers.Get);
                }
            }
            """) with
        {
            ExpectedRoots = [$"minimal-api Handlers.Get() receiver=None parameters=[] {POLICY}"],
            ExpectedDiagnostics = ["UnresolvedBinding Endpoints.Configure(IEndpointRouteBuilder)#map1"]
        });
    }

    [Fact]
    public void AspNetCore_MapFallbackRequestDelegate_Root()
    {
        Run(Case("""
            public static class Endpoints
            {
                public static void Configure(IEndpointRouteBuilder app)
                {
                    app.MapFallback(context => Task.CompletedTask);
                    app.MapFallback("/spa/{**path}", () => "index");
                }
            }
            """) with
        {
            ExpectedRoots =
            [
                $"minimal-api Endpoints.Configure(IEndpointRouteBuilder)#map1 receiver=None parameters=[context:RequestData] {POLICY}",
                $"minimal-api Endpoints.Configure(IEndpointRouteBuilder)#map2 receiver=None parameters=[] {POLICY}"
            ]
        });
    }

    [Fact]
    public void AspNetCore_Version7_NotChecked()
    {
        Run(Case("public class HomeController : ControllerBase { public void Index() { } }" + Startup) with
        {
            TargetFramework = "net7.0",
            ExpectedStatus = RootDiscoveryStatus.NotChecked,
            ExpectedDiagnostics =
            [
                "UnsupportedAssemblyVersion Microsoft.AspNetCore.Http.Abstractions",
                "UnsupportedAssemblyVersion Microsoft.AspNetCore.Mvc",
                "UnsupportedAssemblyVersion Microsoft.AspNetCore.Mvc.Core",
                "UnsupportedAssemblyVersion Microsoft.AspNetCore.Routing",
                "UnsupportedAssemblyVersion Microsoft.Extensions.DependencyInjection.Abstractions"
            ]
        });
    }

    [Fact]
    public void AspNetCore_DependencyInjectionAbstractionsVersion7_IsNotChecked()
    {
        Run(Case("public class HomeController : ControllerBase { public void Index() { } }" + Startup) with
        {
            TargetFramework = "net8.0",
            AssemblyVersions = new Dictionary<string, int> { [StubAssemblies.DEPENDENCY_INJECTION_ABSTRACTIONS] = 7 },
            ExpectedStatus = RootDiscoveryStatus.NotChecked,
            ExpectedDiagnostics = ["UnsupportedAssemblyVersion Microsoft.Extensions.DependencyInjection.Abstractions"]
        });
    }

    [Fact]
    public void AspNetCore_NoAspNetCoreReference_CheckedEmpty()
    {
        var result = Run(new ProviderCase(nameof(AspNetCore_NoAspNetCoreReference_CheckedEmpty),
                                          [("Case.cs", "public class HomeController { public void Index() { } }")])
        {
            OmittedAssemblies = [StubAssemblies.MVC, StubAssemblies.MVC_CORE, StubAssemblies.ROUTING, StubAssemblies.HTTP_ABSTRACTIONS]
        });

        Assert.Equal(RootDiscoveryStatus.Checked, result.Status);
    }

    [Fact]
    public void AspNetCore_OverloadsAndSameNamedControllers_DistinctIds()
    {
        var result = Run(Case("""
            namespace A { public class HomeController : ControllerBase { public void Post(int id) { } public void Post(string name) { } } }
            namespace B { public class HomeController : ControllerBase { public void Post(int id) { } } }
            """ + Startup) with
        {
            ExpectedRoots =
            [
                $"controller-action A.HomeController.Post(int) receiver=PerInvocation parameters=[id:RequestData] {POLICY}",
                $"controller-action A.HomeController.Post(string) receiver=PerInvocation parameters=[name:RequestData] {POLICY}",
                $"controller-action B.HomeController.Post(int) receiver=PerInvocation parameters=[id:RequestData] {POLICY}"
            ]
        });

        Assert.Contains(result.Roots, root => root.StableRootId == "aspnetcore:controller-action:Fixture:T:A.HomeController:M:A.HomeController.Post(System.String)");
        Assert.Contains(result.Roots, root => root.StableRootId == "aspnetcore:controller-action:Fixture:T:B.HomeController:M:B.HomeController.Post(System.Int32)");
    }

    [Fact]
    public void AspNetCore_LineShift_StableIds()
    {
        ProviderFixture.AssertStableIds(new AspNetCoreRootProvider(), Case("""
            public class HomeController : ControllerBase { public void Index() { } }
            public static class Endpoints
            {
                public static void Configure(IEndpointRouteBuilder app)
                {
                    app.MapGet("/one", () => "one");
                    app.MapGet("/two", () => "two");
                }
            }
            """ + Startup));
    }

    private static RootDiscoveryResult Run(ProviderCase testCase) => ProviderFixture.Run(new AspNetCoreRootProvider(), testCase);

    private static ProviderCase Case(string source, [CallerMemberName] string name = "") => new(name, [("Case.cs", Usings + source)]);
}
