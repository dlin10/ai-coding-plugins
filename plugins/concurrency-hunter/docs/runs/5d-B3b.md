# 5d run B3b

## Сгенерированные файлы demo

В задаче 10 выполнено три запуска `build/record-demo-models.ps1`: первый запуск билдера, повторный запуск
оркестратора с хоста и повторный запуск билдера. Первый запуск завершился кодом 1 при контрольном чтении:
три члена Google.Protobuf оставались целями `not-generated`, а файл Google.Protobuf сохранял прежний формат.
Измерение чтения заменяло fingerprint `b7923638effff8ea` на `b685872d760f90d5`; снимки и базовая линия при этой
остановке не перезаписывались. Запуск оркестратора с хоста записал все 21 файл: записи моделей и защищённые
`counts.findings`, `counts.groups`, `counts.fingerprints` равны HEAD. Повторный запуск билдера завершился кодом 0:
21 файл достаточен, ран только для чтения ничего не генерирует. Ни один запуск билдера не потребовал повторения
из-за отказа `budget`.

Во время код-ревью выполнено ещё три запуска `build/record-demo-models.ps1`. Первый, после исправления
XML-комментариев, завершился кодом 1: контрольное чтение хотело сгенерировать семь членов ASP.NET Core,
поскольку `Microsoft.AspNetCore.json` и `Microsoft.AspNetCore.Routing.json` не были записаны и сохранили
прежнюю версию генератора. Причина — F-0064: `WriteLocked` пробовал `File.Replace` один раз, и временная
`IOException` (`Unable to remove the file to be replaced.`) оставляла файл незаписанным с диагностикой.
Исправленный писатель повторяет замену или перемещение до пяти раз под тем же mutex, с тем же временным
файлом и ожиданиями 50, 100, 200 и 400 мс.

После исправлений F-0056, F-0057, F-0058 и F-0064 один запуск завершился с первого раза кодом 0:
все 21 файл записаны, ран только для чтения ничего не генерирует. Следующий, подтверждающий запуск тоже
завершился кодом 0 и побайтово не изменил ни один из 21 файла; контрольное чтение ничего не генерирует.
Логи этих трёх запусков — `scratch/b3b-cr1/record-demo-models.log`,
`scratch/b3b-cr2/record-demo-models.log` и `scratch/b3b-cr3/record-demo-models-confirm.log`.

Все 21 закоммиченных файла имеют `schemaVersion: 2` и итоговую Generator version
`763a0f038243e2c00a7fb89e2778479d64c0bce2edb2dfc093faf36e01c68950`.
По сравнению с HEAD изменились только поля `schemaVersion` и `generator`; объекты `assembly` и все записи `members`
равны HEAD. Изменившихся записей нет. Новых файлов в папке нет; шесть отказов `library-does-not-compile` ASP.NET Core
сохранены. Тест `DemoGeneratedModelsTests.Committed_files_name_the_current_generator` проходит; до перезаписи заголовков
он падал на всех 21 файлах без `generator`. Все пять тестов `DemoGeneratedModelsTests` прошли в полном наборе.

`MetricsCommandTests.Demo_measurement_matches_the_snapshot` прошёл отдельным тестом и в полном наборе.
`src/ConcurrencyHunter.Cli.Tests/Snapshots/demo.json`, `src/ConcurrencyHunter.Cli.Tests/Snapshots/eshop.json` и
`demo/expected-findings.json` не перезаписаны и равны HEAD. Итоговое измерение `--generation read` также сохраняет
`counts.findings`, `counts.groups` и `counts.fingerprints` снимка HEAD.

## Базовая линия

В задаче 10 `build/check-test-baseline.ps1 -Record` выполнен дважды с `CH_ESHOP_ROOT=C:\Dev\eShopOnContainers`.
Первый прогон не записал базовую линию: единственным падением был
`PublishedExecutableEndToEndTests.Published_server_takes_the_demo_through_all_five_tools_to_a_complete_report`,
который запускал прежний `bin/win-x64/concurrency-hunter.exe` и видел `generated-in-run 2` вместо 0.
После локальной публикации текущего Release через `dotnet publish` повторный прогон с
`CONCURRENCYHUNTER_REQUIRE_E2E=1` прошёл: Core.Tests — 11 308 Passed и 33 Skipped; Cli.Tests — 151 Passed,
без падений. Три теста eShop, в том числе сравнение его снимка, и тест опубликованного exe прошли.

После задачи 10 `build/test-baseline.txt` записывал 11 305 имён кейсов: 11 272 Passed и 33 Skipped; в HEAD — 10 937,
из них 10 904 Passed и 33 Skipped. Добавлено 368 имён, потерянных нет; все прежние Passed остались Passed.
`build/check-baseline-rerecord.ps1` завершился кодом 0: `11272 cases recorded as Passed, 10904 before; 0 lost`.

После исправлений F-0056, F-0057, F-0058 и F-0064 базовая линия перезаписана ещё одним запуском
`build/check-test-baseline.ps1 -Record` с `CH_ESHOP_ROOT=C:\Dev\eShopOnContainers`, после публикации текущего
Release exe. Полный прогон прошёл: Core.Tests — 11 390 Passed и 33 Skipped; Cli.Tests — 151 Passed,
0 Skipped; падений нет. Тесты eShop, сравнение его снимка, компиляция установленных `System.Text.Json` и
`System.Net.Security` и end-to-end тест опубликованного exe прошли.

