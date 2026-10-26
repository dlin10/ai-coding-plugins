# 5d run B4d — генерация по зависимостям и круги генерации

База сравнения — `7d91f569ec3a8eb3a35e668d7fcb5a376beecd36` (`HEAD` при записи). Решение рана — [ADR 0027](../adr/0027-the-run-generates-in-rounds-and-a-generation-takes-its-dependencies-models.md). Здесь записаны решения, результаты перезаписи demo, evals и базиса тестов и холодный/тёплый замер eShop.

## Решения

- D-0001 — После каждого хода исправлений выполняется короткая проверка; полная проверка — в конце (`gate=targeted`, `fullGate=final`).
- D-0002 — B4d снимает границы генерации по зависимостям и целей, достигнутых через модель; работа, разрешённая в библиотечный метод, interface call на коллекции, решённый её объектом, и пересланный тип выделены в B4e.
- D-0003 — Generate и ReadOnly идут кругами до неподвижной точки, отвечая всем новым целям круга; причина `reached-through-model` удалена, доступная для генерации цель без ответа получает `budget` или `not-generated` по режиму.
- D-0004 — Генерацию зависимостей ведёт ран в тех же кругах: передаёт ответы генератору, отвечает названным зависимостям и при необходимости повторяет член; `generate`, model evals и CoreLib-тул по-прежнему берут для зависимостей только встроенные модели.
- D-0005 — Внутри генерации доступны встроенные и сгенерированные ответы зависимостей; проектные модели и model lock не читаются и не влияют на вид зависимости, схема сгенерированного файла остаётся 2.
- D-0006 — Ответ зависимости относится к версии implementation-ссылки библиотечной компиляции, наименьшей подходящей на диске, и пишется в собственный файл этой версии, независимо от restore-графа программы.
- D-0007 — Если все зависимости без ответа получили сохраняемые отказы, первый ответ члена окончателен без повтора; общее правило видов и окончательности задано D-0013 и D-0015, последнее заменяет это решение о повторе.
- D-0008 — Члены зависимостей не входят в coverage и счётчики scope, пока анализ не дошёл до их вызова; их ответы пишутся в собственные файлы, время входит в шаг `generation`.
- D-0009 — Каждая снятая граница проверяется тестами; файлы demo перезаписываются, сдвиги снимка объясняются, холодный Generate и следующий тёплый ReadOnly на eShop записываются без порога с возвратом checkout-а.
- D-0010 — Правила зависимостей и кругов закреплены в ADR 0027; SPEC ссылается на него в заголовке.
- D-0011 — Implementation-ссылки дают дополнительные цели только в компиляциях генерации — библиотеке, драйвере и копии рецептов; цели программы и их причины coverage сохраняются, включая `no-implementation` без сохранённого ответа в обоих режимах (уточнение F-0019).
- D-0012 — `metrics` схемы 1.3 содержит `timings.analyses`: id и число анализов каждого scope, считая первый; поле находится вне среза снимков `counts` и `coverage`.
- D-0013 — Сохраняется только окончательный ответ: каждый увиденный ответ зависимости окончателен и даёт прежний вид, отказ равен отсутствию ответа, модель сравнивается по байтам; окончательность пересчитывается по цепочкам в конце каждого круга, цикл не окончателен.
- D-0014 — Scope анализируется снова только при смене применяемых ответов; круги завершаются, когда файл или бюджет уже не могут дать новую работу и последний анализ прочёл применяемый слой, отказ на месте opaque нового анализа не требует.
- D-0015 — Повтор нужен при смене вида зависимости: opaque → модель, модель → другие байты или модель → opaque; `timeout` видов не несёт, сохраняется и не повторяется, ожидающий повтора ответ при исчерпании бюджета остаётся применённым и несохранённым.

## Demo

Exe опубликован командой `dotnet publish src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj -c Release -r win-x64 --self-contained false -o bin/win-x64 --nologo`; код выхода 0, лог — `scratch/b4d-gate/task5-publish.log`.

Описание `build/record-demo-models.ps1` называет цели вызовов scope всех кругов и члены зависимостей (R1–R4). Проверки сохранены: `budget` в coverage генерации и `not-generated` в coverage чтения запрещены. Зависимости сами в coverage scope не входят; несохранённый неокончательный ответ цели scope обнаруживает проверка `not-generated`.

