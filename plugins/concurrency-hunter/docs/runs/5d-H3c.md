# 5d run H3c

## Проба маршрута свежего объекта

Базовый замер — H3b на `b543c3f4`, `scratch/h3b/probe/`. В admitted demo
(тела Polly 7.2.3 допущены, без записей Polly) 14 находок
`UnwaitedNestedRetryWorker` были 20 парами доступов к одному региону `Polly.Context`.
Каждый вызов `AsyncPolicy.ExecuteAsync(Func<Task>)` создаёт свой `new Context()`;
повторения одного места allocation представлены одним регионом.

Тестовая проба первой задачи проверила две предпосылки правила:

- D1: существующее доказательство Task forwarder принимает все три пробрасывающих
  члена на этих маршрутах в каждом исполнении их экземпляров. Ответ
  `HappensBefore.ForwardedTask` — конечный async-запуск цепочки; с операцией
  возвращаемого вызова сравнивается источник каждого возврата summary, а не этот ответ.
- D2: каждый шаг передаёт только этот `Context`: параметр вызываемого держит ровно
  его регион, входящее ребро — ожидаемый шаг, экземпляры не слиты. Значение вызывающего
  происходит от allocation или от его собственного параметра, без других источников,
  кроме маркера этого параметра; другой операнд неизвестного вызова `Context` не достигает.

Проба связала восемь тел B0–B7 семью шагами; полный отчёт с экземплярами, операциями,
связываниями, источниками и доказательствами видов вызова —
`scratch/h3c/probe/fresh-routes.json` (14 находок, 20 пар, восемь экземпляров,
семь шагов, список ошибок пуст):

| Шаг | Маршрут | Доказанный вид |
|---|---|---|
| S1 | B0 `ExecuteAsync(Func<Task>)` → B1 `ExecuteAsync(..., Context, ...)` | B0 создаёт значение и возвращает задачу этого вызова |
| S2 | B1 → B2 `SetPolicyContext` | Прямой синхронный вызов |
| S3 | B1 → B3 `RestorePolicyContext` | Прямой синхронный вызов |
| S4 | B1 → B4 необобщённый `ImplementationAsync` | `IsAwaitedImmediately`, включая `ConfigureAwait`; B4 — forwarder |
| S5 | B4 → B5 `AsyncRetryPolicy.ImplementationAsync<T>` | Возвращаемый вызов forwarder-а, разрешённый виртуальным dispatch |
| S6 | B5 → B6 `AsyncRetryEngine.ImplementationAsync<T>` | Возвращаемый вызов forwarder-а |
| S7 | B6 → B7 delay wrapper из B5 | Прямой вызов делегата, без ребра в `heap.RepeatedRuns`; B7 не async |

Доступы B2/B3 идут через параметр `Context`, доступ B7 — unknown effect вызова,
получившего `ctx`. У summary доступа к полю не было неизвестных источников его базы:
для B2/B3 проба опиралась на исходники Polly, где база — сам параметр. Это граница
пробы, а не доказательство всего правила.

Первая попытка выявила RMW (D-0015): `RestorePolicyContext` в корневом исполнении
возвращает прочитанное `SetPolicyContext` значение, и движок сворачивает запись в
read-modify-write. Поэтому проверка только простых чтений и записей была недостаточна;
свежесть должна охватывать все части RMW. После уточнения проба прошла
(`scratch/h3c/gates/probe.log`, `scratch/h3c/gates/report.log`). Она была обычным тестом
без обязательной переменной окружения. После снятия выбранных ею находок
`FreshRouteProbeTests` удалён; отчёт сохранён как свидетельство предпосылок.

## Правило

[ADR 0034](../adr/0034-a-fresh-object-reaches-the-calls-its-invocation-waits-for.md)
расширяет правило SPEC 10.2 на значение, переданное аргументом или получателем вниз
по прямым вызовам, на любую глубину, в телах программы и допущенной библиотеки.
Прямым считается ребро, которое связывает передача вызова summary: точная цель,
dispatch по объектам получателя, конструктор или непосредственно вызываемый делегат.
Причина ребра сама по себе этого не доказывает.

