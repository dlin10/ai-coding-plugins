# Product Requirements Document: Concurrency Hunter

| Поле | Значение |
|---|---|
| Статус | Пересмотрен по итогам interview 2026-09-12; готов к инженерной детализации |
| Дата | 2026-09-12 |
| Владелец продукта | Dmitry Linetsky |
| Имя плагина | `concurrency-hunter` |
| Целевая платформа | C# / SDK-style .NET solutions, Windows x64 |
| Основной интерфейс | Одна команда для Codex, Claude Code и Cursor |
| Технический документ | [Спецификация](SPEC.md) |
| Словарь | [CONTEXT.md](../CONTEXT.md); решения в [docs/adr](adr/) |

Этот PRD определяет проблему, цели, границы и проверяемые требования к продукту. Архитектура, алгоритмы, внутренние контракты и способы проверки описаны в спецификации. Термины, выделенные **жирным** при первом появлении, определены в словаре и используются в том же смысле в коде, схемах инструментов и отчёте.

## 1. Проблема и ожидаемый результат

В production .NET backend один объект в памяти может изменяться одновременно из HTTP-запросов, hosted services, tasks, threads, timer callbacks и параллельных циклов. Каждый путь по отдельности выглядит корректно, но при определённом чередовании чтений и записей возникает гонка или теряется обновление. Code review плохо обнаруживает такие дефекты через несколько уровней вызовов, а простые проверки дают шум, если не различают общий и локальный объект, реальную параллельность и достаточную синхронизацию.

Concurrency Hunter анализирует C# solution локально, без запуска приложения, и за один **run** выдаёт самостоятельный отчёт о гонках и lost update: два конкурирующих пути выполнения, общий **resource**, конфликтующие операции, объяснение недостаточной защиты и сценарий interleaving. Что детерминированный анализ разрешить не смог, оформляется как **semantic gap** и передаётся AI host-а на уточнение; принятые ответы становятся **inferred facts** с provenance. Каждый complete run содержит **narrative** AI для групп находок. Все AI-выводы опираются на проверяемые факты из кода.

Оркестратор run это **skill** host-а; **server** анализирует, хранит состояние run и его **deadline**, валидирует всё, что приносит skill, и рендерит отчёт. Сервер сам AI не запускает; см. [ADR 0001](adr/0001-the-skill-drives-the-run.md).

## 2. Цели и метрики успеха

| ID | Цель | Критерий успеха |
|---|---|---|
| G1 | Находить поддерживаемые гонки и lost update в памяти | Recall ≥ 90% на demo-корпусе для полностью поддерживаемых конструкций |
| G2 | Давать находки, пригодные для production triage | ≥ 95% High-confidence findings содержат оба полностью разрешённых пути от execution root/spawn site до access; каждая finding показывает resource, overlap, защиту, uncertainty и сценарий |
| G3 | Контролировать шум | Precision High-confidence findings ≥ 90% на demo-корпусе и на ручном triage High findings eShopOnContainers и nopCommerce с записанным adjudication |
| G4 | Работать с большими solution | Выполнены cold performance targets раздела 6.1 |
| G5 | Давать результат за одну команду | ≥ 95% пилотных запусков создают отчёт без ввода после старта run; по одному прогону skill на demo в каждом из трёх hosts на релиз |
| G6 | Использовать AI проверяемо | 100% inferred facts имеют provenance, confidence и validation status; AI не подавляет deterministic findings; 100% тезисов narrative о location/resource/path ссылаются на существующие evidence IDs |

## 3. Границы текущего релиза

Анализируется только разделяемое состояние внутри одного процесса и одного managed heap. **Execution instance** это одно логическое выполнение root или spawn, не обязанное соответствовать одному OS thread; `await` внутри него его не завершает.

Обязательные **execution roots**:

- ASP.NET Core controller actions, minimal API handlers и gRPC service methods;
- `BackgroundService.ExecuteAsync` и lifecycle `IHostedService`.

Обязательные **spawn sites**:

- `Task.Run`, `Task.Factory.StartNew`, `ContinueWith` и sibling tasks, объединённые `Task.WhenAll`;
- **fire-and-forget** task, то есть вызов, возвращающий `Task`, чей handle ни на одном пути не достигает `await`, `Wait`, `WhenAll` или `WhenAny`; вызов `async void` метода;
- `ThreadPool.QueueUserWorkItem`, `Thread.Start` / `Thread.Join`;
- `Parallel.For`, `Parallel.ForEach`, `Parallel.ForEachAsync`;
- callbacks `System.Threading.Timer` и `System.Timers.Timer.Elapsed`, включая однократные и периодические запуски; `PeriodicTimer`, который не пересекается сам с собой, но пересекается с другими roots.

Разделяемость объекта решается его **heap region** и **ownership**: allocation site, статическое хранилище, DI instance, symbolic receiver или parameter. Регистрации `Microsoft.Extensions.DependencyInjection` (singleton, scoped, transient, instance/factory) это одно из свидетельств ownership, а не единственное: статическое поле с аллокацией, объект, переданный в thread, результат factory на singleton получают identity без DI.

Обязательная поддержка синхронизации и concurrent API:

- `lock` / `Monitor`, включая `TryEnter` и `try/finally`; `System.Threading.Lock` (.NET 9+);
- `Interlocked`, `Volatile.Read/Write` и `volatile` fields;
- `SemaphoreSlim` с доказанной константной capacity `1` и парой `Wait`/`WaitAsync` и `Release` на всех путях; `Mutex` в пределах процесса; `ReaderWriterLockSlim` с read/write/upgradeable modes;
- task/thread joins и `Task.WhenAll` как happens-before при доказанной identity handle;
- операции `ConcurrentDictionary`, `ConcurrentQueue`, `ConcurrentBag`, `ConcurrentStack` по таблице семантики операций.

Не доказывают защиту или порядок в текущем релизе и записаны в known limitations: `Channel<T>` и передача владения через него (объект, переданный в channel, считается `Escaped`), `ManualResetEvent(Slim)`, `CountdownEvent`, `Barrier` и другие event-based ожидания, `SpinLock`, пользовательские `AsyncLock`/`Lock`/`Unlock` любого имени. Точный набор версий, overloads и вариантов публикуется в support matrix.

В текущий scope не входят:

- DB/persistence lost update: EF Core, Dapper, ADO.NET, identity строк/документов/ключей, `RowVersion`, optimistic concurrency, isolation levels, транзакции и DB locking;
- Redis, distributed cache, файлы, очереди и внешние API как разделяемые ресурсы, гонки между процессами, контейнерами или машинами;
- dynamic race detection, instrumentation, production traces и выполнение пользовательского приложения;
- исчерпывающий перебор interleavings, formal verification и гарантия полной soundness/completeness для произвольного C#;
- гарантия полной семантики reflection, `dynamic`, runtime-generated code, native memory, arbitrary unsafe code и неизвестных libraries/native calls;
- deadlock detection, starvation, livelock и диагностика производительности;
- автоматические исправления application code, native IDE extension или language server;
- findings, созданные исключительно LLM без проверяемой опоры на код;
- CI, pull-request gating, SARIF, baseline workflow, публичный standalone CLI, daemon, watcher, background monitor и scheduled scans;
- загрузка пользовательских или сторонних provider assemblies во время работы;
- incremental analysis cache: каждый run это clean full scan; см. раздел 8;
- Linux и macOS; объединение findings между target frameworks одного проекта;
- roots middleware, action filters, SignalR hubs, Razor Pages и MassTransit consumers.

Наличие persistence-вызовов в анализируемом коде не порождает DB verdicts. Наличие или отсутствие DB-защиты не является доказательством для in-memory finding; отсутствие finding не означает безопасность persistence path.

Основные сценарии использования: разовый анализ из coding assistant; независимый повторный запуск после исправления; аудит legacy-системы с группировкой по общему ресурсу и причине; расследование гонок между HTTP и hosted service либо между ветками одного метода.

## 4. Функциональные требования

### 4.1. Запуск и входные данные

**FR-01. Единая команда.** Плагин предоставляет ровно одну команду `/concurrency-hunter [target]` без subcommands. Target это `.sln`, `.slnx`, `.csproj` или workspace directory. Без аргумента target определяется детерминированно: единственная solution в workspace, иначе project graph из корня. При неоднозначности skill вправе один раз спросить пользователя до старта run, пока deadline не отсчитывается; без ответа или в non-interactive режиме создаётся `Failed` diagnostic report с найденными вариантами.

