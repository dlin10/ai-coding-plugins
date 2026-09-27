"""Write gold.json (my manual labels) and ai-input.md (the same members without labels, for the AI-from-docs run)."""
import json
import os
import re

HERE = os.path.dirname(__file__)
docs = json.load(open(os.path.join(HERE, "members-docs.json"), encoding="utf-8"))

# key -> (label, delegate parameter names or None for all delegate-typed ones, assembly, version)
GOLD = {
    "All": ("invoke-now", ["predicate"], "System.Linq", "8.0"),
    "FirstOrDefault": ("invoke-now", ["predicate"], "System.Linq", "8.0"),
    "SingleOrDefault": ("invoke-now", ["predicate"], "System.Linq", "8.0"),
    "Sum": ("invoke-now", ["selector"], "System.Linq", "8.0"),
    "ToDictionary": ("invoke-now", ["keySelector", "elementSelector"], "System.Linq", "8.0"),
    "Where": ("iterator", ["predicate"], "System.Linq", "8.0"),
    "Select": ("iterator", ["selector"], "System.Linq", "8.0"),
    "SelectMany": ("iterator", ["selector"], "System.Linq", "8.0"),
    "OrderBy": ("iterator", ["keySelector"], "System.Linq", "8.0"),
    "GroupBy": ("iterator", ["keySelector", "elementSelector", "resultSelector"], "System.Linq", "8.0"),
    "FindFirst": ("invoke-now", ["match"], "System.Security.Claims", "8.0"),
    "Register": ("framework-event", ["callback"], "System.Private.CoreLib", "8.0"),
    "PolicyExecute": ("invoke-now", ["action"], "Polly", "7.2.3"),
    "AsyncPolicyExecuteAsync": ("invoke-now", ["action"], "Polly", "7.2.3"),
    "WaitAndRetry": ("holder", ["sleepDurationProvider", "onRetry"], "Polly", "7.2.3"),
    "WaitAndRetryAsync": ("holder", ["sleepDurationProvider", "onRetry"], "Polly", "7.2.3"),
    "WaitAndRetryForeverAsync": ("holder", ["sleepDurationProvider", "onRetry"], "Polly", "7.2.3"),
    "EfExecuteAsync": ("invoke-now", ["operation"], "Microsoft.EntityFrameworkCore", "8.0.0"),
    "AddDbContext": ("di-registration", ["optionsAction"], "Microsoft.EntityFrameworkCore", "8.0.0"),
    "MessageParserCtor": ("holder", ["factory"], "Google.Protobuf", "3.21.9"),
    "ForMessage": ("holder", ["parser"], "Google.Protobuf", "3.21.9"),
    "AuthAddPolicy": ("invoke-now", ["configurePolicy"], "Microsoft.AspNetCore.Authorization", "8.0"),
    "CorsAddPolicy": ("invoke-now", ["configurePolicy"], "Microsoft.AspNetCore.Cors", "8.0"),
    "SetIsOriginAllowed": ("framework-event", ["isOriginAllowed"], "Microsoft.AspNetCore.Cors", "8.0"),
    "HealthPredicate": ("framework-event", ["value"], "Microsoft.AspNetCore.Diagnostics.HealthChecks", "8.0"),
    "HealthResponseWriter": ("framework-event", ["value"], "Microsoft.AspNetCore.Diagnostics.HealthChecks", "8.0"),
    "OnMessageReceived": ("framework-event", ["value"], "Microsoft.AspNetCore.Authentication.JwtBearer", "8.0.0"),
    "IWebHostBuilderConfigureAppConfiguration": ("startup", ["configureDelegate"], "Microsoft.AspNetCore.Hosting.Abstractions", "8.0"),
    "IWebHostBuilderConfigureServices": ("startup", ["configureServices"], "Microsoft.AspNetCore.Hosting.Abstractions", "8.0"),
    "WebHostConfigureAppConfiguration": ("startup", ["configureDelegate"], "Microsoft.AspNetCore.Hosting", "8.0"),
    "WebHostConfigureLogging": ("startup", ["configureLogging"], "Microsoft.AspNetCore.Hosting", "8.0"),
    "ConfigureKestrel": ("di-registration", ["options"], "Microsoft.AspNetCore.Server.Kestrel", "8.0"),
    "KestrelListen": ("invoke-now", ["configure"], "Microsoft.AspNetCore.Server.Kestrel.Core", "8.0"),
    "Ok": ("non-delegate", ["value"], "Microsoft.AspNetCore.Mvc.Core", "8.0"),
    "AddCors": ("di-registration", ["setupAction"], "Microsoft.AspNetCore.Cors", "8.0"),
    "AddAuthorization": ("di-registration", ["configure"], "Microsoft.AspNetCore.Authorization.Policy", "8.0"),
    "AddJwtBearer": ("di-registration", ["configureOptions"], "Microsoft.AspNetCore.Authentication.JwtBearer", "8.0.0"),
    "AddJsonOptions": ("di-registration", ["configure"], "Microsoft.AspNetCore.Mvc.Core", "8.0"),
    "AddControllers": ("di-registration", ["configure"], "Microsoft.AspNetCore.Mvc", "8.0"),
    "AddGrpc": ("di-registration", ["configureOptions"], "Grpc.AspNetCore.Server", "2.50.0"),
    "AddSwaggerGen": ("di-registration", ["setupAction"], "Swashbuckle.AspNetCore.SwaggerGen", "6.4.0"),
    "AddIdentityServer": ("di-registration", ["setupAction"], "Duende.IdentityServer", "6.2.0"),
    "AddCheck": ("framework-event", ["check"], "Microsoft.Extensions.Diagnostics.HealthChecks", "8.0"),
    "ConfigureWebHostDefaults": ("startup", ["configure"], "Microsoft.AspNetCore", "8.0"),
    "HostConfigureLogging": ("startup", ["configureLogging"], "Microsoft.Extensions.Hosting", "8.0"),
    "IHostBuilderConfigureAppConfiguration": ("startup", ["configureDelegate"], "Microsoft.Extensions.Hosting.Abstractions", "8.0"),
    "UseSerilog": ("startup", ["configureLogger"], "Serilog.AspNetCore", "6.1.0-dev-00289"),
    "OptionsValue": ("non-delegate", [], "Microsoft.Extensions.Options", "8.0"),
    "AssemblyNameName": ("non-delegate", [], "System.Private.CoreLib", "8.0"),
}

