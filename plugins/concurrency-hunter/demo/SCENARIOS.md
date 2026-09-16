# Каталог сценариев demo

| Поле | Значение |
|---|---|
| Статус | Черновик 2026-09-15; перечень кейсов demo на все фазы v1 |
| Ожидания | [`expected-findings.json`](expected-findings.json), формат в [SPEC 12.2](../docs/SPEC.md) |
| Фазы | [SPEC 14.3](../docs/SPEC.md) |

Каталог говорит, какие кейсы должны появиться в `demo/` и что каждый проверяет. Он не заменяет `expected-findings.json` и не задаёт его записи: код кейса и ожидания пишутся руками в своей фазе до реализации, как требует SPEC 12.2. Вердикт в каталоге это намерение; если при написании кейса он расходится со SPEC, прав SPEC, а каталог исправляется в той же правке.

## Правила

1. Имя кейса это kebab-имя файла; `имя/суффикс` это отдельная запись того же файла, как `id` в `expected-findings.json`.
2. Один файл, один namespace, регистрация через `Add<Case>()` или `Map<Case>()` в `Program.cs`; общих строк между кейсами нет.
3. Кейс фазы P не содержит конструкций поздних фаз, если они не его предмет: в кейсах фазы 3 нет guards и вызовов, способных стать semantic gap, в кейсах фазы 4 нет gap-вызовов. Иначе метка confidence сдвинется в поздней фазе, а запись ожиданий по 12.2 не переписывается.
4. Запись в HTTP action всегда даёт self-pair: action пересекается сам с собой. Поэтому action в кейсах про защиту и порядок только читает, а negatives про disjointness строятся на `BackgroundService`, один экземпляр которого сам с собой не пересекается (TD-064). Spawn-кейсы фазы 3 живут внутри одного `BackgroundService`: до фазы 3 у них нет пересечения вообще.
5. Access в лямбде или local function получает symbol содержащего члена (12.2). Если в файле нужны две записи с одинаковой парой symbol/operation на одном resource, тела spawn и callbacks выносятся в именованные методы, иначе записи неразличимы.
6. Кейсы, которые проверяет не `expected-findings.json`, а тест отчёта или evals, помечены в колонке «Ожидание».

### Колонка «До фазы»

Код кейса появляется в начале своей фазы, а гейт demo до конца фазы проверяет константу предыдущей фазы в `DemoExpectationTests`. Находка, не совпавшая ни с одной записью файла, это false positive. Запись `findings` совпадает только при том же `rule` и тех же `operation`; запись `notDefects` совпадает по resource без учёта rule.

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
| `group-shared-helper-many-callers` | positive | Три action вызывают один helper singleton-а, который пишет поле (TD-076) | DCA1001; одна finding group с occurrence count, проверка: тест отчёта, ждёт фазы 2b |
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

## Фаза 3 — Execution model

