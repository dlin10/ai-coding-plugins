# Product Requirements Document: Concurrency Hunter

| Поле | Значение |
|---|---|
| Статус | Draft for product review |
| Дата | 2026-09-12 |
| Владелец продукта | TBD |
| Имя плагина | `concurrency-hunter` |
| Целевая платформа | C# / SDK-style .NET solutions |
| Основной интерфейс | Одноразовая команда для Codex, Claude Code и Cursor |
| Технический документ | [Первый драфт спецификации](SPEC.md) |

Этот PRD определяет проблему, цели, границы и проверяемые требования к продукту. Архитектура, алгоритмы, внутренние контракты и способы проверки описаны в отдельной спецификации.

## 1. Проблема и ожидаемый результат

В production .NET backend один объект в памяти может изменяться одновременно из HTTP-запросов, hosted services, tasks, threads и параллельных циклов. Каждый путь по отдельности выглядит корректно, но при определённом чередовании чтений и записей возникает гонка или теряется обновление. Code review плохо обнаруживает такие дефекты через несколько уровней вызовов, а простые проверки дают шум, если не различают общий и локальный объект, реальную параллельность и достаточную синхронизацию.

В текущей версии Concurrency Hunter должен локально анализировать C# solution без запуска приложения и за одну команду выдавать самостоятельный отчёт о гонках и lost update: два конкурирующих пути выполнения, общий ресурс, конфликтующие операции, объяснение недостаточной защиты и сценарий interleaving. Если после детерминированного анализа остаются semantic gaps, AI обязан попытаться их уточнить. AI-подготовка итогового отчёта обязательна для каждого complete run. Все AI-выводы должны опираться на проверяемые факты из кода.

## 2. Цели и метрики успеха

| ID | Цель | Критерий успеха |
|---|---|---|
| G1 | Находить поддерживаемые гонки и lost update в памяти | Recall ≥ 90% на утверждённом corpus для полностью поддерживаемых конструкций |
| G2 | Давать находки, пригодные для production triage | ≥ 95% High-confidence findings содержат оба полностью разрешённых пути от execution root/spawn site до source access; каждая находка показывает ресурс, overlap, защиту, uncertainty и сценарий |
| G3 | Контролировать шум | Precision High-confidence findings ≥ 90% на независимо размеченном corpus: ≥ 500 positive/negative cases, включая ≥ 100 production-like cases |
| G4 | Работать с большими solution | Выполнены измеримые performance targets раздела 6 |
| G5 | Давать результат за одну команду | ≥ 95% успешных пилотных запусков создают отчёт без дополнительного ввода после invocation; выполнены критерии воспроизводимости и качества для каждого из трёх hosts из раздела 6.2 |
| G6 | Использовать AI проверяемо | 100% AI-derived targets/effects имеют provenance, confidence и validation status; AI не подавляет deterministic findings; 100% тезисов отчёта о location/resource/path ссылаются на существующие structured evidence IDs |

## 3. Границы текущего релиза

Анализируется только разделяемое состояние внутри одного процесса и одного managed heap. Execution instance — одно логическое выполнение root или branch, которое не обязано соответствовать одному OS thread.

Обязательные источники выполнения:

- ASP.NET Core controller actions и minimal API handlers;
- `BackgroundService.ExecuteAsync` и lifecycle `IHostedService`;
- `Task.Run`, поддерживаемые task factories и sibling tasks, объединённые `Task.WhenAll`;
- `Thread.Start` / `Thread.Join`;
- `Parallel.For`, `Parallel.ForEach` и поддерживаемые parallel async variants;
- callbacks `System.Threading.Timer`, включая однократные и периодические запуски.

Sharing должен учитывать `Microsoft.Extensions.DependencyInjection`: singleton, scoped, transient, instance/factory registrations при доступной информации.

Обязательная поддержка синхронизации и concurrent API:

- `lock` / `Monitor`, включая `TryEnter`;
- отдельные операции `Interlocked`, `Volatile.Read/Write` и `volatile` fields;
- `SemaphoreSlim`, `Mutex`, `ReaderWriterLockSlim` с read/write/upgradeable modes;
- task/thread joins и распознанные completion barriers;
- основные операции `ConcurrentDictionary` и других утверждённых concurrent collections;
- `Channel<T>` и передача владения только для доказанных сценариев.

Точный набор версий, overloads и поддерживаемых вариантов публикуется в compatibility/support matrix. MassTransit не входит в первый этап.

В текущий scope не входят:

- DB/persistence lost update: EF Core, Dapper, ADO.NET, identity строк/документов/ключей, `RowVersion`, optimistic concurrency, isolation levels, транзакции и DB locking;
- Redis, distributed cache, файлы, очереди и внешние API как разделяемые ресурсы, а также гонки между процессами, контейнерами или машинами;
- dynamic race detection, instrumentation, production traces и выполнение пользовательского приложения;
- исчерпывающий перебор interleavings, formal verification и гарантия полной soundness/completeness для произвольного C#;
- гарантия полной семантики reflection, `dynamic`, runtime-generated code, native memory, arbitrary unsafe code и неизвестных libraries/native calls;
- deadlock detection, starvation, livelock и общая диагностика производительности;
- автоматические исправления application code, отдельный native IDE extension или language server;
- findings, созданные исключительно LLM без проверяемой опоры на код;
- CI, pull-request gating, SARIF, baseline workflow, публичный standalone CLI, daemon, watcher, background monitor и scheduled scans;
- загрузка пользовательских или сторонних provider assemblies во время работы.

Наличие persistence-вызовов в анализируемом коде не должно порождать DB verdicts. Наличие или отсутствие DB-защиты не является доказательством для in-memory finding; отсутствие finding не означает безопасность persistence path.

Основные сценарии использования: разовый анализ из coding assistant; независимый повторный запуск после исправления; аудит legacy-системы с группировкой по общему ресурсу и причине; расследование гонок между HTTP и hosted service либо между ветками одного метода.

## 4. Функциональные требования

### 4.1. Запуск и входные данные

**FR-01. Единая команда.** Плагин предоставляет ровно одну команду `/concurrency-hunter [target]` без отдельных subcommands. Target — `.sln`, поддерживаемый `.slnx`, `.csproj` или workspace directory. Без аргумента target определяется детерминированно из активного workspace. При неоднозначности создаётся `Failed` diagnostic report с найденными вариантами.

**FR-02. Один конечный запуск.** Invocation дожидается terminal status и возвращает status, duration, counts и кликабельную ссылку на Markdown-отчёт. Общий лимит одного запуска — 30 минут с момента его начала, включая AI-этапы и ожидание их результатов. Отдельного лимита времени AI нет. По общему тайм-ауту прекращается анализ и отменяются все запущенные этим run AI-сессии и субагенты semantic resolver, включая дочерние; запуск завершается `Incomplete` с причиной timeout и доступными частичными результатами. Промежуточных вопросов нет; после terminal response работа не продолжается. Пользователь/host может отменить запуск. Report bundle создаётся и при `Incomplete`/`Failed`, если доступна файловая система.

**FR-03. Корректные входы и coverage.** Анализ учитывает исходники, generated documents, compiler symbols, nullable context, language version, project/metadata references и конфигурацию анализируемого приложения и сборки, влияющую на семантику. Generated code участвует в анализе путей и эффектов; находки непосредственно в нём не показываются в основном отчёте. Путь через generated code не скрывает finding в user code. Пропущенные проекты и неподдержанные bodies явно отражаются в coverage; невозможность загрузить проект даёт `Incomplete`.

**FR-04. Фиксированное поведение.** Плагин использует фиксированные правила поставляемой версии и не читает пользовательский файл конфигурации плагина. Пользователю не нужно задавать confidence threshold, правила вывода, AI runtime или лимиты запуска. Установка и обновление не запускают анализ автоматически.

