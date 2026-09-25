# Каталог сценариев demo

| Поле | Значение |
|---|---|
| Статус | Черновик 2026-09-15; перечень кейсов demo на все фазы v1; фаза 5 разложена по подфазам 4b и 5a–5e 2026-09-23 |
| Ожидания | [`expected-findings.json`](expected-findings.json), формат в [SPEC 12.2](../docs/SPEC.md) |
| Фазы | [SPEC 14.3](../docs/SPEC.md) |

Каталог говорит, какие кейсы должны появиться в `demo/` и что каждый проверяет. Он не заменяет `expected-findings.json` и не задаёт его записи: код кейса и ожидания пишутся руками в своей фазе до реализации, как требует SPEC 12.2. Вердикт в каталоге это намерение; если при написании кейса он расходится со SPEC, прав SPEC, а каталог исправляется в той же правке.

## Правила

1. Имя кейса это kebab-имя файла; `имя/суффикс` это отдельная запись того же файла, как `id` в `expected-findings.json`.
2. Один файл, один namespace, регистрация через `Add<Case>()` или `Map<Case>()` в `Program.cs`; общих строк между кейсами нет.
3. Кейс фазы P не содержит конструкций поздних фаз, если они не его предмет: в кейсах фазы 3 нет guards и вызовов, способных стать semantic gap, в кейсах фаз 4, 4b и 5a нет gap-вызовов. Иначе метка confidence сдвинется в поздней фазе, а запись ожиданий по 12.2 не переписывается.
4. Запись в HTTP action всегда даёт self-pair: action пересекается сам с собой. Поэтому action в кейсах про защиту и порядок только читает, а negatives про disjointness строятся на `BackgroundService`, один экземпляр которого сам с собой не пересекается (TD-064). Spawn-кейсы фазы 3 живут внутри одного `BackgroundService`: до фазы 3 у них нет пересечения вообще.
5. Access в лямбде или local function получает symbol содержащего члена (12.2). Если в файле нужны две записи с одинаковой парой symbol/operation на одном resource, тела spawn и callbacks выносятся в именованные методы, иначе записи неразличимы.
6. Кейсы, которые проверяет не `expected-findings.json`, а тест отчёта или evals, помечены в колонке «Ожидание».

### Колонка «До фазы»

Код кейса появляется в своей фазе вместе с изменением движка, которое делает его записи верными; в 4b и 5a matcher новой фазы включён с первой задачи, потому что каждый кейс входит вместе с таким изменением (вопрос 11). В остальных фазах matcher переключается в последней задаче, если кейсы добавляются раньше их реализации. Находка, не совпавшая ни с одной записью файла, это false positive. Запись `findings` совпадает только при том же `rule` и тех же `operation`; запись `notDefects` совпадает по resource без учёта rule.

| Пометка | Значение |
|---|---|
| — | Ранний анализатор находки не даёт или даёт ту же identity |
| nD | Ранняя находка возможна; её прикрывает запись `notDefects`, если resource совпадёт |
| ⚠ | Ранняя находка с другим `rule`, `operation` или resource; прикрыть нечем, см. вопрос 11 |

## Фаза 1a — написаны

| Кейс | Тип | Суть | Ожидание |
|---|---|---|---|
| `static-field-unlocked-read-write` | positive | Статик пишет POST, читает GET, без защиты | `/post-self`, `/post-get` DCA1001 |
| `static-field-same-lock` | negative | Тот же статик, все accesses под одним статическим `lock` | нет |

## Фаза 1b — написаны

| Кейс | Тип | Суть | Ожидание |
|---|---|---|---|
| `di-singleton-controller-vs-worker` | positive | DI singleton пишут action и hosted service | `/post-self`, `/post-worker` DCA1001 |
| `minimal-api-read-write` | positive | Minimal API handlers как method groups: POST против себя и GET | `/set-self`, `/set-get` DCA1001 |
| `action-self-overlap` | positive | Один PUT, пересекающийся сам с собой | DCA1001 |
| `static-field-http-vs-worker` | positive | Статик: worker пишет, actions читают; два root provider-а | DCA1001 |
| `hosted-start-vs-action` | positive | `StartAsync` пишет singleton, action читает | DCA1001 |
| `background-stop-reads-own-field` | positive | `ExecuteAsync` пишет поле, `StopAsync` читает | DCA1001 |
| `poco-controller-self-overlap` | positive | Controller без MVC base class | DCA1001 |
| `primary-constructor-injection` | positive | Singleton через primary constructor controller-а | DCA1001 |
| `from-services-action-parameter` | positive | Singleton через параметр `[FromServices]` | DCA1001 |
| `shared-library-static` | оба | Статик общей библиотеки; Web и Worker это два process scope (ADR 0005) | `/web-self` DCA1001; `/worker-vs-web` нет |
| `di-scoped-per-request` | negative | Форма singleton-гонки, но scoped | нет |
| `di-transient` | negative | Transient, новый экземпляр на каждый resolve | нет |
| `di-singleton-same-lock-on-instance` | negative | Controller против worker, оба под `lock` на сам singleton | нет |
| `startup-write-before-run` | negative | Единственная запись из `Program` до старта host | нет |
| `monitor-enter-exit-same-gate` | negative | `Monitor.Enter`/`Exit` на одном gate | нет |
| `ambiguous-registration-scoped-wins` | negative | Singleton и scoped регистрации одного типа, побеждает последняя | нет |
| `di-factory-registration` | positive | `AddSingleton(_ => new RateCard())`, action пишет (PRD 3, TD-040) | DCA1001, self-pair |
| `di-instance-registration` | positive | `AddSingleton(new PriceList())`, worker пишет, action читает (PRD 3, TD-040) | DCA1001 |
| `minimal-api-lambda-handler` | positive | `MapPost(route, (SignalState state, string value) => state.Value = value)`: access в лямбде, symbol это метод `Map<Case>` (TD-121, 12.2) | DCA1001, self-pair |
| `non-action-public-method` | negative | Public метод controller-а с `[NonAction]` пишет singleton; других записей нет (TD-121) | нет |
| `hosted-service-registered-twice` | negative | `AddHostedService<W>()` дважды; `TryAddEnumerable` оставляет один экземпляр, `ExecuteAsync` пишет своё свойство (TD-064) | нет |
| `test-project-not-a-scope` | negative | Проект `Demo.Tests` в `Demo.slnx` ссылается на Worker; его worker и worker Worker-а пишут статик общей библиотеки; пакеты xunit удлиняют restore (ADR 0005, TD-062a) | нет на паре test/worker |

Последние шесть строк закрывают дыры покрытия фазы 1b (DI provider, `AspNetCoreRootProvider`, process scope): они написаны первыми в фазе 2 с `phase: "1b"`; если текущий анализатор их не проходит, это дефект 1b по SPEC 12.3.

## Фаза 2 — написаны