SWAGGER_ID = ("M:Microsoft.Extensions.DependencyInjection.SwaggerGenServiceCollectionExtensions.AddSwaggerGen("
              "Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions})")


def pick(key):
    candidates = docs.get(key) or []
    if key == "AddSwaggerGen":
        return {"id": SWAGGER_ID, "summary": "", "params": {}, "returns": "", "remarks": ""}
    # the eShop call uses the overload with the fewest parameters among the matches
    return min(candidates, key=lambda c: c["id"].count(","))


gold, lines = [], []
for number, (key, (label, params, assembly, version)) in enumerate(GOLD.items(), start=1):
    entry = pick(key)
    gold.append({"key": key, "id": entry["id"], "label": label, "delegateParams": params,
                 "assembly": assembly, "version": version})
    lines.append(f"### {number}. `{entry['id']}`")
    lines.append(f"Assembly: {assembly} {version}")
    if entry["summary"]:
        lines.append(f"Summary: {entry['summary']}")
    for name, text in entry["params"].items():
        lines.append(f"- param `{name}`: {text}")
    if entry["returns"]:
        lines.append(f"Returns: {entry['returns']}")
    if entry["remarks"]:
        lines.append(f"Remarks: {entry['remarks']}")
    lines.append(f"Parameter(s) to classify: {', '.join(params) if params else '(none: the member takes no delegate; classify the call itself)'}")
    lines.append("")

json.dump(gold, open(os.path.join(HERE, "gold.json"), "w", encoding="utf-8"), indent=1)
open(os.path.join(HERE, "ai-input.md"), "w", encoding="utf-8").write("\n".join(lines))
print(len(gold), "members")
from collections import Counter
print(Counter(g["label"] for g in gold))
