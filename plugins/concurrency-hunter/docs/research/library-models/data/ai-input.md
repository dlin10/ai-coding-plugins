### 1. `M:System.Linq.Enumerable.All``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})`
Assembly: System.Linq 8.0
Summary: Determines whether all elements of a sequence satisfy a condition.
- param `source`: An System.Collections.Generic.IEnumerable`1 that contains the elements to apply the predicate to.
- param `predicate`: A function to test each element for a condition.
Returns: true if every element of the source sequence passes the test in the specified predicate, or if the sequence is empty; otherwise, false.
Parameter(s) to classify: predicate

### 2. `M:System.Linq.Enumerable.FirstOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})`
Assembly: System.Linq 8.0
Summary: Returns the first element of the sequence that satisfies a condition or a default value if no such element is found.
- param `source`: An System.Collections.Generic.IEnumerable`1 to return an element from.
- param `predicate`: A function to test each element for a condition.
Returns: default(TSource) if source is empty or if no element passes the test specified by predicate; otherwise, the first element in source that passes the test specified by predicate.
Parameter(s) to classify: predicate

### 3. `M:System.Linq.Enumerable.SingleOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})`
Assembly: System.Linq 8.0
Summary: Returns the only element of a sequence that satisfies a specified condition or a default value if no such element exists; this method throws an exception if more than one element satisfies the condition.
- param `source`: An System.Collections.Generic.IEnumerable`1 to return a single element from.
- param `predicate`: A function to test an element for a condition.
Returns: The single element of the input sequence that satisfies the condition, or default(TSource) if no such element is found.
Parameter(s) to classify: predicate

### 4. `M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32})`
Assembly: System.Linq 8.0
Summary: Computes the sum of the sequence of System.Int32 values that are obtained by invoking a transform function on each element of the input sequence.
- param `source`: A sequence of values that are used to calculate a sum.
- param `selector`: A transform function to apply to each element.
Returns: The sum of the projected values.
Parameter(s) to classify: selector

### 5. `M:System.Linq.Enumerable.ToDictionary``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2})`
Assembly: System.Linq 8.0
Summary: Creates a System.Collections.Generic.Dictionary`2 from an System.Collections.Generic.IEnumerable`1 according to specified key selector and element selector functions.
- param `source`: An System.Collections.Generic.IEnumerable`1 to create a System.Collections.Generic.Dictionary`2 from.
- param `keySelector`: A function to extract a key from each element.
- param `elementSelector`: A transform function to produce a result element value from each element.
Returns: A System.Collections.Generic.Dictionary`2 that contains values of type TElement selected from the input sequence.
Parameter(s) to classify: keySelector, elementSelector

### 6. `M:System.Linq.Enumerable.Where``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})`
Assembly: System.Linq 8.0
Summary: Filters a sequence of values based on a predicate.
- param `source`: An System.Collections.Generic.IEnumerable`1 to filter.
- param `predicate`: A function to test each element for a condition.
Returns: An System.Collections.Generic.IEnumerable`1 that contains elements from the input sequence that satisfy the condition.
Parameter(s) to classify: predicate

### 7. `M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})`
Assembly: System.Linq 8.0
Summary: Projects each element of a sequence into a new form.
- param `source`: A sequence of values to invoke a transform function on.
- param `selector`: A transform function to apply to each element.
Returns: An System.Collections.Generic.IEnumerable`1 whose elements are the result of invoking the transform function on each element of source.
Parameter(s) to classify: selector

### 8. `M:System.Linq.Enumerable.SelectMany``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Collections.Generic.IEnumerable{``1}})`
Assembly: System.Linq 8.0
Summary: Projects each element of a sequence to an System.Collections.Generic.IEnumerable`1 and flattens the resulting sequences into one sequence.
- param `source`: A sequence of values to project.
- param `selector`: A transform function to apply to each element.
Returns: An System.Collections.Generic.IEnumerable`1 whose elements are the result of invoking the one-to-many transform function on each element of the input sequence.
Parameter(s) to classify: selector

### 9. `M:System.Linq.Enumerable.OrderBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})`
Assembly: System.Linq 8.0
Summary: Sorts the elements of a sequence in ascending order according to a key.
- param `source`: A sequence of values to order.
- param `keySelector`: A function to extract a key from an element.
Returns: An System.Linq.IOrderedEnumerable`1 whose elements are sorted according to a key.
Parameter(s) to classify: keySelector