| Кейс | Тип | Суть | Ожидание |
|---|---|---|---|
| `rmw-singleton-counter` | positive | Два POST читают счётчик и пишут +1 | DCA1002 |
| `rmw-three-layers` | positive | Web → Application → Domain, lost update в Domain (TC-01) | DCA1002 |
| `alias-two-fields` | positive | Один объект в двух полях: запись через одно, чтение через другое (TC-20) | DCA1001 |
| `interface-dispatch-di` | positive | Interface call разрешён единственной DI-регистрацией | DCA1001 |
| `virtual-dispatch-points-to` | оба | CHA достигает двух overrides, allocation есть только у одного | `/loud-handler` DCA1001; `/quiet-handler` нет |
| `delegate-field` | positive | Запись достижима только через делегат в поле | DCA1001 |
| `lambda-and-local-function` | positive | Записи в local function и в лямбде приписаны action | `/local-function`, `/lambda` DCA1001 |
| `escape-into-singleton` | positive | Свежий объект публикуется в singleton до заполнения | `/registry`, `/escaped-draft` DCA1001 |
| `factory-returns-shared-static` | positive | «Factory» возвращает один статический объект (TC-15) | DCA1001 |
| `rmw-forms` | positive | Singleton, по action и полю на форму: `x += y`, свойство get-compute-set, чтение в local и запись через несколько statements (TD-014, FR-07) | `/compound-assignment`, `/property`, `/split` DCA1002 |
| `rmw-forms/through-methods` | positive | Тот же singleton: `RaiseLevel` читает поле через private `GetLevel` и пишет через private `SetLevel`; RMW приписан `SetLevel` (TD-014) | DCA1002 |
| `stale-read-without-dependency` | positive | Метод singleton-а, вызванный action, читает поле в local и пишет в него значение из запроса, не зависящее от прочитанного (TD-073) | `/write-self`, `/read-write` DCA1001, не DCA1002 |
| `recursive-summary` | positive | Рекурсивный `Walk(depth)` singleton-а пишет `_lastDepth` на каждом уровне; action (TD-023) | DCA1001 |
| `generic-singleton-per-type-argument` | оба | `Store<T>` singleton для `Order` и `Invoice`; два worker-а пишут `Store<Order>`, третий `Store<Invoice>` (TD-022) | `/same-type` DCA1001; `/other-type` нет |
| `escape-via-array-element` | positive | Worker кладёт свежий объект в массив singleton-а и продолжает писать в него; action читает элемент (TD-052) | DCA1001 |
| `escape-via-out-parameter` | positive | Метод singleton-а отдаёт внутренний объект через `out`; action пишет в него, worker пишет через поле (TD-052) | `/action-self`, `/action-worker` DCA1001 |
| `escape-via-captured-closure` | positive | Worker захватывает локальный объект в лямбду и сохраняет её в поле singleton-а; action вызывает делегат, который пишет объект; worker пишет его же (TD-052, FR-06) | `/captured-note`, `/callback-slot` DCA1001 |
| `escape-via-static-assignment` | positive | Worker создаёт объект, присваивает в статик и пишет в него; action читает через статик, поэтому каждая пара это настоящая гонка (TD-052) | `/static-slot`, `/escaped-object` DCA1001 |
| `deep-access-path-wildcard` | positive | Цепочка из 13 сегментов глубже лимита TD-042, запись из action; ожидания верны для любого лимита от 1 до 11 (TD-042, TD-103) | `/write-self`, `/read-write` DCA1001, метка Medium, не High, uncertainty «wildcard» |
| `custom-lock-by-name` | positive | Класс `SimpleLock` с `Lock()`/`Unlock()` без синхронизации внутри; оба writer-а «под ним» (TD-086, FR-09) | DCA1001 |
| `group-shared-helper-many-callers` | positive | Три action вызывают один helper singleton-а, который пишет поле (TD-076) | DCA1001; одна finding group с occurrence count, проверка: тест отчёта, проверяется в 2b: одна finding с шестью occurrences (ADR 0007) |
| `owned-local-allocation` | negative | Объект на запрос, не escape-ится | нет |
| `distinct-allocation-sites` | negative | Один тип и поле, два allocation site, по worker-у на каждый | `/left`, `/right` нет |
| `receiver-sensitivity` | negative | Одно тело метода, два receiver-а (TC-15) | `/first`, `/second` нет |
| `factory-distinct-call-sites` | negative | Один `new` в static factory, два call site (TC-15) | нет |
| `same-lock-via-field` | negative | `lock` на readonly поле того же singleton на вызов ниже root | нет |
| `lock-held-by-caller` | negative | Lock берётся в root, access в callee | нет |
| `singleton-configured-in-constructor` | negative | Свойство пишется только при конструировании singleton-а | нет |
| `unreachable-static-writer` | negative | Запись вне reachable set (TC-07) | нет |
| `lock-identity-through-alias` | negative | `_second = _gate` в конструкторе; один worker пишет под `lock (_gate)`, другой под `lock (_second)` (TD-045) | нет |
| `static-constructor-initialization` | negative | Статик пишется только в `static` конструкторе, action читает | нет (ADR 0006) |

## Фаза 2 — конструирование

Кейсы правила [ADR 0006](../docs/adr/0006-a-construction-belongs-to-the-execution-that-triggers-it.md): доступы конструкции принадлежат execution, которое её запускает, а доступы к собственному объекту и статикам своего типа кандидатов не образуют, пока конструкция не публикует объект. Ответы окончательные для v1; `singleton-configured-in-constructor` и `static-constructor-initialization` выше следуют тому же правилу.

| Кейс | Тип | Суть | Ожидание |
|---|---|---|---|
| `controller-constructor-static-counter` | positive | Конструктор controller-а делает `_created++` своего статика; конструктор выполняется внутри каждого action | DCA1002 |
| `construction-other-state` | positive | Конструктор lazily resolved singleton-а и type initializer пишут статики другого типа; actions читают и пишут их | `/singleton-ctor`, `/type-initializer`, `/action-self` DCA1001 |
| `hosted-constructor-before-roots` | negative | Конструктор hosted service пишет статик при старте host, до всех roots; action читает | нет |
| `constructor-leaks-this` | positive | Конструктор singleton-а публикует `this` в статик и затем пишет своё свойство; action читает через статик | `/published-field`, `/registry-slot` DCA1001 |

## Фаза 2b — DI semantics

| Кейс | Тип | Суть | Ожидание |
|---|---|---|---|
| `locator-singleton-vs-worker` | positive | Action и worker получают singleton через `IServiceProvider.GetRequiredService` и пишут одно свойство (TD-121) | DCA1001 |
| `locator-scoped-via-create-scope` | negative | Worker берёт scoped-сервис из собственного `CreateScope`, action пишет экземпляр своего запроса; это разные объекты (TD-121) | нет |
| `locator-transient-distinct` | negative | Transient через `IServiceProvider` в action и в worker-е это новый экземпляр на каждый resolve (TD-121) | нет |
| `locator-unregistered-opaque` | negative | Тип нигде не зарегистрирован, action и worker берут его через `GetService`; разделяемого singleton-а анализ не выдумывает (TD-121) | нет |
| `factory-interface-dispatch` | positive | `AddSingleton<IClock>(_ => new WallClock())`: interface call разрешается в реализацию из factory, action и worker пишут один экземпляр (TD-121) | DCA1001 |
| `factory-resolves-other-service` | positive | Factory интерфейса возвращает другой singleton; action пишет через интерфейс, worker напрямую (TD-121, TD-040) | DCA1001 |
| `instance-registration-touches-static` | negative | Конструктор экземпляра в `AddSingleton(new SeedList())` пишет статик при старте, до всех roots; action читает (TD-040, ADR 0006) | нет |
| `factory-scoped-per-request` | negative | `AddScoped(_ => new RequestTag())`: два action пишут экземпляр своего запроса (TD-040) | нет |

## Фаза 3 — написаны

Все кейсы, кроме timer-ов против action и gRPC, живут в одном `BackgroundService` (правило 4).