### 4.2. Обнаружение и исключение гонок

**FR-05. Межпроцедурный анализ.** Анализ связывает accesses через несколько method/project layers, включая constructors, direct/virtual/interface calls, delegates, lambdas, local functions и распознанные reflection-free factories. Находка сохраняет путь и происхождение evidence. Неизвестный вызов не считается отсутствием эффектов.

**FR-06. Идентичность и разделяемость ресурса.** Совпадение имён или типов не доказывает общий объект. Анализ различает aliases одного объекта, доказанно независимые объекты и экземпляры из разных factory/call contexts, поля и непересекающиеся элементы arrays/spans/collections. Объект, доказанно ограниченный одним execution instance, не порождает finding. Sharing учитывает field/static assignment, return, ref/out, closures, task/thread captures, delegate storage, collection insertion и неизвестные capturing calls. Недостаток информации не означает confinement.

**FR-07. Семейства правил.** Поддерживаются следующие дефекты:

| Rule ID | Название | Проверяемое поведение |
|---|---|---|
| DCA1001 | Unprotected shared write | Находит may-overlap read/write и write/write к общему ресурсу без достаточного ordering/protection |
| DCA1002 | Non-atomic read-modify-write | Находит зависимую от прочитанного значения запись, способную потерять конкурирующее обновление |
| DCA1003 | Inconsistent synchronization | Находит разные synchronization identities/modes либо защищённый access против незащищённого |
| DCA1004 | Unsafe compound concurrent operation | Находит неатомарную последовательность individually safe операций |

`x++`, `x += y`, getter-compute-setter и разнесённый read/compute/write распознаются как эквивалентные формы non-atomic RMW. Read/read пары не являются conflicts.

**FR-08. Реальная возможность параллельного выполнения.** HTTP invocations могут overlap внутри процесса. Один `BackgroundService.ExecuteAsync` на одном доказанном instance не размножается автоматически, но может overlap с другими roots и spawned branches. `Task.Run` может overlap с parent до доказанного join; sibling task lifetimes и iterations `Parallel.*` могут overlap. `Task.WhenAll` не делает синхронные участки до фактического task overlap параллельными. Callbacks `System.Threading.Timer` могут overlap с другими roots и с другими invocations того же callback при повторных запусках. Отключение timer или обычный `Dispose()` не доказывают завершения уже допущенных callbacks. Await/join/barrier исключает гонку только при доказанном завершении соответствующего execution handle; exception/cancellation paths не позволяют считать пропускаемый join или release безусловным.

**FR-09. Достаточная защита.** Finding исключается при доказанно разных ресурсах, confinement, отсутствии overlap, happens-before, несовместимых условиях путей или общей достаточной синхронизации/атомарности. Защита должна охватывать оба конфликтующих access и относиться к одному объекту с совместимыми modes. `SemaphoreSlim` считается mutex только при доказанной capacity `1` и корректной wait/release паре. `volatile` не делает RMW атомарным; `Interlocked` защищает только охваченную операцию и location. Thread-safe collection не делает безопасным произвольный compound sequence. Название пользовательского `Lock/Unlock` не является доказательством защиты.

**FR-10. Условия путей.** Учитываются branch, null/type tests, enum/boolean equality, простые numeric comparisons, switch cases и index/key expressions. Доказанно несовместимые пути и непересекающиеся selectors не дают finding. Неподдержанные predicates, неизвестная совместимость и timeout остаются явной uncertainty и не трактуются как безопасность.

### 4.3. AI и неопределённость

**FR-11. Условия применения AI.** AI выполняется средствами текущего host-а: Codex, Claude Code или Cursor. AI semantic-gap refinement обязателен только при наличии semantic gaps, оставшихся после детерминированного анализа. Если gaps нет, semantic resolver не вызывается и AI-запрос для него не выполняется; это не препятствует complete status. Само наличие reflection, `dynamic` или нестандартного вызова не требует resolver, если их семантика уже разрешена. При наличии gaps AI уточняет возможные targets, reads/writes/RMW, captures/escapes, callbacks/spawn и returned aliases только в пределах доступного кода, metadata и configuration; неразрешимые случаи остаются `Unknown`. AI-композиция итогового отчёта обязательна для каждого complete run; complete AI-off path отсутствует.

