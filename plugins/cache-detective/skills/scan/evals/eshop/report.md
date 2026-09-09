# Cache Detective report

Generated: `2026-09-05T02:58:00Z`  
Repository: `C:\Temp\cd-g5\eshop` (a scratch clone of `C:\Dev\eShopOnContainers` at `7e5ae7b` with `skills/scan/evals/eshop/planted-webmvc-catalog-cache.patch` applied; the working checkout was not touched)  
Solutions requested: `src/eShopOnContainers-ServicesAndWebApps.sln`  
Solutions indexed: `src/eShopOnContainers-ServicesAndWebApps.sln` (355 vertices, 436 edges, 42 events, 24 external sources before annotations)  
Database: none configured — the step was skipped, not failed (see Needs checking)

Configuration used unchanged from `.cache-detective/workspace.json`: two event recognizers — eShop's `IEventBus.Publish` / `IIntegrationEventHandler<T>.Handle`, and the Ordering outbox `IOrderingIntegrationEventService.AddAndSaveEventAsync` as a publisher-only recognizer. Default budget 60 s. Static-analyst budget 50, fully spent.

## Summary

- Visible findings: 4
- Suppressed findings: 0
- Confirmed: 0
- Likely: 4
- Needs checking: 3 groups (database step skipped, 26 solution diagnostics, 10 unresolved rows)
- Solutions with load or indexing failures: 0 (the solution indexed; every diagnostic is per project and listed below)
- Database objects indexed: 0

Before annotations the scan reported one finding, `EXTERNAL_NO_TTL` on `catalog:items:{page}:{take}:{brand}:{type}` at confidence `unknown`, because its only dependency was an HTTP call whose endpoint was unresolved. Annotation `1` joined that call to `CatalogController.ItemsAsync`; the external source stopped being a leaf, that finding disappeared, and the four findings below appeared. All four belong under Likely findings because every chain crosses the annotated `serves` edge.

## Confirmed findings

None.

## Likely findings

### CROSS_SERVICE_GAP — `Catalog.API/CatalogController.UpdateProductAsync(CatalogItem)`

- Finding: `f:2`
- Confidence: `likely`
- Solution: `src/eShopOnContainers-ServicesAndWebApps.sln`
- TTL: none
- Budget: 60 s

Chain:

    writes    Catalog.API/Controllers/CatalogController.cs:223            UpdateProductAsync -> dbo.Catalog (confirmed)
    reads     Catalog.API/Controllers/CatalogController.cs:40             ItemsAsync <- dbo.Catalog (confirmed)
    serves    WebMVC/Services/CatalogService.cs:36 -> Catalog.API/Controllers/CatalogController.cs:26   GET {purchaseurl}/c/api/{v}/catalog/items{?} -> ItemsAsync (likely, annotation 1)
    reads     WebMVC/Services/CatalogService.cs:36                        GetCatalogItems -> GET {purchaseurl}/c/api/{v}/catalog/items{?} (confirmed)
    caches    WebMVC/Services/CatalogService.cs:43                        catalog:items:{page}:{take}:{brand}:{type} (no TTL) (confirmed)

Event chain (the handler at the head publishes, and no consumer invalidates the key):

    publishes Catalog.API/IntegrationEvents/CatalogIntegrationEventService.cs:32 -> Catalog.API...ProductPriceChangedIntegrationEvent (confirmed)
    consumes  Basket.API/IntegrationEvents/EventHandling/ProductPriceChangedIntegrationEventHandler.cs:3 <- Basket.API...ProductPriceChangedIntegrationEvent (likely: contract duplicated across services)
    consumes  Webhooks.API/IntegrationEvents/ProductPriceChangedIntegrationEventHandler.cs:3 <- Webhooks.API...ProductPriceChangedIntegrationEvent (likely: contract duplicated across services)
    publishes Catalog.API/IntegrationEvents/CatalogIntegrationEventService.cs:32 -> Catalog.API...OrderStockRejectedIntegrationEvent (confirmed)
    consumes  Ordering.API/Application/IntegrationEvents/EventHandling/OrderStockRejectedIntegrationEventHandler.cs:2 <- Ordering.API...OrderStockRejectedIntegrationEvent (likely: contract duplicated across services)
    publishes Catalog.API/IntegrationEvents/CatalogIntegrationEventService.cs:32 -> Catalog.API...OrderStockConfirmedIntegrationEvent (confirmed)
    consumes  Ordering.API/Application/IntegrationEvents/EventHandling/OrderStockConfirmedIntegrationEventHandler.cs:3 <- Ordering.API...OrderStockConfirmedIntegrationEvent (likely: contract duplicated across services)