Последние восемь кейсов написаны после code review фазы 3 по SPEC 12.3: каждый закрепляет форму, на которой ревью нашло ложное доказательство порядка, а стерегли её до сих пор только точечные тесты `ConcurrencyHunter.Core.Tests`. Все восемь проверены на падение: мутация, снимающая охраняемое правило, красит гейт demo.

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `task-run-vs-parent` | positive | `Task.Run`, лямбда пишет поле; родитель пишет его до `await` handle | DCA1001 | TD-065 | — |
| `task-factory-start-new` | positive | То же через `Task.Factory.StartNew` без join | DCA1001 | TD-065 | — |
| `task-handle-join-order` | оба | Handle в local; лямбда пишет поля A и B, родитель пишет A до `await t` и B после | `/before-await` DCA1001; `/after-await` нет | TD-065, TD-067 | — |
| `task-handle-awaited-elsewhere` | negative | Handle сохраняется в поле и awaited в другом методе до записи родителя: spawn с join, не fire-and-forget | нет | TD-065, CONTEXT.md | — |
| `task-wait-join` | оба | Родитель пишет поле A до `t.Wait()` и поле B после | `/before-wait` DCA1001; `/after-wait` нет | TD-067 | — |
| `continue-with` | оба | `Task.Run(First).ContinueWith(Second)`, тела именованные (правило 5); `Second` пишет поле, которое пишут `First` и родитель после запуска | `/vs-parent` DCA1001; `/antecedent-vs-parent` DCA1001: родитель пишет до `await` цепочки; `/vs-antecedent` нет (вопрос 3) | TD-065 | — |
| `thread-pool-queue-user-work-item` | positive | `QueueUserWorkItem` и `UnsafeQueueUserWorkItem`, по полю на вариант; callback и родитель пишут | `/queue`, `/unsafe-queue` DCA1001 | TD-065, TC-12 | — |
| `thread-start-join` | оба | `new Thread(...).Start()`; родитель пишет поле A до `Join()` и поле B после | `/before-join` DCA1001; `/after-join` нет | TD-065, TD-067 | — |
| `parallel-for-shared-total` | positive | `Parallel.For(0, n, i => _total += i)` | DCA1002 | TD-068 | — |
| `parallel-foreach` | positive | `Parallel.ForEach(items, x => _last = x)` | DCA1001 | TD-068 | — |
| `parallel-foreach-async` | positive | `Parallel.ForEachAsync`, тело с `await` делает RMW поля | DCA1002 | TD-065, TC-12 | — |
| `when-all-siblings` | оба | Именованные `WriteA`/`WriteB` пишут поле, `await Task.WhenAll(...)`, затем родитель пишет его же | `/siblings` DCA1001; `/after-when-all` (`WriteA`), `/after-when-all-b` (`WriteB`) нет | TD-066 | — |
| `when-all-synchronous-prefix` | оба | `Task.WhenAll(StepA(), StepB())`: оба async-метода пишут поле P до первого `await` и поле Q после | `/prefix` нет; `/after-first-await` DCA1001 | TD-066 | — |
| `when-any-no-join` | positive | `await Task.WhenAny(a, b)`, затем родитель пишет поле, которое пишет `b` | DCA1001 | TD-067 | — |
| `fire-and-forget-vs-awaited` | оба | `DropAsync()` без await пишет поле D; `SaveAsync()` awaited пишет поле S; родитель пишет D и S после вызовов | `/dropped` DCA1001; `/awaited` нет | TD-065, 12.2 | — |
| `async-void-call` | positive | Вызов `async void` метода, который пишет поле после `await`; родитель пишет его же | DCA1001 | TD-065 | — |
| `join-skipped-on-exception` | positive | `try { Risky(); await t; } catch (InvalidOperationException) { }`, затем запись: при исключении join пропущен | DCA1001 | TD-069 | — |
| `event-wait-not-ordering` | positive | Spawn пишет поле и вызывает `ManualResetEventSlim.Set`; родитель ждёт `Wait()` и пишет | DCA1001, known limitation | TD-086, PRD 3 | — |
| `threading-timer-vs-action` | positive | `System.Threading.Timer` в singleton-е, callback пишет, action читает | DCA1001; `/callback-self` DCA1001: периодический callback пересекается сам с собой | TD-061, TC-12 | — |
| `threading-timer-self-overlap` | positive | Периодический timer, callback делает RMW своего поля | DCA1002 | TD-061 | — |
| `timer-state-sharing` | positive | Объект передан как `state`, callback пишет в него, action читает через singleton | DCA1001; `/callback-self` DCA1001: периодический callback | TD-061 | — |
| `timer-captured-alias` | positive | Callback-лямбда захватывает объект и пишет его, action читает | DCA1001; `/callback-self` DCA1001: периодический callback | TD-061 | — |
| `timer-never-activated` | negative | `new Timer(cb, null, Timeout.Infinite, Timeout.Infinite)` без `Change`; callback пишет, action читает | нет | TD-061 | — |
| `timer-one-shot` | оба | `dueTime` 0, `period` `Infinite`; callback делает RMW поля A и пишет поле B, которое читает action | `/self` нет; `/vs-action` DCA1001 | TD-061 | — |
| `timer-change-reactivates` | positive | Timer создан выключенным, `Change(0, 1000)` делает его периодическим; callback делает RMW | DCA1002 | TD-061 | — |
| `timer-dispose-does-not-join` | positive | `timer.Dispose()`, затем запись в поле callback-а | DCA1001 | TD-061, TC-12 | — |
| `timer-dispose-async-awaited` | оба | `await timer.DisposeAsync()`, затем запись поля A; callback ещё запускает fire-and-forget работу, которая пишет поле B | `/after-dispose` нет; `/detached-work` DCA1001 | TD-061 | — |
| `timer-dispose-wait-handle` | оба | `Dispose(waitHandle)` и `waitHandle.WaitOne()`; запись поля A до ожидания и поля B после | `/before-wait` DCA1001; `/after-wait` нет | TD-061, TC-12 | — |
| `timers-timer-elapsed` | оба | `System.Timers.Timer.Elapsed` с `AutoReset = true`: RMW поля A, запись поля B, которое читает action | `/self` DCA1002; `/vs-action` DCA1001; `/last-sample-self` DCA1001: периодический callback пишет поле B | TD-061 | — |
| `periodic-timer-loop` | оба | `PeriodicTimer` в `ExecuteAsync`: итерация делает RMW поля A и пишет поле B, которое читает action | `/iterations` нет; `/vs-action` DCA1001 | TD-061a | — |
| `grpc-service-method` | positive | gRPC service method пишет singleton; нужен `Grpc.AspNetCore` и proto codegen, generated code появляется в demo до фазы 6 | DCA1001, self-pair | TD-121 | — |
| `conditional-join-inside-work` | оба | Внешняя задача ждёт один свой spawn на всех путях, другой только в одной ветке; родитель пишет оба поля после `await` внешней задачи | `/maybe` DCA1001; `/always` нет | TD-067, ADR 0008 | — |
| `async-tail-entry` | оба | Отделённый async-вызов: `await Task.Yield()`, затем spawn, запись поля до `await` этого spawn-а и другого после него | `/before` DCA1001; `/after` нет | TD-065, TD-067 | — |
| `branching-join` | оба | Три spawn-а до ветвления: обе ветки делают `Wait()` над одним handle, и каждая ждёт ещё свой | `/waited` нет; `/first`, `/second` DCA1001 | TD-067 | — |
| `callee-joins-on-all-paths` | оба | Виртуальный `Run()` с двумя реализациями: базовая ждёт одну задачу, производная обе; handles лежат в полях receiver-а (вопрос 15) | `/always` нет; `/maybe` DCA1001 | TD-067 | — |
| `await-conditional-task` | оба | `await (c ? A() : B())` и тот же conditional через local с записью между вызовом и `await` | `/awaited-first`, `/awaited-second` нет; `/deferred-first`, `/deferred-second` DCA1001 | TD-065, TD-067 | — |
| `when-all-continue-with` | positive | `Task.WhenAll(...).ContinueWith(...)`: составная форма нераспознана, `await ...Unwrap()` порядка не даёт | `/vs-parent` DCA1001; `/self` DCA1001: хвост continuation пересекается сам с собой | TD-065 | — |
| `maybe-null-handle` | оба | `Task? t = null; if (c) t = Task.Run(...); try { await t!; } catch { }` рядом с тем же `try`/`await` над всегда присвоенным handle | `/dropped` DCA1001; `/taken` нет | TD-067, TD-069 | — |
| `dispose-async-configure-await` | negative | `await timer.DisposeAsync().ConfigureAwait(false)`, затем запись поля, которое пишет callback | нет | TD-061, TC-12 | — |

