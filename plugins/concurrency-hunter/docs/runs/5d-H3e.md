# 5d run H3e

## Причина

Базовый замер на `145b385f`: `scratch/h3e/baseline-145b385f-callsite/` и
`scratch/h3b/h3e-baseline-145b385f-ddbee9c3257a4911a30492fe42db2404/`.
С записями Polly demo давал 277 троек, с допуском тел Polly 7.2.3 — 278:
оставалась настоящая гонка `0d8df6303781e5e8` (`DCA1001`, High),
`UnwaitedNestedRetryWorker._left` в `PollyNestedRetry.cs:56` с собой.
Внешний синхронный ретрай не ждёт `SeedAsync`; поздняя попытка внутреннего ретрая пишет
`_left = 1` в продолжении внутреннего вызова, одновременно с первой попыткой следующего
прохода внешнего ретрая в корне. Записи уже давали `e74967c4012d1c33` (первая запись со
второй) и `b8e1d12245dfa912` (вторая с собой), но все синхронные части помещали в корень,
который сам с собой не пересекается, и первую запись с собой не спаривали.

## Правило

[ADR 0036](../adr/0036-the-later-runs-of-a-sequential-awaited-call-start-in-its-continuation.md), SPEC TD-034a. Первый запуск остаётся в исполнении вызывающего P.
Для ребра делегата с `IsAsyncSpawn` и отношением `RunRelation.SequentialAwaited`
синхронная часть async-цели входит также в продолжение вызова C, `AsyncChild(P, caller, operation)`:
`_tailOf[(C, key)] = C` и `Entry` с `ExecutionEntryKind.Spawn` и `BodySegment.Prefix`,
рядом с прежней Tail-записью. Это чтение отношения не зависит от слоя записи.
Не-async цель, `sequential`, `concurrent`, held delegates, допущенные тела библиотек и слой
порядка не менялись. После этой правки движок больше не менялся; задача документов правит
только комментарии.

## Проба и классификация

Gate задачи 1 прошёл: records demo получил ровно нужную тройку, остальные прогоны сохранили
каждую тройку. `CallSiteProbeTests` и `HandleExecutionProbeTests` после задачи 4:
6 Passed, 0 Failed, 0 Skipped (`scratch/h3e/gates/measure.log`). Равенство demo с допуском
проверяется без пропуска; handle-проба не ожидает admitted-only находки.

Проба по остальным Core-тестам (`scratch/h3e/probe-inventory.md`) дала 17005 Passed,
100 Failed, 39 Skipped в восьми методах. Классификация D-0008:

- R6, пары, скрытые прежним размещением: `AwaitedRunsBoundaryMatrixTests`, next-run для
  `add-distinct`, `add-same`, `append-distinct`, `append-same`, `combine-distinct`, `combine-same`:
  библиотека ждёт задачу только последней цели multicast; `two-awaited`, `two-mixed`:
  запись не упорядочивает разные параметры; `field`, `parameter`, `phi-call`: вызов вне допуска,
  порядок его запусков не доказан. `ExecutionStartsMatrixTests`, `Admitted` × `NoneWaits`:
  два отсоединённых seeding теперь дают `1-1`, `1-2`, `2-2`.
- D-0008 (a), более широкий ответ: вложенный `sequential-awaited` в Prefix допущенного
  делегата исполняют P и C, `OnlyExecution` отказывает в допуске. Затронуты
  `PerPassAdmissionMatrixTests.Every_cell_is_exact` (12 клеток, `AwaitedRun`),
  `PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited`
  (7 строк), `PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it` (30 строк),
  `Outer_wait_of_the_projects_library_is_read_by_its_form` (32 строки). Ожидания называют
  причину и ссылку, в том числе пару продолжения с собой, найденную при первой проверке задачи 2.
- D-0008 (b), более широкий ответ: задача, которую поздний запуск запускает и ждёт в Prefix,
  новый `C>spawn`, не доказана завершённой через задачу вызова. Затронуты
  `PollyRetryAttemptMatrixTests.Every_cell_is_exact_outside_the_wider_list` (4 клетки) и
  `Await_of_the_retry_call_past_the_catch_keeps_the_pair_of_a_child_an_attempt_left`
  (две строки `leaves: false`).
- D-0008 (c), более широкий ответ: `SequentialRuns` не упорядочивает разные продолжения
  одного допущенного вызова; next-run в `AwaitedRunsBoundaryMatrixTests` для `phi-sync`.

В тестах и PLAN метки D-0005 и D-0008 (a), (b), (c) заменены номерами issue #413–#416
(раздел «Остатки для каталога»). Никакая из трёх причин не исправляется этим раном.