Assumption: annotation `1` — the WebMVC `ICatalogService` client's base address is `PurchaseUrl` + `/c/api/v1/catalog/` and the gateway routes `/c/` to Catalog.API, so the unfiltered `items` request reaches `CatalogController.ItemsAsync`; the filtered branches of the same URL reach `ItemsByTypeIdAndBrandIdAsync` / `ItemsByBrandIdAsync`, which read the same table.  
Reason: the `serves` edge is an annotation and the event hops join contracts duplicated per service by short name; nothing on the chain is proven end to end. The event chain lists every type recovered through the shared publish helper `PublishThroughEventBusAsync`, so the two stock events raised by `OrderStatusChangedToAwaitingValidationIntegrationEventHandler` appear beside the price-changed event that `UpdateProductAsync` itself raises.

invalidation: not found in Basket.API, Catalog.API, EventBus, IntegrationEventLogEF, Ordering.API, Ordering.Infrastructure, Webhooks.API

### UNGUARDED_WRITE — `Catalog.API/CatalogController.CreateProductAsync(CatalogItem)`

- Finding: `f:3`
- Confidence: `likely`
- Solution: `src/eShopOnContainers-ServicesAndWebApps.sln`
- TTL: none
- Budget: 60 s

Chain:

    writes    Catalog.API/Controllers/CatalogController.cs:260            CreateProductAsync -> dbo.Catalog (confirmed)
    reads     Catalog.API/Controllers/CatalogController.cs:40             ItemsAsync <- dbo.Catalog (confirmed)
    serves    WebMVC/Services/CatalogService.cs:36 -> Catalog.API/Controllers/CatalogController.cs:26   GET {purchaseurl}/c/api/{v}/catalog/items{?} -> ItemsAsync (likely, annotation 1)
    reads     WebMVC/Services/CatalogService.cs:36                        GetCatalogItems -> GET {purchaseurl}/c/api/{v}/catalog/items{?} (confirmed)
    caches    WebMVC/Services/CatalogService.cs:43                        catalog:items:{page}:{take}:{brand}:{type} (no TTL) (confirmed)

Assumption: annotation `1` (as above).  
Reason: the `serves` edge is an annotation. This handler publishes no event, so the rule is `UNGUARDED_WRITE`.

invalidation: not found in Catalog.API

### UNGUARDED_WRITE — `Catalog.API/CatalogController.DeleteProductAsync(int)`

- Finding: `f:4`
- Confidence: `likely`
- Solution: `src/eShopOnContainers-ServicesAndWebApps.sln`
- TTL: none
- Budget: 60 s

Chain:

    writes    Catalog.API/Controllers/CatalogController.cs:281            DeleteProductAsync -> dbo.Catalog (confirmed)
    reads     Catalog.API/Controllers/CatalogController.cs:40             ItemsAsync <- dbo.Catalog (confirmed)
    serves    WebMVC/Services/CatalogService.cs:36 -> Catalog.API/Controllers/CatalogController.cs:26   GET {purchaseurl}/c/api/{v}/catalog/items{?} -> ItemsAsync (likely, annotation 1)
    reads     WebMVC/Services/CatalogService.cs:36                        GetCatalogItems -> GET {purchaseurl}/c/api/{v}/catalog/items{?} (confirmed)
    caches    WebMVC/Services/CatalogService.cs:43                        catalog:items:{page}:{take}:{brand}:{type} (no TTL) (confirmed)

Assumption: annotation `1` (as above).  
Reason: the `serves` edge is an annotation.

invalidation: not found in Catalog.API

### UNGUARDED_WRITE — `Catalog.API/OrderStatusChangedToPaidIntegrationEventHandler.Handle(OrderStatusChangedToPaidIntegrationEvent)`

- Finding: `f:5`
- Confidence: `likely`
- Solution: `src/eShopOnContainers-ServicesAndWebApps.sln`
- TTL: none
- Budget: 60 s

Chain:

    writes    Catalog.API/IntegrationEvents/EventHandling/OrderStatusChangedToPaidIntegrationEventHandler.cs:28   Handle -> dbo.Catalog (likely: the stock is reduced on the tracked entity and saved)
    reads     Catalog.API/Controllers/CatalogController.cs:40             ItemsAsync <- dbo.Catalog (confirmed)
    serves    WebMVC/Services/CatalogService.cs:36 -> Catalog.API/Controllers/CatalogController.cs:26   GET {purchaseurl}/c/api/{v}/catalog/items{?} -> ItemsAsync (likely, annotation 1)
    reads     WebMVC/Services/CatalogService.cs:36                        GetCatalogItems -> GET {purchaseurl}/c/api/{v}/catalog/items{?} (confirmed)
    caches    WebMVC/Services/CatalogService.cs:43                        catalog:items:{page}:{take}:{brand}:{type} (no TTL) (confirmed)