**FR-12. Проверяемый вклад AI.** AI-derived facts имеют supporting evidence, provenance, confidence и validation status. Несуществующие symbols/locations, несовместимые и неподтверждённые гипотезы не влияют на findings. Принятые факты могут расширять анализ и набор находок, но не удалять deterministic facts/findings. AI-inferred synchronization, atomicity или happens-before не подавляют candidate без отдельного deterministic доказательства. Невалидированная гипотеза допустима только как unresolved analysis note.

**FR-13. Достоверный narrative.** AI объясняет и группирует structured results, описывает interleaving и предлагает fix options. Допустимы локальные исправления через общую синхронизацию или атомарные операции и изменения организации кода: устранение общего изменяемого состояния, изменение владения объектами или последовательное выполнение. Рекомендации относятся к текущему in-memory scope и не применяются плагином автоматически. Каждый AI fix suggestion явно помечен `verify manually` и объясняет, что проверить перед применением. AI не меняет rule, severity, confidence, fingerprint, source paths, evidence mode или coverage и не придумывает runtime values/events. Тезисы о коде ссылаются на существующие evidence IDs. Невалидный final report не публикуется как завершённый.

**FR-14. Честный неполный результат.** AI failure, исчерпание budget, unsupported constructs, unresolved calls и gaps видны в coverage/uncertainty. Material semantic gap — достижимый из root пробел, способный изменить shared-state effects, alias/escape или overlap verdict — требует `Incomplete`; нематериальный gap допускает complete status со scope note. Недоступность, unauthorized state или невалидный результат обязательной для данного run AI-роли дают `Incomplete`/`Failed` и технический fallback report: для semantic resolver это применяется при наличии gaps, для report composer — в каждом run. Отсутствие gaps и соответствующий пропуск resolver не считаются AI failure.

### 4.4. Отчёт и triage

**FR-15. Самостоятельный report bundle.** Пользователь получает основной Markdown-отчёт, versioned structured findings в JSON и metadata запуска. Отчёт содержит status, target, timestamp, версии, duration, executive summary, coverage, unsupported/incomplete boundaries и findings, сгруппированные по shared resource/root cause. Для каждой finding указаны rule, severity, confidence label/score, общий ресурс, accesses A/B, roots и ordered code paths, alias/overlap evidence, анализ защиты, path feasibility, AI contributions, uncertainty, interleaving, remediation и stable fingerprint. Отчёт понятен без знания внутреннего представления analyzer.

**FR-16. Confidence и происхождение evidence.** `Deterministic` означает, что verdict не зависит от AI-inferred facts; `AI-Assisted` — что хотя бы один необходимый target/effect/alias fact получен через AI. Режимы визуально различаются. Confidence 0–100 ранжирует качество evidence и явно не является вероятностью; severity отражает impact и оценивается отдельно. Self-reported AI confidence не повышает score напрямую. Weak/name-based inference ограничивает finding уровнем `Medium`; `High` требует подтверждения всех необходимых inferred facts точными symbol/type/source/config constraints и отсутствия material unresolved gap.

**FR-17. Управляемый объём результатов.** Основной Markdown-отчёт показывает все уровни confidence: High, Medium и Low; минимальный уровень — Low. Секции следуют в порядке High, Medium, Low; Medium findings показаны полностью сразу после High, без сворачивания подробностей. Применяются правила для generated code и явных suppressions. Одна semantic cause группируется с representative locations и occurrence count. Fingerprints устойчивы к сдвигу строк, но меняются при изменении смысла finding; смена AI model без изменения validated facts сама по себе не меняет finding identity. Отчёт включает короткие source snippets после redaction. Source links кликабельны там, где это поддерживает host.

