# Каталог сценариев demo

| Поле | Значение |
|---|---|
| Статус | Черновик; перечень кейсов demo на все фазы v1 |
| Ожидания | [`expected-findings.json`](expected-findings.json), формат в [SPEC 12.2](../docs/SPEC.md) |
| Фазы | [`docs/PLAN.md`](../docs/PLAN.md) |
| Вопросы | [`docs/QUESTIONS.md`](../docs/QUESTIONS.md) |

Каталог говорит, какие кейсы должны появиться в `demo/` и что каждый проверяет. Он не заменяет `expected-findings.json` и не задаёт его записи: код кейса и ожидания пишутся руками в своей фазе до реализации, как требует SPEC 12.2. Вердикт в каталоге это намерение; если при написании кейса он расходится со SPEC, прав SPEC, а каталог исправляется в той же правке.

## Правила

1. Имя кейса это kebab-имя файла; `имя/суффикс` это отдельная запись того же файла, как `id` в `expected-findings.json`.
2. Один файл, один namespace, регистрация через `Add<Case>()` или `Map<Case>()` в `Program.cs`; общих строк между кейсами нет.
3. Кейс фазы P не содержит конструкций поздних фаз, если они не его предмет: в кейсах фазы 3 нет guards и вызовов, способных стать semantic gap, в кейсах фаз 4, 4b и 5a нет gap-вызовов. Иначе метка confidence сдвинется в поздней фазе, а запись ожиданий по 12.2 не переписывается.
4. Запись в HTTP action всегда даёт self-pair: action пересекается сам с собой. Поэтому action в кейсах про защиту и порядок только читает, а negatives про disjointness строятся на `BackgroundService`, один экземпляр которого сам с собой не пересекается (TD-064). Spawn-кейсы фазы 3 живут внутри одного `BackgroundService`: до фазы 3 у них нет пересечения вообще.
5. Access в лямбде или local function получает symbol содержащего члена (12.2). Если в файле нужны две записи с одинаковой парой symbol/operation на одном resource, тела spawn и callbacks выносятся в именованные методы, иначе записи неразличимы.
6. Кейсы, которые проверяет не `expected-findings.json`, а тест отчёта или evals, помечены в колонке «Ожидание».

### Колонка «До фазы»

Код кейса появляется в своей фазе вместе с изменением движка, которое делает его записи верными; в 4b и 5a matcher новой фазы включён с первой задачи, потому что каждый кейс входит вместе с таким изменением (вопрос 11). В остальных фазах matcher переключается в последней задаче, если кейсы добавляются раньше их реализации. Находка, не совпавшая ни с одной записью файла, это false positive. Запись `findings` совпадает только при том же `rule` и тех же `operation`; запись `notDefects` совпадает по resource без учёта rule. В подфазах фазы 5 ранний анализатор — это предыдущая подфаза.

| Пометка | Значение |
|---|---|
| — | Ранний анализатор находки не даёт или даёт ту же identity |
| nD | Ранняя находка возможна; её прикрывает запись `notDefects`, если resource совпадёт |
| ⚠ | Ранняя находка с другим `rule`, `operation` или resource; прикрыть нечем, см. вопрос 11 |

## Фаза 1a

| Кейс | Тип | Суть | Ожидание |
|---|---|---|---|
| `static-field-unlocked-read-write` | positive | Статик пишет POST, читает GET, без защиты | `/post-self`, `/post-get` DCA1001 |
| `static-field-same-lock` | negative | Тот же статик, все accesses под одним статическим `lock` | нет |

## Фаза 1b

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

## Фаза 2

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

## Фаза 3

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

## Фаза 4

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
| `ref-parameter-chain-beyond-depth` | positive | Два action передают поле singleton по `ref` через девять методов и инкрементируют его в конце | DCA1002 на `Value` | TD-012, вопрос 93 | 5d run A3 |
| `ref-return-chain-beyond-depth` | positive | Два action пишут ячейку массива singleton через `ref`, возвращённый девятью методами | DCA1001 на `_cells[0]` | TD-012, вопрос 93 | 5d run A3 |
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