Допустимы три вида: синхронное тело без async, итератора и доказанного forwarder-а;
async-вызов или forwarder с `await` прямо на вызове; вызов, задающий единственный
источник задачи каждого возврата доказанного forwarder-а, если тот создал значение
или каждое его входящее ребро относится ко второму или третьему виду.
Доказательство параметра и получателя принадлежит не телу, а экземпляру: он не слит,
связывания идут только от непустого множества входящих рёбер, регионы слота точно
совпадают с объединением переданных регионов, каждое ребро прямое, допустимого вида
и передаёт свежее значение без посторонних источников. Это наименьшая неподвижная
точка от allocations: рекурсия с передачей собственного значения не доказывает себя,
рекурсия с новой allocation допустима.

Свежесть относится к собственным полям значения и unknown effect на них.
RMW и составной доступ свежи, когда свежи все их части. Свежесть держателя не
распространяется на поле другого объекта, ячейки хранимого массива или структуру
и ячейки хранимой коллекции. Не передают её spawn, позднее и блокирующее ожидание,
брошенная задача forwarder-а, completion value, capture, связывание корня или host-а
и добавленные моделью, held delegate, потреблением, перечислением или comparer-ом
рёбра. Другой входящий путь или слияние также оставляет пары. Два свежих доступа
по-прежнему отсекает `SKIP_FRESH_OBJECT`. Стоящие эффекты элементов массива и
coverage `unresolved-base` сохраняют только свежесть из allocation в том же теле.

## Замеры с записями и с допуском

Тройки — (fingerprint, rule, confidence). После последней правки движка online-запись
моделей завершилась до замеров. Итоговые файлы четырёх прогонов:
`scratch/h3b/h3c-task4-final-fcd7b45d60034c7bab99826bc825abe6/`;
сверка — `scratch/h3c/task4-triples-final.log`. Первое сравнение с H3b также сохранено
в `scratch/h3c/task2-triples.log`.

| Прогон | Находки до → после | Результат сверки троек |
|---|---:|---|
| demo records | 277 → 277 | Все тройки сохранены |
| demo admitted | 293 → 279 | Сняты ровно 14 троек `Context` |
| eShop records | 2 → 2 | Обе тройки сохранены |
| eShop admitted | 2 → 2 | Обе тройки сохранены и равны records |