В Prefix теперь могут быть два вызывающих исполнения, что меняет допуск через `OnlyExecution`.
Spawn и timer sites получают C вторым родителем; async-вызовы из Prefix в C получают `C>async`.
Все читатели entries, visits и steps видят Prefix в C: construction status (без интервала
конструирования), `FinishUnknownDelegateCalls` и его `SERIALIZED`, recursion marking в `Child`,
path symbols находок. Для этих следствий отдельный код не добавлялся.

## Замеры с записями, допуском и контролем

Тройка — (fingerprint, rule, confidence). Повторные файлы задачи 4:
`scratch/h3e/probe-20261114-201137-0482449-901a441e1b3f4893a6dcf52360166bac/`;
handle-проба пишет четыре прогона под `scratch/h3b/`, без control.

| Прогон | Находки до → после | Результат сверки троек |
|---|---:|---|
| demo records | 277 → 278 | Добавлена ровно `0d8df6303781e5e8`, `DCA1001`, High; равен admitted |
| demo admitted | 278 → 278 | Каждая тройка сохранена |
| demo control | 281 → 281 | Каждая тройка сохранена |
| eShop records | 2 → 2 | Обе тройки сохранены |
| eShop admitted | 2 → 2 | Обе тройки сохранены |
| eShop control | 36 → 36 | Каждая тройка сохранена |

`demo-residuals.json` больше не содержит находок: 1 → 0. eShop сохраняет
`7c919c7aedc4b38e` и `fa3fbc528c46593e`. `demo/expected-findings.json` не изменён.

## Матрица размещения и pin #408

`LaterRunsPlacementMatrixTests`: 36 клеток — 3 отношения (`Concurrent`, `Sequential`,
`SequentialAwaited`) × 4 цели (`AsyncLambda`, `AsyncMethod`, `Forwarder`, `TaskRun`)
× 3 слоя (`BuiltIn`, `Project`, `Generated`), 109 Passed результатов.
Первый запуск в caller проверяется в каждой клетке; поздняя синхронная часть есть также в C
и даёт пару с записью после dropped call только у `SequentialAwaited` с async-целью.
Шесть клеток не-async целей перечислены как известный пропуск D-0005. Generated проверяется
через IR: читатель generated-моделей пока не принимает `runs`.

Прежняя клетка `Admitted/NoneWaits` нашла скрытые пары через продолжение и больше не pin для #408.
Отдельный факт `ExecutionStartsMatrixTests.Two_instances_of_one_body_at_one_operation_give_one_access`
оставляет пропуск видимым без записи `sequential-awaited`: два получателя вызывают `RunAsync()`
без ожидания, тело пишет `_sink.Value = 0; await Task.Yield(); _sink.Value = 1;`.
Факт даёт `1-1`, пропускает `0-1` и требует один доступ `0`.

## Модели demo и снимки

Online `build/record-demo-models.ps1` после изменения движка записал пять прежних файлов
(`scratch/h3e/t1-record-demo-models.log`), затем прочёл их без генерации. Изменена только строка
`generator`, ни один ответ члена не изменён:

- до: `53880bd0dbf69237a8c64db9b51a9fe923f6c7daf73dfd5e95eb943553019114`;
- после: `0bab732c268c4f85d8cfd1b87dc56b4a3a655215e7824730354ffc19e190dfe3`.

Файлы: Google.Protobuf 3.30.2, Grpc.AspNetCore.Server 2.71.0, Grpc.Core.Api 2.71.0,
Polly.Core 8.7.0, Polly 7.2.3. Причина версии — токены размещения в `ExecutionModel.cs`;
комментарии задачи документов её не меняют. Перепись фреймворков, CoreLib и snapshot model evals
не перезаписывались.

Перед замерами снимков опубликован текущий `bin/win-x64` (`scratch/h3e/t4-publish.log`).
Все 30 проектов eShop имели assets; checkout eShop не менялся.
Метрики: `scratch/h3e/t4-demo-metrics.json` (`--generation read`) и
`scratch/h3e/t4-eshop-metrics.json` (`--generation off`); снимки перезаписаны из них.
Следующая таблица целиком переносит строки `scratch/h3e/snapshots.md`, сравнение с `HEAD`.
Причины в таблице следуют диагностическому журналу.