Resolver в 5b не вызывается: gaps видны в coverage и uncertainty, а resolver evals по этим кейсам добавляются в 5f. Раны фазы и кейсы каждого рана — в [`docs/PLAN.md`](../docs/PLAN.md), раздел 2.

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `unknown-call-model-not-noop` | positive | Worker передаёт список singleton-а `CollectionsMarshal.SetCount` — члену BCL без модели TD-034a и вне таблицы ADR 0010, который его меняет; action читает `Count` | gap не разрешён; DCA1001 на структуре списка с uncertainty: неразрешённый `UnknownEffect` конфликтует как запись (вопрос 12); метка не выше Medium: gap решает operation (TD-039) | TD-025, TD-034 | — |
| `reflection-primitive-args-no-gap` | negative | `MethodInfo.Invoke` статического метода только со строками и числами | нет gap; проверка: тест coverage, с 5f resolver evals | TD-034 | — |
| `library-table-no-gap` | negative | `ILogger`, `JsonSerializer`, `HttpClient` получают singleton-объекты | нет gap; проверка: тест coverage, с 5f resolver evals | TD-034a | — |
| `gap-materiality-order` | gap | Два gap: один достигает трёх roots и двух регионов, другой одного | порядок gaps в coverage, с 5f и очереди `get_gaps`; проверка: тест coverage, с 5f resolver evals. Gap-вызовы стоят в actions, поэтому их `UnknownEffect` даёт DCA1001 Medium между actions и в self-pair каждого (правило 4): на `Slot.Value` и на `Tally.Count`; `readonly`-поле `Board.Slot` эффект только читает | TD-039a | — |
| `opaque-task-source` | positive | Хелпер возвращает либо известную запущенную задачу, либо задачу из opaque-фабрики; родитель `await`-ит результат и пишет поле работы. Отложен из фазы 3: opaque-вызов становится gap по TD-034, а метку находки решает TD-108 (вопрос 16) | DCA1001; метка не выше Medium: gap решает overlap (TD-039) | TD-034, TD-108 | — |
| `mixed-source-timer` | positive | Подписка на `c ? knownDisabledTimer : factory.Get()`; callback пишет поле, action читает. Отложен из фазы 3 по той же причине | DCA1001 и self-pair: смешанный источник периодический; метка не выше Medium: gap решает overlap (TD-039) | TD-034, TD-061 | — |
| `channel-handoff` | negative | Producer пишет объект в `Channel<T>` и продолжает его менять, consumer читает | нет находки; объект, который пишет producer, держит singleton, и запись в канал — gap в coverage; результат чтения с записанным объектом не связан (вопрос 7), и видимая uncertainty пары — этот gap; проверка: тест отчёта | TD-086, PRD 3 | — |
| `unknown-library-captures-delegate` | positive | Worker отдаёт `CancellationToken.Register` — члену без модели TD-034a, не распознанному как spawn или timer, — лямбду, которая пишет singleton; action читает. Перенесён из подфазы проверки AI-фактов: делегат, отданный opaque-вызову, исполняется в неизвестном исполнении (ADR 0011) | DCA1001 `Deterministic`: тело лямбды в неизвестном вызове делегата пересекается с чтением action; со своей же записью — нет: лямбду один раз отдаёт worker, который запускается один раз, и вызов исполняет её по одному разу за раз; метка не выше Medium (gap решает overlap) | TD-034, TD-039, ADR 0011 | — |
| `ref-local-split-read-modify-write` | positive | Action `Post` и worker над одним singleton-ом читают его `int`-поле через `ref`-локальную переменную и отдельным оператором пишут через неё же прочитанное плюс один (вопрос 25) | DCA1002 `Post` с самим собой и с worker: запись зависит от чтения своего же ресурса, и чтение сворачивается в неё, как у поля | вопрос 25 | — |
| `list-element-field-write` | positive | Singleton держит список, заполненный при создании; action `Post` пишет поле `Items[0]`, worker читает то же поле `Items[0]` (вопрос 30) | DCA1001 `Post` с самим собой и с worker: индексатор отдаёт объект, который держит список, как ячейка массива, и запись ложится на него | вопрос 30, ADR 0010 | — |
| `foreach-over-list-element-write` | positive | Singleton держит список, заполненный при создании; action `Post` и worker в `foreach` по нему инкрементируют поле каждого элемента (вопрос 30) | DCA1002 `Post` с самим собой и с worker: переменная цикла — объект, который держит список, как в `foreach` по массиву | вопрос 30, ADR 0010 | — |
| `dictionary-pair-key-and-value` | positive | Singleton держит `Dictionary`, ключи которого — объекты одного типа из исходников, а значения — другого, заполненный при создании; action `Post` и worker в `foreach (var (key, value) in ...)` пишут поле ключа и инкрементируют поле значения (вопрос 30) | DCA1001 на поле типа ключа и DCA1002 на поле типа значения, каждая `Post` с самим собой и с worker; ни одной находки на другом ресурсе: ключи и значения лежат в разных хранилищах словаря, и ни одно не получает доступов другого | вопрос 30, ADR 0010 | — |
| `interface-count-reads-only` | negative | Singleton держит `List<Item>`, заполненный при создании, и отдаёт его как `IReadOnlyCollection<Item>`; action `Post` и worker читают через него `Count` (вопрос 31) | нет находки и нет gap: вызов через интерфейс решается объектом, на который указывает получатель, и `Count` списка только читает его структуру; проверка: тест coverage — оба root-а читают структуру списка, и ни один не делает на нём unknown effect | вопрос 31, ADR 0010 | — |
| `interface-add-and-count` | positive | Singleton держит один `List<Item>` и отдаёт его и как `ICollection<Item>`, и как `IReadOnlyCollection<Item>`; action `Post` добавляет через первый, worker читает `Count` через второй (вопрос 31) | DCA1001 на структуре списка `Post` с самим собой и с worker, DCA1001 на его ячейке `Post` с самим собой, других находок и gap нет — то же, что дают те же члены, вызванные на самом списке | вопрос 31, ADR 0010 | — |
| `array-element-increment` | positive | Singleton держит `int[]`; action `Post(int i)` делает `Slots[i]++`, worker в цикле `for` инкрементирует каждую ячейку (вопрос 33) | DCA1002 на ячейке массива `Post` с самим собой и с worker: инкремент — его полная запись, чтение ячейки и запись в неё прочитанного плюс один | вопрос 33 | — |
| `dictionary-indexer-compound` | positive | Singleton держит `Dictionary<string, int>` с ключом `"hits"`, положенным при создании; action `Post` и worker делают `Counts["hits"] += 1` (вопрос 33) | ровно то, что даёт `Counts["hits"] = Counts["hits"] + 1`: DCA1004 на ячейке `["hits"]` `Post` с самим собой и с worker и DCA1001 на структуре словаря — `Post` с самим собой (чтение/запись и запись/запись) и с worker (чтение/запись, запись/чтение, запись/запись) | вопрос 33, ADR 0010 | — |
| `property-accessor-increment` | positive | Singleton с `private int _level` и `public int Level { get { return _level; } set { _level = value; } }`; action `Post` и worker делают `Level++` (вопрос 33) | DCA1002 на `_level` `Post` с самим собой и с worker: getter читает поле, setter пишет в него прочитанное плюс один; доступы лежат в `set_Level`, находка одна с двумя вхождениями | вопрос 33 | — |