Обычные demo (277) и eShop (2) сохраняют каждую тройку. В admitted demo остаются
ровно все 277 троек records и две прежние тройки: `beab0b89c04944ad`
(`DCA1001`, High, `AsyncRetryWorker._joined`) за
[#402](https://github.com/dlin10/ai-coding-plugins/issues/402) и
`0d8df6303781e5e8` (`DCA1001`, High, `UnwaitedNestedRetryWorker._left`) за
[#404](https://github.com/dlin10/ai-coding-plugins/issues/404).
eShop сохраняет `7c919c7aedc4b38e` и `fa3fbc528c46593e`.
Посторонних исчезновений, добавлений, смен правила или confidence нет.
`demo/expected-findings.json` не изменён.

Снятые тройки admitted demo; причина у всех одна — доступы к `Context`, переданному
вниз по доказанным шагам S1–S7, стали свежими:

| Отпечаток | Правило | Confidence |
|---|---|---|
| `0524864340b4353b` | DCA1002 | High |
| `2379952072974ddb` | DCA1002 | High |
| `2cf8baf23ae18f63` | DCA1001 | Medium |
| `311c9df2db3d44da` | DCA1001 | Medium |
| `3a949021a7a660a5` | DCA1001 | Medium |
| `494eb6ff8187f085` | DCA1001 | High |
| `79f689686e9e9b59` | DCA1001 | Medium |
| `807b9682d3bff99c` | DCA1001 | Medium |
| `807deb9f1da2bbdf` | DCA1002 | Medium |
| `84fdefd055027cc4` | DCA1001 | Medium |
| `be087dc34329ce4b` | DCA1001 | High |
| `c203ff7acbe7a484` | DCA1002 | Medium |
| `e38e848da2220a30` | DCA1001 | High |
| `ec7a1c9eb38b5705` | DCA1001 | Medium |

Исходный demo-факт H3 в `CallSiteProbeTests` остаётся Skipped с причиной только
`Waits for #402 and #404: a pass in a library loop's continuation, later runs of a sequential-awaited record.`
Снятие находок `Context` не объявляет эти два остатка исправленными.

## Матрицы и границы правила

`FreshHandOffMatrixTests` содержит 235 клеток, без `Wider`:

- 150 базовых сочетаний 16 видов вызова, аргумента/получателя и пяти доступов
  (write, read, RMW, unknown effect, поле объекта из поля). Получатель неприменим
  к статическому вызову и вызову делегата моделью.
- 72 дополнительных сочетания для синхронного instance-вызова, awaited async
  и цепочки forwarder-ов: смешанный RMW, unknown effect на получателе и с holder-ом,
  запись и RMW ячейки массива в поле, структура списка и составной доступ к словарю;
  fresh+loaded, слияние, host-injected и рекурсивное связывание. Для аргумента добавлены
  два свежих источника и рекурсия с новой allocation.
- 10 маршрутов глубины два: static, instance, virtual, direct delegate, awaited async,
  `ConfigureAwait`, awaited forwarder, цепочка forwarder-ов, unawaited async и `Task.Run`.
- Три смешанных входа: awaited+unawaited async, awaited+брошенный forwarder
  и direct+model.

Оси вызова включают также forwarder-создатель, поздний `await`, блокирующее ожидание,
model delegate и iterator. Прямой вызов делегата с аргументом отделён от связанного
получателя делегата: последний приходит через делегат и свежести не получает.
Проверяются и положительные передачи, и сохранение пар на каждом отрицательном пути.

Пять фактов матрицы:

- `There_are_235_cells_covering_every_value_of_every_axis` — число и уникальность клеток,
  покрытие каждого значения осей call, channel, effect, origin и depth.
- `Every_cell_is_exact_and_the_wider_list_is_empty` — предпосылки, точная свежесть и пары
  каждой клетки; список `Wider` пуст.
- `No_cell_is_fresh_where_its_expectation_pairs` — ни одна отрицательная клетка
  не получила свежесть и не потеряла требуемую пару.
- `Edges_a_model_a_held_delegate_a_consumption_or_a_comparer_adds_are_not_direct` —
  рёбра held delegate, потребления, вызова члена моделью и синтезированного comparer
  отделены от прямых.
- `Freshness_and_pairs_do_not_depend_on_walk_order_or_the_candidate_index` —
  обычные и слитые экземпляры, обратный порядок и перестановки fresh-fixpoint,
  обратный обход исполнений и seeds 17, 93, 106; доступы и пары совпадают,
  candidate index даёт те же пары, что эталонный перебор.

Все пять фактов прошли: 5 Passed, 0 Failed, 0 Skipped
(`scratch/h3c/task4-matrix-final.log`). Дополнительный выбранный набор:
322 Passed, 0 Failed, 0 Skipped (`scratch/h3c/task4-focused.log`).
`FreshDeduplicationTests` добавляет восемь сочетаний отношений наблюдений
Equal/Incomparable/Stronger/Weaker и порядка FreshFirst/NotFreshFirst;
`HandedSlotTests` — десять форм параметра/получателя, присваивания, преобразования
и phi, в том числе с посторонним параметром, получателем, allocation, null и default.

В задаче 3 `CompletionValueMatrixTests` сохранила границу completion value:
близнец напрямую передаёт свежий объект ожидаемому `Use`, сама клетка получает
его из completion задачи. Поэтому ровно пять прежних клеток шире близнеца
только по свежести (`fresh False, expected True`):

- `C_FromResult_Argument_Await_Fresh`;
- `C_ValueTaskFromResult_Argument_Await_Fresh`;
- `C_ValueTaskOfValue_Argument_Await_Fresh`;
- `C_CompletionSource_Argument_Await_Fresh`;
- `C_CompletionSourceTry_Argument_Await_Fresh`.

Это не новые клетки или имена базиса. `HandedFreshRule` требует именно этого
расхождения у каждой перечисленной клетки и не разрешает иных различий;
прежние `FieldRule` и `Wider` не расширены. Исходная проверка выявила эти пять
расхождений (`scratch/h3c/task3-before.log`); после уточнения ожиданий прошли
все шесть тестов матрицы (`scratch/h3c/task3-after.log`).

## Модели demo и снимки

После последнего изменения движка online `build/record-demo-models.ps1` записал
пять прежних файлов моделей и прочёл их без новой генерации
(`scratch/h3c/task4-record-models.log`). Во всех пяти изменён только `generator`,
ответы сохранены:

- до: `237825e2038faad6e461abb329a96888144626af1b399f015fb8a30540d27762`;
- после: `5a16b2b0646aed499a9ab0dac59da643b7196d58e8060d029ed484f247a85457`.

Причина смены версии — изменение токенов движка. Фреймворк-перепись и CoreLib
не регенерировались; снимок model evals не перезаписывался.

Перед полным набором текущий движок опубликован в `bin/win-x64`
(`scratch/h3c/publish.log`); все 30 проектов eShop имеют `obj/project.assets.json`.
Отдельные Release-проверки снимков прошли: 2 Passed, 0 Failed, 0 Skipped
(`scratch/h3c/snapshot-tests-before.log`, сводка `scratch/h3c/snapshots.md`).
Оба снимка совпали целиком: все `counts`, pair skip и assumption counters,
`coverage` и отпечатки неизменны. Движение чисел снимков отсутствует,
причин для перезаписи нет; `demo.json` и `eshop.json` не переписаны.

| Цель | Находки | Occurrences | Группы | `pairs.skips.ordered` | `assumed.observers` | `assumed.thread-context` | `assumed.thread-pool-entry` |
|---|---:|---:|---:|---:|---:|---:|---:|
| demo | 277 → 277 | 296 → 296 | 223 → 223 | 63 → 63 | 4 → 4 | 4 → 4 | 0 → 0 |
| eShop | 2 → 2 | 2 → 2 | 2 → 2 | 1564 → 1564 | 1564 → 1564 | 1564 → 1564 | 0 → 0 |

## Базис тестов

Полная запись `build/check-test-baseline.ps1 -Record -KeepResults scratch/h3c/baseline-results`
с `CH_ESHOP_ROOT=C:\Dev\eShopOnContainers`, без `CH_MODEL_EVALS` и
`CH_MODEL_EVALS_RECORD`, прошла (`scratch/h3c/baseline-record.log`). После исправления
code review базис перезаписан так же, без `-KeepResults`
(`scratch/h3c/orch/record-baseline.log`); итоги ниже взяты из этой записи:

| Набор | Passed до → после | Failed до → после | Skipped до → после | Всего до → после |
|---|---:|---:|---:|---:|
| Core | 17075 → 17100 | 0 → 0 | 40 → 40 | 17115 → 17140 |
| CLI, включая E2E | 174 → 174 | 0 → 0 | 0 → 0 | 174 → 174 |

Базис: 17093 → 17118 различных с учётом регистра имён (строк файла),
17053 → 17078 Passed, 40 → 40 Skipped. При сравнении имён без учёта регистра
(`Sort-Object -Unique`) это 17086 → 17111: семь пар имён уже различались только
регистром, их состав не менялся. Число результатов TRX больше числа строк базиса,
потому что повторённые полные имена базис хранит один раз.

Добавлено 26 имён, удалено одно; все добавления Passed. Из них одно — замена
прежнего отрицательного факта свежести параметра, ещё одно фиксирует неожидаемый
вызов, ещё одно (из исправления code review) — свежесть, которую receiver
непрозрачного вызова и неразрешённой диспетчеризации несёт к unknown effect,
восемь проверяют deduplication, десять — handed slot, пять — матрицу.
Итого +25 имён и +25 Passed; ни один прежний Passed не потерян без замены.
`check-baseline-rerecord.ps1` принял замену
`Parameter_bound_to_a_fresh_object_is_not_fresh` →
`Parameter_bound_to_a_fresh_object_by_a_waited_call_is_fresh`
(`scratch/h3c/baseline-rerecord.log`).

Полные добавленные имена:

- `ConcurrencyHunter.Core.Tests.Engine.FreshDeduplicationTests.Fresh_and_not_fresh_observations_of_one_access_are_kept_apart(relation: "Equal", order: "FreshFirst")`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshDeduplicationTests.Fresh_and_not_fresh_observations_of_one_access_are_kept_apart(relation: "Equal", order: "NotFreshFirst")`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshDeduplicationTests.Fresh_and_not_fresh_observations_of_one_access_are_kept_apart(relation: "Incomparable", order: "FreshFirst")`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshDeduplicationTests.Fresh_and_not_fresh_observations_of_one_access_are_kept_apart(relation: "Incomparable", order: "NotFreshFirst")`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshDeduplicationTests.Fresh_and_not_fresh_observations_of_one_access_are_kept_apart(relation: "Stronger", order: "FreshFirst")`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshDeduplicationTests.Fresh_and_not_fresh_observations_of_one_access_are_kept_apart(relation: "Stronger", order: "NotFreshFirst")`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshDeduplicationTests.Fresh_and_not_fresh_observations_of_one_access_are_kept_apart(relation: "Weaker", order: "FreshFirst")`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshDeduplicationTests.Fresh_and_not_fresh_observations_of_one_access_are_kept_apart(relation: "Weaker", order: "NotFreshFirst")`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshHandOffMatrixTests.Edges_a_model_a_held_delegate_a_consumption_or_a_comparer_adds_are_not_direct`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshHandOffMatrixTests.Every_cell_is_exact_and_the_wider_list_is_empty`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshHandOffMatrixTests.Freshness_and_pairs_do_not_depend_on_walk_order_or_the_candidate_index`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshHandOffMatrixTests.No_cell_is_fresh_where_its_expectation_pairs`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshHandOffMatrixTests.There_are_235_cells_covering_every_value_of_every_axis`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshObjectTests.Opaque_and_unresolved_receiver_effects_preserve_local_and_handed_freshness`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshObjectTests.Parameter_bound_by_a_call_not_awaited_is_not_fresh`;
- `ConcurrencyHunter.Core.Tests.Engine.FreshObjectTests.Parameter_bound_to_a_fresh_object_by_a_waited_call_is_fresh`;
- `ConcurrencyHunter.Core.Tests.Engine.HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver(form: "Assignment")`;
- `ConcurrencyHunter.Core.Tests.Engine.HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver(form: "Conversion")`;
- `ConcurrencyHunter.Core.Tests.Engine.HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver(form: "ParameterDirect")`;
- `ConcurrencyHunter.Core.Tests.Engine.HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver(form: "PhiOfOneParameter")`;
- `ConcurrencyHunter.Core.Tests.Engine.HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver(form: "PhiWithAllocation")`;
- `ConcurrencyHunter.Core.Tests.Engine.HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver(form: "PhiWithAnotherParameter")`;
- `ConcurrencyHunter.Core.Tests.Engine.HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver(form: "PhiWithDefault")`;
- `ConcurrencyHunter.Core.Tests.Engine.HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver(form: "PhiWithNull")`;
- `ConcurrencyHunter.Core.Tests.Engine.HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver(form: "PhiWithReceiver")`;
- `ConcurrencyHunter.Core.Tests.Engine.HandedSlotTests.Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver(form: "ReceiverDirect")`;

Удалённое имя:

- `ConcurrencyHunter.Core.Tests.Engine.FreshObjectTests.Parameter_bound_to_a_fresh_object_is_not_fresh` — заменено ожидаемым положительным фактом выше.

Временная проба маршрутов в финальный базис не попала, после реализации правила
она удалена. Новых completion-value имён нет. Пропуски model evals в обычном наборе
сохранены, исходный H3 demo-факт остаётся Skipped за #402 и #404.

## Серверные проверки

Исполняемый gate задачи документов и финальные gates плана выполняет сервер.
Задача документов не запускала их повторно; сохранённые результаты выше относятся
к пробе и предыдущим задачам. Финальные G1–G4, включая model evals с
`CH_MODEL_EVALS=1` без записи snapshot, не объявляются пройденными этим документом.