Итоговая базовая линия записывает 11 387 имён кейсов: 11 354 Passed и 33 Skipped — на 82 имени больше,
чем после задачи 10, и на 450 больше HEAD. `build/check-baseline-rerecord.ps1` завершился кодом 0:
`11354 cases recorded as Passed, 10904 before; 0 lost`. Логи — `scratch/b3b-cr2/baseline-record.log` и
`scratch/b3b-cr2/baseline-rerecord.log`. Подтверждающий запуск перезаписи demo базовую линию не меняет.

Логи билдера задачи 10 сохранены в `scratch/b3b-task10/`: `record-1.log`, `record-2.log`, `metrics-snapshot.log`,
`publish.log`, `baseline-record.log`, `baseline-record-2.log` и `baseline-rerecord.log`.
Исполняемый гейт задачи билдер не запускал: его выполняет сервер.

## Model evals

В задаче 11 перезаписан `skills/hunt/evals/models/generator-snapshot.json` (`generator-snapshot.json`).
Исправление компиляции TD-034b добавляет getter auto-свойству с setter-ом без getter-а после тримминга;
та же правка снимает CS0276. Поэтому установленная implementation .NET 10.0.13 у `System.Text.Json` и
`System.Net.Security` теперь компилируется, и два члена доходят до следующих ограничений генератора:

| Раздел | Член | Ответ HEAD → ответ рана | `reachedBodies` | Причина сдвига |
|---|---|---|---|---|
| `effects` | ```M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions)``` | `library-does-not-compile` → `closure-bound` | 0 → 1693 | После исправления auto-свойства построение reachable set доходит до границы замыкания; модели по-прежнему нет |
| `accessors` | `M:System.Net.Security.SslClientAuthenticationOptions.set_RemoteCertificateValidationCallback(System.Net.Security.RemoteCertificateValidationCallback)` | `library-does-not-compile` → `analysis-failed` | 0 → 87 | После исправления auto-свойства драйвер синтезируется, но анализ не достигает тела setter-а: `the call never reached body:System.Net.Security:M:System.Net.Security.SslClientAuthenticationOptions.set_RemoteCertificateValidationCallback(System.Net.Security.RemoteCertificateValidationCallback)`; модели нет, судьба остаётся шире эталонной; открытый дефект — вопрос 184 к 5d run B4 |

Отдельный диагностический запуск Release CLI `generate --assembly System.Net.Security --version 10.0.0.0
--framework net10.0 --member 'M:System.Net.Security.SslClientAuthenticationOptions.set_RemoteCertificateValidationCallback(System.Net.Security.RemoteCertificateValidationCallback)'
--out scratch/b3b-cr1/ssl.json` подтвердил `analysis-failed` с указанным `detail` и 87 достигнутыми телами.
JSON-ответ сохранён в `scratch/b3b-cr1/ssl.json`, вывод команды — в `scratch/b3b-cr1/ssl.log`.
Это отказ анализа компилируемой библиотеки; его исправление вынесено в вопрос 184.

В `packages` изменились только следующие счётчики этих же implementation-сборок:

| Сборка | `bodies` (без изменения) | `externBodies` HEAD → ран | `driversSynthesized` HEAD → ран | Причина |
|---|---|---|---|---|
| `System.Text.Json` | 3296 | 0 → 31 | 0 → 1 | Успешная компиляция копии учитывает правки тел `extern` и позволяет построить драйвер; 24 тела `extern` связаны с неоднозначным `BooleanConverter` (CS0104), остаток вынесен в вопрос 182 |
| `System.Net.Security` | 870 | 0 → 1 | 0 → 1 | Успешная компиляция копии учитывает одно переписанное тело `extern` и позволяет построить драйвер |

Другие ответы `members`, `effects`, `linq`, `accessors`, их implementation identity и остальные поля
`packages` не изменились. Пороги не менялись: `exact` — 11, `fateGoldEntries` — 4, `effectsExact` — 8,
`linqExact` — System.Linq 17 и System.Linq.Queryable 0, `accessorsExact` — 6. Опасных сужений нет;
появления моделей у двух изменившихся членов нет.

Первый прогон без записи выявил эти два отличия от прежнего снимка; затем записывающий прогон прошёл:
30 Passed, 0 Failed, 0 Skipped (`scratch/b3b-evals/record.log`, отчёты и trx в `scratch/b3b-evals/record/`).
Снимки demo и eShop в этой задаче не перезаписывались. Итоговую проверку model evals без записи и
полные гейты рана выполняет сервер после документов.

## Документы

Задача 12 обновляет TD-034b и TD-034c, гарантии отмены конвейера, раздел 10.6 и правила тестов SPEC,
ADR 0012 и ADR 0023, строку B3b и временные границы PLAN, QUESTIONS и команду перезаписи demo в
`AGENTS.md`. Числа порогов в SPEC приведены к уже записанному снимку HEAD: `fateGoldEntries` 3 → 4,
`linqExact.System.Linq` 5 → 17; это исправление устаревшего текста, сами пороги в ране не менялись.
Вопросы 106, 115, 130 и 181 закрыты ссылками на требования; остаток CS0104 и CS0051 записан вопросом 182 к 5d run B4, ASP0029 и strong-name ключ копии — вопросом 183 к 5d run C.
Код, сгенерированные модели и снимки в задаче 12 не меняются; дополнительных запусков
`record-demo-models` она не требует: в задаче 10 их три; последующие три запуска относятся к код-ревью и перечислены выше.