**FR-18. Suppressions.** Поддерживаются локальные исключения через документированный source attribute/comment mechanism и repository-wide список точечных исключений для разных файлов. В обоих случаях `reason` обязателен, `owner` и `expiry` необязательны. Suppression скрывает только exact semantic match; заданный expiry проверяется при каждом запуске, просроченное исключение не применяется. Исключение влияет только на представление находки и не считается доказательством безопасности; finding и причина исключения сохраняются в structured results и suppressed summary для аудита.

### 4.5. Повторные запуски и расширяемость

**FR-19. Эквивалентность повторного анализа.** Warm incremental result семантически равен clean full scan того же revision и входов анализируемого приложения при фиксированных engine, built-in-provider, skill, AI model/prompt/schema versions и одинаковых AI-ответах на соответствующие semantic packets. Для независимых запусков с реальным AI действуют критерии раздела 6.2. Изменённые accesses проверяются и против неизменённого кода. Кэш локален; должен быть возможен clean full scan без его использования; corrupt/partial/incompatible cache отбрасывается с warning, а run пересчитывается либо явно завершается `Incomplete`/`Failed`.

**FR-20. Расширение framework coverage.** Поддержка нового execution root добавляется встроенным provider вместе с новой версией plugin/engine и не требует переработки downstream analysis/report engines. Новые roots используют общие правила и формат evidence. Отсутствующая либо несовместимая framework semantics видна в coverage; runtime/user providers не загружаются.

## 5. Статусы результата

| Status | Значение |
|---|---|
| `CompleteWithFindings` | Анализ и AI report завершены, semantic-gap refinement выполнен при наличии gaps; в отчёте есть findings |
| `CompleteClean` | Анализ и AI report завершены, semantic-gap refinement выполнен при наличии gaps; reportable findings нет |
| `Incomplete` | Есть частичные результаты, но пропущены проекты/semantics, достигнут общий тайм-аут, исчерпан внутренний analysis budget, не завершена требуемая AI phase либо остался material gap |
| `Failed` | Анализ нельзя начать или получить достоверные частичные результаты |
| `Cancelled` | Пользователь/host отменил invocation |

Только `CompleteClean` допускает формулировку «в поддерживаемом и проанализированном scope findings не обнаружены». `Incomplete`, `Failed` и `Cancelled` визуально отличаются от clean result.

## 6. Нефункциональные требования

### 6.1. Производительность

Измерения выполняются на зафиксированных synthetic и production-like repositories без restore; filesystem cache прогрет отдельно от analyzer cache.

- Developer reference: 8 logical cores, 16 GB RAM, SSD.
- High-capacity workstation: 16 logical cores, 32 GB RAM, SSD.

| Сценарий | Target (P95) |
|---|---|
| Cold scan, 100 KLOC / до 30 проектов, developer reference | ≤ 90 секунд, peak RSS ≤ 4 GB |
| Cold one-shot run, 1 MLOC / до 300 проектов, high-capacity workstation | ≤ 12 минут, peak RSS ≤ 12 GB |
| Повторный one-shot run, ≤ 50 изменённых files при валидном workspace cache, high-capacity workstation | ≤ 60 секунд |
| No-op warm scan | ≤ 15 секунд |
| Подготовка structured JSON/report skeleton после findings | ≤ 5 секунд для 10 000 findings до deduplication |

P95 targets являются целями производительности; общий тайм-аут запуска из FR-02 составляет 30 минут. Его отсчёт включает работу analyzer, AI semantic refinement, report composition, ожидание и throttling host-а. Отдельного временного бюджета AI нет. Targets пересматриваются только через versioned benchmark decision; молчаливый пропуск effects/findings ради скорости запрещён. Внутренние solver и cache-reuse budgets описаны в [спецификации](SPEC.md).

### 6.2. Надёжность и совместимость