### 10. `M:System.Linq.Enumerable.GroupBy``4(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2},System.Func{``1,System.Collections.Generic.IEnumerable{``2},``3})`
Assembly: System.Linq 8.0
Summary: Groups the elements of a sequence according to a specified key selector function and creates a result value from each group and its key. The elements of each group are projected by using a specified function.
- param `source`: An System.Collections.Generic.IEnumerable`1 whose elements to group.
- param `keySelector`: A function to extract the key for each element.
- param `elementSelector`: A function to map each source element to an element in an System.Linq.IGrouping`2.
- param `resultSelector`: A function to create a result value from each group.
Returns: A collection of elements of type TResult where each element represents a projection over a group and its key.
Parameter(s) to classify: keySelector, elementSelector, resultSelector

### 11. `M:System.Security.Claims.ClaimsPrincipal.FindFirst(System.Predicate{System.Security.Claims.Claim})`
Assembly: System.Security.Claims 8.0
Summary: Retrieves the first claim that is matched by the specified predicate.
- param `match`: The function that performs the matching logic.
Returns: The first matching claim or null if no match is found.
Parameter(s) to classify: match

### 12. `M:System.Threading.CancellationToken.Register(System.Action)`
Assembly: System.Private.CoreLib 8.0
Summary: Registers a delegate that will be called when this System.Threading.CancellationToken is canceled.
- param `callback`: The delegate to be executed when the System.Threading.CancellationToken is canceled.
Returns: The System.Threading.CancellationTokenRegistration instance that can be used to unregister the callback.
Parameter(s) to classify: callback

### 13. `M:Polly.Policy.Execute(System.Action)`
Assembly: Polly 7.2.3
Summary: Executes the specified action within the policy.
- param `action`: The action to perform.
Parameter(s) to classify: action

### 14. `M:Polly.AsyncPolicy.ExecuteAsync(System.Func{System.Threading.Tasks.Task})`
Assembly: Polly 7.2.3
Summary: Executes the specified asynchronous action within the policy.
- param `action`: The action to perform.
Parameter(s) to classify: action

### 15. `M:Polly.RetrySyntax.WaitAndRetry(Polly.PolicyBuilder,System.Int32,System.Func{System.Int32,System.TimeSpan},System.Action{System.Exception,System.TimeSpan,System.Int32,Polly.Context})`
Assembly: Polly 7.2.3
Summary: Builds a Polly.Policy that will wait and retry retryCount times calling onRetry on each retry with the raised exception, current sleep duration, retry count, and context data. On each retry, the duration to wait is calculated by calling sleepDurationProvider with the current retry number (1 for first retry, 2 for second etc).
- param `policyBuilder`: The policy builder.
- param `retryCount`: The retry count.
- param `sleepDurationProvider`: The function that provides the duration to wait for for a particular retry attempt.
- param `onRetry`: The action to call on each retry.
Returns: The policy instance.
Parameter(s) to classify: sleepDurationProvider, onRetry

### 16. `M:Polly.AsyncRetrySyntax.WaitAndRetryAsync(Polly.PolicyBuilder,System.Int32,System.Func{System.Int32,System.TimeSpan},System.Action{System.Exception,System.TimeSpan,System.Int32,Polly.Context})`
Assembly: Polly 7.2.3
Summary: Builds an Polly.Retry.AsyncRetryPolicy that will wait and retry retryCount times calling onRetry on each retry with the raised exception, the current sleep duration, retry count, and context data. On each retry, the duration to wait is calculated by calling sleepDurationProvider with the current retry number (1 for first retry, 2 for second etc).
- param `policyBuilder`: The policy builder.
- param `retryCount`: The retry count.
- param `sleepDurationProvider`: The function that provides the duration to wait for for a particular retry attempt.
- param `onRetry`: The action to call on each retry.
Returns: The policy instance.
Parameter(s) to classify: sleepDurationProvider, onRetry

