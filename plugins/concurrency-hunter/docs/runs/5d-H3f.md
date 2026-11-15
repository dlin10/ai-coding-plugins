# 5d run H3f

## Причина

Проба допуска тел Polly.Core 8.7.0 в обычном demo показала состояние самой библиотеки:
без call-site input — 278 находок за 7.5 с, с допуском — 1380 записей находок,
147 тел библиотеки и 59.3 с. Из 1102 добавленных записей 1100 содержали оба доступа
в телах Polly.Core к объектам, выделенным Polly.Core: `ResilienceContext` — 864,
`ResilienceProperties` — 216, `ScheduledTaskExecutor` — 16,
`ObjectPool<ResilienceContext>` — 4. Пул выдаёт контекст одному вызывающему через
`Interlocked.CompareExchange`, чего движок не доказывает. Эти числа — исходная проба,
приведённая в [ADR 0037](../adr/0037-two-library-accesses-to-an-object-only-the-library-refers-to-do-not-pair.md).

Находка `polly-execute-invokes-delegate` не изменилась: `687f86f10b06fac5`,
`DCA1002`, Medium на `State.Hits`. Под `PipelineController.Post()` нет доступа
в теле Polly.Core: анализ не вошёл в `ResiliencePipeline.Execute<TResult>`.
Причина этого остаётся отдельной работой, ожидание кейса сохранено.

## Правило

[ADR 0037](../adr/0037-two-library-accesses-to-an-object-only-the-library-refers-to-do-not-pair.md),
SPEC 10.2: два доступа тел библиотеки к library-internal object не образуют candidate.
Объект выделен телом библиотеки при анализе в месте вызова, и программа напрямую
его не держит ни значением тела, ни своим полем, ячейкой или захваченной переменной.
Ссылки из объектов библиотеки и её static не считаются ссылками программы.
Копия value type, которую держит программа, несёт её ссылку на вложенный объект;
копия, которую держит только библиотека, такой ссылки не создаёт.
Для коллекции и её ячейки проверяется сама коллекция, иначе — владелец поля.

Владелец правила — `ConcurrencyHunter.Accesses.LibraryInternalObjects`:
он читает происхождение и прямых держателей регионов из решённой кучи;
`InterproceduralAccesses` переносит ответ в `Access.IsLibraryInternal`.
`InterproceduralPairing` считает пропущенную пару под `library-internal`
(`SKIP_LIBRARY_INTERNAL`) последней, после всех прежних причин и достаточной защиты.
Это правило о принадлежности состояния, не доказательство синхронизации.
Доступ программы, включая её делегат, объект программы, собственные поля объекта
библиотеки, который держит программа, static библиотеки, merged region и группа
с открытым для подстановки регионом сохраняют пары.

Четырнадцать фактов `LibraryInternalObjectTests` проверяют правило и его границы.
Обычные прогоны без call-site input сохраняют прежнее поведение.

## Замеры

Число находок здесь — число записей в измерении, включая повторы между scopes.
После правила источники — `scratch/h3/package-probe/demo-records.json`,
`demo-admitted.json` и `summary.md`: попытка `rule` от 2026-11-15,
14:48:17Z–14:51:08Z, outcome passed. До правила — исходная проба выше.

| Правило | Допуск | Находки | Тела библиотеки | Секунды |
|---|---|---:|---:|---:|
| До | Без допуска | 278 | 0 | 7.5 |
| До | Polly.Core 8 | 1380 | 147 | 59.3 |
| После | Без допуска | 278 | 0 | 8.1087346 |
| После | Polly.Core 8 | 280 | 147 | 34.1551217 |

Сняты 1100 записей на внутренних объектах библиотеки; все тройки обычного прогона
сохранены, потерянных и изменённых троек нет. Находок с доступом в Polly.Core
после правила нет. Pipeline-кейс сохраняет `687f86f10b06fac5`, `DCA1002`, Medium.
Разность времени — наблюдение двух запусков, не установленный порог скорости.

Остались ровно две добавленные находки на `project-model-awaited-runs`,
`RunsState.Done`, обе `DCA1001`: `befe953c12bf1af5` — Medium,
`e8ae8a52f8b90cc9` — High. Тестовый input допускает тело вызова, у которого есть
проектная запись; тело ретрая даёт эти пары, а проектная запись их не даёт.
Это состояние программы, и правило ADR 0037 его не снимает. Допуск из deciding gaps
в будущем ране #279 не допускает вызов, на который уже есть запись.

