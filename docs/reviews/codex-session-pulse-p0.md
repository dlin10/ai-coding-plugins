# Ревью плана «Codex Session Pulse P0» (#249–#254)

Дата: 2026-11-05. Ветка: `claude/great-bohr-s4vriv`. Ничего не реализовано — только отчёт.

## 0. Что не удалось проверить

Техническая спецификация v1.2 (ссылка на ChatGPT в каждом issue) недоступна без логина: страница отдаёт пустую оболочку. Ревью сделано по PRD и критериям в issues #249–#254, по официальной документации hooks Codex (learn.chatgpt.com/docs/hooks), по разборам формата rollout (сверены по трём источникам) и по измеренным фактам из этого репозитория — `plugins/plan-forge-flow/CONTEXT.md` (как Codex запускает plugin-hooks на Windows) и `plugins/roslyn-mcp/docs/adr/0004, 0008, 0010` (job objects Codex Desktop, доставка бинаря, выход из дерева процессов). Там, где спецификация могла уже ответить, замечание помечено «сверить со спекой».

## 1. Понимание фичи

Codex Session Pulse — аналог мода `mods/session-pulse` для Claude Code, но для Codex Desktop на Windows 11 x64. У Codex нет API модов, поэтому вместо полосы над промптом делается внешний WPF-оверлей, который не забирает фокус и следует за окном Codex. Данные берутся не из хоста, а косвенно:

- из локальных rollout-файлов `~/.codex/sessions/**/rollout-*.jsonl` (события `token_count` с `last_token_usage`);
- из plugin-hooks `SessionStart`, `UserPromptSubmit`, `SessionEnd`, которые запускают тот же exe в режиме `--hook` и передают метаданные в долгоживущий `--overlay` по Named Pipe.

Показывается: cached/input последнего запроса, «последнее наблюдение» кэша, оценка TTL (явно как оценка), скорость (или «—»), источник данных и уверенность автоматического выбора потока (main/fork, без ручного выбора). Один WinExe обслуживает оба режима; один экземпляр overlay на пользовательскую сессию (mutex); overlay завершается после закрытия последнего окна Codex. Без сети; Auto Warm и UI Automation вынесены из P0. Поставка — Codex-only плагин в каталоге репозитория, self-contained `win-x64`, smoke в Windows Sandbox.

Главное отличие от Claude-мода: тот получает usage прямо из `turn.step` внутри хоста, а здесь всё — реконструкция по файлам и внешним сигналам. Отсюда вся сложность плана: корреляция hook ↔ rollout, выбор потока, жизненный цикл процесса.

## 2. Что в плане хорошо

- Декомпозиция по слоям (измерение → core → процесс/IPC → UI → упаковка) с явной передачей результатов между сессиями и итоговой приёмкой в #254.
- #250 как этап снятия рисков до кода — правильно: формат и контракт хуков меняются от версии к версии.
- Консервативные принципы в критериях: `last_token_usage` вместо суммирования total; «отсутствует» ≠ 0; TTL — оценка, истечение оценки не miss; неоднозначный `SessionEnd` не чистит состояние; `inferred` при неопределённости; никаких текстов чатов в диагностике; только чтение.
- Критерии приёмки в основном измеримы и разделяют «действие выполнено» и «событие получено».
- Исключение UI Automation и Auto Warm из P0 — верное сужение.

## 3. Находки и риски, по убыванию важности

### R1. Overlay, запущенный из hook, может умереть вместе с тредом (job objects) — не измеряется и не учтён