Первая запись завершилась с кодом 1: `Demo.Worker`, `M:Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(System.String[])` остался с причиной `budget` (`scratch/b4d-gate/task5-record-demo-first.log`, измерение — `task5-first-generation.json` в том же каталоге). Окончательные ответы, которые прогон успел сохранить, оставлены для повторной штатной записи; бюджеты и проверки не менялись.

Диагностический `metrics --target demo/Demo.slnx --generation read` после восстановления проектных моделей завершился с кодом 0 (`scratch/b4d-gate/task5-diagnostic-read.json`): отпечатки равны `HEAD`, группы 215 → 215, находки 267 → 267, вхождения 286 → 286. Восемь целей scope имели `not-generated`; это ещё не была успешная запись моделей. Проектный `demo/.concurrency-hunter/models/demo.json` восстановлен побайтно. Прямое сравнение находок генерирующего прогона со снимком не применяется: скрипт намеренно убирает проектную запись `Options.Create` на время генерации.

Повторный `pwsh -NoProfile` с `build/record-demo-models.ps1` завершился с кодом 0 (`scratch/b4d-gate/task5-record-demo-retry.log`): 70 файлов, при чтении генерировать нечего. Оба прогона скрипта — с прежним бюджетом; первый использовал 900,002 с в генерации из 926,494 с полного времени. Все 70 файлов имеют версию генератора `6ca3d85f1cdc1fdb55ce1e5b08762d623363b30505713723d7c088ee106c97f2` и схему 2. Записаны 24 модели и 369 отказов: против `HEAD` это 21 → 70 файлов, 3 → 24 модели, 75 → 369 отказов. Все 78 прежних ответов сохранены без изменений; добавлены 315 ответов зависимостей (R3, R4). Новые 49 файлов добавлены в индекс; неотслеживаемых файлов под `demo/.concurrency-hunter/models/generated/` нет.

`src/ConcurrencyHunter.Cli.Tests/Snapshots/demo.json` переснят из `scratch/demo-read.json`, полученного проверкой скрипта с `--generation read`: срез `counts` и `coverage`, UTF-8 без BOM и без завершающего перевода строки. Сдвинувшихся значений и добавленных/удалённых элементов массива относительно `HEAD` — 0, поэтому строк отдельных сдвигов нет. Новые ответы зависимостей не входят в coverage scope (R2), а прежние ответы целей scope не изменились. В том числе не изменились отказы и коды причин шести членов хостинга из ADR 0027; их зависимости не дали им другой описывающей модели. Отпечатки, 215 групп, 267 находок и 286 вхождений сохранены. Число анализов вне среза снимка: `Demo.Web = 2`, `Demo.Worker = 1`. В итоговом чтении у обоих scope нет `not-generated`; проектный файл моделей восстановлен побайтно.

## Снимок evals

Обновление рантайма: `10.0.13 / 8.0.32 → 10.0.14 / 8.0.33` (D-0016).

Сохранённый сервером базис `HEAD` на этом рантайме сравнен со снимком `HEAD`: кроме идентичности сборок, ответы не изменились, строк отдельных ответов `runtime` нет. Меняется `implementation` у 11 ответов members, 4 effects, 129 linq и 12 accessors; наборы содержат соответственно 18, 18, 129 и 12 ответов. Пороги базиса: `exact = 12`, `fateGoldEntries = 3`, `effectsExact = 8`, `linqExact.System.Linq = 17`, `linqExact.System.Linq.Queryable = 0`, `accessorsExact = 8`.

Снимок `skills/hunt/evals/models/generator-snapshot.json` перезаписан в отдельном `pwsh -NoProfile` под `CH_MODEL_EVALS=1` и `CH_MODEL_EVALS_RECORD=1`: `dotnet test src/ConcurrencyHunter.Core.Tests/ConcurrencyHunter.Core.Tests.csproj -c Release --no-restore` с фильтром классов `ModelEvalTests`, `EffectsEvalTests`, `LinqOracleTests`, `AccessorEvalTests`. Код выхода 0; 38 Passed, 0 Failed, 0 Skipped, 5 мин 46 с (`scratch/b4d-gate/task5-evals-record.log`). После записи все поля, кроме `implementation` и `implementationVersion`, сравнены с базисом `HEAD` на этом рантайме и с закоммиченным снимком `HEAD`: оба сравнения равны. Патч не сдвинул ответы evals; ни один порог не снижен.