Все кейсы, кроме timer-ов против action и gRPC, живут в одном `BackgroundService` (правило 4).

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `task-run-vs-parent` | positive | `Task.Run`, лямбда пишет поле; родитель пишет его до `await` handle | DCA1001 | TD-065 | — |
| `task-factory-start-new` | positive | То же через `Task.Factory.StartNew` без join | DCA1001 | TD-065 | — |
| `task-handle-join-order` | оба | Handle в local; лямбда пишет поля A и B, родитель пишет A до `await t` и B после | `/before-await` DCA1001; `/after-await` нет | TD-065, TD-067 | — |
| `task-handle-awaited-elsewhere` | negative | Handle сохраняется в поле и awaited в другом методе до записи родителя: spawn с join, не fire-and-forget | нет | TD-065, CONTEXT.md | — |
| `task-wait-join` | оба | Родитель пишет поле A до `t.Wait()` и поле B после | `/before-wait` DCA1001; `/after-wait` нет | TD-067 | — |
| `continue-with` | оба | `Task.Run(First).ContinueWith(Second)`, тела именованные (правило 5); `Second` пишет поле, которое пишут `First` и родитель после запуска | `/vs-parent` DCA1001; `/vs-antecedent` нет (вопрос 3) | TD-065 | — |
| `thread-pool-queue-user-work-item` | positive | `QueueUserWorkItem` и `UnsafeQueueUserWorkItem`, по полю на вариант; callback и родитель пишут | `/queue`, `/unsafe-queue` DCA1001 | TD-065, TC-12 | — |
| `thread-start-join` | оба | `new Thread(...).Start()`; родитель пишет поле A до `Join()` и поле B после | `/before-join` DCA1001; `/after-join` нет | TD-065, TD-067 | — |
| `parallel-for-shared-total` | positive | `Parallel.For(0, n, i => _total += i)` | DCA1002 | TD-068 | — |
| `parallel-foreach` | positive | `Parallel.ForEach(items, x => _last = x)` | DCA1001 | TD-068 | — |
| `parallel-foreach-async` | positive | `Parallel.ForEachAsync`, тело с `await` делает RMW поля | DCA1002 | TD-065, TC-12 | — |
| `when-all-siblings` | оба | Именованные `WriteA`/`WriteB` пишут поле, `await Task.WhenAll(...)`, затем родитель пишет его же | `/siblings` DCA1001; `/after-when-all` нет | TD-066 | — |
| `when-all-synchronous-prefix` | оба | `Task.WhenAll(StepA(), StepB())`: оба async-метода пишут поле P до первого `await` и поле Q после | `/prefix` нет; `/after-first-await` DCA1001 | TD-066 | — |
| `when-any-no-join` | positive | `await Task.WhenAny(a, b)`, затем родитель пишет поле, которое пишет `b` | DCA1001 | TD-067 | — |
| `fire-and-forget-vs-awaited` | оба | `DropAsync()` без await пишет поле D; `SaveAsync()` awaited пишет поле S; родитель пишет D и S после вызовов | `/dropped` DCA1001; `/awaited` нет | TD-065, 12.2 | — |
| `async-void-call` | positive | Вызов `async void` метода, который пишет поле после `await`; родитель пишет его же | DCA1001 | TD-065 | — |
| `join-skipped-on-exception` | positive | `try { Risky(); await t; } catch (InvalidOperationException) { }`, затем запись: при исключении join пропущен | DCA1001 | TD-069 | — |
| `event-wait-not-ordering` | positive | Spawn пишет поле и вызывает `ManualResetEventSlim.Set`; родитель ждёт `Wait()` и пишет | DCA1001, known limitation | TD-086, PRD 3 | — |
| `threading-timer-vs-action` | positive | `System.Threading.Timer` в singleton-е, callback пишет, action читает | DCA1001 | TD-061, TC-12 | — |
| `threading-timer-self-overlap` | positive | Периодический timer, callback делает RMW своего поля | DCA1002 | TD-061 | — |
| `timer-state-sharing` | positive | Объект передан как `state`, callback пишет в него, action читает через singleton | DCA1001 | TD-061 | — |
| `timer-captured-alias` | positive | Callback-лямбда захватывает объект и пишет его, action читает | DCA1001 | TD-061 | — |
| `timer-never-activated` | negative | `new Timer(cb, null, Timeout.Infinite, Timeout.Infinite)` без `Change`; callback пишет, action читает | нет | TD-061 | — |
| `timer-one-shot` | оба | `dueTime` 0, `period` `Infinite`; callback делает RMW поля A и пишет поле B, которое читает action | `/self` нет; `/vs-action` DCA1001 | TD-061 | — |
| `timer-change-reactivates` | positive | Timer создан выключенным, `Change(0, 1000)` делает его периодическим; callback делает RMW | DCA1002 | TD-061 | — |
| `timer-dispose-does-not-join` | positive | `timer.Dispose()`, затем запись в поле callback-а | DCA1001 | TD-061, TC-12 | — |
| `timer-dispose-async-awaited` | оба | `await timer.DisposeAsync()`, затем запись поля A; callback ещё запускает fire-and-forget работу, которая пишет поле B | `/after-dispose` нет; `/detached-work` DCA1001 | TD-061 | — |
| `timer-dispose-wait-handle` | оба | `Dispose(waitHandle)` и `waitHandle.WaitOne()`; запись поля A до ожидания и поля B после | `/before-wait` DCA1001; `/after-wait` нет | TD-061, TC-12 | — |
| `timers-timer-elapsed` | оба | `System.Timers.Timer.Elapsed` с `AutoReset = true`: RMW поля A, запись поля B, которое читает action | `/self` DCA1002; `/vs-action` DCA1001 | TD-061 | — |
| `periodic-timer-loop` | оба | `PeriodicTimer` в `ExecuteAsync`: итерация делает RMW поля A и пишет поле B, которое читает action | `/iterations` нет; `/vs-action` DCA1001 | TD-061a | — |
| `grpc-service-method` | positive | gRPC service method пишет singleton; нужен `Grpc.AspNetCore` и proto codegen, generated code появляется в demo до фазы 6 | DCA1001, self-pair | TD-121 | — |