Assumption: annotation `1` (as above).  
Reason: the write itself is inferred (an entity method followed by `SaveChangesAsync`), and the `serves` edge is an annotation. The handler is a consumer of an Ordering event; the chain still belongs to it, because it is the code that would have to invalidate.

invalidation: not found in Catalog.API

## Needs checking

### Database step skipped

- Kind: `database`
- Solution: —
- Reason: `.cache-detective/workspace.json` names no database. eShopOnContainers has four databases and Cache Detective indexes one catalogue per workspace; none of the paths above runs through a stored procedure, a trigger or a view, so nothing here is cut short by it. A chain that did would stop where the code stops.

Chain or diagnostic:

    index_database: not called (no database configured)

### Solution diagnostics from `index_solution` (26)

- Kind: `diagnostic`
- Solution: `src/eShopOnContainers-ServicesAndWebApps.sln`
- Reason: the solution indexed and every service's handlers are present, but MSBuildWorkspace reported 26 diagnostics of kind `Failure`. One is real: `src/docker-compose.dcproj` cannot be opened because `.dcproj` is not a language project. The other 25 are NuGet audit warnings (NU1902 / NU1903 — `Azure.Identity`, `System.Data.SqlClient`, `System.IdentityModel.Tokens.Jwt`, `Duende.IdentityServer`) that the workspace surfaces as failures for Basket.API, Catalog.API, Ordering.API, WebHost.Customization, Identity.API, WebStatus and Payment.API. They do not stop indexing. The first page of diagnostics holds 25 rows; the 26th, on the second page, was not retrieved.

Chain or diagnostic:

    Failure  Cannot open project 'src/docker-compose.dcproj' because the file extension '.dcproj' is not associated with a language.
    Failure  Msbuild failed when processing 'Basket.API.csproj' with message: Package 'Azure.Identity' 1.5.0-beta.3 has a known high severity vulnerability (and 23 more of this shape)

### Unresolved rows left after the budget (10)

- Kind: `call`
- Solution: `src/eShopOnContainers-ServicesAndWebApps.sln`
- Reason: the static-analyst budget of 50 was spent; the ten rows below are framework calls inside Identity.API (Duende IdentityServer, `IConfiguration`) whose implementations live in packages. No cache key depends on Identity.API, so they lower no finding's confidence. They are listed under Unresolved.

Chain or diagnostic:

    see Unresolved

## Unresolved

- `call` at `src/Services/Identity/Identity.API/Quickstart/Device/DeviceController.cs:115` — No implementation found for `Duende.IdentityServer.Services.IDeviceFlowInteractionService.HandleRequestAsync(string, ConsentResponse)`.
- `call` at `src/Services/Identity/Identity.API/Quickstart/Grants/GrantsController.cs:53` — No implementation found for `Duende.IdentityServer.Services.IIdentityServerInteractionService.GetAllUserGrantsAsync()`.
- `call` at `src/Services/Identity/Identity.API/Quickstart/Grants/GrantsController.cs:58` — No implementation found for `Duende.IdentityServer.Stores.IClientStore.FindClientByIdAsync(string)`.
- `call` at `src/Services/Identity/Identity.API/Quickstart/Grants/GrantsController.cs:45` — No implementation found for `Duende.IdentityServer.Services.IIdentityServerInteractionService.RevokeUserConsentAsync(string)`.
- `call` at `src/Services/Identity/Identity.API/Quickstart/Grants/GrantsController.cs:46` — No implementation found for `Duende.IdentityServer.Services.IEventService.RaiseAsync(Event)`.
- `call` at `src/Services/Identity/Identity.API/Quickstart/Home/HomeController.cs:48` — No implementation found for `Duende.IdentityServer.Services.IIdentityServerInteractionService.GetErrorContextAsync(string)`.
- `call` at `src/Services/Identity/Identity.API/Quickstart/Home/HomeController.cs:21` — No implementation found for `Microsoft.Extensions.Configuration.IConfiguration.GetChildren()`.
- `call` at `src/Services/Identity/Identity.API/Quickstart/Home/HomeController.cs:21` — No implementation found for `Microsoft.Extensions.Configuration.IConfiguration.GetSection(string)`.
- `call` at `src/Services/Identity/Identity.API/Quickstart/Home/HomeController.cs:24` — No implementation found for `Microsoft.Extensions.Configuration.IConfiguration.GetChildren()`.
- `call` at `src/Services/Identity/Identity.API/Quickstart/Home/HomeController.cs:24` — No implementation found for `Microsoft.Extensions.Configuration.IConfiguration.GetSection(string)`.