**FR-02. Один конечный run.** Skill дожидается **terminal status** и возвращает status, duration, counts и кликабельную ссылку на Markdown-отчёт. Общий лимит run 30 минут с момента его старта на сервере, включая AI-этапы и ожидание host-а; отдельного лимита AI нет. По deadline сервер не выдаёт новых пакетов и фрагментов, а всё, что приходит после него, записывает как **late response** и не применяет; skill не стартует новых AI-задач и отменяет запущенные субагенты по мере возможностей host-а. Run завершается `Incomplete` с причиной timeout и частичными результатами. После terminal response работа не продолжается. Пользователь/host может отменить run. Report bundle создаётся и при `Incomplete`/`Failed`, если доступна файловая система.

**FR-03. Корректные входы и coverage.** Анализ учитывает исходники, generated documents, compiler symbols, nullable context, language version, project/metadata references и конфигурацию сборки, влияющую на семантику. Анализируется **reachable set**: тела методов, достижимые из execution roots и spawn sites; тело вне него не выполняется в процессе, не анализируется и не считается против coverage. Generated code участвует в анализе путей и эффектов; findings непосредственно в нём не показываются в основном отчёте, но путь через generated code не скрывает finding в user code. Пропущенные проекты и неподдержанные bodies явно отражаются в **coverage**; невозможность загрузить проект даёт `Incomplete`.

**FR-04. Фиксированное поведение.** Плагин использует фиксированные правила поставляемой версии и не читает конфигурацию анализа: пользователю не нужно задавать confidence threshold, правила вывода, AI runtime или лимиты. Единственный файл репозитория, который плагин читает, это список suppressions по FR-18, и он влияет только на представление. Установка и обновление не запускают анализ автоматически.

### 4.2. Обнаружение и исключение гонок

**FR-05. Межпроцедурный анализ.** Анализ связывает **accesses** через несколько method/project layers, включая constructors, direct/virtual/interface calls, delegates, lambdas, local functions и распознанные reflection-free factories. Finding сохраняет путь и происхождение evidence. Неизвестный вызов не считается отсутствием эффектов.

**FR-06. Идентичность и разделяемость ресурса.** Совпадение имён или типов не доказывает общий объект. Анализ различает aliases одного объекта, включая один объект в двух полях, доказанно независимые объекты и экземпляры из разных factory/call contexts, поля и непересекающиеся элементы arrays/spans/collections. Объект, доказанно ограниченный одним execution instance, не порождает finding. Sharing учитывает field/static assignment, return, ref/out, closures, task/thread captures, delegate storage, collection insertion и неизвестные capturing calls. Недостаток информации не означает confinement.

**FR-07. Семейства правил.** Поддерживаются следующие дефекты:

| Rule ID | Название | Проверяемое поведение |
|---|---|---|
| DCA1001 | Unprotected shared write | Находит may-overlap read/write и write/write к общему ресурсу без достаточного ordering/protection |
| DCA1002 | Non-atomic read-modify-write | Находит зависимую от прочитанного значения запись, способную потерять конкурирующее обновление |
| DCA1003 | Inconsistent synchronization | Находит разные synchronization identities/modes либо защищённый access против незащищённого |
| DCA1004 | Unsafe compound concurrent operation | Находит неатомарную последовательность individually safe операций |

Все четыре правила это одна процедура принятия решения, а rule ID назначается по классификации результата: одна пара accesses даёт ровно одну finding. DCA1003 имеет приоритет над DCA1001, когда protection analysis даёт partial, different-identity или incompatible-mode; DCA1004 имеет приоритет над DCA1002, когда обе операции атомарны по отдельности над modeled cell коллекции. `x++`, `x += y`, getter-compute-setter и разнесённый read/compute/write это эквивалентные формы non-atomic RMW. Read/read пары не являются conflicts.