## Фаза 4 — Protection и selectors

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `interlocked-increment` | negative | `Interlocked.Increment(ref _count)` в action | нет | TD-082 | nD |
| `interlocked-compare-exchange-loop` | negative | CAS-цикл: чтение в local, вычисление, `CompareExchange` | нет | TD-072, TD-082 | nD |
| `interlocked-mixed-with-plain-write` | positive | `Interlocked.Increment` в одном worker-е, `_count = 0` в другом | DCA1001 или DCA1003 (вопрос 5) | FR-07, TD-082 | ⚠ |
| `volatile-rmw-not-atomic` | positive | `volatile int _hits; _hits++` в action | DCA1002 | TD-082, FR-09 | — |
| `volatile-read-write-flag` | negative | `Volatile.Write(ref _stop, true)` в action, `Volatile.Read(ref _stop)` в worker-е | нет | TD-082 | nD |
| `lock-different-identity` | positive | Два worker-а пишут поле под `lock (_a)` и `lock (_b)` | DCA1003 `different-identity` | TD-081, FR-07 | ⚠ |
| `lock-protected-vs-unprotected` | positive | Один worker пишет под `lock`, другой без | DCA1003 `partial` | FR-07 | ⚠ |
| `lock-on-fresh-object` | positive | `lock (new object())` в методе singleton-а, два worker-а | DCA1003 | TD-044, TD-081 | ⚠ |
| `monitor-try-enter` | оба | Поле A пишется внутри `if (Monitor.TryEnter(gate))` с `Exit` в `finally`; поле B после `TryEnter` без проверки результата | `/guarded` нет; `/unguarded` DCA1003 | TD-080 | nD / ⚠ |
| `system-threading-lock` | negative | `System.Threading.Lock`: `lock (_gate)` в одном worker-е, `using (_gate.EnterScope())` в другом | нет | TD-080 | nD |
| `mutex-in-process` | negative | `Mutex.WaitOne` и `ReleaseMutex` в `finally` в двух worker-ах | нет | TD-083 | nD |
| `semaphore-slim-capacity-one` | negative | `new SemaphoreSlim(1, 1)`, `await WaitAsync()` и `Release()` в `finally` | нет | TD-083 | nD |
| `semaphore-slim-not-a-mutex` | positive | По полю на вариант: capacity из параметра конструктора; константа 2; `Release` вне `finally` | `/unknown-capacity`, `/capacity-two`, `/release-not-in-finally` DCA1003 `partial` | TD-083, PRD 7 п. 2 | ⚠ |
| `reader-writer-lock-slim` | оба | Поле A: чтение под `EnterReadLock`, запись под `EnterWriteLock`; поле B: два worker-а пишут под `EnterReadLock` | `/read-vs-write` нет; `/write-under-read-lock` DCA1003 `incompatible-mode` | TD-083 | nD / ⚠ |
| `spin-lock-not-protection` | positive | Оба writer-а под `SpinLock.Enter`/`Exit` | DCA1001 | PRD 3, TD-086 | — |
| `custom-async-lock-releaser` | positive | Пользовательский `AsyncLock` на `SemaphoreSlim(1, 1)` с releaser-ом `IDisposable`; оба writer-а под `using (await _lock.LockAsync())` | решить (вопрос 6) | TD-086 | ⚠ |
| `concurrent-dictionary-atomic-ops` | negative | `GetOrAdd`, `AddOrUpdate`, `TryUpdate` из action и worker-а | нет | TD-085 | nD |
| `concurrent-dictionary-compound` | positive | `if (!d.ContainsKey(k)) d[k] = v` и `d[k] = d[k] + 1` на разных словарях | `/contains-then-set`, `/indexer-increment` DCA1004 | TD-085 | ⚠ |
| `concurrent-bag-count-then-add` | positive | `if (bag.Count < Limit) bag.Add(x)` в action | DCA1004 | TD-085 | ⚠ |
| `concurrent-queue-single-ops` | negative | `Enqueue` в action, `TryDequeue` в worker-е | нет | PRD 3 | nD |
| `concurrent-dictionary-enumerate-while-mutate` | positive | Worker перечисляет словарь `foreach`, action добавляет | finding, правило по вопросу 4 | TD-085 | ⚠ |
| `dictionary-disjoint-keys-structural` | positive | Обычный `Dictionary`: два worker-а пишут константные ключи `"a"` и `"b"` | DCA1001 на structural resource | TD-070 | ⚠ |
| `static-list-add` | positive | `static readonly List<string>`, `Add` из action | DCA1001, self-pair на structural resource (вопрос 2) | TD-070 | ⚠ |
| `array-disjoint-constant-indices` | negative | Два worker-а пишут `_slots[0]` и `_slots[1]` | нет, без solver | TD-092, TC-17 | nD (вопрос 1) |
| `array-symbolic-indices` | positive | Два worker-а пишут `_slots[i]` и `_slots[j]`, индексы из независимых источников | DCA1001, SAT model | TD-092, TC-17 | вопрос 1 |
| `array-disjoint-guarded-ranges` | negative | `if (i < 5) _slots[i] = …` и `if (j >= 5) _slots[j] = …` | нет, UNSAT | TD-043, TD-092 | nD |
| `parallel-for-disjoint-index` | negative | `Parallel.For(0, n, i => results[i] = …)` | нет | TD-068, TC-17 | nD |
| `span-slices-disjoint` | negative | Два worker-а пишут через `_buffer.AsSpan(0, 8)` и `_buffer.AsSpan(8, 8)` | нет | TD-043 | nD |
| `index-overflow-wraps` | positive | `_slots[(byte)i]` и `_slots[(byte)(i + 256)]`: алгебраически разные, равны после conversion | DCA1001 | TD-043, TC-17 | вопрос 1 |
| `key-equality-comparer` | оба | `ConcurrentDictionary`, compound `TryGetValue` + indexer set в двух worker-ах: ключи `"a"`/`"b"`; `"a"`/`"A"` с `StringComparer.OrdinalIgnoreCase`; пользовательский comparer | `/ordinal-distinct` нет; `/ignore-case-equal` DCA1004; `/custom-comparer` DCA1004 с uncertainty | TD-043 | ⚠ |
| `mutually-exclusive-paths` | negative | Singleton-опции с `readonly bool IsPrimary`; worker A пишет под `if (IsPrimary)`, worker B под `if (!IsPrimary)`; вариант со `switch` по enum | `/boolean`, `/enum-switch` нет, UNSAT (вопрос 10) | TD-090, TD-092, 12.2 | nD |
| `unsupported-guard-kept` | positive | Та же форма с guard `Name.StartsWith("x")` и его отрицанием | DCA1001 с uncertainty «unsupported predicate» | TD-095 | — |