## Фаза 4 — написаны

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `interlocked-increment` | negative | `Interlocked.Increment(ref _count)` в action | нет | TD-082 | nD |
| `interlocked-compare-exchange-loop` | negative | CAS-цикл: чтение в local, вычисление, `CompareExchange` | нет | TD-072, TD-082 | nD |
| `interlocked-mixed-with-plain-write` | positive | `Interlocked.Increment` в одном worker-е, `_count = 0` в другом | DCA1001 (вопрос 5) | FR-07, TD-082 | ⚠ |
| `volatile-rmw-not-atomic` | positive | `volatile int _hits; _hits++` в action | DCA1002 | TD-082, FR-09 | — |
| `volatile-read-write-flag` | negative | `Volatile.Write(ref _stop, true)` в action, `Volatile.Read(ref _stop)` в worker-е | нет | TD-082 | nD |
| `lock-different-identity` | positive | Два worker-а пишут поле под `lock (_a)` и `lock (_b)` | DCA1003 `different-identity` | TD-081, FR-07 | ⚠ |
| `lock-protected-vs-unprotected` | positive | Один worker пишет под `lock`, другой без | DCA1003 `partial` | FR-07 | ⚠ |
| `lock-on-fresh-object` | positive | `lock (new object())` в методе singleton-а, два worker-а | DCA1003 | TD-044, TD-081 | ⚠ |
| `monitor-try-enter` | оба | Поле A пишется внутри `if (Monitor.TryEnter(gate))` с `Exit` в `finally`; поле B после `TryEnter` без проверки результата | `/guarded` нет; `/unguarded` DCA1003 | TD-080 | nD / ⚠ |
| `system-threading-lock` | оба | `System.Threading.Lock` во всех трёх формах: `Enter`/`Exit` в `finally`, `using (_gate.EnterScope())`, `lock (_gate)`; второе поле пишется под `lock (_gate)` и под `lock ((object)_gate)`. Компилятор не переписывает `lock` над `Lock` ни в enter, ни в exit и не даёт региона, поэтому секция читается из самого оператора; там, где её тело компилятор разложил по нескольким блокам, форма не доказывает защиты | `_onDuty` нет; `/mixed-mechanisms` DCA1003 `incompatible-mode` | TD-080, TD-083 | nD / ⚠ |
| `mutex-in-process` | negative | `Mutex.WaitOne` и `ReleaseMutex` в `finally` в двух worker-ах | нет | TD-083 | nD |
| `semaphore-slim-capacity-one` | negative | `new SemaphoreSlim(1, 1)`, `await WaitAsync()` и `Release()` в `finally` | нет | TD-083 | nD |
| `semaphore-slim-not-a-mutex` | positive | По полю на вариант: capacity из параметра конструктора; константа 2; `Release` вне `finally` | `/unknown-capacity`, `/capacity-two`, `/release-not-in-finally` DCA1003 `partial` | TD-083, PRD 7 п. 2 | ⚠ |
| `reader-writer-lock-slim` | оба | Поле A: чтение под `EnterReadLock`, запись под `EnterWriteLock`; поле B: два worker-а пишут под `EnterReadLock` | `/read-vs-write` нет; `/write-under-read-lock` DCA1003 `incompatible-mode` | TD-083 | nD / ⚠ |
| `spin-lock-not-protection` | positive | Оба writer-а под `SpinLock.Enter`/`Exit` | DCA1001 | PRD 3, TD-086 | — |
| `custom-async-lock-releaser` | negative | Пользовательский `AsyncLock` на `SemaphoreSlim(1, 1)` с releaser-ом `IDisposable`; оба writer-а под `using (_lock.Enter())`. Область берётся синхронно: анализ пока не проводит результат async-метода до объекта, который тот вернул, поэтому у `Dispose` над `await`-полученным releaser-ом нет ни одного разрешённого callee | нет (вопрос 6) | TD-086, ADR 0009 | ⚠ |
| `callee-joins-on-a-parameter` | negative | Метод, принимающий `Task` параметром и ждущий его на всех путях, вызванный из local и inline; записи по полю на форму | `/local`, `/inline` нет | ADR 0009, вопрос 15 | ⚠ |
| `concurrent-dictionary-atomic-ops` | negative | `GetOrAdd`, `AddOrUpdate`, `TryUpdate` из action и worker-а | нет | TD-085 | nD |
| `concurrent-dictionary-compound` | positive | `if (!d.ContainsKey(k)) d[k] = v` и `d[k] = d[k] + 1` на разных словарях | `/contains-then-set`, `/indexer-increment` DCA1004 | TD-085 | ⚠ |
| `concurrent-bag-count-then-add` | positive | `if (bag.Count < Limit) bag.Add(x)` в action | DCA1004 | TD-085 | ⚠ |
| `concurrent-queue-single-ops` | negative | `Enqueue` в action, `TryDequeue` в worker-е | нет | PRD 3 | nD |
| `concurrent-dictionary-enumerate-while-mutate` | оба | Action перечисляет `foreach` два словаря, worker пишет в оба: thread-safe и обычный | `/concurrent-dictionary` нет; `/plain-dictionary` DCA1001 по структуре и по записанной ячейке | TD-085, ADR 0010 | ⚠ |
| `dictionary-disjoint-keys-structural` | positive | Обычный `Dictionary`: два worker-а пишут константные ключи `"a"` и `"b"` | DCA1001 на structural resource | TD-070 | ⚠ |
| `static-list-add` | positive | `static readonly List<string>`, `Add` из action | DCA1001, self-pair на структуре и на ячейке, которую `Add` не называет (вопрос 2) | TD-070, ADR 0010 | ⚠ |
| `array-disjoint-constant-indices` | negative | Два worker-а пишут `_slots[0]` и `_slots[1]` | нет, без solver | TD-092, TC-17 | nD (вопрос 1) |
| `array-symbolic-indices` | positive | Два worker-а пишут `_slots[i]` и `_slots[j]`, индексы из независимых источников | DCA1001, SAT model | TD-092, TC-17 | вопрос 1 |
| `array-disjoint-guarded-ranges` | negative | `if (i < 5) _slots[i] = …` и `if (j >= 5) _slots[j] = …` | нет, UNSAT: предикат по локальному индексу и выражение ячейки называют одно значение одинаково, поэтому `i < 5`, `j >= 5` и `i == j` попадают в один запрос | TD-043, TD-092 | nD |
| `parallel-for-disjoint-index` | negative | `Parallel.For(0, n, i => results[i] = …)` | нет | TD-068, TC-17 | nD |
| `span-slices-disjoint` | negative | Два worker-а пишут через `_buffer.AsSpan(0, 8)` и `_buffer.AsSpan(8, 8)` | нет: индексатор, возвращающий ссылку, понижается в element operation над срезаемым массивом, срез сводится к смещению, и ячейки `[0]` и `[8]` разведены | TD-043 | nD |
| `index-overflow-wraps` | positive | `_slots[(byte)i]` и `_slots[(byte)(i + 256)]`: алгебраически разные, равны после conversion | DCA1001 | TD-043, TC-17 | вопрос 1 |
| `key-equality-comparer` | оба | `ConcurrentDictionary`, compound `TryGetValue` + indexer set в двух worker-ах: ключи `"a"`/`"b"`; `"a"`/`"A"` с `StringComparer.OrdinalIgnoreCase`; пользовательский comparer | `/ordinal-distinct` нет; `/ignore-case-equal` DCA1004; `/custom-comparer` DCA1004 с uncertainty | TD-043 | ⚠ |
| `mutually-exclusive-paths` | negative | Singleton-опции с `readonly bool IsPrimary`; worker A пишет под `if (IsPrimary)`, worker B под `if (!IsPrimary)`; вариант со `switch` по enum | `/boolean`, `/enum-switch` нет, UNSAT (вопрос 10) | TD-090, TD-092, 12.2 | nD |
| `unsupported-guard-kept` | positive | Та же форма с guard `Name.StartsWith("x")` и его отрицанием | DCA1001 с uncertainty «unsupported predicate» | TD-095 | — |