## Фаза 5c — модели библиотек

Раны фазы и кейсы каждого рана — в [`docs/PLAN.md`](../docs/PLAN.md), раздел 2.

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `ref-call-target-assignment` | positive | Singleton `Counter`; action `Post` и worker делают `Cells.At(ref x) = 1; _counter.Count++` с ref-возвратом над локальной переменной | DCA1002 на `Count`: `Post` с самим собой и с worker, без другой находки; тело сохраняет доступ после ref-вызова | вопрос 37 | — |
| `array-length-loop-bound` | positive | Singleton держит `int[]`; action `Post(int i)` инкрементирует `Slots[i]` при `i < Slots.Length`, worker инкрементирует элементы в цикле до `Slots.Length` | Только DCA1002 на ячейке массива: `Post` с самим собой и с worker; `Length` — known call без эффекта и gap | вопрос 35 | — |
| `linq-predicate-runs-now` | positive | Singleton `Tally` держит `List<int> Items`, заполненный при создании, и `Hits`; action `Post` вызывает `_tally.Items.Any(x => { _tally.Hits++; return false; })` | Ровно DCA1002 на `Hits` `Post` с самим собой, оба доступа read-modify-write, confidence high, gap нет: встроенная модель `Any` исполняет предикат в исполнении action (`invoke-now`) | TD-034a | — |
| `linq-where-runs-on-enumeration` | positive | Singleton `Filter` в конструкторе кладёт в `Items` список, а в `Matching` — `Items.Where(x => { Hits++; return true; })`; action `Post` и однократный worker делают `foreach (var _ in _filter.Matching) { }` | Ровно DCA1002 на `Hits`: оба доступа read-modify-write с символом конструктора (правило 5), confidence high, gap нет: лямбда исполняется в перечисляющих исполнениях (`iterator`, ADR 0011), а не при создании | TD-034a, ADR 0011 | — |
| `linq-where-tolist-runs-in-caller` | positive | Singleton `Screen` со списком `Items`, заполненным при создании, и счётчиками `Hits` и `Scans`; action `Post` делает `_screen.Items.Where(x => { _screen.Hits++; return true; }).ToList()`, однократный worker — то же со `Scans` | Ровно DCA1002 на `Hits` `Post` с самим собой, оба доступа read-modify-write, confidence high; `Scans` пишет только однократный worker — не дефект: `ToList` перечисляет `Where` там, где его вызвали | TD-034a, ADR 0011 | — |
| `linq-take-keeps-elements` | positive | Singleton `Shelf` при создании кладёт в `List<Slot> Items` два `Slot`; action `Post` делает `foreach (var item in _shelf.Items.Take(1)) item.Value = 1;`, однократный worker читает `_shelf.Items[0].Value` | DCA1001 high на `Value` слотов `Shelf`: запись `Post` с чтением worker, с самим собой и с глубоким чтением `Take` в `Post` при перечислении: `Take` отдаёт собственные элементы списка | TD-034a, ADR 0011 | — |
| `linq-groupby-key-and-elements` | positive | Singleton `Stock` при создании кладёт в `List<Item> Items` два `Item`, каждый со своим `Bin` из конструктора `Item(Bin)`; action `Post` делает `foreach (var g in _stock.Items.GroupBy(x => x.Group)) { g.Key.Count++; foreach (var x in g) x.Value++; }` | Ровно две DCA1002 high `Post` с самим собой: на `Count` объектов `Bin` (ключ группы — то, что вернул селектор ключа) и на `Value` объектов `Item` (элементы группы — элементы источника); `g.Key` — решённый вызов, не неизвестный эффект | TD-034a, ADR 0011 | — |
| `comparer-create-runs-at-compare` | positive | Singleton `Ranking` при создании кладёт в поле `Order` `Comparer<int>.Create((a, b) => { Hits++; return a.CompareTo(b); })`; action `Post` вызывает `_ranking.Order.Compare(1, 2)` | Ровно DCA1002 на `Hits` `Post` с самим собой, оба доступа read-modify-write с символом конструктора, confidence high, gap нет: держатель исполняет делегат там, где вызывают его член (`holder`), в исполнении action | TD-034a, ADR 0012 | — |
| `project-model-holder-fate` | positive | Singleton `Registry` при создании кладёт в поле `First` новый `Tag`, а в поле `Same` — `EqualityComparer<Tag>.Create((a, b) => { Hits++; return ReferenceEquals(a, b); }, t => 0)`; action `Post` вызывает `_registry.Same.Equals(_registry.First, _registry.First)`. Встроенной модели у `Create` нет: `.concurrency-hunter/models/demo.json` описывает его проектной моделью, оба делегата — `holder` `result` | Ровно DCA1002 на `Hits` `Post` с самим собой, оба доступа read-modify-write с символом конструктора, confidence high, gap нет; вызов считается в `known-call-project` своего scope | TD-034c, ADR 0012 | — |
| `project-model-overrides-built-in` | positive | Singleton `Payload` с `Count`; worker передаёт его в `JsonSerializer.SerializeToUtf8Bytes`, а action `Get` читает `Count`. `.concurrency-hunter/models/demo.json` объявляет вызов opaque вместо встроенного глубокого чтения | Ровно DCA1001 `Medium` на `Count`: неизвестный эффект worker против чтения `Get`; gap `unknown-library` на `SerializeToUtf8Bytes`; `opaque-by-project` равен 1 | TD-034a, TD-034c, вопрос 9 | — |