Polly 7 сохраняет приёмку: в `CallSiteProbeTests` и `HandleExecutionProbeTests`
тройки (fingerprint, rule, confidence) с записями равны тройкам с допущенными телами
при withheld встроенных `polly` records. Сохранённая попытка `matrix`
15:04:42Z–15:08:34Z прошла шесть фактов этих проб; demo records/admitted — 278/278,
eShop — 2/2. Контроли без записей и без допуска различаются с записями, как требуют тесты.

## Матрица

`LibraryInternalObjectMatrixTests`: 66 клеток, шесть фактов, более широкий список
пуст. Оси: создатель (library/program); прямой держатель (library static,
library object, inside held object, program local/field/delegate/object field/collection,
library struct/program struct); авторы доступов (library/library, library/program,
program/program); операция (write/write, write/read, read-modify-write, unknown effect);
хранилище (field, struct field, collection structure/cell, array cell через ref);
слияние (none, context limit, open group); ранняя причина (none, read/read, protected);
замыкание (none, closure kept by library, closure delegate held by program).
Это 31 допустимое базовое сочетание + 3 × (3 + 4 + 2 + 2) расширения + 2 замыкания.

Каждая клетка проверяет предпосылки на куче, точный ответ и прежние счётчики
причин, suppressed и assumptions; индекс кандидатов сравнивается с полным перебором.
Принятое сужение ADR 0037 закреплено отдельно: программа держит объект библиотеки,
но гонка касается только вложенного библиотечного объекта, которого программа напрямую
не держит. Такая пара снята даже у небезопасного для разделения внешнего объекта.
Гонка на собственных полях внешнего объекта остаётся.

## Модели demo и снимки

Пять файлов уже переписаны online после последнего изменения движка
(`scratch/h3/record-demo-models.log`); завершающая задача не запускает запись снова.
`git diff HEAD -- demo/.concurrency-hunter/models/generated` меняет только `generator`:

- до: `0bab732c268c4f85d8cfd1b87dc56b4a3a655215e7824730354ffc19e190dfe3`;
- после: `432ff253e89af6a02a2446d24e80f59a347515c52c5dbb35737e05a0ff305093`;

Файлы относительно `demo/.concurrency-hunter/models/generated/`:

- `Google.Protobuf/3.30.2/601a02d3-9487-45ce-a28b-99b5cb103efd.json`;
- `Grpc.AspNetCore.Server/2.71.0/d97c8a76-b299-4045-a880-e98791e0722a.json`;
- `Grpc.Core.Api/2.71.0/9916e2d7-f051-46dd-8851-1d537a7bfa36.json`;
- `Polly.Core/8.7.0/93b7c81c-a268-4bd8-b6c3-71c7d5524935.json`;
- `Polly/7.2.3/6f9b95f3-9db6-444d-9fed-d3cc1f1d71cb.json`.

Причина версии — токены правила в Analysis; ответы членов не изменены.
Чтение этих файлов не генерирует ничего, что подтверждают журнал записи и
`DemoGeneratedModelsTests` сохранённого `rule`-прогона.
Снимки demo и eShop (находки, counts и coverage), `demo/expected-findings.json`,
кейсы demo, встроенные и проектные записи не изменены относительно HEAD.
Snapshot model evals не переписан; финальные model evals принадлежат серверу.

## Базис тестов

Перед записью все 30 `.csproj` eShop имели `obj/project.assets.json`
(`scratch/h3/close-assets.log`); текущий executable опубликован в `bin/win-x64`
(`scratch/h3/close-publish.log`). Затем отдельный процесс с
`CH_ESHOP_ROOT=C:\Dev\eShopOnContainers`, без `CH_MODEL_EVALS` и
`CH_MODEL_EVALS_RECORD`, выполнил `build/check-test-baseline.ps1 -Record -KeepResults
scratch/h3/baseline-results`. Журнал — `scratch/h3/close-baseline.log`, оба TRX
сохранены в указанном каталоге. Core: 17245 Passed, 0 Failed, 39 Skipped;
CLI: 174 Passed, 0 Failed, 0 Skipped. Продолжительность тестов Core — 8 мин 34 с,
CLI — 6 мин 44 с. Прежние 39 пропусков сохранены; eShop прошёл, model evals
без своего флага не запускались.

Базис хранит уникальные имена с учётом регистра, поэтому число его строк отличается
от суммы результатов TRX. Сравнение с HEAD:

| Базис | Passed | Failed | Skipped | Имена |
|---|---:|---:|---:|---:|
| HEAD | 17199 | 0 | 39 | 17238 |
| Рабочее дерево | 17223 | 0 | 39 | 17262 |

Добавлены ровно 24 имени, все Passed: 14 фактов правила, 6 матрицы и 4 пробы.
Удалённых имён и смен исхода нет. `build/check-baseline-rerecord.ps1` завершился с
кодом 0: «17223 cases recorded as Passed, 17199 before; 0 lost»
(`scratch/h3/close-baseline-rerecord.log`). Полный список добавлений:

- `ConcurrencyHunter.Core.Tests.Engine.CallSitePackageProbeTests.Demo_with_polly_core_bodies_admitted_has_no_finding_in_polly_core_code`
- `ConcurrencyHunter.Core.Tests.Engine.CallSitePackageProbeTests.Demo_with_polly_core_bodies_admitted_reaches_library_bodies`
- `ConcurrencyHunter.Core.Tests.Engine.CallSitePackageProbeTests.Demo_without_a_call_site_input_is_the_committed_normal_run`
- `ConcurrencyHunter.Core.Tests.Engine.CallSitePackageProbeTests.Pipeline_case_finding_is_unchanged_with_polly_core_bodies_admitted`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectMatrixTests.Every_cell_is_exact`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectMatrixTests.Every_value_of_every_axis_occurs_and_the_cells_are_counted`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectMatrixTests.Existing_counts_are_those_of_the_pairing_before_the_rule`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectMatrixTests.No_cell_is_skipped_where_its_expectation_pairs`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectMatrixTests.Skips_and_pairs_do_not_depend_on_the_candidate_index`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectMatrixTests.The_inside_of_a_held_library_object_is_the_accepted_narrowing`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.A_library_list_inside_a_library_object_the_program_holds_makes_no_pair`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.A_library_object_handed_to_a_program_delegate_keeps_its_pair`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.A_library_static_field_keeps_its_pair`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.A_library_struct_that_carries_the_object_is_no_program_holder`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.A_library_write_and_a_program_write_keep_their_pair`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.A_merged_library_object_keeps_its_pair`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.A_pair_an_earlier_reason_skips_keeps_its_reason`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.A_program_list_inside_a_library_internal_object_keeps_its_pair`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.A_program_struct_that_carries_the_object_is_a_program_holder`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.A_protected_pair_on_a_library_internal_object_is_still_suppressed`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.Two_library_writes_to_a_library_object_the_program_holds_keep_their_pair`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.Two_library_writes_to_an_object_a_library_static_keeps_make_no_pair`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.Two_library_writes_to_an_object_the_program_created_keep_their_pair`
- `ConcurrencyHunter.Core.Tests.Engine.LibraryInternalObjectTests.Two_library_writes_to_the_inside_of_a_library_object_the_program_holds_make_no_pair`

## Остатки для каталога

- H3f-L1, заведено как [#418](https://github.com/dlin10/ai-coding-plugins/issues/418) (5d): анализ
  в месте вызова не входит в `ResiliencePipeline.Execute<TResult>` в `polly-execute-invokes-delegate`;
  находка остаётся Medium. Причина не исследуется этим раном.
- H3f-L2, заведено как [#419](https://github.com/dlin10/ai-coding-plugins/issues/419) (5g): static
  библиотеки сохраняет пары при анализе в месте вызова; считать его library-internal — отклонённая
  альтернатива ADR 0037, для изменения нужно отдельное решение.
- H3f-L3, заведено как [#420](https://github.com/dlin10/ai-coding-plugins/issues/420) (5d): допуск тел
  Polly.Core 8 вместо проектной записи ретрая `project-model-awaited-runs` добавляет две находки
  на `RunsState.Done`, перечисленные выше.
- H3f-L4 — перепись фреймворков называет устаревший движок; её fingerprint не обновлялся,
  регенерация остаётся за владельцем по ADR 0022. Это
  [#417](https://github.com/dlin10/ai-coding-plugins/issues/417), отдельного issue нет.

Из интервью заведены ещё два: [#421](https://github.com/dlin10/ai-coding-plugins/issues/421) (5d) —
кейсы demo для пессимистичного и оптимистичного таймаута Polly 7 (D-0009, D-0016), и
[#422](https://github.com/dlin10/ai-coding-plugins/issues/422) (фаза 8) — анализ членов фреймворков
в месте вызова по замеру стоимости компиляции (D-0008).

Допуск из deciding gaps по местам вызова и раундам, граница тел, coverage и отказ
от генерации в ране остаются за #279. Новых временных границ SPEC в §3 PLAN нет:
правило ADR 0037 построено целиком, перечисленные остатки — для каталога issues.

## Серверные проверки

Исполняемый gate задачи `-Stage close` и финальные G1–G5 запускает host после задачи;
этот документ не объявляет их пройденными. Полный baseline после publish, model evals
без записи snapshot, проверка документации и финальная проверка scope принадлежат
финальным gates сервера. Сохранённые попытки `rule` и `matrix` и запись базиса
выше не заменяют эти проверки.