## Фаза 5 — Semantic gaps

| Кейс | Тип | Как устроен | Ожидание | Ссылки | До фазы |
|---|---|---|---|---|---|
| `reflection-primitive-args-no-gap` | negative | `MethodInfo.Invoke` статического метода только со строками и числами | нет gap packet; проверка: resolver evals | TD-034 | — |
| `reflection-invoke-target` | positive | `GetMethod("Bump").Invoke(_counter, null)` в action, `Bump` делает RMW | gap; accepted target `Bump`; DCA1002 `AI-Assisted`, метка по TD-108 | TD-036, TD-037, TD-108 | — |
| `reflection-unresolvable-name` | gap | Имя метода приходит из запроса | принятого факта нет; gap в coverage с materiality; проверка: resolver evals | TD-036, TD-039 | — |
| `dynamic-call-target` | positive | `dynamic sink = _sink; sink.Record(path)` в action | gap; accepted target; DCA1001 `AI-Assisted` | TD-034 | — |
| `memory-cache-returns-shared-object` | positive | `IMemoryCache.GetOrCreate("settings", _ => new Settings())` отдаёт один объект всем запросам; action пишет в него | gap; accepted `ReturnAlias`; DCA1001 `AI-Assisted`; если план фазы 5 внесёт `IMemoryCache` в таблицу TD-034a, кейс станет deterministic | TD-034a, TD-036 | ⚠ |
| `unknown-library-captures-delegate` | positive | Метод пакета вне таблицы получает лямбду, которая пишет singleton; пакет выбирает план фазы 5 | gap `captures-delegate`; DCA1001 `AI-Assisted` | TD-034, TD-036 | — |
| `unknown-call-model-not-noop` | positive | Singleton-список передаётся методу того же пакета, который его мутирует; resolver недоступен | gap не разрешён; finding с uncertainty (вопрос 12) | TD-025, TD-034 | — |
| `gap-second-round` | gap | Reflection указывает на метод с `dynamic`-вызовом, цель которого делает ещё один reflection-вызов | rounds 1 и 2 разрешены; gap третьего уровня в coverage; проверка: resolver evals | TD-131, TC-05 | — |
| `gap-materiality-order` | gap | Два gap: один достигает трёх roots и двух регионов, другой одного | порядок очереди и coverage; проверка: resolver evals | TD-039a | — |
| `library-table-no-gap` | negative | `ILogger`, `JsonSerializer`, `HttpClient` получают singleton-объекты | нет gap packets; проверка: resolver evals | TD-034a | — |
| `json-serialize-reads-deep` | positive | GET сериализует singleton через `JsonSerializer.Serialize`, worker пишет вложенное поле | DCA1001 `Deterministic` по эффекту `reads-deep` | TD-034a | — |
| `ef-core-no-db-verdict` | negative | Scoped `DbContext`, два запроса делают `item.Stock--` и `SaveChangesAsync`; нужен пакет EF Core | нет находки и нет DB verdict (вопрос 14) | PRD 7 п. 4, TD-034a | ⚠ |
| `channel-handoff` | positive | Producer пишет объект в `Channel<T>` и продолжает его менять, consumer читает | finding или uncertainty (вопрос 7) | TD-086 | — |

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
| 1 | Как в `expected-findings.json` записывается resource элемента: `accessPath` по 12.2 называет только поля и auto-properties, записи для `ElementSelector` и structural resource коллекции нет | selector-кейсы, `dictionary-disjoint-keys-structural`, `static-list-add` | 4 |
| 2 | В какой фазе моделируются мутации обычных `List<T>` и `Dictionary<TKey, TValue>`: TD-085 и строка фазы 4 говорят о concurrent collections, TD-034a о `System.*` по immutability и чистоте; от этого же зависит escape через collection insertion из TD-052 | `static-list-add`, `dictionary-disjoint-keys-structural` | 4 или 5 |
| 3 | Даёт ли `ContinueWith` happens-before от antecedent к continuation; TD-065–067 этого не говорят | `continue-with` | 3 |
| 4 | Правило для enumerate + mutate: DCA1004 требует `LostUpdate`, а перечисление против вставки не lost update | `concurrent-dictionary-enumerate-while-mutate` | 4 |
| 5 | Atomic операция против незащищённой записи: DCA1001 или DCA1003, то есть считается ли atomic защитой в `protectionAnalysis` | `interlocked-mixed-with-plain-write` | 4 |
| 6 | Пользовательский `AsyncLock` с исходником: TD-086 запрещает считать его защитой, а межпроцедурный must-hold через возвращённый releaser мог бы её доказать | `custom-async-lock-releaser` | 4 |
| 7 | Как результат `ChannelReader.ReadAsync` связывается с записанным объектом; фаза для `Channel<T>` в 14.3 не названа | `channel-handoff` | 5 |
| 8 | Закрыт: конструкторы и type initializers решены в [ADR 0006](../docs/adr/0006-a-construction-belongs-to-the-execution-that-triggers-it.md); ownership `[ThreadStatic]`, `ThreadLocal`, `AsyncLocal` переносится в фазу 5 | `static-constructor-initialization` и кейсы «Фаза 2 — конструирование»; кейсов на thread-local нет | 5 |
| 9 | Корень репозитория для `.concurrency-hunter/suppressions.json`, когда demo лежит внутри репозитория CodexPlugins (14.1 п. 6) | `suppress-file-fingerprint` | 6 |
| 10 | Получает ли `readonly` значение одного региона одну canonical identity в независимых instances, если TD-092 даёт им отдельные bindings | `mutually-exclusive-paths`, `unsupported-guard-kept` | 4 |
| 11 | Как ввести ⚠-кейс, не покраснев на гейте предыдущей фазы: код кейса в одной задаче с классификацией, переключение константы фазы в той же задаче или правка матчера | все ⚠ | план каждой фазы |
| 12 | Конфликтует ли `UnknownEffect` как запись по TD-072, когда gap не разрешён | `unknown-call-model-not-noop` | 5 |
| 13 | Что именно redaction убирает из snippets | `redaction-in-snippet` | 6 |
| 14 | Region и ownership сущности, которую вернул opaque persistence-вызов EF Core | `ef-core-no-db-verdict` | 5 |