### 17. `M:Polly.AsyncRetrySyntax.WaitAndRetryForeverAsync(Polly.PolicyBuilder,System.Func{System.Int32,System.TimeSpan},System.Action{System.Exception,System.Int32,System.TimeSpan})`
Assembly: Polly 7.2.3
Summary: Builds an Polly.Retry.AsyncRetryPolicy that will wait and retry indefinitely calling onRetry on each retry with the raised exception and retry count. On each retry, the duration to wait is calculated by calling sleepDurationProvider with the current retry number (1 for first retry, 2 for second etc)
- param `policyBuilder`: The policy builder.
- param `sleepDurationProvider`: A function providing the duration to wait before retrying.
- param `onRetry`: The action to call on each retry.
Returns: The policy instance.
Parameter(s) to classify: sleepDurationProvider, onRetry

### 18. `M:Microsoft.EntityFrameworkCore.ExecutionStrategyExtensions.ExecuteAsync(Microsoft.EntityFrameworkCore.Storage.IExecutionStrategy,System.Func{System.Threading.Tasks.Task})`
Assembly: Microsoft.EntityFrameworkCore 8.0.0
Summary: Executes the specified asynchronous operation.
- param `strategy`: The strategy that will be used for the execution.
- param `operation`: A function that returns a started task.
Returns: A task that will run to completion if the original task completes successfully (either the first time or after retrying transient failures). If the task fails with a non-transient error or the retry limit is reached, the returned task will become faulted and the exception must be observed.
Remarks: See Connection resiliency and database retries for more information and examples.
Parameter(s) to classify: operation