- Детерминированные результаты семантически совпадают на 100% между Codex, Claude Code и Cursor при одинаковых входах и версиях engine/built-in-provider, skill и schemas. При одинаковых AI-ответах совпадают также итоговые structured findings, severity, confidence и coverage; критерии проверки описаны в SPEC, разделе 9.5.
- При работе с реальным AI каждый host отдельно выполняет метрики G1–G3 и G6, включая Recall ≥ 90% на corpus полностью поддерживаемых конструкций и precision High-confidence findings ≥ 90%. Все опубликованные AI-выводы проходят validation; AI не подавляет deterministic findings. Дополнительные AI-assisted findings и формулировки могут различаться между hosts и повторными запусками при сохранении provenance и видимой uncertainty. Неразрешённый material gap по-прежнему означает `Incomplete`.
- Crash-free completion ≥ 99.5% на поддерживаемом compatibility corpus перед релизом.
- Ошибка отдельного неподдержанного method body не роняет весь scan, но делает coverage incomplete. Отмена контролируема; частичные cache entries не используются как complete.
- Первая версия поддерживает SDK-style C# projects для .NET 8, .NET 9 и .NET 10 со стабильными версиями языка, допустимыми для соответствующего target framework; preview не поддерживается. Проверенные сочетания C#/.NET SDK/Roslyn/MSBuild публикуются в compatibility matrix.
- Windows и Linux обязательны; macOS — после подтверждения спроса либо при отсутствии дополнительной стоимости поддержки.
- Multi-targeting учитывается по отдельным compilations; эквивалентные findings объединяются с сохранением target frameworks.
- Release содержит installation/command guide для трёх hosts, compatibility и supported construct matrices, known limitations/false-negative cases, AI evidence guide, root-provider authoring guide и report triage guide.

### 6.3. Безопасность и privacy

- Analyzer не запускает application assemblies и не изменяет source tree, project files или build outputs. Restore/network activity по умолчанию не инициируется analyzer-ом; локальный core не требует сети.
- Cache и reports не содержат secrets, environment variables или полные исходники. Отчёт включает ограниченные source snippets после redaction; artifacts по умолчанию размещаются вне source tree.
- До первого запуска пользователь принимает AI data policy с описанием host/provider boundary. Redaction, repository exclusions, payload size limits, payload audit и content manifest применяются всегда; solution целиком AI не передаётся.
- Run metadata фиксирует текущий host, provider/model, retention, data residency, версию/hash skill, prompt/schema versions и payload hashes.
- Внешняя telemetry — только opt-in и без source content.

### 6.4. Диагностика

Отчёт и metadata показывают timings/counts по фазам, coverage, cache hit rate, gaps, accepted/rejected AI inferences, unresolved calls, потери precision, candidates до/после filters, результаты refinement/timeouts, AI token/latency data при доступности, memory high-water mark, причину завершения и результат отмены AI-сессий/субагентов при timeout/cancellation.

## 7. Критерии приёмки