## Фаза 5d — генератор моделей

Раны фазы и кейсы каждого рана — в [`docs/PLAN.md`](../docs/PLAN.md), раздел 2. AutoMapper и FluentValidation проверяются model evals.

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `interface-call-reaches-override` | positive | Два action вызывают `IStep.Run` на `CountingStep` и `ScoringStep` через интерфейс; базовые реализации virtual и abstract | DCA1002 `High` на singleton `Tally.Hits` и `Score.Points`: вызов через интерфейс исполняет override | TD-060b, вопрос 39 | — |
| `late-receiver-base-call` | positive | Action получает `Runner` из `Runners.Create()` и вызывает базовый `Run` с делегатом, пишущим в singleton `Tally.Hits` | DCA1002 `High` на `Hits` `Post` с самим собой: поздно найденный receiver выполняет делегат только в action | TD-023, вопрос 36 | — |
| `pattern-binds-tested-object` | positive | Action привязывает `Slot` из `Board.Entry`; `Meter.Above` читает property pattern на аргументе `other`; worker пишет `Main` и `Spare` | DCA1002 `High` на `Slot.Count` и DCA1001 `High` на `Spare.Level`; на `Main.Level` дефекта нет | TD-090, вопрос 38 | — |
| `deconstruct-calls-source-method` | positive | Action разбирает singleton `Sensor.Last` через `var (value, _)`, а worker пишет `Reading.Value` | DCA1001 `High` на `Reading.Value`: `Deconstruct` читает поле в action, worker пишет его отдельно | TD-090, вопрос 38 | — |
| `type-test-fast-path` | positive | Два action передают `List<int>` в `EachIs` и `EachAs`; обе функции проверяют быстрый путь `Batch`, затем перечисляют список | DCA1002 `High` на singleton `Tally.Hits` и `Score.Points`: невозможный вызов `Batch.Apply` не исполняется, делегат работает только при перечислении | TD-090, вопрос 38 | — |
| `enumerator-method-runs-at-movenext` | positive | `Shelf.GetEnumerator()` возвращает `IEnumerator<Item>` через `yield`; action перечисляет singleton `Shelf` | DCA1002 `High` на `Item.Seen`: тело `GetEnumerator` работает при `MoveNext` в action | TD-060b, вопрос 39 | — |
| `explicit-enumerator-runs-body` | positive | Action явно вызывает `GetEnumerator()` у `Feed.Touch(...)`, затем `MoveNext()`; другой action перечисляет `Crate` | DCA1002 `High` на `Item.Seen`: тело `Feed.Touch` работает при `MoveNext` в action без неизвестного перечисления | TD-060b, вопрос 39 | — |
| `array-sort-writes-cells` | positive | Singleton `Board` держит два `Item` в `Cells`; action `Post` сортирует их через `Array.Sort` с comparison и инкрементирует `Compared`, worker читает `Rank` каждого элемента | Ровно три находки `High`: DCA1002 на `Compared` `Post` с самим собой, DCA1001 на `Cells.[?]` запись `Post` с чтением worker и с самой собой; сравнение работает в action, чтение ячеек самим `Sort` скрыто его записью, gap на `Sort` нет | TD-034a, вопрос 46 | — |
| `deserialize-returns-new-object` | positive | Hosted worker разбирает `Profile` и публикует его в singleton `ProfileStore.Current`; action инкрементирует `current.Home.Changes` | Ровно две находки `High`: DCA1002 на новом `Address.Changes` `Post` с самим собой, DCA1001 на `Current` запись worker с чтением `Post`; создание графа не даёт доступа к `Home` | TD-034a, вопрос 43 | — |
| `project-model-kept-object` | positive | Action `Post` получает `Settings` из четырёхаргументного `GetOrCreate`; проектная модель исполняет factory сейчас и оставляет результат у cache | Ровно DCA1002 `High` на новом `Settings.Version`, `Post` с самим собой; gap нет | TD-034a, вопрос 43 | — |
| `project-model-try-get-value` | positive | Hosted worker кладёт новый `Session` через `Set`, action получает его через `TryGetValue` в `out` и инкрементирует `Hits`; проектные модели связывают kept storage | Ровно DCA1002 `High` на `Session.Hits`, `Post` с самим собой; gap нет | TD-034a, вопросы 43 и 42 | — |
| `field-like-event-raised-by-worker` | positive | Singleton `TickCounter` в конструкторе подписывает `OnTick` на field-like событие `Tick` singleton-а `Ticker`; hosted worker в цикле вызывает `Fire()`, то есть `Tick?.Invoke()`; action читает счётчик через `Read()` | Ровно DCA1001 `High` на `TickCounter._ticks`: read-modify-write `OnTick` в исполнении worker с чтением `Read` в action; подписка сохраняет обработчик в поле, которое компилятор объявляет для события, а вызов события исполняет его в исполнении worker | TD-012, вопрос 62, ADR 0014 | — |
| `field-like-event-raised-in-request` | negative | Те же `Ticker` и `TickCounter`, зарегистрированные `Scoped`; worker нет, action сам вызывает `Fire()` и читает `Read()` | Дефекта на `TickCounter._ticks` нет: инкремент и чтение есть, оба в исполнении того запроса, которому принадлежат объекты | TD-012, вопрос 62, ADR 0014 | — |
| `awaited-result-shared` | positive | Async-метод singleton-а `TallyStore.GetAsync()` после `await Task.Yield()` возвращает `Tally` из своего поля; action `Post` ждёт его и инкрементирует `Hits` | Ровно DCA1002 `High` на `Tally.Hits` singleton-а, `Post` с самим собой: completion value задачи async-метода — то, что тело вернуло | Вопросы 94 и 105 | — |
| `task-run-result-shared` | positive | Action `Post` ждёт `Task.Run(() => _board.Tally)` над singleton-ом `Board` и инкрементирует `Hits` | Ровно DCA1002 `High` на `Tally.Hits` singleton-а, `Post` с самим собой: задача `Task.Run` завершается тем, что вернула работа | Вопрос 105 | — |
| `completion-source-handoff` | positive | Hosted worker создаёт `Tally`, отдаёт его через `TaskCompletionSource<Tally>` singleton-а `Handoff` (`SetResult`) и дальше в цикле инкрементирует `Hits`; action `Post` ждёт `Source.Task` и инкрементирует `Hits` | Ровно две DCA1002 `High` на `Tally.Hits` объекта worker: `Post` с worker и `Post` с самим собой — настоящая гонка: `SetResult` не завершает исполнение и ничего не упорядочивает | Вопрос 105 | — |
| `awaited-fresh-result` | negative | Async-фабрика singleton-а `TallyFactory.CreateAsync()` возвращает новый `Tally` на каждый вызов; action `Post` ждёт его и инкрементирует `Hits` | Дефекта на `Tally.Hits` нет: каждый запрос пишет свой объект, как в синхронном двойнике `factory-distinct-call-sites` | Вопросы 94 и 105 | — |
| `polly-execute-invokes-delegate` | positive | `ResiliencePipeline.Execute(() => _state.Hits++)` в action над singleton-ом; нужен пакет Polly | DCA1002 `Deterministic`: делегат выполняется синхронно в исполнении action | TD-034b | — |
| `polly-circuit-breaker-callback-at-execute` | positive | Singleton держит конвейер Polly с circuit breaker, чей колбэк открытия инкрементирует счётчик; action выполняет через конвейер операцию, которая бросает; нужен пакет Polly | DCA1002 на счётчике `Post` с самим собой: колбэк исполняется внутри `Execute`, даже если драйвер видел его запуск только из другого члена (держатель по достижимости) | TD-034b | — |
| `protobuf-parser-factory-at-parse` | positive | Своё сообщение `IMessage<Msg>` с `MessageParser<Msg>`, чья фабрика инкрементирует счётчик singleton-а; action разбирает тело запроса через `Msg.Parser.ParseFrom`; нужен пакет Google.Protobuf | DCA1002 на счётчике `Post` с самим собой: фабрика — делегат держателя и исполняется при `ParseFrom` | TD-034b | — |
| `kept-callback-stays-unknown` | positive | Action регистрирует на `HttpContext.RequestAborted` колбэк, который пишет singleton; другой action читает | DCA1001 остаётся: колбэк удержан, вызывает его фреймворк, которого нет среди roots, и он исполняется в unknown execution (правило открытого мира) | TD-034b, ADR 0012 | — |