**FR-08. Реальная возможность параллельного выполнения.** HTTP invocations могут overlap внутри процесса. Один `BackgroundService.ExecuteAsync` на одном доказанном instance не размножается автоматически, но может overlap с другими roots и spawned branches. Spawn может overlap с parent до доказанного join; sibling task lifetimes и iterations `Parallel.*` могут overlap. `Task.WhenAll` не делает синхронные участки до фактического task overlap параллельными. Callbacks `System.Threading.Timer` могут overlap с другими roots и с другими invocations того же callback; `PeriodicTimer` с собой не overlap. Отключение timer или обычный `Dispose()` не доказывают завершения уже допущенных callbacks. Await/join/barrier исключает гонку только при доказанном завершении соответствующего execution handle; exception/cancellation paths не позволяют считать пропускаемый join или release безусловным.

**FR-09. Достаточная защита.** Finding исключается при доказанно разных ресурсах, confinement, отсутствии overlap, happens-before, несовместимых условиях путей или общей достаточной синхронизации/атомарности. **Protection** должна охватывать оба конфликтующих access и относиться к одному объекту с совместимыми modes на всех путях. `SemaphoreSlim` считается mutex только при доказанной capacity `1` и корректной wait/release паре; неизвестная capacity даёт partial protection. `volatile` не делает RMW атомарным; `Interlocked` защищает только охваченную операцию и location. Thread-safe collection не делает безопасным произвольный compound sequence. Название пользовательского `Lock/Unlock` не является доказательством защиты.

**FR-10. Условия путей.** Учитываются branch, null/type tests, enum/boolean equality, numeric comparisons с семантикой overflow и conversions, switch cases и index/key expressions. Доказанно несовместимые пути и непересекающиеся selectors не дают finding. Неподдержанные predicates, неизвестная совместимость, недоступный solver и timeout остаются явной uncertainty и не трактуются как безопасность.

### 4.3. AI и неопределённость

**FR-11. Условия применения AI.** AI выполняется в сессии текущего host-а, Codex, Claude Code или Cursor, по правилам skill; сервер отдаёт **gap packets** и дайджесты групп, валидирует ответы и никогда не запускает AI сам. Semantic-gap resolver обязателен при наличии semantic gaps после детерминированного анализа: skill проходит всю очередь пакетов в порядке **materiality**, budget на число пакетов нет, граница только deadline. Gaps, порождённые принятыми inferred facts, идут во второй круг; третьего круга нет. Если gaps нет, resolver не вызывается, и это не препятствует complete status. Само наличие reflection, `dynamic` или нестандартного вызова не создаёт gap, если семантика уже разрешена. Report composer обязателен для каждого complete run: narrative для каждой **finding group** уровня High и Medium и executive summary; для Low groups narrative пишется, если deadline позволяет.

**FR-12. Проверяемый вклад AI.** Inferred facts имеют supporting evidence, provenance, confidence и validation status. Несуществующие symbols/locations, несовместимые и неподтверждённые гипотезы не влияют на findings. Принятые факты могут расширять анализ и набор находок, но не удалять deterministic facts/findings. AI-inferred synchronization, atomicity или happens-before не подавляют candidate без отдельного deterministic доказательства. Невалидированная гипотеза допустима только как unresolved analysis note.

**FR-13. Достоверный narrative.** Сервер рендерит **skeleton** отчёта: статус, coverage, диагностику, каждую finding с двумя code paths, alias/overlap evidence, protection analysis, event skeleton сценария, fingerprint и suppressed summary. AI поставляет narrative группы: interleaving словами, почему найденная защита недостаточна, fix options. Допустимы локальные исправления через общую синхронизацию или атомарные операции и изменения организации кода: устранение общего изменяемого состояния, изменение владения или последовательное выполнение. Каждый fix suggestion помечен `verify manually` и называет проверки перед применением. Narrative ссылается на существующие evidence IDs и ничего не пересказывает; сервер отклоняет фрагмент с выдуманными locations, symbols, runtime values или events и допускает один повтор. AI не меняет rule, severity, confidence, fingerprint, source paths, evidence mode или coverage. Плагин не применяет рекомендации к application code.