1. DCA1001–DCA1004 проходят утверждённые positive/negative cases; проверены межпроцедурные пути минимум через три project/method layers, aliases, независимые объекты, confinement, required roots и synchronization primitives.
2. Same-lock protection, атомарная операция, доказанный join и несовместимые path conditions исключают соответствующие conflicts. Different-lock, volatile RMW и compound concurrent collection cases не исключаются ошибочно; unknown/timeout не означает safe.
3. Достигнуты метрики G1–G3. Corpus размечен независимо; High-confidence precision оценивают минимум два reviewer-а с documented adjudication разногласий.
4. Ни один DB/ORM example не получает DB lost-update/protection verdict; отсутствие findings не представляется доказательством безопасности persistence path.
5. Каждая High finding имеет два source-backed accesses, общий ресурс, overlap evidence, protection result, scenario и stable fingerprint. JSON соответствует versioned schema; narrative имеет валидные evidence references и видимый evidence mode.
6. Проверены стабильность fingerprints при сдвиге строк, различение новой semantic cause и exact-match suppressions обоих видов: локальные и repository-wide. `reason` обязателен, отсутствие `owner`/`expiry` допустимо; просроченные исключения не скрывают findings. Confidence объясним и не представлен как вероятность.
7. Один invocation без последующих вопросов возвращает documented terminal status и report link. Ошибка требуемой AI-роли, incomplete loading, cancellation и corrupt cache не создают ложный `CompleteClean`.
8. Performance targets выполнены в трёх последовательных benchmark runs без нарушения correctness corpus.
9. Warm incremental и clean full finding sets совпадают на randomized differential suite из ≥ 1 000 edit sequences при фиксированных версиях и одинаковых AI-ответах на соответствующие semantic packets.
10. На reflection/`dynamic`/unknown-library corpus AI создаёт ожидаемые validated targets/effects, отклоняет invented/incompatible hypotheses и сохраняет неразрешимые случаи как `Unknown`. Невалидные source locations, symbols, values и scenario events не попадают в final narrative; при невозможности получить валидный отчёт выдаётся technical fallback и non-complete status.
11. Новый synthetic built-in root provider участвует в меж-root finding и отчёте без изменений downstream engines; несовместимая semantics отражается в coverage.
12. Подтверждены 100% semantic parity детерминированных результатов и отдельное выполнение критериев качества каждым host по разделу 6.2. Проверены AI data policy/redaction, неизменность repository/build outputs, отсутствие запуска application assemblies, документация релиза и соблюдение исключений из scope.
13. При отсутствии semantic gaps resolver не вызывается; при успешном анализе и AI report допустимы `CompleteClean` и `CompleteWithFindings`. При наличии gaps resolver обязателен, его failure даёт `Incomplete`/`Failed`. AI report composer обязателен в обоих сценариях; его failure не допускает complete status.
14. Запуск не требует пользовательского файла конфигурации. Основной отчёт включает High, Medium и Low в этом порядке, показывает Medium полностью без сворачивания, скрывает находки в generated code и содержит ограниченные source snippets. AI выполняется текущим host-ом.
15. При достижении 30 минут все работающие AI-сессии/субагенты semantic resolver данного run отменяются, поздние ответы не меняют результат, новые AI-задачи не стартуют. Возвращается `Incomplete` с причиной timeout и техническим отчётом; после terminal response фоновая работа этого run отсутствует.
16. Локальные и архитектурные AI fix suggestions опираются на evidence находки, помечены `verify manually` и содержат конкретные проверки перед применением. Плагин не применяет рекомендации к application code.

Технические test layers, corpus и regression policy приведены в [спецификации](SPEC.md).

## 8. Дальнейшее развитие

Полноценное обнаружение deadlocks запланировано отдельной будущей версией Concurrency Hunter с собственными PRD и SPEC. При её проработке определяются матрица поддерживаемых synchronization primitives, task/thread waits и async dependencies, требования к evidence, confidence и coverage, отдельный ground-truth corpus, критерии точности и performance targets. Номер версии и необходимость major version определяются после оценки изменений engine и совместимости форматов. Реализация и приёмка текущей версии не зависят от готовности deadlock detection.

Анализ persistent/distributed resources — БД (включая EF Core/Dapper), Redis, файлов и внешних API — рассматривается как отдельное будущее направление вне текущего релиза. Его требования и решения должны быть проработаны в отдельном PRD/SPEC. Архитектура, форма поставки и необходимость major version определяются при той проработке.

Другие возможные будущие направления без обязательств: providers для MassTransit, Quartz, Hangfire, NServiceBus, Wolverine, TPL Dataflow и Akka.NET, IDE experience/safe code actions и static/runtime correlation. Каждое направление требует отдельного PRD или изменения scope.

Технические решения текущего релиза, включая проверку воспроизводимости и качества в каждом host, а также задачи дальнейшей инженерной проработки находятся в [спецификации](SPEC.md).