### 19. `M:Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.AddDbContext``1(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Microsoft.EntityFrameworkCore.DbContextOptionsBuilder},Microsoft.Extensions.DependencyInjection.ServiceLifetime,Microsoft.Extensions.DependencyInjection.ServiceLifetime)`
Assembly: Microsoft.EntityFrameworkCore 8.0.0
Summary: Registers the given context as a service in the Microsoft.Extensions.DependencyInjection.IServiceCollection.
- param `serviceCollection`: The Microsoft.Extensions.DependencyInjection.IServiceCollection to add services to.
- param `optionsAction`: An optional action to configure the Microsoft.EntityFrameworkCore.DbContextOptions for the context. This provides an alternative to performing configuration of the context by overriding the Microsoft.EntityFrameworkCore.DbContext.OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder) method in your derived context. If an action is supplied here, the Microsoft.EntityFrameworkCore.DbContext.OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder) method will still be run if it has been overridden on the derived context. Microsoft.EntityFrameworkCore.DbContext.OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder) configuration will be applied in addition to configuration performed here. In order for the options to be passed into your context, you need to expose a constructor on your context that takes Microsoft.EntityFrameworkCore.DbContextOptions`1 and passes it to the base constructor of Microsoft.EntityFrameworkCore.DbContext.
- param `contextLifetime`: The lifetime with which to register the DbContext service in the container.
- param `optionsLifetime`: The lifetime with which to register the DbContextOptions service in the container.
Returns: The same service collection so that multiple calls can be chained.
Remarks: Use this method when using dependency injection in your application, such as with ASP.NET Core. For applications that don't use dependency injection, consider creating Microsoft.EntityFrameworkCore.DbContext instances directly with its constructor. The Microsoft.EntityFrameworkCore.DbContext.OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder) method can then be overridden to configure a connection string and other options. Entity Framework Core does not support multiple parallel operations being run on the same Microsoft.EntityFrameworkCore.DbContext instance. This includes both parallel execution of async queries and any explicit concurrent use from multiple threads. Therefore, always await async calls immediately, or use separate DbContext instances for operations that execute in parallel. See Avoiding DbContext threading issues for more information and examples. See Using DbContext with dependency injection for more information and examples.
Parameter(s) to classify: optionsAction

### 20. `M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0})`
Assembly: Google.Protobuf 3.21.9
Summary: Creates a new parser.
- param `factory`: Function to invoke when a new, empty message is required.
Remarks: The factory method is effectively an optimization over using a generic constraint to require a parameterless constructor: delegates are significantly faster to execute.
Parameter(s) to classify: factory

### 21. `M:Google.Protobuf.FieldCodec.ForMessage``1(System.UInt32,Google.Protobuf.MessageParser{``0})`
Assembly: Google.Protobuf 3.21.9
Summary: Retrieves a codec suitable for a message field with the given tag.
- param `tag`: The tag.
- param `parser`: A parser to use for the message type.
Returns: A codec for the given tag.
Parameter(s) to classify: parser

### 22. `M:Microsoft.AspNetCore.Authorization.AuthorizationOptions.AddPolicy(System.String,System.Action{Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder})`
Assembly: Microsoft.AspNetCore.Authorization 8.0
Summary: Add a policy that is built from a delegate with the provided name.
- param `name`: The name of the policy.
- param `configurePolicy`: The delegate that will be used to build the policy.
Parameter(s) to classify: configurePolicy

### 23. `M:Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions.AddPolicy(System.String,System.Action{Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder})`
Assembly: Microsoft.AspNetCore.Cors 8.0
Summary: Adds a new policy.
- param `name`: The name of the policy.
- param `configurePolicy`: A delegate which can use a policy builder to build a policy.
Parameter(s) to classify: configurePolicy

### 24. `M:Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder.SetIsOriginAllowed(System.Func{System.String,System.Boolean})`
Assembly: Microsoft.AspNetCore.Cors 8.0
Summary: Sets the specified isOriginAllowed for the underlying policy.
- param `isOriginAllowed`: The function used by the policy to evaluate if an origin is allowed.
Returns: The current policy builder.
Parameter(s) to classify: isOriginAllowed

### 25. `P:Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions.Predicate`
Assembly: Microsoft.AspNetCore.Diagnostics.HealthChecks 8.0
Summary: Gets or sets a predicate that is used to filter the set of health checks executed.
Remarks: If Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions.Predicate is null, the Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckMiddleware will run all registered health checks - this is the default behavior. To run a subset of health checks, provide a function that filters the set of checks.
Parameter(s) to classify: value

### 26. `P:Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions.ResponseWriter`
Assembly: Microsoft.AspNetCore.Diagnostics.HealthChecks 8.0
Summary: Gets or sets a delegate used to write the response.
Remarks: The default value is a delegate that will write a minimal text/plain response with the value of Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport.Status as a string.
Parameter(s) to classify: value

### 27. `P:Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents.OnMessageReceived`
Assembly: Microsoft.AspNetCore.Authentication.JwtBearer 8.0.0
Summary: Invoked when a protocol message is first received.
Parameter(s) to classify: value

### 28. `M:Microsoft.AspNetCore.Hosting.IWebHostBuilder.ConfigureAppConfiguration(System.Action{Microsoft.AspNetCore.Hosting.WebHostBuilderContext,Microsoft.Extensions.Configuration.IConfigurationBuilder})`
Assembly: Microsoft.AspNetCore.Hosting.Abstractions 8.0
Summary: Adds a delegate for configuring the Microsoft.Extensions.Configuration.IConfigurationBuilder that will construct an Microsoft.Extensions.Configuration.IConfiguration.
- param `configureDelegate`: The delegate for configuring the Microsoft.Extensions.Configuration.IConfigurationBuilder that will be used to construct an Microsoft.Extensions.Configuration.IConfiguration.
Returns: The Microsoft.AspNetCore.Hosting.IWebHostBuilder.
Remarks: The Microsoft.Extensions.Configuration.IConfiguration and Microsoft.Extensions.Logging.ILoggerFactory on the Microsoft.AspNetCore.Hosting.WebHostBuilderContext are uninitialized at this stage. The Microsoft.Extensions.Configuration.IConfigurationBuilder is pre-populated with the settings of the Microsoft.AspNetCore.Hosting.IWebHostBuilder.
Parameter(s) to classify: configureDelegate

### 29. `M:Microsoft.AspNetCore.Hosting.IWebHostBuilder.ConfigureServices(System.Action{Microsoft.Extensions.DependencyInjection.IServiceCollection})`
Assembly: Microsoft.AspNetCore.Hosting.Abstractions 8.0
Summary: Adds a delegate for configuring additional services for the host or web application. This may be called multiple times.
- param `configureServices`: A delegate for configuring the Microsoft.Extensions.DependencyInjection.IServiceCollection.
Returns: The Microsoft.AspNetCore.Hosting.IWebHostBuilder.
Parameter(s) to classify: configureServices

### 30. `M:Microsoft.AspNetCore.Hosting.WebHostBuilderExtensions.ConfigureAppConfiguration(Microsoft.AspNetCore.Hosting.IWebHostBuilder,System.Action{Microsoft.Extensions.Configuration.IConfigurationBuilder})`
Assembly: Microsoft.AspNetCore.Hosting 8.0
Summary: Adds a delegate for configuring the Microsoft.Extensions.Configuration.IConfigurationBuilder that will construct an Microsoft.Extensions.Configuration.IConfiguration.
- param `hostBuilder`: The Microsoft.AspNetCore.Hosting.IWebHostBuilder to configure.
- param `configureDelegate`: The delegate for configuring the Microsoft.Extensions.Configuration.IConfigurationBuilder that will be used to construct an Microsoft.Extensions.Configuration.IConfiguration.
Returns: The Microsoft.AspNetCore.Hosting.IWebHostBuilder.
Remarks: The Microsoft.Extensions.Configuration.IConfiguration and Microsoft.Extensions.Logging.ILoggerFactory on the Microsoft.AspNetCore.Hosting.WebHostBuilderContext are uninitialized at this stage. The Microsoft.Extensions.Configuration.IConfigurationBuilder is pre-populated with the settings of the Microsoft.AspNetCore.Hosting.IWebHostBuilder.
Parameter(s) to classify: configureDelegate

### 31. `M:Microsoft.AspNetCore.Hosting.WebHostBuilderExtensions.ConfigureLogging(Microsoft.AspNetCore.Hosting.IWebHostBuilder,System.Action{Microsoft.AspNetCore.Hosting.WebHostBuilderContext,Microsoft.Extensions.Logging.ILoggingBuilder})`
Assembly: Microsoft.AspNetCore.Hosting 8.0
Summary: Adds a delegate for configuring the provided Microsoft.Extensions.Logging.LoggerFactory. This may be called multiple times.
- param `hostBuilder`: The Microsoft.AspNetCore.Hosting.IWebHostBuilder to configure.
- param `configureLogging`: The delegate that configures the Microsoft.Extensions.Logging.LoggerFactory.
Returns: The Microsoft.AspNetCore.Hosting.IWebHostBuilder.
Parameter(s) to classify: configureLogging

### 32. `M:Microsoft.AspNetCore.Hosting.WebHostBuilderKestrelExtensions.ConfigureKestrel(Microsoft.AspNetCore.Hosting.IWebHostBuilder,System.Action{Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions})`
Assembly: Microsoft.AspNetCore.Server.Kestrel 8.0
Summary: Configures Kestrel options but does not register an IServer. See Microsoft.AspNetCore.Hosting.WebHostBuilderKestrelExtensions.UseKestrel(Microsoft.AspNetCore.Hosting.IWebHostBuilder).
- param `hostBuilder`: The Microsoft.AspNetCore.Hosting.IWebHostBuilder to configure.
- param `options`: A callback to configure Kestrel options.
Returns: The Microsoft.AspNetCore.Hosting.IWebHostBuilder.
Parameter(s) to classify: options

### 33. `M:Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions.Listen(System.Net.IPAddress,System.Int32,System.Action{Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions})`
Assembly: Microsoft.AspNetCore.Server.Kestrel.Core 8.0
Summary: Bind to the given IP address and port. The callback configures endpoint-specific settings.
Parameter(s) to classify: configure

### 34. `M:Microsoft.AspNetCore.Mvc.ControllerBase.Ok(System.Object)`
Assembly: Microsoft.AspNetCore.Mvc.Core 8.0
Summary: Creates an Microsoft.AspNetCore.Mvc.OkObjectResult object that produces an Microsoft.AspNetCore.Http.StatusCodes.Status200OK response.
- param `value`: The content value to format in the entity body.
Returns: The created Microsoft.AspNetCore.Mvc.OkObjectResult for the response.
Parameter(s) to classify: value

### 35. `M:Microsoft.Extensions.DependencyInjection.CorsServiceCollectionExtensions.AddCors(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions})`
Assembly: Microsoft.AspNetCore.Cors 8.0
Summary: Adds cross-origin resource sharing services to the specified Microsoft.Extensions.DependencyInjection.IServiceCollection.
- param `services`: The Microsoft.Extensions.DependencyInjection.IServiceCollection to add services to.
- param `setupAction`: An System.Action`1 to configure the provided Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions.
Returns: The Microsoft.Extensions.DependencyInjection.IServiceCollection so that additional calls can be chained.
Parameter(s) to classify: setupAction