## Фаза 5e — проверка AI-фактов

Ответы resolver подаются in-process из рукописных файлов, написанных до реализации; живого AI в gate нет. Вопрос 21 решается до записи ожиданий этих кейсов.

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `reflection-invoke-target` | positive | `GetMethod("Bump").Invoke(_counter, null)` в action, `Bump` делает RMW | gap; accepted target `Bump`; DCA1002 `AI-Assisted`, метка по TD-108 | TD-036, TD-037, TD-108 | — |
| `reflection-unresolvable-name` | gap | Имя метода приходит из запроса | принятого факта нет; gap в coverage с materiality; проверка: resolver evals | TD-036, TD-039 | — |
| `dynamic-call-target` | positive | `dynamic sink = _sink; sink.Record(path)` в action | gap; accepted target; DCA1001 `AI-Assisted` | TD-034 | — |
| `memory-cache-returns-shared-object` | positive | `IMemoryCache.GetOrCreate("settings", _ => new Settings())` отдаёт один объект всем запросам; action пишет в него | gap; accepted `ReturnAlias`; DCA1001 `AI-Assisted`; модель TD-034a с `keeps` у cache и возвратом `kept:` сделала бы кейс deterministic | TD-034a, TD-036 | ⚠ |

## Фаза 5f — протокол resolver

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `gap-second-round` | gap | Reflection указывает на метод с `dynamic`-вызовом, цель которого делает ещё один reflection-вызов | rounds 1 и 2 разрешены; gap третьего уровня в coverage; проверка: resolver evals | TD-131, TC-05 | — |

