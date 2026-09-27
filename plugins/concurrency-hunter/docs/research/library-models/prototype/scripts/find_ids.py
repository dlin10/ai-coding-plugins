"""Find the DocumentationCommentIds and XML docs of the eShop gap members in the reference/package XML doc files."""
import glob
import json
import os
import re
import xml.etree.ElementTree as ET

HOME = os.path.expanduser("~")
PKG = os.path.join(HOME, ".nuget", "packages")
NETCORE = r"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\8.0.31\ref\net8.0"
ASPNET = r"C:\Program Files\dotnet\packs\Microsoft.AspNetCore.App.Ref\8.0.31\ref\net8.0"

XML_DIRS = [
    NETCORE,
    ASPNET,
    os.path.join(PKG, "polly", "7.2.3", "lib", "netstandard2.0"),
    os.path.join(PKG, "google.protobuf", "3.21.9", "lib", "net5.0"),
    os.path.join(PKG, "serilog.aspnetcore", "6.1.0-dev-00289", "lib", "net5.0"),
    os.path.join(PKG, "swashbuckle.aspnetcore.swaggergen", "6.4.0", "lib", "net6.0"),
    os.path.join(PKG, "microsoft.aspnetcore.authentication.jwtbearer", "8.0.0", "lib", "net8.0"),
    os.path.join(PKG, "grpc.aspnetcore.server", "2.50.0", "lib", "net7.0"),
    os.path.join(PKG, "microsoft.entityframeworkcore", "8.0.0", "lib", "net8.0"),
    os.path.join(PKG, "duende.identityserver", "6.2.0", "lib", "net7.0"),
]

