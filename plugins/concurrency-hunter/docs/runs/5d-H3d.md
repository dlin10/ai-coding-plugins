# 5d run H3d

## Причина

Базовый замер — финальная проба H3c на `3e69c1c2`,
`scratch/h3b/h3c-rule-20261113-212101-4532157-c4bb1034505343cbbcb9c2854450252e/`.
В admitted demo (тела Polly 7.2.3 допущены в месте вызова, записи Polly сняты) оставалась
`beab0b89c04944ad` (`DCA1001`, High) — записи `_joined` у `AsyncRetryWorker`
(`demo/Demo.Web/Cases/PollyRetryAttempts.cs`), хотя каждая попытка ретрая ждёт свой `Task.Run`
([#402](https://github.com/dlin10/ai-coding-plugins/issues/402)).

Исполнение одно на родителя и место (`async:{bodyId}#{op}`, `spawn:…`, `timer:…`). Worker вызывает
одну политику ретрая дважды (`PollyRetryAttempts.cs:49`, `:50`), поэтому у каждого допущенного тела
Polly два method instance, и у шести async-исполнений Polly по два старта (`SpawnAnchor`).
Конструктор `HappensBefore` хранил один старт на исполнение — первый в порядке экземпляров
вызывающих (`_anchorOfChild.TryAdd`), — и пять читателей брали его за все: `PassCompletionProof.Start`,
`SequentialRuns`, `IsComposite`, `JoinedFrom` и `OnThreadPool`. Доказательство прохода прочло старт
второго вызова, отсеклось на его `await`, а путь первого вызова — `await` адаптера может выйти
исключением, движок ловит его, повторяет и начинает следующую попытку — снова дошёл до того же
`Task.Run` без отсечения. В обратную сторону это пропущенная гонка: старт, который не первый, не
проверялся вовсе.

## Испробованное разделение по экземплярам

Первая одобренная версия рана разделила ключи исполнений по method instance: у каждого исполнения
стал один старт. Она меняла `ExecutionModel.cs`, `ConflictFindings.cs`, тесты
`TaskForwarderJoinMatrixTests`, `HandleExecutionProbeTests`, `CallSiteProbeTests`, пять моделей demo и
добавляла `ExecutionIdentityMatrixTests`. Находку она снимала, но слой порядка читает одно исполнение
на родителя и место примерно в двадцати местах — свой и следующий проход, хвосты async-тел, запуски
допущенных вызовов, пути графа порядка, решение `no-overlap`, пересечение semantic gaps, — и
последовательные программы получили пары: две миграции, передающие один seeder в ретрай, два
получателя, каждый из которых ждёт вложенный `Task.Run`, тело, которое ждёт свой `Task.Run` и затем
пишет поле, два получателя на взаимоисключающих ветвях. Попытка записать базис с разделением
отказала (`scratch/h3d/baseline-record-reverted-split.log`): Core — 2 Failed, 17107 Passed, 40 Skipped;
упали `PerPassAdmissionTests.Same_seeder_in_two_sequential_migrations_has_no_pair` и
`ConfirmationAccessesTests.Each_execution_role_is_collected_as_its_readers_read_it`
(повтор — `scratch/h3d/t3-failing-rerun.log`). Разделение отменено задачей 1 (D-0018):
`ExecutionModel.cs` и `ConflictFindings.cs` равны `HEAD`, `ExecutionIdentityMatrixTests` удалён.

## Правило

[ADR 0035](../adr/0035-every-start-of-an-execution-is-proven-on-its-own.md), SPEC TD-034a, TD-060,
TD-065. Исполнение остаётся одним на родителя и место; идентичность исполнений, отпечатки находок и
пар не меняются. Каждый читатель единственного старта читает все старты исполнения — якоря с этим
потомком, отобранные как в конструкторе (родитель и потомок оба в `_executions`):

- Доказательство прохода доказывает каждый старт своим кадром, join-ами и отсечениями; отсечения
  разных стартов не складываются, свидетельства завершения подъёма (`_completions`) хранятся по
  старту. Подъём к исполнению с несколькими стартами идёт через каждый старт, чей вызов
  (`CallerInstance`, операция и ребро кучи) достигает экземпляра подъёма, каждый — своим кадром.
  Следующий проход — любой старт (`AfterCall = false`). Работа упорядочена, только когда это так для
  каждого старта. Запрос одного старта исполнения с несколькими вне выбравшего его подъёма или кадра
  ничего не доказывает — и никогда не первый.
- Запуски допущенного вызова доказываются для каждого старта продолжения (`SequentialRuns`).
- `IsComposite`: составной, если составна работа хотя бы одного старта; продолжение `ContinueWith`
  следует за antecedent, только когда каждый старт доказывает один и тот же (ребро публикуется один
  раз), иначе оно составное. `JoinedFrom`: точка каждого применимого старта достигает своих
  потребителей — `BoundsEnd` ограничивает конец, только когда так у каждого, `LeavesWorkRunning` —
  когда так у любого. `OnThreadPool` читает владельца лишь потому, что поле уходит: все старты делят
  операцию места, ответ у них один.
- Графы, которые перестраивает `Withholding`, читаются так же.

Правило handle (`HandleExecution`, `ProveHandleSource`, ADR 0008) не изменено; собственные рёбра spawn
графа (точка и join в `Path`, join-ы возврата `Parallel`) по-прежнему читают один старт за всё
исполнение (D-0020, остаток ниже).

## Замеры с записями и с допуском

Тройки — (fingerprint, rule, confidence). После последней правки движка online-запись моделей
завершилась до замеров. Итоговые файлы четырёх прогонов — последний замер gate задачи 3:
`scratch/h3b/h3d-records-20261114-134627-1504465-86ce66de8e644eb0b30034dae451c43f/`; те же тройки дали
замеры `h3d-rule-…` и `h3d-matrix-…` рядом с ним.

| Прогон | Находки до → после | Результат сверки троек |
|---|---:|---|
| demo records | 277 → 277 | Все тройки сохранены |
| demo admitted | 279 → 278 | Снята ровно `beab0b89c04944ad` |
| eShop records | 2 → 2 | Обе тройки сохранены |
| eShop admitted | 2 → 2 | Обе тройки сохранены и равны records |

Обычные demo (277) и eShop (2) сохраняют каждую тройку. В admitted demo остаются ровно все 277
троек records и одна прежняя тройка `0d8df6303781e5e8` (`DCA1001`, High,
`UnwaitedNestedRetryWorker._left`): попытка может выйти исключением раньше ожидания, это настоящая
гонка, и она за [#404](https://github.com/dlin10/ai-coding-plugins/issues/404).
`demo-residuals.json` перечисляет одну находку — её. eShop сохраняет `7c919c7aedc4b38e` и
`fa3fbc528c46593e`. Посторонних исчезновений, добавлений, смен правила или confidence нет.
`demo/expected-findings.json` не изменён.

Исходный demo-факт H3 в `CallSiteProbeTests` остаётся Skipped, теперь с причиной только
`Waits for #404: later runs of a sequential-awaited record.`; ни один пропуск больше не называет #402.

## Матрица `ExecutionStartsMatrixTests`

Два и более старта одного исполнения закреплены в коде программы. Оси: что начинает исполнение
(`Async` — хвост async-вызова, `TaskRun` — `Task.Run`, `Admitted` — продолжение делегата допущенного
вызова Polly 7.2.3) и как корень начинает экземпляры (`BothWait` — каждый ждёт сразу,
`OneWaitsOtherTwice` — один ждёт, другой начат дважды без ожидания, `NoneWaits` — никто не ждёт, пока
не начаты оба). Из девяти сочетаний клетки `Admitted/OneWaitsOtherTwice` нет: 8 клеток, `Wider` пуст.
Получатели `a` и `b` берутся каждый из своего `new`, `a` — первым, так что первым граф хранил старт `a`.

Десять фактов, все Passed (`scratch/h3d/t2-matrix.log`: 10 Passed, 0 Failed, 0 Skipped):

- `There_are_8_cells_covering_every_value_of_every_axis` — число и уникальность клеток, покрытие
  каждого значения обеих осей, пустой `Wider` и ровно одна известная пропущенная пара.
- `Every_cell_has_exactly_the_required_pairs` — старты, каждый из которых ждёт, пар не оставляют;
  старт без ожидания, достигнутый дважды, оставляет пару записи места с собой рядом с ожидающим;
  старты, ни один из которых не ждёт до начала другого, оставляют свои (`1-1`; у `Admitted` — `1-2` и
  `2-2`).
- `Every_cell_has_one_execution_with_a_start_per_instance` — запись места идёт в одном исполнении, и у
  него по старту на каждый экземпляр тела (R1).
- `Pairs_do_not_depend_on_walk_order` — обратный обход и seeds 17, 93, 106 дают те же доступы и пары (R6).
- `The_sequential_shapes_have_no_pair` — четыре последовательные формы без пар: две миграции с одним
  seeder, два получателя с вложенным `Task.Run`, тело, ждущее свой `Task.Run` и затем пишущее поле,
  два получателя на исключающих ветвях.
- `A_continuation_with_two_antecedents_is_ordered_after_neither` и
  `A_continuation_with_one_antecedent_is_ordered_after_it` — continuation следует за antecedent,
  только когда каждый старт доказывает один и тот же.
- `A_spawn_composite_for_one_start_is_composite` — spawn, у одного старта которого работа составная
  (async-делегат `a` и не-async делегат `b`, возвращающий задачу), составной: его ожидания ничего не
  упорядочивают.
- `Exclusive_starts_each_joined_in_their_branch_stay_bounded` и
  `Exclusive_starts_one_not_joined_keep_their_pair` — join ограничивает работу, только когда учтён
  каждый старт.

Известная пропущенная пара: клетка `Admitted` × `NoneWaits` требует `1-2` — синхронной записи одной
миграции с записью продолжения другой — и не получает её. Причина — выдача доступов
(`InterproceduralAccesses`) ключует доступ по телу, а не по method instance (D-0021): запись второй
миграции до `await` стоит на месте первой и упорядочена перед обоими стартами продолжения
(`scratch/h3d/t2-probe.txt`: `SKIP ordered=1`, одна запись `Value = 1` на месте `#lambda1`). Это
пропущенная гонка и на `HEAD`, и в коде программы тоже; факт требует, чтобы пара оставалась
пропущенной, и упадёт, когда причину устранят.

## Модели demo и снимки

После последнего изменения движка online `build/record-demo-models.ps1` записал пять прежних файлов
моделей и прочёл их без новой генерации (`scratch/h3d/t1-record-demo-models.log`). Во всех пяти
изменён только `generator`, ответы сохранены:

- до: `5a16b2b0646aed499a9ab0dac59da643b7196d58e8060d029ed484f247a85457`;
- после: `53880bd0dbf69237a8c64db9b51a9fe923f6c7daf73dfd5e95eb943553019114`.

Причина смены версии — изменение токенов движка (`HappensBefore.cs`, `PassCompletionProof.cs`).
Фреймворк-перепись и CoreLib не регенерировались; снимок model evals не перезаписывался.

Перед проверкой снимков текущий движок опубликован в `bin/win-x64` (`scratch/h3d/t3-publish.log`);
все проекты eShop имеют `obj/project.assets.json`. Release-проверки снимков
`MetricsCommandTests.Demo_measurement_matches_the_snapshot` и `EShopSnapshotTests` прошли: 2 Passed,
0 Failed, 0 Skipped (`scratch/h3d/t3-snapshots.log`, сводка `scratch/h3d/snapshots.md`). Оба снимка
совпали целиком: все `counts`, pair skip и assumption counters, `coverage` и отпечатки неизменны.
Движения чисел снимков нет, ни одно число не сдвинулось, и причин для перезаписи нет; `demo.json` и
`eshop.json` не переписаны.

| Цель | Находки | Occurrences | Группы | `pairs.skips.ordered` | `assumed.observers` | `assumed.thread-context` | `assumed.thread-pool-entry` |
|---|---:|---:|---:|---:|---:|---:|---:|
| demo | 277 → 277 | 296 → 296 | 223 → 223 | 63 → 63 | 4 → 4 | 4 → 4 | 0 → 0 |
| eShop | 2 → 2 | 2 → 2 | 2 → 2 | 1564 → 1564 | 1564 → 1564 | 1564 → 1564 | 0 → 0 |

## Базис тестов

Полная запись `build/check-test-baseline.ps1 -Record -KeepResults scratch/h3d/baseline-results` с
`CH_ESHOP_ROOT=C:\Dev\eShopOnContainers`, без `CH_MODEL_EVALS` и `CH_MODEL_EVALS_RECORD`, прошла
(`scratch/h3d/baseline-record.log`); `check-baseline-rerecord.ps1` принял её: 17088 случаев Passed
против 17078, потерянных нет (`scratch/h3d/baseline-rerecord-check.log`).

| Набор | Passed до → после | Failed до → после | Skipped до → после | Всего до → после |
|---|---:|---:|---:|---:|
| Core | 17100 → 17110 | 0 → 0 | 40 → 40 | 17140 → 17150 |
| CLI, включая E2E | 174 → 174 | 0 → 0 | 0 → 0 | 174 → 174 |

Базис: 17118 → 17128 различных с учётом регистра имён (строк файла), 17078 → 17088 Passed,
40 → 40 Skipped. При сравнении имён без учёта регистра это 17111 → 17121: семь пар имён по-прежнему
различаются только регистром.

Добавлено 10 имён, все Passed, — десять фактов матрицы; удалённых имён нет:

- `ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.A_continuation_with_one_antecedent_is_ordered_after_it`;
- `ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.A_continuation_with_two_antecedents_is_ordered_after_neither`;
- `ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.A_spawn_composite_for_one_start_is_composite`;
- `ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.Every_cell_has_exactly_the_required_pairs`;
- `ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.Every_cell_has_one_execution_with_a_start_per_instance`;
- `ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.Exclusive_starts_each_joined_in_their_branch_stay_bounded`;
- `ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.Exclusive_starts_one_not_joined_keep_their_pair`;
- `ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.Pairs_do_not_depend_on_walk_order`;
- `ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.The_sequential_shapes_have_no_pair`;
- `ConcurrencyHunter.Core.Tests.Engine.ExecutionStartsMatrixTests.There_are_8_cells_covering_every_value_of_every_axis`.

Факты пробы `HandleExecutionProbeTests` и `CallSiteProbeTests` сохранили имена и остались Passed;
исходный demo-факт H3 остаётся Skipped за #404.

## Остатки для каталога

- Рёбра spawn графа порядка от точки и от join и join-ы возврата `Parallel` читают один старт за всё
  исполнение (D-0020): `a.Start(); Value = 2; b.Start();` с `Start() => Task.Run(...)` пропускает гонку
  задачи `a` с `Value = 2`. Заведено как [#407](https://github.com/dlin10/ai-coding-plugins/issues/407),
  временная граница PLAN.
- Выдача доступов сливает method instances одного тела: доступы двух экземпляров на одной операции в
  одном исполнении выдаются одним, на месте первого, и гонка доступа второго пропускается (D-0021;
  известная пропущенная пара клетки `Admitted` × `NoneWaits`). Заведено как
  [#408](https://github.com/dlin10/ai-coding-plugins/issues/408), временная граница PLAN.
- Читатели и виды старта без клетки или факта, ответ которых различается между чтением первого старта
  и чтением каждого старта (F-0056): `CompleteBeforeAccess` над несколькими стартами (нужен родитель,
  который повторяется), `ParentAllows` над родителем с несколькими стартами, выбор подъёма среди
  стартов, которые ждут по-разному, `SequentialRuns` по собственному старту каждого вызова вместе с
  `CompleteBefore`/`OutersAllow`/`RunPassAllows`, `JoinWouldOrder` над несколькими стартами, и виды
  старта: создание таймера, `Parallel.For`/`ForEach`, `StartNew` и `Thread.Start`. Заведено как
  [#409](https://github.com/dlin10/ai-coding-plugins/issues/409).
- `0d8df6303781e5e8` остаётся за [#404](https://github.com/dlin10/ai-coding-plugins/issues/404).
- Перепись фреймворков называет устаревший движок, пока её владелец не регенерирует её.

## Серверные проверки

Исполняемый gate задачи документов и финальные gates плана выполняет сервер. Задача документов не
запускала их; сохранённые результаты выше относятся к предыдущим задачам. Финальные G1–G4, включая
model evals с `CH_MODEL_EVALS=1` без записи snapshot, этим документом не объявляются пройденными.