## Базис тестов

`build/check-test-baseline.ps1 -Record -KeepResults scratch/b4d-gate/task5-baseline-results` выполнен в отдельном `pwsh -NoProfile` с `CH_ESHOP_ROOT=C:\Dev\eShopOnContainers`, `CONCURRENCYHUNTER_REQUIRE_E2E=1` и снятыми `CH_MODEL_EVALS` / `CH_MODEL_EVALS_RECORD`. Перед запуском подтверждены `obj/project.assets.json` всех 30 проектов eShop. Код выхода 0 (`scratch/b4d-gate/task5-record-baseline.log`): CLI — 153 Passed, 0 Failed, 0 Skipped, 3 мин 53 с; Core — 11738 Passed, 0 Failed, 39 Skipped, 5 мин 18 с. Записаны 11735 уникальных кейсов, из них 11696 Passed.

Сохранённые TRX подтверждают Passed для всех четырёх eShop-тестов, шести тестов `DemoExpectationTests` (включая confidence), семи `DemoGeneratedModelsTests` и шести `PublishedExecutableEndToEndTests`. Снимок eShop и таблица CoreLib не перезаписывались.

`build/check-baseline-rerecord.ps1` завершился с кодом 0 (`scratch/b4d-gate/task5-check-baseline-rerecord.log`): 11665 → 11696 Passed, четыре прежних кейса имеют допустимые замены. Три строки `GeneratedCoverageTests.Refusal_cause_follows_mode_and_outcome` заменили `ReachedThroughModel` на `RunModelNotKept`: в Generate причина `reached-through-model` → null, в None null → null, в ReadOnly `reached-through-model` → `not-generated` (R2). `RunGenerationPassTests.Member_reached_only_through_a_model_is_listed_reached_through_model` переименован в `RunGenerationPassTests.Member_reached_only_through_a_model_is_generated_and_applied`: достигнутая через модель цель генерируется и применяется (R1). Прежних Passed-кейсов без замены нет.

Исполняемый гейт задачи `build/check-phase5d-runB4d.ps1 -Task 5` и общие гейты рана оставлены серверу. Версия плагина не повышалась: задача 5 перезаписывает артефакты текущего генератора и не выпускает релиз.

## Замер eShop

`pwsh -NoProfile -File ./build/check-phase5d-runB4d.ps1 -Measure` выполнен в отдельном процессе с `CH_ESHOP_ROOT=C:\Dev\eShopOnContainers`; код выхода 0, лог — `scratch/b4d-gate/task6-measure.log`. Перед замером подтверждены `obj/project.assets.json` всех 30 проектов eShop и успешная Release-сборка. Проверка копирования и возврата прошла семь случаев: существующая и отсутствующая папка при успехе и ошибках после холодного и тёплого прогонов, а также ошибка самого копирования.

После первой попытки задачи 7 владелец gate-скрипта исправил перекрытие `$RECORD` локальной переменной в `Assert-Documents`: текст записи рана больше не используется как путь файла. Правка скрипта меняет входы замера, поэтому текущие `measure.json`, `eshop-generate.json` и `eshop-read.json` содержат повторный замер после исправления. Лог `task6-measure.log` выше относится к первоначальному замеру; билдер задачи 7 повторный замер не запускал. После первого раунда код-ревью (F-0022) отпечаток входов замера берёт версию генератора, посчитанную `build/GeneratorFingerprint.cs`, вместо собственного хэша исходников Analysis и Core, в котором не было `plugins/Directory.Build.props` и `plugins/Directory.Packages.props`. Поэтому замер снят в третий раз (лог `scratch/b4d-gate/measure-f0022.log`), и текущие файлы содержат его.

Холодный прогон — `metrics --generation on`, без сгенерированных ответов в начале; тёплый — следующий за ним `metrics --generation read` по сохранённым ответам холодного. Ниже — актуальные строки скрипта из `scratch/b4d-gate/measure.json`, время в секундах. Записи включают ответы зависимостей; порога для чисел нет. Повторные замеры изменили только полное время: холодное 971.9 → 970.5 → 973.0 с, тёплое 74.7 → 68.7 → 66.6 с; число анализов, время генерации, число записей и цели с причиной `budget` совпадают с первоначальным замером.