# (key, id prefix up to the opening parenthesis, required substrings of the parameter list)
MEMBERS = [
    ("All", "M:System.Linq.Enumerable.All``1", []),
    ("FirstOrDefault", "M:System.Linq.Enumerable.FirstOrDefault``1", ["System.Func{``0,System.Boolean}"]),
    ("SingleOrDefault", "M:System.Linq.Enumerable.SingleOrDefault``1", ["System.Func{``0,System.Boolean}"]),
    ("Sum", "M:System.Linq.Enumerable.Sum``1", ["System.Func{``0,System.Int32}"]),
    ("ToDictionary", "M:System.Linq.Enumerable.ToDictionary``3", ["System.Func{``0,``1}", "System.Func{``0,``2}"]),
    ("Where", "M:System.Linq.Enumerable.Where``1", ["System.Func{``0,System.Boolean}"]),
    ("Select", "M:System.Linq.Enumerable.Select``2", ["System.Func{``0,``1}"]),
    ("SelectMany", "M:System.Linq.Enumerable.SelectMany``2", ["System.Func{``0,System.Collections.Generic.IEnumerable{``1}}"]),
    ("OrderBy", "M:System.Linq.Enumerable.OrderBy``2", ["System.Func{``0,``1}"]),
    ("GroupBy", "M:System.Linq.Enumerable.GroupBy``4", ["System.Func{``0,``1}", "System.Func{``0,``2}", "System.Func{``1,System.Collections.Generic.IEnumerable{``2},``3}"]),
    ("FindFirst", "M:System.Security.Claims.ClaimsPrincipal.FindFirst", ["System.Predicate"]),
    ("Register", "M:System.Threading.CancellationToken.Register", ["System.Action"]),
    ("PolicyExecute", "M:Polly.Policy.Execute", ["System.Action"]),
    ("AsyncPolicyExecuteAsync", "M:Polly.AsyncPolicy.ExecuteAsync", ["System.Func{System.Threading.Tasks.Task}"]),
    ("WaitAndRetry", "M:Polly.RetrySyntax.WaitAndRetry", ["Polly.PolicyBuilder,System.Int32,System.Func{System.Int32,System.TimeSpan},System.Action{System.Exception,System.TimeSpan,System.Int32,Polly.Context}"]),
    ("WaitAndRetryAsync", "M:Polly.AsyncRetrySyntax.WaitAndRetryAsync", ["Polly.PolicyBuilder,System.Int32,System.Func{System.Int32,System.TimeSpan},System.Action{System.Exception,System.TimeSpan,System.Int32,Polly.Context}"]),
    ("WaitAndRetryForeverAsync", "M:Polly.AsyncRetrySyntax.WaitAndRetryForeverAsync", ["Polly.PolicyBuilder,System.Func{System.Int32,System.TimeSpan},System.Action{System.Exception,System.Int32,System.TimeSpan}"]),
    ("EfExecuteAsync", "M:Microsoft.EntityFrameworkCore.ExecutionStrategyExtensions.ExecuteAsync", ["Microsoft.EntityFrameworkCore.Storage.IExecutionStrategy,System.Func{System.Threading.Tasks.Task}"]),
    ("AddDbContext", "M:Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.AddDbContext``1", ["System.Action{Microsoft.EntityFrameworkCore.DbContextOptionsBuilder},Microsoft.Extensions.DependencyInjection.ServiceLifetime,Microsoft.Extensions.DependencyInjection.ServiceLifetime"]),
    ("MessageParserCtor", "M:Google.Protobuf.MessageParser`1.#ctor", ["System.Func{`0}"]),
    ("ForMessage", "M:Google.Protobuf.FieldCodec.ForMessage``1", ["System.UInt32,Google.Protobuf.MessageParser{``0}"]),
    ("AuthAddPolicy", "M:Microsoft.AspNetCore.Authorization.AuthorizationOptions.AddPolicy", ["System.Action"]),
    ("CorsAddPolicy", "M:Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions.AddPolicy", ["System.Action"]),
    ("SetIsOriginAllowed", "M:Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder.SetIsOriginAllowed", []),
    ("HealthPredicate", "P:Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions.Predicate", []),
    ("HealthResponseWriter", "P:Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions.ResponseWriter", []),
    ("OnMessageReceived", "P:Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents.OnMessageReceived", []),
    ("IWebHostBuilderConfigureAppConfiguration", "M:Microsoft.AspNetCore.Hosting.IWebHostBuilder.ConfigureAppConfiguration", []),
    ("IWebHostBuilderConfigureServices", "M:Microsoft.AspNetCore.Hosting.IWebHostBuilder.ConfigureServices", ["System.Action{Microsoft.Extensions.DependencyInjection.IServiceCollection}"]),
    ("WebHostConfigureAppConfiguration", "M:Microsoft.AspNetCore.Hosting.WebHostBuilderExtensions.ConfigureAppConfiguration", ["System.Action{Microsoft.Extensions.Configuration.IConfigurationBuilder}"]),
    ("WebHostConfigureLogging", "M:Microsoft.AspNetCore.Hosting.WebHostBuilderExtensions.ConfigureLogging", ["System.Action{Microsoft.AspNetCore.Hosting.WebHostBuilderContext,Microsoft.Extensions.Logging.ILoggingBuilder}"]),
    ("ConfigureKestrel", "M:Microsoft.AspNetCore.Hosting.WebHostBuilderKestrelExtensions.ConfigureKestrel", ["System.Action{Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions}"]),
    ("KestrelListen", "M:Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions.Listen", ["System.Net.IPAddress,System.Int32,System.Action"]),
    ("Ok", "M:Microsoft.AspNetCore.Mvc.ControllerBase.Ok", ["System.Object"]),
    ("AddCors", "M:Microsoft.Extensions.DependencyInjection.CorsServiceCollectionExtensions.AddCors", ["System.Action"]),
    ("AddAuthorization", "M:Microsoft.Extensions.DependencyInjection.PolicyServiceCollectionExtensions.AddAuthorization", ["System.Action"]),
    ("AddJwtBearer", "M:Microsoft.Extensions.DependencyInjection.JwtBearerExtensions.AddJwtBearer", ["Microsoft.AspNetCore.Authentication.AuthenticationBuilder,System.Action"]),
    ("AddJsonOptions", "M:Microsoft.Extensions.DependencyInjection.MvcCoreMvcBuilderExtensions.AddJsonOptions", []),
    ("AddControllers", "M:Microsoft.Extensions.DependencyInjection.MvcServiceCollectionExtensions.AddControllers", ["System.Action"]),
    ("AddGrpc", "M:Microsoft.Extensions.DependencyInjection.GrpcServicesExtensions.AddGrpc", ["System.Action"]),
    ("AddSwaggerGen", "M:Microsoft.Extensions.DependencyInjection.SwaggerGenServiceCollectionExtensions.AddSwaggerGen", ["System.Action"]),
    ("AddIdentityServer", "M:Microsoft.Extensions.DependencyInjection.IdentityServerServiceCollectionExtensions.AddIdentityServer", ["System.Action"]),
    ("AddCheck", "M:Microsoft.Extensions.DependencyInjection.HealthChecksBuilderDelegateExtensions.AddCheck", ["System.Func{Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult},System.Collections.Generic.IEnumerable{System.String},System.Nullable{System.TimeSpan}"]),
    ("ConfigureWebHostDefaults", "M:Microsoft.Extensions.Hosting.GenericHostBuilderExtensions.ConfigureWebHostDefaults", ["Microsoft.Extensions.Hosting.IHostBuilder,System.Action{Microsoft.AspNetCore.Hosting.IWebHostBuilder}"]),
    ("HostConfigureLogging", "M:Microsoft.Extensions.Hosting.HostingHostBuilderExtensions.ConfigureLogging", ["System.Action{Microsoft.Extensions.Hosting.HostBuilderContext,Microsoft.Extensions.Logging.ILoggingBuilder}"]),
    ("IHostBuilderConfigureAppConfiguration", "M:Microsoft.Extensions.Hosting.IHostBuilder.ConfigureAppConfiguration", []),
    ("UseSerilog", "M:Serilog.SerilogWebHostBuilderExtensions.UseSerilog", ["System.Action{Microsoft.AspNetCore.Hosting.WebHostBuilderContext,Serilog.LoggerConfiguration},System.Boolean,System.Boolean"]),
    ("OptionsValue", "P:Microsoft.Extensions.Options.IOptions`1.Value", []),
    ("AssemblyNameName", "P:System.Reflection.AssemblyName.Name", []),
]