### 36. `M:Microsoft.Extensions.DependencyInjection.PolicyServiceCollectionExtensions.AddAuthorization(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Microsoft.AspNetCore.Authorization.AuthorizationOptions})`
Assembly: Microsoft.AspNetCore.Authorization.Policy 8.0
Summary: Adds authorization policy services to the specified Microsoft.Extensions.DependencyInjection.IServiceCollection.
- param `services`: The Microsoft.Extensions.DependencyInjection.IServiceCollection to add services to.
- param `configure`: An action delegate to configure the provided Microsoft.AspNetCore.Authorization.AuthorizationOptions.
Returns: The Microsoft.Extensions.DependencyInjection.IServiceCollection so that additional calls can be chained.
Parameter(s) to classify: configure

### 37. `M:Microsoft.Extensions.DependencyInjection.JwtBearerExtensions.AddJwtBearer(Microsoft.AspNetCore.Authentication.AuthenticationBuilder,System.Action{Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions})`
Assembly: Microsoft.AspNetCore.Authentication.JwtBearer 8.0.0
Summary: Enables JWT-bearer authentication using the default scheme Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme. JWT bearer authentication performs authentication by extracting and validating a JWT token from the Authorization request header.
- param `builder`: The Microsoft.AspNetCore.Authentication.AuthenticationBuilder.
- param `configureOptions`: A delegate that allows configuring Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions.
Returns: A reference to builder after the operation has completed.
Parameter(s) to classify: configureOptions