## Фаза 5 — подфазы

Фаза 5 SPEC разбита 2026-09-23 на подфазы 4b и 5a–5e ([SPEC 14.3](../docs/SPEC.md)). Кейсы прежнего раздела «Фаза 5» разложены по ним без изменения намерения, кроме трёх правок: вопросы 7 и 12 закрыты решениями из [CONTEXT.md](../CONTEXT.md), а `ef-core-no-db-verdict` переехал в 5e, потому что «нет находки» требует доказанного entity tracking. Кейсы 4b, 5e и вопросы 20–21 добавлены при разбиении. В колонке «До фазы» ранний анализатор — это предыдущая подфаза.

## Фаза 4b — остатки фазы 4

Кейсы `iterator-created-under-lock`, `iterator-enumerated-under-lock`, `iterator-escapes-to-field` и `iterator-held-monitor` проверяют исполнение тела при перечислении, неизвестное исполнение и свойства удержаний через `yield return`. Кейс `async-tail-acquisition` проверяет, что невыжданный async-вызов поднимает к вызывающему только синхронный префикс.

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `index-guard-at-call-site` | оба | Два worker-а вызывают запись `_slots[k] = v` с индексами под непересекающимися guard-ами; второй вариант передаёт индекс через два уровня вызовов, третий использует пересекающиеся диапазоны; по массиву и методу на вариант | `/disjoint`, `/two-levels` нет; `/overlapping` DCA1001 | TD-090, TD-092, вопрос 17 | nD / — |
| `index-from-field-guarded` | negative | Worker читает индекс из поля, проверяет его и пишет элемент, другой worker пишет ячейку вне проверенного диапазона | нет: guard и индекс используют одно прочитанное значение | TD-092, вопрос 17 | nD |
| `constant-argument-cells` | negative | Два worker-а передают разные константы в общий метод записи элемента массива | нет: точные ячейки различаются без solver | TD-043, TD-092 | nD |
| `custom-slice-offsets` | оба | Пользовательская структура-окно над массивом: `Slice(start, length)` и ref-returning индексатор `ref _array[_offset + i]`; два worker-а пишут `[0]` окон `Slice(0, 8)` и `Slice(8, 8)`; в другом варианте смещение хранится в изменяемом поле; по массиву на вариант | `/proven-offset` нет: смещение доказано из тел и `readonly`-полей; `/mutable-offset` DCA1001 на `[?]` | TD-043, вопрос 18 | вопрос 18 |
| `ref-local-write` | positive | Один worker пишет поле через ref local, другой увеличивает то же поле | DCA1002 на поле | TD-012, вопрос 18 | — |
| `ref-return-write` | positive | Один worker пишет элемент через ref return, другой пишет тот же элемент прямо | DCA1001 на `[0]` | TD-012, вопрос 18 | — |
| `out-and-ref-arguments` | positive | Поле или элемент singleton-а передаётся как `out` либо `ref` аргумент метода с исходником; другой worker обращается к тому же месту | `/out` DCA1001; `/ref` DCA1002 | TD-012, вопрос 18 | — |
| `iterator-created-under-lock` | positive | Worker создаёт итератор под `lock`, перечисляет вне него и тем самым пишет поле в теле; другой worker пишет поле под тем же `lock` | DCA1003 `partial` | TD-060b, вопрос 19 | — |
| `iterator-enumerated-under-lock` | negative | Worker создаёт итератор вне `lock`, перечисляет под ним; другой worker пишет то же поле под тем же `lock` | нет | TD-060b, вопрос 19 | nD |
| `iterator-escapes-to-field` | positive | Worker кладёт итератор в свойство singleton-а; видимого перечисления нет, тело пишет поле | DCA1001: неизвестное исполнение пересекается с собой | TD-060b, вопрос 19 | — |
| `iterator-held-monitor` | positive | Итератор берёт `Monitor` и делает `yield return` без освобождения; вызывающий пишет после цикла и освобождает в `finally`, другая сторона пишет под `lock` | DCA1003 `partial`: удержание поднято через приостановку | TD-083, TD-060b, вопрос 19 | — |
| `async-tail-acquisition` | positive | Async-метод берёт `SemaphoreSlim(1, 1)` только после `await Task.Yield()`; worker вызывает его без `await`, пишет поле и освобождает семафор в `finally`; другой worker пишет поле под тем же семафором | DCA1003 `partial`: захват хвоста не поднимается к вызывающему | TD-060a, ADR 0009, вопрос 22 | nD |

## Фаза 5a — известные вызовы

Семейства таблицы TD-034a проверяются contract tests (см. «Вне demo»); demo показывает эффект, который меняет находку. Состав таблицы взят из переписи вызовов метаданных в demo и eShopOnContainers 2026-09-24, а не из представления о том, что встречается.

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `json-serialize-reads-deep` | positive | GET сериализует singleton через `JsonSerializer.Serialize`, worker пишет вложенное поле | DCA1001 `Deterministic` по эффекту `reads-deep` | TD-034a | — |
| `logger-arg-reads-deep` | positive | Action передаёт объект singleton-а аргументом шаблона `LogInformation`, worker пишет его поле | DCA1001 по deep read аргумента `args` | TD-034a | — |
| `ef-add-shared-entity` | positive | Один worker создаёт `new` исходный подкласс `DbContext` и добавляет в него через `Add` сущность, которую держит singleton; другой worker пишет поле этой сущности; нужен пакет EF Core | DCA1001 по записи аргумента `entity` | TD-034a | — |

## Фаза 5b — неизвестные вызовы без AI