**FR-14. Честный неполный результат.** Semantic gaps никогда не меняют terminal status: разрешённые и неразрешённые перечислены в coverage с materiality, а finding, чей путь пересекает неразрешённый gap, получает запись в uncertainty и штраф к confidence. `Incomplete` означает, что фаза не завершилась: не загрузился проект, сработал deadline, исчерпан внутренний analysis budget, упала обязательная AI-роль. Недоступность или невалидный результат resolver при наличии gaps или composer для High/Medium групп дают `Incomplete` с fallback-отчётом из skeleton. Отсутствие gaps и пропуск resolver не являются AI failure.

### 4.4. Отчёт и triage

**FR-15. Самостоятельный report bundle.** Пользователь получает **report bundle**: `report.md`, versioned `findings.json` и `run-metadata.json`. Отчёт содержит status, target, timestamp, версии, duration, executive summary, coverage, unsupported/incomplete boundaries и findings, сгруппированные по shared resource/root cause. Для каждой finding указаны rule, severity, confidence label/score, ресурс, accesses A/B, roots и ordered code paths, alias/overlap evidence, анализ защиты, path feasibility, AI contributions, uncertainty, interleaving, remediation и stable fingerprint. Отчёт понятен без знания внутреннего представления analyzer.

**FR-16. Confidence и происхождение evidence.** `Deterministic` означает, что verdict не зависит от inferred facts; `AI-Assisted` означает, что хотя бы один необходимый target/effect/alias fact получен через AI. Режимы визуально различаются. Confidence 0–100 ранжирует качество evidence и явно не является вероятностью; severity отражает impact и оценивается отдельно. Self-reported AI confidence не повышает score напрямую. Weak/name-based inference ограничивает finding уровнем `Medium`; `High` требует подтверждения всех необходимых inferred facts точными symbol/type/source/config constraints и отсутствия неразрешённого gap на пути finding.

**FR-17. Управляемый объём результатов.** Основной отчёт показывает High, Medium и Low в этом порядке; Medium показаны полностью сразу после High. Low finding без narrative остаётся в отчёте со skeleton и пометкой причины. Применяются правила для generated code и suppressions. Одна semantic cause это одна finding group с representative locations и occurrence count. Fingerprints устойчивы к сдвигу строк, но меняются при изменении смысла finding; смена AI model без изменения validated facts не меняет identity. Отчёт включает короткие source snippets после redaction. Source links кликабельны там, где host это поддерживает.

**FR-18. Suppressions.** Два механизма. Локальный: атрибут, распознаваемый по простому имени `ConcurrencyHunterSuppress`, на методе или типе, где лежит access A или B, с rule ID и обязательным `reason`; класс атрибута пользователь объявляет сам, NuGet не нужен. Repository-wide: файл `.concurrency-hunter/suppressions.json` в корне репозитория с записями по fingerprint. В обоих `reason` обязателен, `owner` и `expiry` необязательны. **Suppression** скрывает только exact semantic match; просроченная или невалидная запись не применяется и видна в диагностике. Suppression влияет только на представление, не считается доказательством безопасности; finding и причина сохраняются в structured results и suppressed summary.

### 4.5. Повторные запуски и расширяемость

**FR-19. Повторный анализ.** Каждый run это clean full scan; кэш результатов анализа между runs не ведётся. Method summaries несут content hashes входов и зависимостей с первой версии, чтобы incremental cache мог появиться позже без смены модели. Кэш inferred facts по payload hash допустим и не освобождает ответ от валидации.

**FR-20. Расширение framework coverage.** Поддержка нового execution root добавляется встроенным provider вместе с новой версией plugin/engine и не требует переработки downstream engines. Новые roots используют общие правила и формат evidence. Отсутствующая либо несовместимая framework semantics видна в coverage; runtime/user providers не загружаются.

## 5. Статусы результата

| Status | Значение |
|---|---|
| `CompleteWithFindings` | Все фазы завершены, resolver выполнен при наличии gaps, narrative есть у всех High и Medium групп; в отчёте есть findings |
| `CompleteClean` | То же, reportable findings нет |
| `Incomplete` | Есть частичные результаты, но фаза не завершилась: пропущены проекты, сработал deadline, исчерпан внутренний analysis budget, не завершена обязательная AI-роль |
| `Failed` | Анализ нельзя начать или получить достоверные частичные результаты |
| `Cancelled` | Пользователь/host отменил run |