### 38. `M:Microsoft.Extensions.DependencyInjection.MvcCoreMvcBuilderExtensions.AddJsonOptions(Microsoft.Extensions.DependencyInjection.IMvcBuilder,System.Action{Microsoft.AspNetCore.Mvc.JsonOptions})`
Assembly: Microsoft.AspNetCore.Mvc.Core 8.0
Summary: Configures Microsoft.AspNetCore.Mvc.JsonOptions for the specified builder. Uses default values from JsonSerializerDefaults.Web.
- param `builder`: The Microsoft.Extensions.DependencyInjection.IMvcBuilder.
- param `configure`: An System.Action to configure the Microsoft.AspNetCore.Mvc.JsonOptions.
Returns: The Microsoft.Extensions.DependencyInjection.IMvcBuilder.
Parameter(s) to classify: configure

### 39. `M:Microsoft.Extensions.DependencyInjection.MvcServiceCollectionExtensions.AddControllers(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Microsoft.AspNetCore.Mvc.MvcOptions})`
Assembly: Microsoft.AspNetCore.Mvc 8.0
Summary: Adds services for controllers to the specified Microsoft.Extensions.DependencyInjection.IServiceCollection. This method will not register services used for views or pages.
- param `services`: The Microsoft.Extensions.DependencyInjection.IServiceCollection to add services to.
- param `configure`: An System.Action`1 to configure the provided Microsoft.AspNetCore.Mvc.MvcOptions.
Returns: An Microsoft.Extensions.DependencyInjection.IMvcBuilder that can be used to further configure the MVC services.
Remarks: This method configures the MVC services for the commonly used features with controllers for an API. This combines the effects of Microsoft.Extensions.DependencyInjection.MvcCoreServiceCollectionExtensions.AddMvcCore(Microsoft.Extensions.DependencyInjection.IServiceCollection), Microsoft.Extensions.DependencyInjection.MvcApiExplorerMvcCoreBuilderExtensions.AddApiExplorer(Microsoft.Extensions.DependencyInjection.IMvcCoreBuilder), Microsoft.Extensions.DependencyInjection.MvcCoreMvcCoreBuilderExtensions.AddAuthorization(Microsoft.Extensions.DependencyInjection.IMvcCoreBuilder), Microsoft.Extensions.DependencyInjection.MvcCorsMvcCoreBuilderExtensions.AddCors(Microsoft.Extensions.DependencyInjection.IMvcCoreBuilder), Microsoft.Extensions.DependencyInjection.MvcDataAnnotationsMvcCoreBuilderExtensions.AddDataAnnotations(Microsoft.Extensions.DependencyInjection.IMvcCoreBuilder), and Microsoft.Extensions.DependencyInjection.MvcCoreMvcCoreBuilderExtensions.AddFormatterMappings(Microsoft.Extensions.DependencyInjection.IMvcCoreBuilder). To add services for controllers with views call Microsoft.Extensions.DependencyInjection.MvcServiceCollectionExtensions.AddControllersWithViews(Microsoft.Extensions.DependencyInjection.IServiceCollection) on the resulting builder. To add services for pages call Microsoft.Extensions.DependencyInjection.MvcServiceCollectionExtensions.AddRazorPages(Microsoft.Extensions.DependencyInjection.IServiceCollection) on the resulting builder.
Parameter(s) to classify: configure

### 40. `M:Microsoft.Extensions.DependencyInjection.GrpcServicesExtensions.AddGrpc(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Grpc.AspNetCore.Server.GrpcServiceOptions})`
Assembly: Grpc.AspNetCore.Server 2.50.0
Summary: Adds gRPC services to the specified Microsoft.Extensions.DependencyInjection.IServiceCollection.
- param `services`: The Microsoft.Extensions.DependencyInjection.IServiceCollection for adding services.
- param `configureOptions`: An System.Action`1 to configure the provided Grpc.AspNetCore.Server.GrpcServiceOptions.
Returns: An Grpc.AspNetCore.Server.IGrpcServerBuilder that can be used to further configure the gRPC services.
Parameter(s) to classify: configureOptions

