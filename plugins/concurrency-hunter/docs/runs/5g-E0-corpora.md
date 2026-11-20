# 5g run E0 — замер корпусов без генерации

## Причина

На eShop после пяти ранов движка осталось две находки и нет решающих gaps
([#422](https://github.com/dlin10/ai-coding-plugins/issues/422)), а demo ловит только записанное
в нём. Порядок ранов E2–E6 выбирается по коду, которого анализ ещё не видел, поэтому
[#430](https://github.com/dlin10/ai-coding-plugins/issues/430) меряет nopCommerce, OrchardCore
и eShopOnAbp командой `metrics --generation off`. Скорость здесь не цель: её держит фаза 7
([#276](https://github.com/dlin10/ai-coding-plugins/issues/276)). Код движка ран не меняет.

## Как мерили

Исполняемый файл — Release-сборка `src/ConcurrencyHunter.Cli` с HEAD `c6942c89`,
`bin/Release/net10.0/win-x64/concurrency-hunter.exe`, версия движка 0.1.0. Пределы по умолчанию
(depth 8, contexts 16, scc 16). Каждый корпус шёл один, с восстановленными пакетами
(`dotnet restore … --disable-build-servers -nodeReuse:false`), лог и JSON — в `scratch/e0-corpora/<корпус>/`.

У `metrics` нет ни срока, ни предела члена при выключенной генерации: ран идёт, пока не
сойдутся все шаги. Поэтому за каждым раном стоял сторож `scratch/e0-corpora/nop/guard.ps1`:
раз в 15 с он читал commit charge машины (`TotalVirtualMemorySize − FreeVirtualMemory`,
предел 63,7 ГБ) и останавливал процесс по точному PID, сняв стек, при commit больше 58 ГБ или
по сроку. Срок nopCommerce — 22:40 (решение владельца), остальных — 3 часа.

Перед каждым раном нагрузка машины мерилась суммой `TotalProcessorTime` всех процессов за 5 с
(`scratch/e0-corpora/load.ps1`). Во время ранов сторож писал commit и свободную память;
занятость CPU другими процессами во время ранов не мерилась.

`metrics` не пишет ни статус рана, ни правило и confidence находок, ни виды неподдержанных
операций. Их дал отдельный харнесс `scratch/e0-corpora/harness`: сборка с именем
`ConcurrencyHunter.Cli.Tests`, которому Analysis, Core и Cli открывают internal-члены. Режим
`analysis` зовёт тот же `MetricsCommand.MeasureAsync`, что и CLI, затем
`RunStatusRules.Decide` и `ReportRenderer.Render` и пишет `findings.json`, `report.md`,
`summary.json`. Режим `census` повторяет `UnsupportedCensusTests.AssertCensus` по каждому
scope: `EngineFixture.ReachScope`, решение кучи, операции `IrUnknownOperation` с причиной
`unsupported` в телах, до которых дошла куча. Проверка харнесса: на eShop
(`CH_ESHOP_ROOT=C:\Dev\eShopOnContainers`) перепись совпала со столбцом `eshop` во всех
12 видах, 824 тела, 40,6 с.

| Корпус | Решение | Коммит | Дерево | Целевой фреймворк | Scopes |
|---|---|---|---|---|---:|
| nopCommerce | `src/NopCommerce.sln` | `918444773367deb734b93d3ad80e17325d6c147a` | чистое | net9.0 | 1 (`Nop.Web`) |
| OrchardCore | `OrchardCore.slnx` | `b9c4b2f23e56ef11fbdbd28603c871d1b0fc9deb` | чистое | net10.0 | 3 (`Application.Pages`, `Cms.Web`, `Mvc.Web`) |
| eShopOnAbp | `EShopOnAbp.sln` | `7bfed7a322d16b34a5a66208c03486fa5dfacee9` | неотслеживаемая `.cache-detective/` | net9.0 | 12 |

Встроенные модели и перепись фреймворков описывают только .NET 10. У nopCommerce и
eShopOnAbp, собранных под net9.0, каждый вызов члена фреймворка — opaque-вызов с
`UnsupportedAssemblyVersion` (на eShopOnAbp 4653 такие диагностики по 162 сборкам). eShop
меряется на рабочем дереве, переведённом на net10.0; эти два корпуса не переводились.

## nopCommerce

| | |
|---|---|
| Нагрузка до рана | 2,2 % из 16 ядер (19:38:34), commit — не мерился |
| Начало | 19:39:01 |
| Терминального статуса нет | остановлен сторожем по сроку 22:40:12, wall 10 873 с |
| Стадия на остановке | `ExecutionModel+Builder.AssignRegionExecutions` (модель исполнений scope `Nop.Web`) |
| CPU на остановке | 12 834 с |
| Рабочий набор | 10,5 ГБ на 19:41, 17,0 ГБ на 20:35, наибольший замеченный 21,2 ГБ на 20:52, 19,7 ГБ на остановке |
| Машина во время рана | commit 46,8–47,4 ГБ из 63,7, свободно 0,7–2,1 ГБ |

Стадии по образцам стека (`scratch/e0-corpora/nop/stack-*.txt`): 19:52, 20:00 и три образца
20:37 — решение кучи (`WholeProgram+Solver.Propagate` → `Call`, `CallTargets`,
`SubstitutionText`, `Dispatch` → `ProgramIndex.Implementation`, `Dirty`); 20:47, 20:50, 21:13,
21:44 и 22:40 — `AssignRegionExecutions`. Fixpoint кучи единственного scope сошёлся
за 60–68 минут, при CPU около 4,8–5,7 тыс. с; модель исполнений шла дальше 1 ч 50 мин и не кончилась.

Трасса `dotnet-trace` (60 с, `dotnet-sampled-thread-time` и `dotnet-common`, 20:47,
`scratch/e0-corpora/nop/cpu-final.nettrace`) — исключительное время рабочего потока:
`AssignRegionExecutions` 3,9 %, `String.GetNonRandomizedHashCode` 3,6 %,
`OrdinalComparer.GetHashCode` 3,0 %, `HashSet.AddIfNotPresent` 1,7 %; остальное — ожидающие
потоки. Первая трасса 20:35 только с `dotnet-sampled-thread-time` дала пару образцов и ничего не показала.

Причина в коде: `ExecutionModel.cs`, хвост `AssignRegionExecutions` — хаотичный fixpoint без
worklist. Каждый круг для каждого DI-ребра родитель → ребёнок копирует всё множество
исполнений родителя (`Executions(parent).ToArray()`) в ребёнка, пока что-то меняется. У
`Nop.Web` около 1200 методов действий в `src/Presentation` и глубокий граф сервисов: множества исполнений велики,
а каждый круг заново хеширует все их строки.

Находок, coverage, gaps и переписи неподдержанных операций по nopCommerce нет: ран не дошёл
до доступов, а перепись — тот же час решения кучи, который владелец отложил вместе с повтором.
Ручной разбор High (SPEC 12.2, `skills/hunt/evals/nopcommerce/expected.json`) ждёт этого
повтора: nopCommerce прогоняется ещё раз один и с долгим сроком, например на ночь.

## OrchardCore

| | |
|---|---|
| Нагрузка до рана | 1,4 % из 16 ядер (22:40:28), commit 25,5 ГБ, свободно 20,6 ГБ |
| Начало | 22:40:38 |
| Терминального статуса нет | остановлен сторожем по commit 58,5 ГБ в 01:28:52, wall 10 097 с |
| Стадия на остановке | `InterproceduralAccesses+ExecutionCollector.HeldOverSpan` (сбор доступов) |
| CPU на остановке | 16 398 с |
| Рабочий набор | 9,6 ГБ на 22:50, 15,6 ГБ на 23:31, наибольший замеченный 27,9 ГБ на 01:02, 23,9 ГБ на остановке |
| Машина во время рана | commit 35,2 → 58,5 ГБ, свободно от 11 ГБ до 0,3 ГБ (00:32) |

Стадии по образцам (`scratch/e0-corpora/orchard/stack-*.txt`): 22:51 — модель исполнений
(`FinishSpawnedExecutions` → `TaskForwarders.OnePath`); с 23:11 до остановки — сбор доступов:
`ExecutionCollector.ResourcesOf`, затем 23:52, 00:12 и 00:33 `BindingEvidenceOf`, 01:13
`DeepReadAccesses` → `CollectionKindOf`, 01:28 `HeldOverSpan`. На каком из scopes ран стоял,
по стеку не видно: три scope идут один за другим.

Причина в коде, которую образцы показывают чаще всего: `InterproceduralAccesses.BindingEvidenceOf`
для каждого доступа к DI-региону заново перебирает все injection bindings и все регистрации
scope, без кеша по региону. В коде OrchardCore около 900 вызовов `services.Add…`/`TryAdd…`.

Находок, coverage и gaps по OrchardCore нет. Перепись неподдержанных операций снята харнессом
отдельно (решение кучи без исполнений и доступов): 4248 тел, 22 вида, 292 с, нагрузка до неё
0,7 % из 16 ядер (01:34:26), commit 21,1 ГБ.

## eShopOnAbp

| | |
|---|---|
| Нагрузка до рана | 1,0 % из 16 ядер (01:29:11), commit 20,9 ГБ, свободно 24,8 ГБ |
| Статус | `CompleteClean` (харнесс, тот же анализ), без причин |
| Время | 61,1 с, из них загрузка 44,6 с, program index 7,2 с; peak working set 590 МБ |
| Проекты | 18 ожидалось (проекты решения), 59 загружено с транзитивными |
| Scopes / корни | 12 / 3: `aspnetcore` 1 (gRPC `PublicProductGrpService`), `hosting` 2 (DbMigrator) |
| Тела / доступы / пары | 135 достижимых тел (сумма по scopes), 28 доступов, 30 сравнений, 0 кандидатов |
| Находки | 0 |

Анализ почти ничего не видит. В восьми host-scopes есть контроллеры (`HomeController : AbpController`,
`PaymentMethodController`, `PaymentRequestController`), но провайдер `aspnetcore` их отбрасывает
с `UnsupportedPattern`: «controllers are not both registered and mapped». ABP добавляет
MVC в своём модуле `AbpAspNetCoreMvcModule`, а маршруты программа строит вызовом ABP
`UseConfiguredEndpoints` (восемь мест), не `AddControllers` и `MapControllers`. Контроллеры, которые ABP строит из application services
(`ConventionalControllers`, пять мест), и обработчики `IDistributedEventHandler<T>`
с `ITransientDependency` (восемь классов) корнями не становятся вовсе: их регистрирует
соглашение ABP. Ещё 23 `UnresolvedBinding` — у классов проверки миграций.

Счётчики coverage, сумма по 12 scopes: `opaque-call` 662, `unresolved-base` 540,
`unproven-reference` 222, `unsupported-operation` 156 (по уникальным телам — 107),
`out-of-range-call` 84, `delegate-to-opaque` 66, `semantic-gap` 54, `known-call` 19.

Semantic gaps по членам — сколько scopes его называет; решающих gaps нет, потому что нет находок:

| Член | Scopes |
|---|---:|
| `Serilog.LoggerConfigurationAsyncExtensions.Async(LoggerSinkConfiguration, Action<LoggerSinkConfiguration>, int, bool)` | 11 |
| `Volo.Abp.Modularity.AbpModule.Configure<TOptions>(Action<AbpDistributedCacheOptions>)` | 7 |
| `HealthChecksBuilderDelegateExtensions.AddCheck(…, Func<HealthCheckResult>, …)` | 5 |
| `HttpClientFactoryServiceCollectionExtensions.ConfigureHttpClientDefaults(…, Action<IHttpClientBuilder>)` | 5 |
| `OpenTelemetryLoggingExtensions.AddOpenTelemetry(…, Action<OpenTelemetryLoggerOptions>)` | 5 |
| `OpenTelemetryBuilder.WithMetrics(Action<MeterProviderBuilder>)` | 5 |
| `OpenTelemetryBuilder.WithTracing(Action<TracerProviderBuilder>)` | 5 |
| `IHostBuilder.ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder>)` | 2 |
| `WebHostBuilderKestrelExtensions.ConfigureKestrel`, `KestrelServerOptions.ListenAnyIP`, `OneTimeRunner.Run(Action)`, `AddLogging`, `ConfigureLogging`, `IHostBuilder.ConfigureServices`, `AbpApplicationFactory.Create<TStartupModule>`, `AbpModule.Configure<TOptions>(Action<AbpAutoMapperOptions>)`, `ResourceBuilderExtensions.WithEndpoint<T>` | по 1 |

Все они — конфигурация при старте, принимающая делегат; находок они не решают.

## Перепись неподдержанных операций

`skills/hunt/evals/metrics/unsupported-census.md` получил столбцы `orchardcore` и `eshoponabp`
и девять новых видов; nopCommerce добавится с повтором. Строки прежних видов: demo и eShop не
изменились. `UnsupportedCensusTests` принимает столбцы после `eshop` и `question: none` у вида,
который ни один вопрос пока не покрывает; оба его факта прошли с `CH_ESHOP_ROOT`.

Новые виды OrchardCore:

| Вид | OrchardCore | Что это | Вопрос |
|---|---:|---|---|
| `ImplicitIndexerReference` | 34 | `s[..n]` через неявный индексатор с `Range` | нет |
| `Range` | 34 | выражение диапазона `a..b` | нет |
| `TypeParameterObjectCreation` | 11 | `new T()` | нет |
| `DynamicInvocation` | 9 | вызов на `dynamic` | 67, как `DynamicMemberReference` |
| `None` | 7 | throw-выражение (6), `stackalloc` (1) | нет |
| `Utf8String` | 4 | литерал `"…"u8` | нет |
| `TranslatedQuery` | 3 | синтаксис запроса LINQ | нет |
| `Spread` | 2 | `..xs` в collection expression | 60 |
| `CollectionExpressionElementsPlaceholder` | 1 | элемент collection expression `[entry]` | 60 |

У прежних видов OrchardCore даёт самые большие числа: `CollectionExpression` 372,
`DefaultValue` 255, `SimpleAssignment` 205, `TypeOf` 194, `InterpolatedStringText` 185.
eShopOnAbp — только прежние виды, все в коде старта.

## Остаётся

- Повтор nopCommerce один, с долгим сроком, — его находки, coverage, gaps и перепись; затем
  ручной разбор High в `skills/hunt/evals/nopcommerce/expected.json`
  ([#445](https://github.com/dlin10/ai-coding-plugins/issues/445)).
- Проверочный повтор OrchardCore возможен, только когда сбор доступов перестанет расти по
  памяти: на этой машине 3 часа и 58 ГБ commit его не хватило. Модель исполнений nopCommerce,
  сбор доступов OrchardCore и решение кучи `Nop.Web` берёт новый ран E-scale
  ([#441](https://github.com/dlin10/ai-coding-plugins/issues/441): #442, #443, #444).
- eShopOnAbp и nopCommerce меряют движок с opaque-фреймворком, пока их деревья на net9.0
  (перевод на net10.0 — в #445).
- Новые виды переписи — вопросы рана E1 (#446–#450; `DynamicInvocation` — #304, `Spread` и
  элемент collection expression — #298); корни ABP на eShopOnAbp — вопрос рана E2 (#451).