## Фаза 5g — остаток семантики

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `ef-core-no-db-verdict` | negative | Scoped `DbContext`, два запроса делают `item.Stock--` и `SaveChangesAsync`; нужен пакет EF Core | нет находки и нет DB verdict (вопрос 14) | PRD 7 п. 4, TD-034a | ⚠ |
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
| TD-123, TD-124: модели библиотек — exact overload, version range, duplicate и conflicting declarations, out-of-range и слои в coverage | Contract tests встроенных семейств 5a и 5c в `ConcurrencyHunter.Core.Tests`; model evals генератора 5d на эталоне членов (SPEC 12.1) |
| TC-05: отказ invented и incompatible гипотез | Resolver evals на пакетах `reflection-invoke-target` и `dynamic-call-target` |
| TC-06, TC-14: отказ narrative, `verify manually`, категории remediation | Composer evals на `rmw-singleton-counter` (атомарность), `escape-into-singleton` (владение), `fire-and-forget-vs-awaited` (порядок) |
| TC-09: resolver недоступен | Plugin tests: demo с gaps даёт `Incomplete`, target без gaps `Complete` |
| TC-10: deadline, late responses, cancellation | Plugin tests |
| TC-13: fingerprint при сдвиге строк и при смене semantic cause | Plugin tests на копии demo |
| Robustness: malformed project, недоступный Z3, недоступный AI | Tests с собственными fixtures; в `Demo.slnx` не входят, иначе гейт падает на `LoadComplete` |
| TC-19: три hosts | `skills/hunt/evals/<host>/` |
| TC-21: изменение Common | Baseline cache-detective |