### 41. `M:Microsoft.Extensions.DependencyInjection.SwaggerGenServiceCollectionExtensions.AddSwaggerGen(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions})`
Assembly: Swashbuckle.AspNetCore.SwaggerGen 6.4.0
Parameter(s) to classify: setupAction

### 42. `M:Microsoft.Extensions.DependencyInjection.IdentityServerServiceCollectionExtensions.AddIdentityServer(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{Duende.IdentityServer.Configuration.IdentityServerOptions})`
Assembly: Duende.IdentityServer 6.2.0
Summary: Adds IdentityServer.
- param `services`: The services.
- param `setupAction`: The setup action.
Parameter(s) to classify: setupAction

### 43. `M:Microsoft.Extensions.DependencyInjection.HealthChecksBuilderDelegateExtensions.AddCheck(Microsoft.Extensions.DependencyInjection.IHealthChecksBuilder,System.String,System.Func{Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult},System.Collections.Generic.IEnumerable{System.String},System.Nullable{System.TimeSpan})`
Assembly: Microsoft.Extensions.Diagnostics.HealthChecks 8.0
Summary: Adds a new health check with the specified name and implementation.
- param `builder`: The Microsoft.Extensions.DependencyInjection.IHealthChecksBuilder.
- param `name`: The name of the health check.
- param `tags`: A list of tags that can be used to filter health checks.
- param `check`: A delegate that provides the health check implementation.
- param `timeout`: An optional System.TimeSpan representing the timeout of the check.
Returns: The Microsoft.Extensions.DependencyInjection.IHealthChecksBuilder.
Parameter(s) to classify: check

### 44. `M:Microsoft.Extensions.Hosting.GenericHostBuilderExtensions.ConfigureWebHostDefaults(Microsoft.Extensions.Hosting.IHostBuilder,System.Action{Microsoft.AspNetCore.Hosting.IWebHostBuilder})`
Assembly: Microsoft.AspNetCore 8.0
Summary: Configures a Microsoft.Extensions.Hosting.IHostBuilder with defaults for hosting a web app. This should be called before application specific configuration to avoid it overwriting provided services, configuration sources, environments, content root, etc.
- param `builder`: The Microsoft.Extensions.Hosting.IHostBuilder instance to configure.
- param `configure`: The configure callback
Returns: A reference to the builder after the operation has completed.
Remarks: The following defaults are applied to the Microsoft.Extensions.Hosting.IHostBuilder: use Kestrel as the web server and configure it using the application's configuration providers configure Microsoft.AspNetCore.Hosting.IWebHostEnvironment.WebRootFileProvider to include static web assets from projects referenced by the entry assembly during development adds the HostFiltering middleware adds the ForwardedHeaders middleware if ASPNETCORE_FORWARDEDHEADERS_ENABLED=true, enable IIS integration
Parameter(s) to classify: configure