```text
cold analyses Basket.API: 2
cold analyses Catalog.API: 2
cold analyses Identity.API: 2
cold analyses Mobile.Shopping.HttpAggregator: 2
cold analyses Ordering.API: 2
cold analyses Ordering.BackgroundTasks: 2
cold analyses Ordering.SignalrHub: 2
cold analyses Payment.API: 2
cold analyses Web.Shopping.HttpAggregator: 2
cold analyses WebMVC: 2
cold analyses WebSPA: 1
cold analyses WebStatus: 2
cold analyses WebhookClient: 1
cold analyses Webhooks.API: 1
cold total: 973.0
cold generation: 900.0
cold entries: 84 (models 14, refusals 70)
cold budget: 491
warm analyses Basket.API: 2
warm analyses Catalog.API: 2
warm analyses Identity.API: 2
warm analyses Mobile.Shopping.HttpAggregator: 2
warm analyses Ordering.API: 2
warm analyses Ordering.BackgroundTasks: 2
warm analyses Ordering.SignalrHub: 2
warm analyses Payment.API: 2
warm analyses Web.Shopping.HttpAggregator: 2
warm analyses WebMVC: 2
warm analyses WebSPA: 1
warm analyses WebStatus: 2
warm analyses WebhookClient: 1
warm analyses Webhooks.API: 1
warm total: 66.6
warm generation: 0.0
warm entries: 84 (models 14, refusals 70)
warm budget: 0
```

В режиме чтения цель без ответа получает `not-generated`, поэтому нулевой `warm budget` не означает, что у каждой цели есть сгенерированная модель (R2).

Полные измерения — `scratch/b4d-gate/eshop-generate.json` и `scratch/b4d-gate/eshop-read.json`; логи прогонов — `measure-cold.log` и `measure-warm.log` в том же каталоге. Отпечаток входов текущего `measure.json` — версия генератора (`6ca3d85f1cdc1fdb55ce1e5b08762d623363b30505713723d7c088ee106c97f2`), исходники и проект `src/ConcurrencyHunter.Cli` и сам скрипт: `dd215740e57dc82f7cf647cf38494841934c8d646187c3ab4242b56f693ac7d3`. Он относится к замеру после исправления F-0022; исходники после него не менялись.

До замера `.concurrency-hunter/` под корнем eShop не было. Скрипт в `finally` удалил созданную папку и проверил исходный набор файлов и их байты; независимая проверка после завершения подтвердила отсутствие папки и пустой `git status --porcelain=v1`, как до замера. Checkout eShop восстановлен. Скрипт замера и код в задаче 6 не правились; версия плагина не повышалась, поскольку задача записывает измерение и не меняет поставляемое поведение. Гейт `build/check-phase5d-runB4d.ps1 -Task 6` оставлен серверу.

## Документы

SPEC TD-034b, G-6 и TD-034c описывают модели и виды зависимостей, цели implementation-ссылок компиляций генерации, круги, окончательность, хранение и повтор ответа. TD-124 считает цели всех кругов и known-call последнего анализа; причины `reached-through-model` больше нет, а `no-implementation` без сохранённого ответа остаётся в Generate и ReadOnly (F-0019). Вывод `metrics` — схема 1.3 с `timings.analyses` вне среза снимков. Заголовок SPEC включает ADR 0027. PLAN содержит построенный B4d и новый B4e, двадцать три рана в разделе 2 и две оставшиеся границы B4e в разделе 3. ADR 0027 и CONTEXT в задаче 7 не правились.

`pwsh -NoProfile -File ./build/check-docs.ps1` выполнен с кодом выхода 0: SPEC и CONTEXT не называют фазу, ран, вопрос, интервью или дату процесса разработки. `git diff --check -- docs/SPEC.md docs/PLAN.md docs/runs/5d-B4d.md` также завершился с кодом 0. В трёх изменённых документах сохранены UTF-8 без BOM и CRLF; завершающих пробелов нет.

Билдер задачи 7 меняет только документацию: код, gate-скрипт и артефакты задач 5–6 не правились; запись синхронизирована с готовыми результатами повторного замера владельца скрипта. Версия плагина не повышалась. Первая серверная проверка `build/check-phase5d-runB4d.ps1 -Task 7` завершилась с кодом 1 из-за перекрытия `$RECORD`, описанного выше. Повтор исполняемого гейта, короткая проверка исправлений и полные гейты рана оставлены серверу; результаты прежних задач выше не означают их повторного запуска в задаче 7.