Только `CompleteClean` допускает формулировку «в поддерживаемом и проанализированном scope findings не обнаружены»; неразрешённые gaps при этом перечислены в coverage. `Incomplete`, `Failed` и `Cancelled` визуально отличаются от clean result.

## 6. Нефункциональные требования

### 6.1. Производительность

Измерения выполняются на зафиксированных repositories без restore; filesystem cache прогрет. Один записанный прогон `metrics` на фазу разработки, файл в `evals/metrics`.

- Developer reference: 8 logical cores, 16 GB RAM, SSD.
- High-capacity workstation: 16 logical cores, 32 GB RAM, SSD.

| Сценарий | Корпус | Target (P95) |
|---|---|---|
| Cold run, 100 KLOC / до 30 проектов, developer reference | eShopOnContainers | ≤ 90 секунд deterministic phases, peak RSS ≤ 4 GB |
| Cold run, 1 MLOC / до 300 проектов, high-capacity workstation | OrchardCore | ≤ 12 минут deterministic phases, peak RSS ≤ 12 GB |
| Подготовка `findings.json` и skeleton `report.md` | любой | ≤ 5 секунд для 10 000 findings до deduplication |

Targets относятся к deterministic фазам сервера; AI interludes ограничены только общим deadline 30 минут из FR-02. Targets пересматриваются через versioned benchmark decision; молчаливый пропуск effects/findings ради скорости запрещён. Внутренние solver budgets описаны в спецификации.

### 6.2. Надёжность и совместимость

- Три host-а используют один analyzer binary, один skill и одни схемы, поэтому детерминированные результаты совпадают по построению и проверяются одним test suite. На релиз выполняется по одному прогону skill на demo в каждом host с сохранённым `report.md`.
- Метрики G1–G3 и G6 измеряются на demo-корпусе и ручном triage OSS-корпусов по разделу 7; live-AI findings и формулировки могут различаться между hosts и повторными запусками при сохранении provenance и видимой uncertainty.
- Robustness cases suite проходят; четыре OSS-корпуса, eShopOnContainers, nopCommerce, OrchardCore, eShopOnAbp, завершаются с terminal status.
- Ошибка отдельного неподдержанного method body не роняет весь run, но делает coverage incomplete. Отмена контролируема.
- Первая версия поддерживает SDK-style C# projects для .NET 8, .NET 9 и .NET 10 со стабильными версиями языка; preview не поддерживается. Multi-targeted project анализируется по одному target framework, самому новому из поддерживаемых, выбор записан в coverage.
- Windows x64. Linux и macOS в разделе 8.
- Release содержит installation/command guide для трёх hosts, support matrix конструкций и примитивов, known limitations/false-negative cases, AI evidence guide, root-provider authoring guide и report triage guide.

### 6.3. Безопасность и privacy