def render(element):
    """Element text with <see cref>, <paramref>, <typeparamref> and <see langword> kept as their names."""
    parts = []
    if element.tag in ("see", "seealso") and not (element.text or "").strip():
        reference = element.get("cref") or element.get("langword") or element.get("href") or ""
        parts.append(reference.split(":", 1)[-1])
    elif element.tag in ("paramref", "typeparamref"):
        parts.append(element.get("name", ""))
    else:
        parts.append(element.text or "")
        for child in element:
            parts.append(render(child))
            parts.append(child.tail or "")
        return "".join(parts)
    return "".join(parts)


def text(element):
    return re.sub(r"\s+", " ", render(element)).strip() if element is not None else ""


def load():
    docs = {}
    for directory in XML_DIRS:
        for path in glob.glob(os.path.join(directory, "*.xml")):
            try:
                root = ET.parse(path).getroot()
            except ET.ParseError:
                continue
            for member in root.iter("member"):
                name = member.get("name")
                if name:
                    docs[name] = {
                        "file": os.path.basename(path),
                        "summary": text(member.find("summary")),
                        "params": {p.get("name"): text(p) for p in member.findall("param")},
                        "returns": text(member.find("returns")),
                        "remarks": text(member.find("remarks")),
                    }
    return docs


def main():
    docs = load()
    result = {}
    for key, prefix, needles in MEMBERS:
        hits = [name for name in docs
                if (name == prefix or name.startswith(prefix + "(") or name.startswith(prefix + "~"))
                and all(needle in name for needle in needles)]
        print(f"{key}: {len(hits)}")
        for hit in hits:
            print("    ", hit)
        result[key] = [{"id": hit, **docs[hit]} for hit in hits]
    with open(os.path.join(os.path.dirname(__file__), "members-docs.json"), "w", encoding="utf-8") as out:
        json.dump(result, out, indent=1, ensure_ascii=False)


main()