Resolver в 5b не вызывается: gaps видны в coverage и uncertainty, а resolver evals по этим кейсам добавляются в 5d.

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `unknown-call-model-not-noop` | positive | Singleton-список передаётся методу того же пакета, который его мутирует | gap не разрешён; finding с uncertainty: неразрешённый `UnknownEffect` конфликтует как запись (вопрос 12) | TD-025, TD-034 | — |
| `reflection-primitive-args-no-gap` | negative | `MethodInfo.Invoke` статического метода только со строками и числами | нет gap; проверка: тест coverage, с 5d resolver evals | TD-034 | — |
| `library-table-no-gap` | negative | `ILogger`, `JsonSerializer`, `HttpClient` получают singleton-объекты | нет gap; проверка: тест coverage, с 5d resolver evals | TD-034a | — |
| `gap-materiality-order` | gap | Два gap: один достигает трёх roots и двух регионов, другой одного | порядок gaps в coverage, с 5d и очереди `get_gaps`; проверка: тест coverage, с 5d resolver evals | TD-039a | — |
| `opaque-task-source` | positive | Хелпер возвращает либо известную запущенную задачу, либо задачу из opaque-фабрики; родитель `await`-ит результат и пишет поле работы. Отложен из фазы 3: opaque-вызов становится gap по TD-034, а метку находки решает TD-108 (вопрос 16) | DCA1001 | TD-034, TD-108 | — |
| `mixed-source-timer` | positive | Подписка на `c ? knownDisabledTimer : factory.Get()`; callback пишет поле, action читает. Отложен из фазы 3 по той же причине | DCA1001 и self-pair: смешанный источник периодический | TD-034, TD-061 | — |
| `channel-handoff` | negative | Producer пишет объект в `Channel<T>` и продолжает его менять, consumer читает | нет находки: объект producer-а `Escaped`, результат чтения с ним не связан, пара видна как uncertainty; проверка: тест отчёта (вопрос 7) | TD-086, PRD 3 | — |

## Фаза 5c — проверка AI-фактов

Ответы resolver подаются in-process из рукописных файлов, написанных до реализации; живого AI в gate нет. Вопрос 21 решается до записи ожиданий этих кейсов.

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `reflection-invoke-target` | positive | `GetMethod("Bump").Invoke(_counter, null)` в action, `Bump` делает RMW | gap; accepted target `Bump`; DCA1002 `AI-Assisted`, метка по TD-108 | TD-036, TD-037, TD-108 | — |
| `reflection-unresolvable-name` | gap | Имя метода приходит из запроса | принятого факта нет; gap в coverage с materiality; проверка: resolver evals | TD-036, TD-039 | — |
| `dynamic-call-target` | positive | `dynamic sink = _sink; sink.Record(path)` в action | gap; accepted target; DCA1001 `AI-Assisted` | TD-034 | — |
| `memory-cache-returns-shared-object` | positive | `IMemoryCache.GetOrCreate("settings", _ => new Settings())` отдаёт один объект всем запросам; action пишет в него | gap; accepted `ReturnAlias`; DCA1001 `AI-Assisted`; если `IMemoryCache` войдёт в таблицу TD-034a, кейс станет deterministic | TD-034a, TD-036 | ⚠ |
| `unknown-library-captures-delegate` | positive | Метод пакета вне таблицы получает лямбду, которая пишет singleton; пакет выбирает план 5c | gap `captures-delegate`; DCA1001 `AI-Assisted` | TD-034, TD-036 | — |

## Фаза 5d — протокол resolver

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `gap-second-round` | gap | Reflection указывает на метод с `dynamic`-вызовом, цель которого делает ещё один reflection-вызов | rounds 1 и 2 разрешены; gap третьего уровня в coverage; проверка: resolver evals | TD-131, TC-05 | — |

## Фаза 5e — остаток семантики

AutoMapper и FluentValidation проверяются contract tests таблицы; demo-кейс для них нужен, только если план 5e найдёт форму, которую contract test не видит.

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `ef-core-no-db-verdict` | negative | Scoped `DbContext`, два запроса делают `item.Stock--` и `SaveChangesAsync`; нужен пакет EF Core | нет находки и нет DB verdict (вопрос 14) | PRD 7 п. 4, TD-034a | ⚠ |
| `polly-execute-invokes-delegate` | positive | `ResiliencePipeline.Execute(() => _state.Hits++)` в action над singleton-ом; нужен пакет Polly | DCA1002 `Deterministic`: делегат выполняется синхронно в исполнении action | TD-034a | — |
| `mediatr-send-dispatches-handler` | positive | Action `await _mediator.Send(new Bump())`; обработчик `Bump` делает `_state.Hits++` над singleton-ом; нужен пакет MediatR | DCA1002 `Deterministic`: `Send` вызывает обработчик, найденный по типу запроса | TD-034a | — |
| `thread-static-slot` | оба | `[ThreadStatic]` счётчик: `_hits++` в action; второе `[ThreadStatic]` поле получает ссылку на список singleton-а, и action делает `Add` | `/slot` нет, как и запись самого слота: у каждого потока своя ячейка, а синхронный `++` смену потока не пересекает; `/shared-object` DCA1001 на структуре и ячейке списка: локален слот, а не объект | TD-051, вопрос 8 | nD |
| `thread-local-values` | positive | `ThreadLocal<List<int>>` с `trackAllValues: true`: action добавляет в список своего потока, worker перечисляет `Values` | DCA1001 action × worker на структуре списка; action × action нет | TD-052, вопрос 8 | ⚠ |
| `async-local-flow` | оба | `AsyncLocal<Counter>`: action кладёт новый объект и делает `Hits++`; вариант, где action запускает `Task.Run` с `Hits++` и делает `Hits++` сам до `await` задачи | `/per-request` нет: значение течёт только в своё исполнение; `/flows-to-child` DCA1002: дочерняя задача получает тот же объект и пересекается с родителем до join | TD-065, вопрос 8 | nD / — |
| `keyed-singleton-services` | оба | `AddKeyedSingleton<Counter>` с ключами `"a"`, `"b"` и `"c"`; worker-ы с `[FromKeyedServices]` делают `Value++`: два с ключами `"a"` и `"b"`, два с ключом `"c"` | `/distinct-keys` нет; `/same-key` DCA1002 | TD-040, TD-121, вопрос 20 | nD / — |
| `type-valued-registration` | positive | `AddSingleton(typeof(IStats), typeof(Stats))`; два worker-а получают `IStats` и делают `Hits++` | DCA1002 | TD-040, TD-121 | ⚠ |

## Фаза 6 — Triage

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `suppress-attribute` | оба | Атрибут `ConcurrencyHunterSuppress` объявлен в demo; по singleton-у на вариант: точный rule и reason; пустой reason; expiry в прошлом; чужой rule ID | `/exact` скрыт и в suppressed summary; `/empty-reason`, `/expired`, `/wrong-rule` в отчёте с диагностикой; проверка: тест отчёта | TD-104, TC-13 | — |
| `suppress-file-fingerprint` | оба | Запись `.concurrency-hunter/suppressions.json` по fingerprint и невалидная запись | `/exact` скрыт; `/invalid` в отчёте с диагностикой (вопрос 9) | TD-104, TC-13 | — |
| `generated-code-access` | оба | Файл с заголовком `// <auto-generated/>`: запись внутри generated кода; generated helper вызывает user-метод с записью | `/access-in-generated` в `findings.json`, не в отчёте; `/path-through-generated` в отчёте; проверка: тест отчёта | FR-03, 9.3 | — |
| `redaction-in-snippet` | positive | Рядом с access строковый литерал вида connection string с паролем | snippet в отчёте после redaction (вопрос 13) | TC-11 | — |
| `multi-target-project` | оба | Библиотека `net8.0;net9.0;net10.0`: гонка под `#if NET10_0_OR_GREATER`, другая под `#if !NET10_0_OR_GREATER`; нужны targeting packs .NET 8 и 9 | `/newest-tfm` DCA1001; `/older-tfm` нет; выбор TFM в coverage | SPEC 4.1 | nD |

## Фаза 7