- Analyzer не запускает application assemblies и не изменяет source tree, project files или build outputs. Restore/network activity не инициируется; локальный core не требует сети.
- Report bundle размещается в `%LOCALAPPDATA%\concurrency-hunter\runs\<repo-hash>\<run-id>\`; в репозиторий не пишется ничего. Bundle не содержит secrets, environment variables или полные исходники; snippets проходят redaction.
- AI data policy описана в README и называет границу: пакеты и дайджесты уходят в сессию host-а и его субагентов, solution целиком AI не передаётся. Redaction, payload size limits и content manifest применяются всегда.
- Run metadata фиксирует host, provider/model, версию/hash skill, prompt/schema versions и payload hashes.
- Внешняя telemetry отсутствует.

### 6.4. Диагностика

Отчёт и metadata показывают timings/counts по фазам, размер reachable set, coverage, число gap packets и кругов resolver, accepted/rejected inferred facts, unresolved calls, потери precision, candidates до/после фильтров, результаты solver и timeouts, группы с narrative и без, late responses, AI token/latency data при доступности, memory high-water mark и причину завершения.

## 7. Критерии приёмки

1. DCA1001–DCA1004 проходят demo positive/negative cases; проверены межпроцедурные пути минимум через три project/method layers, aliases включая один объект в двух полях, независимые объекты, confinement, все обязательные roots и spawn sites, все обязательные примитивы.
2. Same-lock protection, атомарная операция, доказанный join, disjoint константные и символьные selectors и несовместимые path conditions исключают соответствующие conflicts. Different-lock, volatile RMW, semaphore с неизвестной capacity и compound concurrent collection cases не исключаются ошибочно; unknown, недоступный solver и timeout не означают safe.
3. Достигнуты метрики G1–G3 на demo-корпусе; expectations demo написаны до реализации и не генерируются из результата анализатора; High findings eShopOnContainers и nopCommerce разобраны вручную с записанным adjudication.
4. Ни один DB/ORM example не получает DB lost-update/protection verdict.
5. Каждая High finding имеет два source-backed accesses, ресурс, overlap evidence, protection result, сценарий и stable fingerprint. JSON соответствует versioned schema; narrative имеет валидные evidence references и видимый evidence mode.
6. Проверены стабильность fingerprints при сдвиге строк, различение новой semantic cause и exact-match suppressions обоих видов; просроченные исключения не скрывают findings; confidence не представлен как вероятность.
7. Один run возвращает documented terminal status и report link. Ошибка требуемой AI-роли, incomplete loading и cancellation не создают ложный `CompleteClean`.
8. Cold performance targets выполнены на записанном `metrics`-прогоне без ухудшения demo-корпуса.
9. На reflection/`dynamic`/unknown-library demo cases resolver создаёт ожидаемые validated inferred facts, отклоняет invented/incompatible гипотезы, оставляет неразрешимое в coverage; run без gaps не вызывает resolver; второй круг обрабатывает gaps, порождённые принятыми фактами, третьего нет.
10. Новый synthetic built-in root provider участвует в меж-root finding и отчёте без изменений downstream engines.
11. Один binary для трёх hosts; по одному прогону skill на demo в каждом host с сохранённым отчётом. Проверены AI data policy, неизменность repository/build outputs, отсутствие запуска application assemblies и документация релиза.
12. Основной отчёт включает High, Medium и Low в этом порядке, Medium полностью, скрывает findings в generated code, содержит короткие redacted snippets; все High и Medium группы имеют narrative; Low без narrative помечены причиной.
13. По deadline сервер не выдаёт новых пакетов, late responses записаны и не применены, run возвращает `Incomplete` с причиной timeout и fallback-отчётом из skeleton.
14. Все fix suggestions опираются на evidence группы, помечены `verify manually` и содержат конкретные проверки; фрагмент без них отклонён сервером. Application code не изменён.
15. Reachable set записан в coverage; тело вне него не анализируется и не занижает coverage.
16. Изменение в `plugins/Common` проходит baseline cache-detective без перезаписи его снапшотов.

## 8. Дальнейшее развитие

**Incremental analysis cache.** Content-addressed кэш IR, summaries, точек графа и refinement с инвалидацией по зависимостям и дифференциальной проверкой равенства warm и clean результатов. Проектируется после первого cold-бенчмарка на OSS-корпусах, когда измерена стоимость каждой фазы; summaries уже несут хэши входов и зависимостей.

**Вторая волна providers.** Middleware `InvokeAsync`, action filters, SignalR hubs, Razor Pages handlers, затем MassTransit, Quartz, Hangfire, NServiceBus, Wolverine, TPL Dataflow, Akka.NET. Каждый это один built-in `IExecutionRootProvider` с contract tests.

**Синхронизация второй волны.** `Channel<T>` с доказательством передачи владения; `ManualResetEvent(Slim)`, `CountdownEvent`, `Barrier` как источники happens-before; `SpinLock`.

**Server-driven AI mode.** Запуск vendor CLI из сервера по образцу plan-forge-flow, через тот же validating submit-путь и тот же deadline; даёт настоящую отмену по deadline и resolver внутри job. Требует правки FR-11 и AI data policy; см. ADR 0001.

**Платформы и targets.** Linux, затем macOS; объединение findings между target frameworks одного проекта.

**Deadlock detection.** Отдельная будущая версия с собственными PRD и SPEC: модель ожиданий, synchronization modes, task/thread waits, async continuations, отдельный corpus и targets. Реализация текущей версии от неё не зависит.

**Persistent/distributed resources.** БД, включая EF Core и Dapper, Redis, файлы, внешние API как отдельное направление с собственным resource/operation/protection model и отдельным PRD/SPEC.