Измерено в этом репозитории (ADR 0004 roslyn-mcp): Codex 0.165 кладёт дочерние процессы MCP-серверов в job object с `KILL_ON_JOB_CLOSE` (limit flags `0x2000`) и запрещает breakaway; Codex Desktop делает то же, но закрывает job при закрытии треда (#217). Если hook-процессы запускаются в таком же job (не измерено), то overlay, порождённый из `--hook`, завершится при закрытии треда, из которого его запустили — ровно противоположно требованию «overlay живёт до закрытия последнего окна». Вдобавок Claude Code (ADR 0010) завершает дерево процессов через `taskkill /T` по parent PID; как Codex завершает hook по таймауту — неизвестно.

Рекомендации:
- В #250 добавить измерение: `IsProcessInJob` + `QueryInformationJobObject` из hook-процесса (limit flags, разрешён ли breakaway), и что происходит с дочерним процессом hook после закрытия треда и окна.
- В #252 заложить паттерн ADR 0010: hook запускает короткоживущий `start-overlay`, который стартует overlay с `CREATE_BREAKAWAY_FROM_JOB`, а при `ERROR_ACCESS_DENIED` — явно деградирует (см. R2: overlay должен уметь запускаться и не из hook).

### R2. Hooks могут не работать вовсе: trust по хэшу, отключение, апдейты

По документации: non-managed hooks «must be reviewed and trusted before they run», доверие записывается «against the hook's current hash», новые или изменённые hooks «are marked for review and skipped until trusted», а обновление плагина, меняющее bundled-скрипт, «requires a new review even if the hook definition stays the same». Hooks можно выключить целиком (`[features] hooks = false`, `allow_managed_hooks_only`). Пропущенный hook даёт только предупреждение при старте.

Следствие: после каждого релиза плагина (а релиз здесь = бамп версии, каталог хранит копию per version) overlay молча перестанет появляться, пока пользователь не нажмёт Review hooks. План этого не покрывает.

Рекомендация — сделать hooks необязательными для базовой работы:
- Основной источник для автоселекта — `FileSystemWatcher` на корне `sessions/`: «самый недавно дописанный rollout не-subagent» и есть активный поток. Hooks только повышают уверенность (`confirmed` вместо `inferred`) и дают `transcript_path` (R3).
- Overlay должен запускаться и без hook: ярлык/команда `--overlay`, опционально автозапуск. Тогда Review hooks — улучшение, а не условие.
- В #254 добавить критерий: «после бампа версии плагина пользователь видит понятное состояние (hooks не доверены) и overlay продолжает работать в режиме inferred».

### R3. Hook уже сообщает путь к rollout: `transcript_path`

Каждый command-hook получает в stdin `session_id`, `transcript_path` (string | null), `cwd`, `hook_event_name`, `model`; `SessionStart` добавляет `source` ∈ {`startup`, `resume`, `clear`, `compact`, `fork`}; `UserPromptSubmit` — `turn_id`, `prompt`, `permission_mode`; `SessionEnd` — `reason` (пока всегда `other`). Значит, искать `rollout-*-<id>.jsonl` по всему дереву `sessions/` (тысячи файлов) не нужно — путь приходит с событием, включая актуальный сегмент (см. R7). Критерий #250 «проверена связь hook и rollout» стоит переформулировать конкретно: «`transcript_path` в Desktop на Windows не null и указывает на файл, чьё `session_meta.id` равно `session_id`».

Также полезен `source: "fork"` — он прямо отвечает на вопрос #250 про fork, а `source: "resume"` — про resume (resume дописывает тот же файл в старой папке дня, нового файла не создаёт).

### R4. `UserPromptSubmit` приносит полный текст prompt в stdin

Это содержимое чата, потенциально сотни килобайт (вставленные файлы). Критерий «IPC не передаёт содержимое prompt» верен, но недостаточен: hook-режим должен читать stdin с лимитом и потоковым парсером, извлекая только нужные поля, не писать stdin в диагностику ни при какой ошибке, и не падать на обрезанном JSON (при лимите чтения). Зафиксировать как критерий #252.

### R5. Hook синхронный по умолчанию и добавляет задержку каждому промпту; `SessionEnd` имеет таймаут 1 с

Документация: «By default, Codex waits for a command hook to finish»; есть `"async": true` (до 8 фоновых одновременно), но «`SessionEnd` hooks always run synchronously», таймаут по умолчанию 1 с, максимум 3 с.

Self-contained single-file WPF exe в режиме `--hook` — это полный старт .NET-рантайма, а на первом запуске ещё и распаковка native-библиотек WPF (см. R10). Холодный старт — секунды, тёплый — сотни миллисекунд на каждый prompt.

Рекомендации:
- `"async": true` для `SessionStart` и `UserPromptSubmit`; для `SessionEnd` задать `timeout: 3` и уложить доставку в ~300 мс (connect) + ~200 мс (write), числа — в критерий #252 вместо «ограничены по времени».
- Пересмотреть требование «один WinExe»: отдельный крошечный hook-бинарь (NativeAOT console, ~2 МБ, старт ~10 мс) в том же релизе почти ничего не стоит. Если оставлять один exe — гарантировать, что путь `--hook` не загружает `PresentationFramework` (никаких WPF-типов в статике `Program`), и задать в #254 порог тёплого старта hook (например, ≤150 мс).

### R6. Семантика `SessionEnd` по документации уже снимает часть неопределённости — и меняет дизайн очистки

`SessionEnd` «runs for the main thread when you archive or delete a conversation that's still open, when Codex closes normally, or after a conversation has been idle and isn't open in any connected client for 30 minutes»; «Switching away from a conversation … doesn't end the session right away»; для subagents не запускается.

Следствия:
- Закрытие окна Desktop ≠ `SessionEnd` треда: тред живёт на app-server до 30 минут idle. Решение PRD привязать выход overlay к окнам Win32, а не к `SessionEnd`, — верное; стоит прямо записать, что `SessionEnd` для выхода overlay не используется.
- 30-минутный idle `SessionEnd` может прийти для треда, который пользователь всё ещё видит в окне. Очистка состояния по `SessionEnd` вредна. Предлагаю вовсе убрать «поколения» и «подтверждённую очистку одного main thread» из #252: состояние потоков хранится в LRU по recency (последняя запись в rollout / последний hook), `SessionEnd` только меняет статус на `ended` и понижает приоритет в автоселекте. «Устаревший rollout не воскрешает завершённое состояние» тогда сводится к сравнению времени последней записи с временем `SessionEnd`. Это убирает самый сложный критерий этапа.

### R7. Формат rollout: fork, сегменты, subagents — конкретика для #251

- Имя файла: `rollout-<ISO8601>-<thread-id>[_<rollout-id>].jsonl`. Суффикс `_<rollout-id>` появляется, когда revert создаёт новый сегмент («paginated history», 0.160+). Один тред может состоять из нескольких физических файлов — вот что на самом деле стоит за «rotation» в критерии; `transcript_path` из hook должен указывать на актуальный сегмент (проверить в #250).
- Fork 0.160+: `session_meta` ребёнка несёт `forked_from_id` и `history_base {thread_id, end_ordinal_exclusive, end_byte_offset}`, префикс родителя не копируется. Legacy (≤0.149): префикс копируется в файл ребёнка целиком, включая `token_count` родителя, и в файле две `session_meta` (вторая с `id` = `forked_from_id`). Reader обязан атрибутировать каждую `token_count` ближайшей предшествующей `session_meta` и считать метрики ребёнка только после его собственной `session_meta`. Критерий «общий root ID не объединяет main/fork» лучше переписать в терминах полей: `id`, `forked_from_id`, `history_base.thread_id`.
- Subagents: своя `session_meta` с `thread_source: "subagent"` и `parent_thread_id` (наблюдалось сторонними парсерами; подтвердить в #250). Hooks `SubagentStart`/`SubagentStop` дают их жизненный цикл без чтения их rollout. Критерий «subagent usage не замещает main» реализуется фильтром по `thread_source`.
- Холодные файлы сжимаются в `.jsonl.zst` и материализуются обратно при resume: watcher должен пережить delete + create того же треда.
- `token_count` бывает с `info: null` (только `rate_limits`) — критерий «отсутствие ≠ 0» это покрывает; в 0.162 замечен верхнеуровневый тип `token_usage_record` — зафиксировать в #250, что это и дублирует ли он `token_count`.
- `input_tokens` включает `cached_input_tokens` (так же трактует `ProviderUsage.Codex` в plan-forge-flow), поэтому 245120 / 245349 = 99,91% — это доля cached от input. Стоит записать формулу в PRD.
- `cli_version` из `session_meta` в Desktop может отличаться от установленной CLI; reader должен писать её в диагностику и ветвить парсер по ней.

### R8. Что реально можно показать: скорость, TTL и то, чего в PRD нет

- Скорость. В rollout нет стриминг-дельт и времени первого токена — только `task_started`/`task_complete` с `duration_ms` на уровне хода, включая выполнение инструментов. Значит «—» будет почти всегда. Либо убрать скорость из P0 (упрощает #251 и #253), либо честно назвать метрику «throughput хода» (output tokens за ход / duration_ms) с пометкой. Сейчас критерии #251 и #253 допускают обе трактовки — надо выбрать.
- TTL. В Claude-моде уже есть алгоритм обучения TTL (`learnTtl`: пауза + доля cached, два «тихих» miss подряд, хит после паузы доказывает длинный TTL). Его стоит перенести как правило 1:1 с другими стартовыми константами и описать в PRD как модель оценки — иначе критерий «TTL помечен как оценка» не проверяем. Для OpenAI документированный TTL кэша — 5–10 минут неактивности (до часа вне пика), для бэкенда Codex не документирован; стартовое значение — `Unknown` до первого обучения.
- Rate limits. В каждом `token_count` лежит `rate_limits` с `primary`/`secondary` (`used_percent`, `window_minutes`, `resets_at`) — ровно те 5-часовое и недельное окна, которые Claude-мод хотел показывать и убрал в 0.4.0 за отсутствием данных. Добавить в снимок и в оверлей дёшево и даёт больше пользы, чем скорость. Предлагаю как P0.5 (или заменить ими скорость).

### R9. «Подходящее окно Codex»: приложение, процесс, несколько окон

- Официальная документация описывает один «ChatGPT desktop app» с выбором «ChatGPT or Codex»; имя исполняемого файла на Windows по отчётам пользователей разное: `Codex.exe` в сборке из Microsoft Store, `ChatGPT.exe` в других. Это Electron (классы `Chrome_WidgetWin_1`). Changelog упоминает «secondary app windows» — окон может быть несколько. Детектор окна должен быть: (а) измерен в #250 — имя процесса, класс, шаблон заголовка, количество top-level окон; (б) настраиваемым (имя процесса/заголовок) с диагностикой «окно не найдено».
- Если приложение общее с ChatGPT, «последнее окно Codex закрыто» перестаёт быть однозначным (окно ChatGPT в режиме Chat — это Codex-окно или нет?). Зафиксировать правило в PRD.
- Без UI Automation overlay не знает, какой тред показывает foreground-окно. При двух окнах метрики могут относиться не к тому окну; PRD это принимает через `inferred`. Дешёвое усиление без UIA: сопоставлять `cwd` из hook с заголовком окна (если Desktop показывает проект в title — проверить в #250), и при ≥2 окнах всегда показывать `inferred`.
- Win32-чеклист для #253, чтобы критерии были измеримы: `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` (нет Alt+Tab, нет активации); не глобальный `TOPMOST`, а следование z-order окна Codex по `EVENT_SYSTEM_FOREGROUND`; `SetWinEventHook` (`EVENT_OBJECT_LOCATIONCHANGE`, `MINIMIZESTART/END`) вместо таймера — это и есть «idle CPU» из #254; `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` вместо `GetWindowRect` (невидимые рамки на Win11); манифест `PerMonitorV2` + `DpiChanged` в WPF; не использовать cross-process owner (`GWLP_HWNDPARENT`) — это недокументированный режим с побочными эффектами активации.

### R10. Упаковка: self-contained single-file WPF и доставка бинаря

- WPF не грузит native-библиотеки из bundle: нужен `IncludeNativeLibrariesForSelfExtract=true`, т.е. распаковка в `%TEMP%\.net` при первом запуске. ADR 0008 уже ловил здесь MAX_PATH и решал через `DOTNET_BUNDLE_EXTRACT_BASE_DIR`. Для hook-режима это ещё и задержка первого prompt (R5).
- Размер self-contained .NET 10 + WPF — порядка 150–180 МБ. Как он попадёт на машину? У соседних плагинов — GitHub Release asset, который cmd-launcher скачивает при первом старте. Для hook «первый старт» = первый prompt пользователя, качать синхронно нельзя. Варианты: (а) скачивание в async `SessionStart`, hook-режим no-op пока exe нет; (б) framework-dependent (~15 МБ) с требованием .NET Desktop Runtime — все .NET-плагины репозитория и так требуют .NET 10, критерий «без Desktop Runtime» самоналоженный и его стоит обсудить явно; (в) zip-папка вместо single-file — нет распаковки, быстрее старт. Рекомендация: явно выбрать между (б) и (а)+(в) до #252, потому что от этого зависит, где лежит exe и как его находит hook-команда.
- Codex хранит копию плагина per version (`~/.codex/plugins/cache/<marketplace>/<plugin>/<version>`), так что exe в `bin/` в git класть нельзя — как у соседей, это release asset. #254 говорит «локальный пакет», а каталог репозитория подразумевает установку из GitHub — согласовать.
- Нет work items на: CI workflow (`build-codex-session-pulse.yml` на `windows-latest`), release workflow, `AGENTS.md`/`README.md`/`CHANGELOG.md`/`docs/adr` по конвенции репозитория (AGENTS.md требует читать per-plugin AGENTS.md и держать версию в README-заголовке).
- Имя: `session-pulse` уже занято модом в `.claude-plugin/marketplace.json`. Плагину нужно другое имя (например `codex-session-pulse`), запись только в `.agents/plugins/marketplace.json` (валидатор это допускает: Claude-каталог проверяется на superset, Codex-only папка обязана быть в Codex-каталоге), и секция в корневом README рядом с модом с перекрёстной ссылкой.
- Hook-файл: `hooks/hooks.json` с `commandWindows` и `${PLUGIN_ROOT}` — ровно тот путь, который plan-forge-flow измерил (команда выполняется в обнаруженном shell пользователя, pwsh, или в `cmd.exe`). Шелл официально не документирован — ещё один пункт в #250.

### R11. IPC: Named Pipe + mutex можно заменить на более простой и надёжный механизм

Критерии #252 про «холодный параллельный старт», «готовность pipe» и «таймауты» описывают гонку, которой может не быть. Альтернатива: hook пишет один маленький JSON-файл события (atomic rename) в `%LOCALAPPDATA%\<plugin>\inbox\`; overlay читает inbox при старте и дальше через `FileSystemWatcher`. Плюсы: события не теряются, пока overlay стартует; нет connect-таймаутов и readiness; hook завершается за миллисекунды; тестируется без процессов. Mutex остаётся только для single-instance.

Если оставлять pipe: `PipeOptions.CurrentUserOnly`, имя с SID и версией (как ADR 0004), mutex в `Local\`, connect ≤300 мс, и обязательно сценарий «UserPromptSubmit пришёл раньше, чем overlay открыл pipe».

### R12. Последовательность и параллелизм work items

- #251 заблокирован #250, но reader и метрики можно начать на синтетических фикстурах и rollouts от CLI (формат общий, разница — версия CLI внутри Desktop, которую #250 и уточнит). Предлагаю вести #250 и #251 параллельно; #250 дополняет фикстуры обезличенными примерами.
- #253 заблокирован #252 без необходимости: UI можно строить против стаб-снимка, если контракт Snapshot зафиксирован заранее. Предлагаю вынести контракт в #249 (или в начало #251): `threadId`, `kind` (main/fork/subagent), `selection` (confirmed/inferred/unknown + причина), `lastRequestAt`, `cachedInput`, `input`, `cacheRatio`, `ttl {estimate?, basis}`, `speed?` (или `turnThroughput?`), `rateLimits?`, `source {status, path?, cliVersion}`. Тогда #252 и #253 идут параллельно, интеграция — в #254.
- #250 требует участия пользователя (Desktop), остальные — нет; это стоит отразить в метках (`ready-for-agent` на #251–#253 есть, на #250 и #254 нет — логично).

### R13. Что добавить в критерии приёмки

- #250: job limits и breakaway hook-процесса; время старта WinExe в hook-режиме; `transcript_path`; что приходит при `source: fork/resume/clear`, при закрытии окна и треда (приходит ли `SessionEnd`, когда), при subagent (`SubagentStart`); процесс/класс/заголовок окна Desktop и число окон; наличие `rate_limits` и `token_usage_record`; `cli_version` в `session_meta` против версии CLI; shell, в котором выполняется `commandWindows`; поведение trust после смены версии плагина.
- #252: числа для таймаутов; «подтверждённое закрытие последнего окна» через Win32 (`EnumWindows` по процессу + grace, число задать), а не через `SessionEnd`; лимит чтения stdin; старт overlay через detached-промежуточный процесс и измеренный `exitsWithJob`.
- #253: решить судьбу скорости (R8); настройки и темы — минимум (корень rollouts, позиция, скрывать без фокуса), остальное — после P0.
- #254: пороги для idle CPU и working set (например, ≤0,5% и ≤80 МБ), сценарий «после обновления версии плагина», сценарий «hooks не доверены», проверка, что `npm run validate:plugins` видит новый Codex-only плагин и что каталожная запись не ломает Claude/Cursor каталоги.

### R14. Безопасность и приватность

- Codex запускает hooks с рабочей директорией сессии (`cwd` = репозиторий пользователя, возможно недоверенный). Hook-процесс не должен читать ничего по относительным путям и ничего из `cwd`.
- Корень rollouts из настроек: только чтение, проверка на reparse points (как в `roslyn-mcp-cache.ps1`), отказ от путей вне профиля по умолчанию.
- Диагностика: без путей профиля, без исходных идентификаторов (критерий есть); добавить ротацию и размер.
- «Auto Warm выключен и не отправляет запросы» — в P0 нет сетевой активности вовсе; записать в README как гарантию.

## 4. Сводное предложение по архитектуре

1. Hooks — необязательный усилитель, не фундамент: базовый автоселект по `FileSystemWatcher` на `sessions/`, hooks дают `confirmed` и `transcript_path`. Overlay запускается и без hook.
2. Hook-путь — лёгкий: отдельный маленький бинарь или гарантированно «без WPF» путь в одном exe; `async: true` для `SessionStart`/`UserPromptSubmit`; `SessionEnd` с `timeout: 3`.
3. Старт overlay из hook — через detached-промежуточный процесс с breakaway (ADR 0010) и измеренным поведением под job Codex Desktop.
4. Inbox-файлы вместо pipe (или pipe с жёсткими таймаутами и сценарием «событие раньше готовности»).
5. Состояние потоков — LRU по recency; `SessionEnd` меняет статус, не удаляет; поколений нет.
6. Reader атрибутирует `token_count` ближайшей `session_meta`, различает main/fork/subagent по `id`/`forked_from_id`/`history_base`/`thread_source`/`parent_thread_id`, понимает сегменты `_<rollout-id>` и `.zst`.
7. В снимок добавить `rate_limits`; скорость — убрать из P0 или назвать «throughput хода».
8. Контракт Snapshot зафиксирован до кода; #250∥#251, #252∥#253.
9. Дистрибуция решена до #252: framework-dependent против self-contained + async-скачивание; exe — release asset, не в git; CI/release workflow и документация по конвенциям репозитория; имя плагина не конфликтует с модом.

## 5. Вердикт по work items

| Issue | Вердикт | Главные замечания |
|---|---|---|
| #249 P0 | Принять с правками | Зафиксировать контракт Snapshot, правило «что такое окно Codex», судьбу скорости, модель оценки TTL, способ дистрибуции, имя плагина; добавить work items на CI/release/docs. |
| #250 1/5 | Расширить | Добавить измерения из R13 (job/breakaway, старт hook, `transcript_path`, окно/процесс, `SessionEnd` при закрытии окна, shell, trust после апдейта). Не держать #251 заблокированным целиком. |
| #251 2/5 | Принять с правками | Переписать критерии про fork/subagent в терминах полей (R7); сегменты вместо «rotation»; формула cached/input; `rate_limits` в снимок; решить про скорость. |
| #252 3/5 | Пересмотреть дизайн | Job objects (R1), hooks необязательны (R2), лимит stdin (R4), async + числа таймаутов (R5), убрать поколения/очистку по `SessionEnd` (R6), inbox вместо pipe (R11), выход overlay только по Win32. |
| #253 4/5 | Принять с правками | Детектор окна настраиваемый и измеренный (R9); Win32-чеклист как критерии; минимум настроек; параллельно с #252 на стаб-снимке. |
| #254 5/5 | Расширить | Пороги CPU/WS, сценарии «после апдейта» и «hooks не доверены», валидатор и каталоги, документация и workflow по конвенциям репозитория. |

## 6. Источники

- Hooks Codex: learn.chatgpt.com/docs/hooks (события, поля stdin, `async`, таймауты `SessionEnd`, trust по хэшу, `commandWindows`, plugin `hooks/hooks.json`).
- Формат rollout: daymade `codex_rollout_format.md` (проверен на ~2600 файлах 0.142–0.149, цитирует 0.160), dev.to «What Codex CLI writes to disk» (0.162.1: `token_count`, `rate_limits`, `turn_context`), danielvaughan.com (archive/resume/fork/`.zst`).
- Приложение: learn.chatgpt.com/docs/app («ChatGPT desktop app», «Choose ChatGPT or Codex»), changelog 2026-10-08 («secondary app windows»), отчёты пользователей об именах `Codex.exe`/`ChatGPT.exe`.
- Репозиторий: `plugins/plan-forge-flow/CONTEXT.md` (plugin hooks Codex на Windows, измерено на 0.162.1), `plugins/roslyn-mcp/docs/adr/0004|0008|0010`, `mods/session-pulse/hooks/format.ts` (`learnTtl`), `scripts/validate-plugins.mjs` (правила каталогов).