Новых кейсов demo нет: корпуса eShopOnContainers, nopCommerce, OrchardCore, eShopOnAbp и `skills/hunt/evals/<corpus>/expected.json`. Подтверждённый на корпусе FP или FN становится минимальным кейсом по SPEC 12.3 в той фазе, где он найден.

## Вне demo

| Сценарий | Где проверяется |
|---|---|
| TC-02: отсутствие Cartesian | `metrics` |
| TC-03, TC-04, TC-16, TC-18: synthetic provider, duplicate id, непроверенная область против пустого результата | Provider fixture в `ConcurrencyHunter.Core.Tests` |
| TC-15: малый context budget не теряет may-effects | Unit test с уменьшенной константой |
| TD-123, TD-124: таблица библиотек — exact overload, version range, duplicate и conflicting declarations, out-of-range в coverage | Contract tests в `ConcurrencyHunter.Core.Tests`, семейства 5a и 5e |
| TC-05: отказ invented и incompatible гипотез | Resolver evals на пакетах `reflection-invoke-target` и `dynamic-call-target` |
| TC-06, TC-14: отказ narrative, `verify manually`, категории remediation | Composer evals на `rmw-singleton-counter` (атомарность), `escape-into-singleton` (владение), `fire-and-forget-vs-awaited` (порядок) |
| TC-09: resolver недоступен | Plugin tests: demo с gaps даёт `Incomplete`, target без gaps `Complete` |
| TC-10: deadline, late responses, cancellation | Plugin tests |
| TC-13: fingerprint при сдвиге строк и при смене semantic cause | Plugin tests на копии demo |
| Robustness: malformed project, недоступный Z3, недоступный AI | Tests с собственными fixtures; в `Demo.slnx` не входят, иначе гейт падает на `LoadComplete` |
| TC-19: три hosts | `skills/hunt/evals/<host>/` |
| TC-21: изменение Common | Baseline cache-detective |

## Открытые вопросы

Решаются в плане указанной фазы до написания кейса.