| key | old | new | cause |
| --- | --- | --- | --- |
| demo.json/counts/accesses | 1992 | 1996 | R1 добавляет две синхронные записи вложенного ретрая в продолжении вызова и две записи потомков Task.Run, новым родителем которых стало продолжение вызова AsyncRetryWorker. |
| demo.json/counts/findings | 277 | 278 | Первая запись вложенного ретрая без ожидания теперь спаривается с собой между внешними проходами: 0d8df6303781e5e8 (DCA1001, High). |
| demo.json/counts/occurrences | 296 | 301 | Четыре новых access occurrences открывают пять пар: два occurrence 0d8df6303781e5e8, один дополнительный occurrence первой записи со второй в UnwaitedNestedRetryWorker и два дополнительных в AsyncRetryWorker. |
| demo.json/counts/pairs/assumed/observers | 4 | 7 | Три из пяти новых ordered comparisons требуют предположения observers блокирующего ожидания вызова ретрая. |
| demo.json/counts/pairs/assumed/thread-context | 4 | 7 | Те же три новых ordered comparisons требуют предположения thread-context того же блокирующего ожидания. |
| demo.json/counts/pairs/candidates | 299 | 304 | Синхронные доступы поздних запусков и их новые потомки Task.Run добавляют пять неупорядоченных конфликтующих пар; solver сохраняет все пять, увеличивая число occurrences. |
| demo.json/counts/pairs/cartesianBound | 968137 | 973711 | Четыре новых доступа вне конструирования в Demo.Web увеличивают треугольную декартову границу на 5574; Demo.Worker не меняется. |
| demo.json/counts/pairs/comparisons | 2107 | 2117 | Индекс кандидатов сравнивает десять новых пар с синхронными доступами поздних запусков: пять ordered и пять candidates. |
| demo.json/counts/pairs/skips/ordered | 63 | 68 | Пять новых сравнений с синхронными доступами поздних запусков доказаны упорядоченными через допущенный sequential-awaited вызов или его ожидание. |
| demo.json/coverage/Demo.Web/accesses | 1391 | 1395 | Обе синхронные записи вложенного ретрая и обе записи потомков Task.Run с новым родителем принадлежат Demo.Web. |
| eshop.json/counts/accesses | 4495 | 4552 | R1 добавляет 37 синхронных access occurrences поздних запусков в Catalog.API и 20 в Ordering.API. |
| eshop.json/counts/pairs/assumed/observers | 1564 | 1659 | Все 95 новых ordered comparisons с синхронными доступами поздних запусков требуют observers, как прежние пары, упорядоченные ожиданием ретрая. |
| eshop.json/counts/pairs/assumed/thread-context | 1564 | 1659 | Те же 95 новых ordered comparisons требуют thread-context; предположение thread-pool-entry не добавляется. |
| eshop.json/counts/pairs/cartesianBound | 1248648 | 1293838 | Новые 37 доступов Catalog.API и 20 Ordering.API увеличивают сумму треугольных декартовых границ scopes на 45190. |
| eshop.json/counts/pairs/comparisons | 8007 | 8284 | Индекс кандидатов сравнивает 277 новых пар с синхронными доступами поздних запусков: 95 ordered и 182 read-read. |
| eshop.json/counts/pairs/largestBucket | 15 | 17 | Синхронные occurrences поздних запусков добавляют два доступа в самый большой bucket ресурса. |
| eshop.json/counts/pairs/skips/ordered | 1564 | 1659 | Ожидание ретрая доказывает завершение для 95 новых конфликтующих сравнений с синхронными доступами поздних запусков; ни одно не становится кандидатом. |
| eshop.json/counts/pairs/skips/read-read | 3783 | 3965 | Остальные 182 новых сравнения относятся к двум чтениям и отсеиваются проверкой read-read. |
| eshop.json/coverage/Catalog.API/accesses | 1081 | 1118 | R1 посещает синхронную часть sequential-awaited делегата в продолжении вызова: 37 новых occurrences в Catalog.API. |
| eshop.json/coverage/Ordering.API/accesses | 214 | 234 | R1 посещает синхронную часть sequential-awaited делегата в продолжении вызова: 20 новых occurrences в Ordering.API. |

Четыре добавленных demo-доступа: `PollyNestedRetry.cs:28` и `:56` в продолжении внутреннего
вызова, `PollyRetryAttempts.cs:49` и `:52` в новых `C>spawn` у `AsyncRetryWorker`.
Пять новых occurrence: две пары line 56 с собой (root/continuation и continuation/continuation),
одна дополнительная line 56 × line 58, две дополнительные line 52 с собой (D-0008 (b)).
Диагностическое удаление четырёх новых доступов вернуло старые comparison, ordered и assumption
totals demo; слой порядка не менялся. В eShop `CatalogContextSeed.SeedAsync` даёт 37 новых
access occurrences, `OrderingContextSeed.SeedAsync` — 20. Набор дубликатов parent/continuation
диагностической пробы включает и прежние tail-доступы; числа 57, 95 и 182 взяты из метрик,
а не из этого более широкого набора. Остальные числа снимков сохранены.

## Базис тестов