### 45. `M:Microsoft.Extensions.Hosting.HostingHostBuilderExtensions.ConfigureLogging(Microsoft.Extensions.Hosting.IHostBuilder,System.Action{Microsoft.Extensions.Hosting.HostBuilderContext,Microsoft.Extensions.Logging.ILoggingBuilder})`
Assembly: Microsoft.Extensions.Hosting 8.0
Summary: Adds a delegate for configuring the provided Microsoft.Extensions.Logging.ILoggingBuilder. This may be called multiple times.
- param `hostBuilder`: The Microsoft.Extensions.Hosting.IHostBuilder to configure.
- param `configureLogging`: The delegate that configures the Microsoft.Extensions.Logging.ILoggingBuilder.
Returns: The same instance of the Microsoft.Extensions.Hosting.IHostBuilder for chaining.
Parameter(s) to classify: configureLogging

### 46. `M:Microsoft.Extensions.Hosting.IHostBuilder.ConfigureAppConfiguration(System.Action{Microsoft.Extensions.Hosting.HostBuilderContext,Microsoft.Extensions.Configuration.IConfigurationBuilder})`
Assembly: Microsoft.Extensions.Hosting.Abstractions 8.0
Summary: Sets up the configuration for the remainder of the build process and application. This can be called multiple times and the results will be additive. The results will be available at Microsoft.Extensions.Hosting.HostBuilderContext.Configuration for subsequent operations, as well as in Microsoft.Extensions.Hosting.IHost.Services.
- param `configureDelegate`: The delegate for configuring the Microsoft.Extensions.Configuration.IConfigurationBuilder that will be used to construct the Microsoft.Extensions.Configuration.IConfiguration for the application.
Returns: The same instance of the Microsoft.Extensions.Hosting.IHostBuilder for chaining.
Parameter(s) to classify: configureDelegate

### 47. `M:Serilog.SerilogWebHostBuilderExtensions.UseSerilog(Microsoft.AspNetCore.Hosting.IWebHostBuilder,System.Action{Microsoft.AspNetCore.Hosting.WebHostBuilderContext,Serilog.LoggerConfiguration},System.Boolean,System.Boolean)`
Assembly: Serilog.AspNetCore 6.1.0-dev-00289
Summary: Sets Serilog as the logging provider.
- param `builder`: The web host builder to configure.
- param `configureLogger`: The delegate for configuring the Serilog.LoggerConfiguration that will be used to construct a Serilog.Core.Logger.
- param `preserveStaticLogger`: Indicates whether to preserve the value of Serilog.Log.Logger.
- param `writeToProviders`: By default, Serilog does not write events to Microsoft.Extensions.Logging.ILoggerProviders registered through the Microsoft.Extensions.Logging API. Normally, equivalent Serilog sinks are used in place of providers. Specify true to write events to all providers.
Returns: The web host builder.
Remarks: A Microsoft.AspNetCore.Hosting.WebHostBuilderContext is supplied so that configuration and hosting information can be used. The logger will be shut down when application services are disposed.
Parameter(s) to classify: configureLogger

### 48. `P:Microsoft.Extensions.Options.IOptions`1.Value`
Assembly: Microsoft.Extensions.Options 8.0
Summary: The default configured TOptions instance
Parameter(s) to classify: (none: the member takes no delegate; classify the call itself)

### 49. `P:System.Reflection.AssemblyName.Name`
Assembly: System.Private.CoreLib 8.0
Summary: Gets or sets the simple name of the assembly. This is usually, but not necessarily, the file name of the manifest file of the assembly, minus its extension.
Returns: The simple name of the assembly.
Parameter(s) to classify: (none: the member takes no delegate; classify the call itself)