| # | Вопрос | Кейсы | Фаза |
|---|---|---|---|
| 1 | Закрыт в фазе 4: ячейка это отдельный сегмент `accessPath` сразу после поля коллекции — `[0]` для доказанной константы, `["a"]` для доказанного ключа, `[0..8]` для консервативного диапазона, `[?]` для всего остального; сегмент входит в идентичность ресурса, а к самой коллекции обращаются без него (SPEC 12.2, TD-043, [ADR 0010](../docs/adr/0010-a-collections-structure-is-a-resource-of-its-own.md)) | selector-кейсы, `dictionary-disjoint-keys-structural`, `static-list-add` | 4 |
| 2 | Закрыт в фазе 4: обычные `List<T>` и `Dictionary<TKey, TValue>` моделируются здесь же, по таблице членов ADR 0010, наравне с concurrent-коллекциями; общая таблица семантики библиотек TD-034a остаётся в фазе 5, где по ней же решается escape через collection insertion | `static-list-add`, `dictionary-disjoint-keys-structural` | 4 |
| 3 | Закрыт. Даёт ли `ContinueWith` happens-before от antecedent к continuation. Да: continuation упорядочен после antecedent, когда antecedent доказанно её receiver; отделившаяся работа antecedent не упорядочена | `continue-with` | 3 |
| 4 | Закрыт в фазе 4: enumerate + mutate это не составная операция и не DCA1004. Перечисление читает структуру, вставка её пишет; на thread-safe коллекции обе операции атомарны по структуре и пары нет, на обычной не атомарна ни одна и срабатывает обычное правило конфликта. DCA1004 остаётся потерей обновления и не расширяется (ADR 0010, TD-085) | `concurrent-dictionary-enumerate-while-mutate` | 4 |
| 5 | Закрыт. Atomic операция против незащищённой записи: DCA1001. Атомарность это свойство операции по TD-071 и TD-072, а не защита: пара, у которой одна сторона неатомарна, остаётся обычным незащищённым конфликтом, и `protectionAnalysis` о ней ничего не знает | `interlocked-mixed-with-plain-write` | 4 |
| 6 | Закрыт [ADR 0009](../docs/adr/0009-a-synchronization-wrapper-is-transparent-never-a-lock-type.md): обёртка прозрачна, а не распознана. Защиту доказывает тот же must-hold анализ на регионе нижележащего примитива, если захват сводится к моделируемому примитиву с доказанной identity, освобождение сводится к нему же и происходит на всех путях | `custom-async-lock-releaser` | 4 |
| 7 | Закрыт 2026-09-23: в первой версии результат `ChannelReader.ReadAsync` с записанным объектом не связан — ни детерминированно, ни принятым фактом. Записанный объект `Escaped`, пара producer–consumer видна как uncertainty и никогда как безопасность; связь приходит со второй волной `Channel` (PRD 8, [CONTEXT.md](../CONTEXT.md)) | `channel-handoff` | 5b |
| 8 | Закрыт: конструкторы и type initializers решены в [ADR 0006](../docs/adr/0006-a-construction-belongs-to-the-execution-that-triggers-it.md); ownership `[ThreadStatic]`, `ThreadLocal`, `AsyncLocal` переносится в подфазу 5e | `static-constructor-initialization` и кейсы «Фаза 2 — конструирование»; thread-local кейсы в разделе 5e | 5e |
| 9 | Корень репозитория для `.concurrency-hunter/suppressions.json`, когда demo лежит внутри репозитория CodexPlugins (14.1 п. 6) | `suppress-file-fingerprint` | 6 |
| 10 | Закрыт в фазе 4: да, но только когда доказано всё сразу — регион представляет ровно один объект на процесс (статическое хранилище, DI singleton или allocation site, выполняющийся не более одного раза), поле `readonly` или get-only auto-property, вне конструирования своего региона его никто не пишет, и чтение происходит после этого конструирования. Всё остальное это значение одного исполнения, которого не разделяет никакое другое, и пару оно снять не может. Граница односторонняя: ошибка здесь подавляет настоящую гонку (TD-090, TD-092) | `mutually-exclusive-paths`, `unsupported-guard-kept` | 4 |
| 11 | Закрыт: файл ⚠-кейса входит в demo в той же задаче, что и изменение движка, дающее его находкам записанную identity. Matcher новой фазы включается в первой задаче, если каждый кейс входит вместе с таким изменением, как в 4b; иначе — в последней | все ⚠ | план каждой фазы |
| 12 | Закрыт в интервью фазы 5: да. Неразрешённый opaque-вызов может читать и писать всё, что достижимо из receiver и аргументов, поэтому с любой стороны пары конфликтует как запись и никогда не доказывает безопасность ([CONTEXT.md](../CONTEXT.md)) | `unknown-call-model-not-noop` | 5b |
| 13 | Что именно redaction убирает из snippets | `redaction-in-snippet` | 6 |
| 14 | Region и ownership сущности, которую вернул opaque persistence-вызов EF Core. Частично закрыт в интервью фазы 5: сущность принадлежит `DbContext` только при доказанном entity tracking, а недоказанный tracking не доказывает и изоляцию ([CONTEXT.md](../CONTEXT.md)). Поэтому до 5e материализация EF изоляцию не доказывает; region при доказанном tracking и без него решает план 5e | `ef-core-no-db-verdict` | 5e |
| 15 | Закрыт в фазе 4: связывание параметра именует экземпляр, а не summary, поэтому handle, переданный параметром, доказан так же, как прочитанный из поля, и `void Run(Task t) => t.Wait();` упорядочивает вызывающего | `callee-joins-on-all-paths`, `callee-joins-on-a-parameter` | 4 |
| 16 | Создаёт ли вызов нераспознанного interface-метода semantic gap по TD-034 и опускает ли TD-108 метку таких находок до Medium: от этого зависит, можно ли писать кейсы неизвестного происхождения задачи и таймера раньше фазы 5 | `opaque-task-source`, `mixed-source-timer` | 5b |
| 17 | Закрыт в фазе 4b: guard передаётся по каждому пути от root к access и связывается с входным значением параметра по значению; переприсвоение и `ref`/`out`/`in` не связываются. При превышении 16 путей условия выше слияния теряются только для точности; разные точные константные ячейки разводятся без solver | `index-guard-at-call-site`, `index-from-field-guarded`, `constant-argument-cells` | 4b |
| 18 | Закрыт в фазе 4b: access через ссылку относится ко всем доказанным местам в точке чтения или записи, а недоказанная связь учитывается в coverage. Смещение пользовательского среза доказывается только из тела через `readonly`-поля, заданные при конструировании из связанных аргументов; иначе ячейка `[?]`. Имя члена чужого типа не служит доказательством | `custom-slice-offsets`, `ref-local-write`, `ref-return-write`, `out-and-ref-arguments` | 4b |
| 19 | Закрыт в фазе 4b: тело итератора исполняется при перечислении по ADR 0011, а сбежавший итератор получает неизвестное исполнение. Удержание, открытое вызываемым и закрытое вызывающим, сохраняет пересечение `yield return` и непарность permits по поправке ADR 0009; после невыжданного async-вызова поднимается только синхронный префикс | `iterator-created-under-lock`, `iterator-enumerated-under-lock`, `iterator-escapes-to-field`, `iterator-held-monitor` | 4b |
| 20 | Как `resource.region` из SPEC 12.2 называет keyed-регистрацию: `di:<ImplementationType>@<Lifetime>` не различает ключи, и запись `notDefects` на одном ключе запретила бы находку на другом | `keyed-singleton-services` | 5e |
| 21 | Снимает ли принятый target или эффект консервативную модель того же opaque call site. Если нет, у кейса 5c рядом с `AI-Assisted`-находкой остаётся находка от `UnknownEffect` — на wildcard-ресурсе receiver-а или на той же паре, но `Deterministic`, — и ожидания 5c обязаны её назвать. TD-038 запрещает удалять deterministic effect, но не говорит, считается ли им модель по умолчанию | кейсы 5c | 5c |
| 22 | Закрыт в фазе 4b измерением на движке до правки: захват синхронного префикса невыжданного async-вызова поднимался к вызывающему верно, а захват после первого `await` тоже поднимался и ложно защищал запись вызывающего. Исправлено: вызывающий получает только удержанное везде, где тело может вернуть управление, — на каждом `await` и в конце тела; выжданный вызов по-прежнему поднимает всё, что тело оставляет открытым | `async-tail-acquisition` | 4b |
| 23 | Отложен из код-ревью 4b (F-0071): пишет ли `new External(out state.Value)` к конструктору без исходника в `state.Value` в месте вызова, как с 4b это делает вызов метода без тела. Правило то же; на создании объекта его нет, и access теряется | кейс не написан | 5b |
| 24 | Отложен из код-ревью 4b (F-0072): считается ли сбежавшим итератор, который захватила лямбда, если делегат сохранён в поле без видимого вызова. Побег проверяется по полям, но не по ячейкам захвата, поэтому неизвестное исполнение не создаётся и доступы тела теряются | кейс не написан | 5b |
| 25 | Отложен из код-ревью 4b (F-0070): классифицируется ли как DCA1002 чтение и зависимая от него запись через одну ссылку в раздельных операторах (`ref int r = ref state.Value; var old = r; r = old + 1;`). Сейчас пара остаётся, но как DCA1001: зависимость ищется только среди полевых access | кейс не написан | 5b |
| 26 | Закрыт после код-ревью 5a систематическим проходом (остаток F-0018 и F-0042): да. Держатель коллекции — любое поле, которое на неё указывает: экземплярное поле объекта из исходников, статическое поле или поле библиотечного объекта, — каким бы путём known call её ни получил: через локальную переменную, параметр или ячейку другой коллекции. Тем же правилом читаются коллекция в поле библиотечного объекта (`StrongBox<List<T>>.Value`) и готовый срез массива, переданного через локальную переменную; срез массива, которого не держит никакое поле, остаётся недоказанной ссылкой. Проход сравнил эффект с обычным чтением или записью того же поля в 1729 сочетаниях вида коллекции, способа достижения и эффекта; за глубиной 8 и у библиотечного объекта остаётся wildcard по TD-034a | без кейса demo: `KnownCallShapeTests` | 5a |
| 27 | Закрыт после код-ревью 5a (остаток F-0043): сущности пользовательской последовательности — каждый достижимый из неё объект, на любой глубине и через любого держателя, тип которого совместим с `T` её `IEnumerable<T>`; без `T` или при `T = object` — каждый достижимый объект. Перечислитель может отдать любой из них, а known call его не исполняет; держатели по пути и объекты другого типа читаются, но не пишутся. Последовательность из метаданных вне ADR 0010 вынесена в вопрос 28 | без кейса demo: `ArgumentWriteTests` | 5a |
| 28 | Из вопроса 27: держит ли коллекция вне таблицы ADR 0010 (`HashSet<T>`, `LinkedList<T>`, `Queue<T>`, `Stack<T>`) то, что в неё положил opaque-член. Сейчас куча не связывает её с элементами, поэтому до них не доходят ни deep read (`Enumerable.Count`), ни запись аргумента EF (`AddRange`), и обычный код не перечисляет их тоже. Решается вместе с моделью неизвестного вызова: либо receiver opaque-вызова держит свои аргументы, либо таблица ADR 0010 расширяется | кейс не написан | 5b |
| 29 | Из прохода по вопросам 26–27: эффект `returns-arg` у операторов LINQ, сохраняющих элементы источника (`Take`, `Skip`, `ToList`, `ToArray`, `AsEnumerable`, `Cast`, `OfType`). `AddRange(items.Take(1))` не пишет ни одной сущности, хотя `Take` уже прочитал их deep: результат known call без этого эффекта ни с чем не связан, а TD-034a откладывает `returns-arg` до семейства, которому он нужен | кейс не написан | 5e |
| 30 | Из проверки перечисления 2026-09-24: хранит ли куча элементы `List<T>` и `Dictionary<TKey, TValue>` из таблицы ADR 0010, как хранит элементы массива. Сейчас нет: результат индексатора и `Current` перечислителя списка ни на что не указывает, поэтому `_state.Items[0].Value = 1` и `x.Value = 1` в `foreach` по списку не дают ни одного access, а тот же код над массивом (`_state.C[0].Value = 1`) даёт. Элементы списка знает только deep read known call, по вставкам. Решается вместе с вопросом 28: вставка пишет, а чтение ячейки читает хранилище элементов коллекции в куче | кейс не написан | 5b |

**Как закрывать остаток.** Четыре раунда код-ревью фазы 4 дали 20, 8, 8 и 7 находок, и около половины каждого раунда были следствиями правок предыдущего: правило вводилось в одном слое и не применялось в соседнем, либо две независимо добавленные фичи фазы не сочетались друг с другом — атомарность на элементе и `Span` не знали друг о друге, пока это не проверили попарно. Мутационный тест доказывает, что правка что-то меняет, и не доказывает, что она не открыла дыру рядом. Поэтому остаток этого хвоста дешевле закрывать систематическим проходом — перечислить все распознаватели, все места, где решает каждое правило, и все пары фич, обязанных сочетаться, — чем новыми раундами ревью.