Запись `build/check-test-baseline.ps1 -Record -KeepResults scratch/h3e/baseline-results`
прошла с `CH_ESHOP_ROOT=C:\Dev\eShopOnContainers`, без model evals
(`scratch/h3e/baseline-record.log`). `check-baseline-rerecord.ps1` принял все 73 замены
Passed-строк как другие строки тех же теорий, без необъяснённой потери
(`scratch/h3e/baseline-rerecord-check.log`).

| Набор | Passed до → после | Failed до → после | Skipped до → после | Всего до → после |
|---|---:|---:|---:|---:|
| Core | 17110 → 17221 | 0 → 0 | 40 → 39 | 17150 → 17260 |
| CLI, включая E2E | 174 → 174 | 0 → 0 | 0 → 0 | 174 → 174 |
| Базис, уникальные имена с учётом регистра | 17088 → 17199 | 0 → 0 | 40 → 39 | 17128 → 17238 |

Базис дедуплицирует одинаковые имена результатов, поэтому его сумма меньше суммы результатов
проектов. Добавлено 183 имени, удалено 73: 73 замены ожиданий строк теорий и 110 новых имён
(109 результатов матрицы и pin #408). Смена ожиданий — R6 или перечисленные причины D-0008;
engine не менялся. Единственное изменение статуса на прежнем имени — demo equality fact
`ConcurrencyHunter.Core.Tests.Engine.CallSiteProbeTests.Demo_with_polly_bodies_admitted_and_records_withheld_has_the_findings_of_the_records`:
Skipped → Passed, так как #404 больше не мешает равенству. Ниже каждое добавленное и удалённое имя.

### Добавленные имена (183)

- `ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.Two_instances_of_one_body_at_one_operation_give_one_access`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: AsyncLambda, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: AsyncLambda, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: AsyncLambda, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: AsyncMethod, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: AsyncMethod, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: AsyncMethod, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: Forwarder, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: Forwarder, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: Forwarder, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: TaskRun, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: TaskRun, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Concurrent, target: TaskRun, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: AsyncLambda, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: AsyncLambda, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: AsyncLambda, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: AsyncMethod, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: AsyncMethod, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: AsyncMethod, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: Forwarder, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: Forwarder, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: Forwarder, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: TaskRun, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: TaskRun, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: Sequential, target: TaskRun, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: AsyncLambda, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: AsyncLambda, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: AsyncLambda, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: AsyncMethod, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: AsyncMethod, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: AsyncMethod, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: Forwarder, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: Forwarder, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: Forwarder, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: TaskRun, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: TaskRun, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_continuation_occurrence(relation: SequentialAwaited, target: TaskRun, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: AsyncLambda, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: AsyncLambda, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: AsyncLambda, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: AsyncMethod, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: AsyncMethod, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: AsyncMethod, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: Forwarder, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: Forwarder, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: Forwarder, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: TaskRun, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: TaskRun, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Concurrent, target: TaskRun, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: AsyncLambda, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: AsyncLambda, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: AsyncLambda, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: AsyncMethod, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: AsyncMethod, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: AsyncMethod, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: Forwarder, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: Forwarder, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: Forwarder, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: TaskRun, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: TaskRun, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: Sequential, target: TaskRun, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: AsyncLambda, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: AsyncLambda, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: AsyncLambda, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: AsyncMethod, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: AsyncMethod, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: AsyncMethod, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: Forwarder, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: Forwarder, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: Forwarder, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: TaskRun, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: TaskRun, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_exactly_its_pair_after_a_dropped_call(relation: SequentialAwaited, target: TaskRun, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: AsyncLambda, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: AsyncLambda, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: AsyncLambda, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: AsyncMethod, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: AsyncMethod, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: AsyncMethod, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: Forwarder, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: Forwarder, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: Forwarder, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: TaskRun, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: TaskRun, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Concurrent, target: TaskRun, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: AsyncLambda, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: AsyncLambda, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: AsyncLambda, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: AsyncMethod, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: AsyncMethod, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: AsyncMethod, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: Forwarder, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: Forwarder, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: Forwarder, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: TaskRun, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: TaskRun, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: Sequential, target: TaskRun, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: AsyncLambda, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: AsyncLambda, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: AsyncLambda, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: AsyncMethod, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: AsyncMethod, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: AsyncMethod, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: Forwarder, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: Forwarder, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: Forwarder, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: TaskRun, layer: BuiltIn)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: TaskRun, layer: Generated)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.Every_cell_has_the_first_run_in_the_caller(relation: SequentialAwaited, target: TaskRun, layer: Project)`
- `ConcurrencyHunter.Core.Tests.Engine.LaterRunsPlacementMatrixTests.There_are_36_cells_covering_every_value_of_every_axis`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-awaited", pass: "await _outer.ExecuteAsync(async () => { var s = Se"···, sync: True, self: True, mine: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-awaited-after-may-throw", pass: "var t = _outer.ExecuteAsync(async () => { var s = "···, sync: True, self: True, mine: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-blocked-on", pass: "_outer.ExecuteAsync(async () => { var s = Seed(); "···, sync: True, self: True, mine: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-in-a-loop-awaited", pass: "for (var pass = 0; pass < 2; pass++) { await _oute"···, sync: True, self: True, mine: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-unawaited", pass: "_outer.ExecuteAsync(async () => { var s = Seed(); "···, sync: True, self: True, mine: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-when-all", pass: "await Task.WhenAll(_outer.ExecuteAsync(async () =>"···, sync: True, self: True, mine: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "tail-after-an-unawaited-outer", pass: "var h = HelperDetached(); await h;", sync: True, self: True, mine: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-await-caught", body: "try { await _outer.ExecuteAsync(async () => { var "···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-await-caught-installer", body: "try { await _outer.ExecuteAsync(async () => { var "···, members: "", other: "installer", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-await-caught-on-the-pool", body: "await Task.Run(async () => { try { await _outer.Ex"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-await-in-a-loop-caught", body: "for (var pass = 0; pass < 2; pass++) { try { await"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-helper", body: "var h = Helper(); await h; Mine();", members: "private async Task Helper() { await _outer.Execute"···, other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-helper-in-a-loop", body: "for (var pass = 0; pass < 2; pass++) { var h = Hel"···, members: "private async Task Helper() { await _outer.Execute"···, other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-helper-waited-late", body: "var h = Helper(); Mine(); await h;", members: "private async Task Helper() { await _outer.Execute"···, other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-helper-whose-tail-writes", body: "var h = Helper(); await h;", members: "private async Task Helper() { await _outer.Execute"···, other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-helper-whose-tail-writes-unwait"···, body: "var h = Helper();", members: "private async Task Helper() { await _outer.Execute"···, other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-loop", body: "for (var pass = 0; pass < 2; pass++) { await _oute"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-call-awaited", body: "await _outer.ExecuteAsync(async () => { await _see"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-call-awaited-in-a-loop", body: "for (var pass = 0; pass < 2; pass++) { await _oute"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-level-awaited", body: "await _outer.ExecuteAsync(async () => { await Midd"···, members: "private async Task Middle() { await Seed(); }", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-level-blocked-caught", body: "try { _outer.ExecuteAsync(async () => { await Midd"···, members: "private async Task Middle() { await Seed(); }", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-level-blocked-caught-installer", body: "try { _outer.ExecuteAsync(async () => { await Midd"···, members: "private async Task Middle() { await Seed(); }", other: "installer", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-level-unwaited", body: "_outer.ExecuteAsync(async () => { await Middle(); "···, members: "private async Task Middle() { await Seed(); }", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-get-caught", body: "try { _outer.ExecuteAsync(async () => { var s = Se"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-get-caught-installer", body: "try { _outer.ExecuteAsync(async () => { var s = Se"···, members: "", other: "installer", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-unwaited", body: "_outer.ExecuteAsync(async () => { var s = Seed(); "···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-all-caught", body: "try { var t = _outer.ExecuteAsync(async () => { va"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-all-caught-installer", body: "try { var t = _outer.ExecuteAsync(async () => { va"···, members: "", other: "installer", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-caught", body: "try { _outer.ExecuteAsync(async () => { var s = Se"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-caught-installer", body: "try { _outer.ExecuteAsync(async () => { var s = Se"···, members: "", other: "installer", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-caught-on-the-pool", body: "await Task.Run(() => { try { _outer.ExecuteAsync(a"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-caught-on-the-pool-installer", body: "await Task.Run(() => { try { _outer.ExecuteAsync(a"···, members: "", other: "installer", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-in-a-loop-caught", body: "for (var pass = 0; pass < 2; pass++) { try { _oute"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-uncaught", body: "_outer.ExecuteAsync(async () => { var s = Seed(); "···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-uncaught-installer", body: "_outer.ExecuteAsync(async () => { var s = Seed(); "···, members: "", other: "installer", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-waited-after-may-throw", body: "var t = _outer.ExecuteAsync(async () => { var s = "···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-when-all", body: "await Task.WhenAll(_outer.ExecuteAsync(async () =>"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-await", body: "await Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-await-caught", body: "try { await Runs.Lib.Run(async () => { var s = See"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-await-caught-installer", body: "try { await Runs.Lib.Run(async () => { var s = See"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-await-installer", body: "await Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-get-result", body: "Runs.Lib.Run(async () => { var s = Seed(); await s"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-get-result-caught", body: "try { Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-get-result-caught-installer", body: "try { Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-get-result-installer", body: "Runs.Lib.Run(async () => { var s = Seed(); await s"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-result", body: "_ = Runs.Lib.TaskOf(async () => { var s = Seed(); "···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-result-caught", body: "try { _ = Runs.Lib.TaskOf(async () => { var s = Se"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-result-caught-installer", body: "try { _ = Runs.Lib.TaskOf(async () => { var s = Se"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-result-installer", body: "_ = Runs.Lib.TaskOf(async () => { var s = Seed(); "···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-task-less", body: "Runs.Lib.Fire(async () => { var s = Seed(); await "···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-task-less-caught", body: "try { Runs.Lib.Fire(async () => { var s = Seed(); "···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-task-less-caught-installer", body: "try { Runs.Lib.Fire(async () => { var s = Seed(); "···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-task-less-installer", body: "Runs.Lib.Fire(async () => { var s = Seed(); await "···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-get-result", body: "_ = Runs.Lib.ValueOf(async () => { var s = Seed();"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-get-result-caught", body: "try { _ = Runs.Lib.ValueOf(async () => { var s = S"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-get-result-caught-installer", body: "try { _ = Runs.Lib.ValueOf(async () => { var s = S"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-get-result-installer", body: "_ = Runs.Lib.ValueOf(async () => { var s = Seed();"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-result", body: "_ = Runs.Lib.ValueOf(async () => { var s = Seed();"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-result-caught", body: "try { _ = Runs.Lib.ValueOf(async () => { var s = S"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-result-caught-installer", body: "try { _ = Runs.Lib.ValueOf(async () => { var s = S"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-result-installer", body: "_ = Runs.Lib.ValueOf(async () => { var s = Seed();"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait", body: "Runs.Lib.Run(async () => { var s = Seed(); await s"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-all", body: "var t = Runs.Lib.Run(async () => { var s = Seed();"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-all-caught", body: "try { var t = Runs.Lib.Run(async () => { var s = S"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-all-caught-installer", body: "try { var t = Runs.Lib.Run(async () => { var s = S"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-all-installer", body: "var t = Runs.Lib.Run(async () => { var s = Seed();"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-caught", body: "try { Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "", sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-caught-installer", body: "try { Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-installer", body: "Runs.Lib.Run(async () => { var s = Seed(); await s"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "pair", self: "pair", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PollyRetryAttemptMatrixTests.Await_of_the_retry_call_past_the_catch_keeps_the_pair_of_a_child_an_attempt_left(shape: "direct", leaves: False, pair: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PollyRetryAttemptMatrixTests.Await_of_the_retry_call_past_the_catch_keeps_the_pair_of_a_child_an_attempt_left(shape: "direct", leaves: True, pair: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PollyRetryAttemptMatrixTests.Await_of_the_retry_call_past_the_catch_keeps_the_pair_of_a_child_an_attempt_left(shape: "when-all", leaves: False, pair: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PollyRetryAttemptMatrixTests.Await_of_the_retry_call_past_the_catch_keeps_the_pair_of_a_child_an_attempt_left(shape: "when-all", leaves: True, pair: True)`

### Удалённые имена (73)

- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-awaited", pass: "await _outer.ExecuteAsync(async () => { var s = Se"···, sync: False, self: False, mine: False)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-awaited-after-may-throw", pass: "var t = _outer.ExecuteAsync(async () => { var s = "···, sync: False, self: False, mine: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-blocked-on", pass: "_outer.ExecuteAsync(async () => { var s = Seed(); "···, sync: False, self: False, mine: False)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-in-a-loop-awaited", pass: "for (var pass = 0; pass < 2; pass++) { await _oute"···, sync: False, self: False, mine: False)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-unawaited", pass: "_outer.ExecuteAsync(async () => { var s = Seed(); "···, sync: False, self: False, mine: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "outer-when-all", pass: "await Task.WhenAll(_outer.ExecuteAsync(async () =>"···, sync: False, self: False, mine: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassAdmissionTests.Delegate_of_an_outer_admitted_call_completes_the_work_for_code_after_the_call_only_where_the_call_is_waited(label: "tail-after-an-unawaited-outer", pass: "var h = HelperDetached(); await h;", sync: False, self: False, mine: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-await-caught", body: "try { await _outer.ExecuteAsync(async () => { var "···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-await-caught-installer", body: "try { await _outer.ExecuteAsync(async () => { var "···, members: "", other: "installer", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-await-caught-on-the-pool", body: "await Task.Run(async () => { try { await _outer.Ex"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-await-in-a-loop-caught", body: "for (var pass = 0; pass < 2; pass++) { try { await"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-helper", body: "var h = Helper(); await h; Mine();", members: "private async Task Helper() { await _outer.Execute"···, other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-helper-in-a-loop", body: "for (var pass = 0; pass < 2; pass++) { var h = Hel"···, members: "private async Task Helper() { await _outer.Execute"···, other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-helper-waited-late", body: "var h = Helper(); Mine(); await h;", members: "private async Task Helper() { await _outer.Execute"···, other: "", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-helper-whose-tail-writes", body: "var h = Helper(); await h;", members: "private async Task Helper() { await _outer.Execute"···, other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-helper-whose-tail-writes-unwait"···, body: "var h = Helper();", members: "private async Task Helper() { await _outer.Execute"···, other: "", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-awaited-in-a-loop", body: "for (var pass = 0; pass < 2; pass++) { await _oute"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-call-awaited", body: "await _outer.ExecuteAsync(async () => { await _see"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-call-awaited-in-a-loop", body: "for (var pass = 0; pass < 2; pass++) { await _oute"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-level-awaited", body: "await _outer.ExecuteAsync(async () => { await Midd"···, members: "private async Task Middle() { await Seed(); }", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-level-blocked-caught", body: "try { _outer.ExecuteAsync(async () => { await Midd"···, members: "private async Task Middle() { await Seed(); }", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:observers,thread-context")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-level-blocked-caught-installer", body: "try { _outer.ExecuteAsync(async () => { await Midd"···, members: "private async Task Middle() { await Seed(); }", other: "installer", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-delegate-level-unwaited", body: "_outer.ExecuteAsync(async () => { await Middle(); "···, members: "private async Task Middle() { await Seed(); }", other: "", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-get-caught", body: "try { _outer.ExecuteAsync(async () => { var s = Se"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:observers,thread-context")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-get-caught-installer", body: "try { _outer.ExecuteAsync(async () => { var s = Se"···, members: "", other: "installer", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-unwaited", body: "_outer.ExecuteAsync(async () => { var s = Seed(); "···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-all-caught", body: "try { var t = _outer.ExecuteAsync(async () => { va"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:observers,thread-context")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-all-caught-installer", body: "try { var t = _outer.ExecuteAsync(async () => { va"···, members: "", other: "installer", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-caught", body: "try { _outer.ExecuteAsync(async () => { var s = Se"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:observers,thread-context")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-caught-installer", body: "try { _outer.ExecuteAsync(async () => { var s = Se"···, members: "", other: "installer", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-caught-on-the-pool", body: "await Task.Run(() => { try { _outer.ExecuteAsync(a"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:observers,thread-pool-entry")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-caught-on-the-pool-installer", body: "await Task.Run(() => { try { _outer.ExecuteAsync(a"···, members: "", other: "installer", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-in-a-loop-caught", body: "for (var pass = 0; pass < 2; pass++) { try { _oute"···, members: "", other: "", sync: "ordered:observers,thread-context", self: "ordered:observers,thread-context", mine: "ordered:observers,thread-context")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-uncaught", body: "_outer.ExecuteAsync(async () => { var s = Seed(); "···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-wait-uncaught-installer", body: "_outer.ExecuteAsync(async () => { var s = Seed(); "···, members: "", other: "installer", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-waited-after-may-throw", body: "var t = _outer.ExecuteAsync(async () => { var s = "···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Climb_reads_each_wait_as_the_join_reading_gives_it(label: "outer-when-all", body: "await Task.WhenAll(_outer.ExecuteAsync(async () =>"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-await", body: "await Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-await-caught", body: "try { await Runs.Lib.Run(async () => { var s = See"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-await-caught-installer", body: "try { await Runs.Lib.Run(async () => { var s = See"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-await-installer", body: "await Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-get-result", body: "Runs.Lib.Run(async () => { var s = Seed(); await s"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-get-result-caught", body: "try { Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:observers,thread-context")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-get-result-caught-installer", body: "try { Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-get-result-installer", body: "Runs.Lib.Run(async () => { var s = Seed(); await s"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-result", body: "_ = Runs.Lib.TaskOf(async () => { var s = Seed(); "···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-result-caught", body: "try { _ = Runs.Lib.TaskOf(async () => { var s = Se"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:observers,thread-context")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-result-caught-installer", body: "try { _ = Runs.Lib.TaskOf(async () => { var s = Se"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-result-installer", body: "_ = Runs.Lib.TaskOf(async () => { var s = Seed(); "···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-task-less", body: "Runs.Lib.Fire(async () => { var s = Seed(); await "···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-task-less-caught", body: "try { Runs.Lib.Fire(async () => { var s = Seed(); "···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-task-less-caught-installer", body: "try { Runs.Lib.Fire(async () => { var s = Seed(); "···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-task-less-installer", body: "Runs.Lib.Fire(async () => { var s = Seed(); await "···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-get-result", body: "_ = Runs.Lib.ValueOf(async () => { var s = Seed();"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-get-result-caught", body: "try { _ = Runs.Lib.ValueOf(async () => { var s = S"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-get-result-caught-installer", body: "try { _ = Runs.Lib.ValueOf(async () => { var s = S"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-get-result-installer", body: "_ = Runs.Lib.ValueOf(async () => { var s = Seed();"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-result", body: "_ = Runs.Lib.ValueOf(async () => { var s = Seed();"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-result-caught", body: "try { _ = Runs.Lib.ValueOf(async () => { var s = S"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-result-caught-installer", body: "try { _ = Runs.Lib.ValueOf(async () => { var s = S"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-value-result-installer", body: "_ = Runs.Lib.ValueOf(async () => { var s = Seed();"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait", body: "Runs.Lib.Run(async () => { var s = Seed(); await s"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-all", body: "var t = Runs.Lib.Run(async () => { var s = Seed();"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-all-caught", body: "try { var t = Runs.Lib.Run(async () => { var s = S"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:observers,thread-context")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-all-caught-installer", body: "try { var t = Runs.Lib.Run(async () => { var s = S"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-all-installer", body: "var t = Runs.Lib.Run(async () => { var s = Seed();"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-caught", body: "try { Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "", sync: "ordered:", self: "ordered:", mine: "ordered:observers,thread-context")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-caught-installer", body: "try { Runs.Lib.Run(async () => { var s = Seed(); a"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "pair")`
- `ConcurrencyHunter.Core.Tests.Engine.PerPassClimbTests.Outer_wait_of_the_projects_library_is_read_by_its_form(label: "runs-wait-installer", body: "Runs.Lib.Run(async () => { var s = Seed(); await s"···, members: "", other: "SynchronizationContext.SetSynchronizationContext(n"···, sync: "ordered:", self: "ordered:", mine: "ordered:")`
- `ConcurrencyHunter.Core.Tests.Engine.PollyRetryAttemptMatrixTests.Await_of_the_retry_call_past_the_catch_keeps_the_pair_of_a_child_an_attempt_left(shape: "direct", leaves: False)`
- `ConcurrencyHunter.Core.Tests.Engine.PollyRetryAttemptMatrixTests.Await_of_the_retry_call_past_the_catch_keeps_the_pair_of_a_child_an_attempt_left(shape: "direct", leaves: True)`
- `ConcurrencyHunter.Core.Tests.Engine.PollyRetryAttemptMatrixTests.Await_of_the_retry_call_past_the_catch_keeps_the_pair_of_a_child_an_attempt_left(shape: "when-all", leaves: False)`
- `ConcurrencyHunter.Core.Tests.Engine.PollyRetryAttemptMatrixTests.Await_of_the_retry_call_past_the_catch_keeps_the_pair_of_a_child_an_attempt_left(shape: "when-all", leaves: True)`

## Остатки для каталога

- D-0005, заведено как [#413](https://github.com/dlin10/ai-coding-plugins/issues/413): не-async
  цель сохраняет все синхронные части в caller и пропускает гонку позднего запуска с кодом после
  неожидаемого вызова.
- D-0008 (a), заведено как [#414](https://github.com/dlin10/ai-coding-plugins/issues/414): два
  исполнения вложенного вызова в Prefix лишают его допуска; лишние пары следующей синхронной части
  и продолжения с собой.
- D-0008 (b), заведено как [#415](https://github.com/dlin10/ai-coding-plugins/issues/415):
  завершение задачи позднего запуска не доказано через задачу вызова; лишние пары потомка после
  ожидания вызова.
- D-0008 (c), заведено как [#416](https://github.com/dlin10/ai-coding-plugins/issues/416): разные
  продолжения одного допущенного вызова не упорядочены; лишняя next-run пара.
- Перепись фреймворков называет устаревший движок; владелец решает её регенерацию. Её fingerprint
  не обновлён. CoreLib и snapshot model evals сохранены. Заведено как
  [#417](https://github.com/dlin10/ai-coding-plugins/issues/417).

Первые четыре границы перечислены в §3 PLAN со ссылками на эти issue. #407 и #408 этим раном не
исправляются.

## Серверные проверки

Исполняемый gate задачи документов и финальные G1–G4 запускает host после задачи; этот документ
не объявляет их пройденными. В частности, полная проверка базиса после publish и model evals
с `CH_MODEL_EVALS=1` без записи snapshot принадлежат финальным gates сервера. Приведённые выше
замеры и запись базиса — сохранённые результаты предыдущих задач, а не новый финальный прогон.