## Annotations this run

- `1` `call`: `GET {purchaseurl}/c/api/{v}/catalog/items{?}` (WebMVC `CatalogService.cs:36`) → `handler:…/Catalog.API.Controllers.CatalogController.ItemsAsync(int, int, string)` — base address `PurchaseUrl` + `/c/api/v1/catalog/` (`CatalogService.cs:17`), gateway prefix `/c/` → Catalog.API; the filtered branches of `API.Catalog.GetAllCatalogItems` reach the type/brand endpoints, which read the same table. Changed key `catalog:items:{page}:{take}:{brand}:{type}` and findings f:1–f:5.
- `2` `call`: `GET {purchaseurl}/api/{v}/order/draft/{basketid}` (WebMVC `BasketService.cs:95`) → `handler:…/Web.Shopping.HttpAggregator.Controllers.OrderController.GetOrderDraftAsync(string)` — Basket.API has no `order` controller; the Web.Bff.Shopping aggregator declares `api/v1/order/draft/{basketId}`.
- `3` `call`: `POST {purchaseurl}/api/{v}/basket/items` (WebMVC `BasketService.cs:118`) → `handler:…/Web.Shopping.HttpAggregator.Controllers.BasketController.AddBasketItemAsync(AddBasketItemRequest)` — the aggregator's `[HttpPost] [Route("items")]`.
- `4` `call`: `PUT {purchaseurl}/api/{v}/basket/items` (WebMVC `BasketService.cs:79`) → `handler:…/Web.Shopping.HttpAggregator.Controllers.BasketController.UpdateQuantitiesAsync(UpdateBasketItemsRequest)` — the aggregator's `[HttpPut] [Route("items")]`.
- `5` `key`: `RedisBasketRepository.cs:31` `customerId` → template `{buyerId}` — the basket is keyed by the buyer id alone and lives only in Redis: storage, not a cache. Added key `redis/{buyerId}`.
- `6` `key`: `RedisBasketRepository.cs:46` `basket.BuyerId` → template `{buyerId}` — same key as annotation 5.
- `7` `key`: `RedisBasketRepository.cs:18` `id` → template `{buyerId}` — same key as annotation 5.
- `8` `call`: `RedisBasketRepository.cs:24` `server.Keys()` → external — implemented by the StackExchange.Redis client; enumerates server keys and reads no repository code.
- `9` `event`: `Ordering.Infrastructure/MediatorExtension.cs:19` `mediator.Publish(domainEvent)` → events `BuyerAndPaymentMethodVerifiedDomainEvent`, `OrderCancelledDomainEvent`, `OrderShippedDomainEvent`, `OrderStartedDomainEvent`, `OrderStatusChangedToAwaitingValidationDomainEvent`, `OrderStatusChangedToPaidDomainEvent`, `OrderStatusChangedToStockConfirmedDomainEvent` (namespace `Microsoft.eShopOnContainers.Services.Ordering.Domain.Events`) — the only types queued through `Entity.AddDomainEvent` in Ordering.Domain.
- `10`–`15` `call`: Ordering.SignalrHub `OrderStatusChangedTo{Cancelled,Paid,Shipped,StockConfirmed,Submitted,AwaitingValidation}IntegrationEventHandler.cs` `_hubContext.Clients.Group(...)` → external — ASP.NET Core SignalR pushes to connected clients and reads nothing in the repository.
- `16` `call`: Webhooks.API `GrantUrlTesterService.cs:22` `_clientFactory.CreateClient("GrantClient")` → external — Microsoft.Extensions.Http.
- `17` `call`: Webhooks.API `GrantUrlTesterService.cs:28` `client.SendAsync(msg)` → external — an OPTIONS request to a subscriber-supplied webhook URL outside the repository.
- `18` `call`: WebMVC `TestController.cs:38` `_client.CreateClient(nameof(IBasketService))` → external — Microsoft.Extensions.Http.
- `19`–`50` `call`: Identity.API `AccountController.cs`, `ExternalController.cs`, `ConsentController.cs`, `DeviceController.cs` (rows u:7–u:38) → external — implemented by Duende IdentityServer, ASP.NET Core authentication or `ILogger`; no repository code is reached.

None of the fifty annotations was persisted; they live in this session's graph only.
