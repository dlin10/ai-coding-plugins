# Codex Session Pulse — совместимость с Codex Desktop на Windows

Результаты Шага 0 спецификации (§12, задача #250): что на самом деле присылает установленный Codex
Desktop на Windows и что лежит в rollout. #251, #252 и #253 строятся на этих фактах, а не на
предположениях.

Каждый факт помечен:

- **подтверждено** — наблюдалось на этой машине указанным способом;
- **не установлено** — не наблюдалось; рядом сказано, что именно видели и чего не хватило.

Отсутствие события само по себе ничего не объясняет: где событие не пришло, записано, какое действие
было выполнено и что показали логи Codex.

## 1. Версии

| Что | Значение | Как получено |
|---|---|---|
| Пакет Desktop | `OpenAI.Codex` 26.1103.3517.0, Microsoft Store (MSIX), `DisplayName` «ChatGPT» | `Get-AppxPackage OpenAI.Codex` |
| Версия клиента в логах | `app_server.client_name="Codex Desktop"`, `client_version="26.1103.31037"` | `~/.codex/logs_2.sqlite`, запросы `thread/start` |
| App-server, который запускает Desktop | `%LOCALAPPDATA%\OpenAI\Codex\bin\<hash>\codex.exe app-server`, `codex-cli 0.167.0-alpha.3.2` | дерево процессов, `--version` этого файла |
| `cli_version` в `session_meta` тредов Desktop | `0.167.0-alpha.3.2` | rollout новых тредов, поле `transcript.meta_cli_version` зонда |
| Другой `codex.exe` в той же папке | `%LOCALAPPDATA%\OpenAI\Codex\bin\codex.exe` → `codex-cli 0.130.0-alpha.5`; Desktop его не запускает | `--version`, дерево процессов |
| CLI из npm | `codex-cli 0.166.0` | `codex --version` |
| ОС | Windows 11 Enterprise 10.0.26300 | |

**Подтверждено:** `cli_version` в rollout Desktop совпадает с версией app-server из папки с хэшем, а не
с CLI из npm и не с `bin\codex.exe`. Desktop обновляет app-server сам: в старых rollout Desktop есть
`cli_version` от 0.153.4 до 0.166.0-alpha.5.1. Адаптер нельзя выбирать по версии установленного CLI —
только по `cli_version` из `session_meta`.

## 2. Как это воспроизвести

Наблюдения сняты 2026-11-06 и 2026-11-07. Шаги, отмеченные «UI», выполнялись в окне Desktop
(управление через computer-use, без участия пользователя).

1. Временный плагин в рабочей копии репозитория (в git не попадает):
   - запись `codex-session-pulse` в `.agents/plugins/marketplace.json` (`source: local`);
   - `plugins/codex-session-pulse/.codex-plugin/plugin.json` с `"hooks": "./hooks/hooks.json"`;
   - `hooks/hooks.json` с событиями `SessionStart` (без matcher), `UserPromptSubmit`, `SessionEnd`
     (`timeout: 3`), `SubagentStart`, `SubagentStop`.

   Итоговая форма команды, которая сработала:

   ```json
   "commandWindows": "cmd.exe /d /c \"echo ...>>C:\\Users\\Public\\pulseprobe-trace.txt & C:\\Users\\<user>\\CodexSessionPulseProbe\\bin\\pulseprobe.exe SessionStart 2>>C:\\Users\\Public\\pulseprobe-trace.txt\""
   ```

2. Зонд `pulseprobe.exe` — одноразовый WinExe (.NET 10), в репозиторий не входит. Он:
   - читает stdin до конца и сохраняет его копию;
   - записывает цепочку родителей, `%ComSpec%`, имена переменных окружения и `PLUGIN_ROOT`;
   - вызывает `IsProcessInJob` и `QueryInformationJobObject` (limit flags и список PID job);
   - вызывает `GetCurrentPackageFullName`;
   - по `transcript_path` открывает rollout и сравнивает `session_meta.id` с `session_id`;
   - на `SessionStart` запускает двух потомков (с `CREATE_BREAKAWAY_FROM_JOB` и без), которые раз в
     10 с пишут «я жив».
3. UI: проект Desktop на папке рабочей копии (Codex спросил «Trust this folder»); Settings → Plugins →
   Browse directory → установить плагин. После правки файлов плагина: Settings → Plugins → Marketplace →
   Upgrade; затем Settings → Hooks → обновить → открыть плагин → Trust all.
4. Логи Codex: `~/.codex/logs_2.sqlite`, таблица `logs`, читать только с `mode=ro`. Полезные строки:
   - `app-server event: hook/started` и `hook/completed`;
   - `thread … has no subscribers and is idle; shutting down`;
   - `Failed to create shell snapshot for powershell`.
5. Rollout: `jsonl-inspect` (раздел 9) и обезличенные фикстуры (раздел 10).

## 3. Запуск команды хука на Windows

### 3.1 Оболочка

**Подтверждено:** `commandWindows` выполняет PowerShell 7. Цепочка родителей у каждого хука:
`ChatGPT.exe` → `codex.exe` (app-server) → `pwsh.exe` → `cmd.exe` → исполняемый файл. В логах
app-server детектированная оболочка — `powershell` («Shell snapshot not supported yet for PowerShell»).
Это оболочка пользователя по умолчанию на этой машине; на машине без PowerShell 7 может оказаться
другой (не проверялось).

**Подтверждено** (командная строка родителя, `NtQueryInformationProcess`):

- Codex запускает `"C:\Program Files\PowerShell\7\pwsh.exe" -NoProfile -Command "<commandWindows>"`;
  кавычки внутри `commandWindows` экранирует как `\"`.
- `${PLUGIN_ROOT}` Codex подставляет текстом **до** `pwsh`. В командной строке `pwsh` уже реальный
  путь `~\.codex\plugins\cache\<marketplace>\<plugin>\<version>`, так что `pwsh` не видит его как
  свою переменную.
- Передавая команду в `cmd`, `pwsh` снял кавычки вокруг пути без пробелов:
  `cmd.exe /d /c C:\…\bin\pulse-launcher.cmd UserPromptSubmit`.
- Путь с пробелом через ту же цепочку (`pwsh -NoProfile -Command "cmd.exe /d /c \"…\with space\…\launcher.cmd\" --hook"`)
  проходит, exe получает аргументы и stdin (локальный стенд, раздел 3.5).
- Знак `$` в `commandWindows` разберёт `pwsh` — других переменных, кроме `${PLUGIN_ROOT}`, там быть
  не должно.

**Подтверждено локально, без Desktop:**

- `pwsh -NoProfile -Command <GUI-exe>` не ждёт GUI-процесс: `pwsh` завершился через 305 мс, а зонд
  ещё работал. При этом зонд получил stdin-pipe родителя (63 байта прочитаны).
- `cmd.exe /d /c <GUI-exe>` ждёт GUI-процесс: `cmd` оставался жив, пока жил потомок.

Поэтому команда хука должна сама заставлять оболочку ждать. Форма из спеки
(`cmd.exe /d /c "…\launcher.cmd" --hook`) это делает, голый путь к WinExe — нет.

В Desktop первая попытка с голым путём к WinExe не дала ни одной записи, но причина там была другая
(раздел 3.2): исполняемый файл не нашёлся. Отдельно эффект «pwsh не ждёт» в Desktop не изолирован.

### 3.2 Виртуализация файловой системы MSIX

**Подтверждено:**

- `AppxManifest.xml` пакета включает виртуализацию записи и исключает из неё только
  `$(KnownFolder:LocalAppData)\OpenAI`.
- Хук наследует этот контекст от `ChatGPT.exe`:
  - Папку `C:\Users\<user>\AppData\Local\CodexSessionPulse\probe\bin\`, созданную обычным
    (не пакетным) процессом, команда хука не видит. `cmd` писал «The system cannot find the path
    specified».
  - После переноса exe в `C:\Users\<user>\CodexSessionPulseProbe\bin\` (корень профиля, не `AppData`)
    хук запустился.
- Запись хука в `%LOCALAPPDATA%\CodexSessionPulse\probe\` физически попала в
  `%LOCALAPPDATA%\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\CodexSessionPulse\probe\`.
  Там же лежат перенаправленные папки других плагинов (`roslyn-mcp`, `cache-detective` и др.).
- Значение `%LOCALAPPDATA%` в окружении хука не меняется (`C:\Users\<user>\AppData\Local`).
- `GetCurrentPackageFullName` в хуке возвращает «нет пакета» (15700). Идентичности пакета у процесса
  нет, а перенаправление есть: проверять контекст по этому API нельзя.

Следствия для #252 и #254 — решения за ними, здесь только факты:

- План §4.2 «EXE в `%LOCALAPPDATA%\CodexSessionPulse\bin\<version>\`, кладёт его скрипт кэша»
  работает, только если файл кладёт процесс в том же контексте пакета. Тогда он виден хукам, но не
  обычному процессу пользователя, и наоборот.
- Не перенаправляются: корень профиля, `~/.codex` и папка кэша плагина (`PLUGIN_ROOT`). Файлы
  кэша `~/.codex/plugins/cache/...` хук видит.
- Видимость именованных mutex и pipe между пакетным и обычным процессом здесь не проверялась;
  12.4 подтверждает, что они видны в обе стороны.

### 3.3 stdin, окружение, рабочая папка

**Подтверждено для WinExe-зонда:**

- stdin — pipe (`GetFileType` = `FILE_TYPE_PIPE`); WinExe читает его через
  `Console.OpenStandardInput()` до EOF.
- Размеры: `SessionStart` ≈ 360 байт, `UserPromptSubmit` растёт с длиной промпта (служебный промпт
  Desktop — 1240 символов).

Настоящий `CodexSessionPulse.exe` в Desktop тоже отработал на `UserPromptSubmit` (раздел 3.5). Тип
handle stdin и число прочитанных байт на нём не замерялись: заглушка scaffold их не сообщает.

Окружение (**подтверждено**):

- `PLUGIN_ROOT` = `~\.codex\plugins\cache\dlin10-ai-coding-plugins\codex-session-pulse\<version>`;
- `ComSpec` = `C:\WINDOWS\system32\cmd.exe`;
- `USERNAME` и `USERPROFILE` — реальный пользователь, не учётная запись песочницы;
- текущая папка — `cwd` треда (папка проекта).

Хуки выполняются вне песочницы Codex: так пишет страница Hooks («Hooks run outside of the sandbox»),
и зонд писал в `C:\Users\Public`.

### 3.4 Время

**Подтверждено (логи app-server):** между `hook/started` и `hook/completed` проходит 0,5–0,7 с с
`pwsh` в цепочке. Сюда входит старт `pwsh`, на который плагин повлиять не может. Время самого
зонда — 120–335 мс; это framework-dependent .NET без оптимизации старта, для порога 150 мс оно не
показательно.

### 3.5 Настоящий WinExe

Сборка: `CodexSessionPulse.App` из scaffold (#378, коммит `64e38de6`), `dotnet build -c Release`,
framework-dependent, не single-file. Режим `--hook` в scaffold — заглушка: читает stdin до 1 МиБ и
выходит с 0. Payload — фикстура `hooks/user-prompt-submit.json`.

Лаунчер:

```cmd
@echo off
set "EXE=%~dp0app\CodexSessionPulse.exe"
if exist "%EXE%" "%EXE%" %*
```

Папка с пробелом в пути. Двадцать тёплых запусков после одного прогревочного; на машине параллельно
шли другие сессии, загрузка CPU в момент замера — 22 %:

| Вариант | Медиана | Минимум | Максимум |
|---|---|---|---|
| `CodexSessionPulse.exe --hook` | 77 мс | 74 мс | 89 мс |
| `cmd.exe /d /c ""…\codex-session-pulse-launcher.cmd" --hook"` | **104 мс** | **102 мс** | 108 мс |
| Цепочка Codex: `pwsh -NoProfile -Command "cmd.exe /d /c \"…launcher.cmd\" --hook"` | 421 мс | 391 мс | 476 мс |

Повтор на влитом коде: интеграционная ветка после слияния #378 (`3b593fdd`; в моей ветке
`79b08bb7`). С `64e38de6` в `HookRunner.cs` изменились 5 строк. Та же сборка Release, тот же стенд,
три прогона по 30 тёплых запусков подряд. Загрузка CPU перед прогоном и после: 14 → 31 %,
51 → 17 %, 16 → 23 %; параллельно работали Codex Desktop и другие сборки `dotnet`.

| Вариант | Прогон 1: медиана / мин / макс | Прогон 2 | Прогон 3 |
|---|---|---|---|
| `CodexSessionPulse.exe --hook` | 235 / 81 / 1060 мс | 109 / 85 / 142 мс | 79 / 75 / 98 мс |
| `cmd.exe /d /c ""…\codex-session-pulse-launcher.cmd" --hook"` | **113 / 104** / 289 мс | **117 / 104** / 149 мс | **113 / 103** / 149 мс |
| Цепочка Codex с `pwsh` | 475 / 397 / 953 мс | 435 / 393 / 507 мс | 436 / 402 / 516 мс |

Медиана exe в прогоне 1 выше медианы цепочки с `cmd`. Код здесь ни при чём — это всплеск нагрузки
машины во время прогона. Минимумы во всех прогонах согласуются.

- **Подтверждено:** тёплый `--hook` вместе с лаунчером `cmd` укладывается в 150 мс §4.4. На коде
  `64e38de6` медиана 104 мс; на влитом коде — 113–117 мс при минимуме 103–104 мс, под нагрузкой.
- Холодный старт: первый запуск свежескопированной папки — 131, 132, 125, 126, 125 мс (`64e38de6`).
  На влитом коде: 180, 225, 185, 160, 152; 166, 140, 178; 125, 136, 136 мс. Это не холодный старт
  после перезагрузки: файлы уже в кэше ОС. После перезагрузки старт **не установлен** — его
  повторяет #254.
- Членство в job измерено WinExe-зондом, запущенным той же цепочкой Codex (раздел 4). На
  `CodexSessionPulse.exe` его не повторяли. Заглушка `--hook` не сообщает `IsProcessInJob`, а job
  создаёт Codex для каждого запуска хука до старта `pwsh`, независимо от того, какой exe стоит в
  конце цепочки.
- Старт `pwsh` (≈300 мс) Codex добавляет к каждому хуку сам; плагин на него не влияет. Поэтому
  синхронный хук задерживает промпт больше чем на 150 мс. В спеке `SessionStart` и
  `UserPromptSubmit` объявлены `async: true`; во временном `hooks.json` проверки async не было.
- В Desktop настоящий exe был поставлен на `UserPromptSubmit` через лаунчер из `${PLUGIN_ROOT}\bin`.
  Логи app-server между `hook/started` и `hook/completed`: 956 мс (первый запуск после установки),
  721 мс, 536 мс. Хук выполнился, промпты ушли.
- Сборки на пути `--hook` (через полную цепочку Codex, переключатель
  `CODEX_SESSION_PULSE_DEBUG_ASSEMBLIES=1`): `System.Private.CoreLib`, `CodexSessionPulse`,
  `System.Runtime`, `System.Console`, `System.Threading`, `System.Text.Encoding.Extensions`,
  `System.Runtime.InteropServices`, `System.Text.Json`, `System.Memory`, `System.IO.Pipelines`.
  `PresentationFramework` и `PresentationCore` нет.
- stdin GUI-процесса: exe завершается с 0 и через `cmd`, и через полную цепочку с переданным payload.
  То, что WinExe получает stdin как pipe и читает его до EOF, показал WinExe-зонд в Desktop
  (раздел 3.3).

## 4. Job object

**Подтверждено** (`IsProcessInJob` + `QueryInformationJobObject` из процесса хука, все хуки
одинаково):

- Каждый запуск хука — в своём job с `LimitFlags = 0x2800`:
  - `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (0x2000);
  - `JOB_OBJECT_LIMIT_BREAKAWAY_OK` (0x800).

  В job только процессы этого хука: `pwsh.exe`, `conhost.exe`, `cmd.exe` и сам exe.
- Breakaway разрешён: `CreateProcess` с `CREATE_BREAKAWAY_FROM_JOB` успешен. Потомок оказывается во
  внешнем job с флагами `0x0`.
- Потомок без breakaway остаётся в job хука (`0x2800`) и переживает свой хук: через 20+ минут после
  завершения хука он ещё жив. Это верно и для эфемерного треда, у которого уже пришёл `SessionEnd`.
  Значит, Codex не закрывает job при выходе из хука и при конце треда.
- Закрытие окон не убивает ни одного потомка (раздел 6).
- Выход из приложения (File → Quit ChatGPT) тоже: все 16 потомков, и 8 в job `0x2800`, остались
  живы после того, как процессы `ChatGPT.exe` и `codex.exe` исчезли. Через 4 часа они вышли бы
  сами; остановлены вручную по PID.

Механизм не установлен: кто держит handle job с `KILL_ON_JOB_CLOSE`, если потомок переживает и
хук, и app-server. Для #252 важен сам факт: процесс, запущенный из хука, сейчас не умирает ни с
тредом, ни с окном, ни с приложением. Поэтому overlay должен завершаться сам (§4.4,
`WindowLifetimeMonitor`) и не полагаться на job. Breakaway при этом разрешён, если он понадобится.

ADR 0004 roslyn-mcp описывает другой job — job MCP-сервера, который Desktop закрывает вместе с
тредом. Хуки живут в отдельном job на каждый запуск, и этот вывод на них не переносится.

## 5. События хуков

`session_id` — id треда. Для главного треда и форка он совпадает с `session_meta.id` того rollout,
на который указывает `transcript_path` (**подтверждено** для всех событий с непустым
`transcript_path`). Когда у треда появился второй сегмент (7.4), `transcript_path` в `SessionStart`,
`UserPromptSubmit` и после перезапуска указывал на **актуальный** сегмент
`rollout-…_<rollout-id>.jsonl`; `session_meta.id` в нём тот же.

### 5.1 Поля stdin

| Событие | Поля |
|---|---|
| `SessionStart` | `session_id`, `transcript_path`, `cwd`, `hook_event_name`, `model`, `permission_mode`, `source` |
| `UserPromptSubmit` | `session_id`, `turn_id`, `transcript_path`, `cwd`, `hook_event_name`, `model`, `permission_mode`, `prompt` |
| `SessionEnd` | `session_id`, `transcript_path`, `cwd`, `hook_event_name`, `reason` |
| `SubagentStart` | `session_id`, `turn_id`, `transcript_path`, `cwd`, `hook_event_name`, `model`, `permission_mode`, `agent_id`, `agent_type` |
| `SubagentStop` | `session_id`, `turn_id`, `transcript_path`, `agent_transcript_path`, `cwd`, `hook_event_name`, `model`, `permission_mode`, `stop_hook_active`, `agent_id`, `agent_type`, `last_assistant_message` |

Наблюдавшиеся значения: `permission_mode: "default"`; `reason` у `SessionEnd` — всегда `"other"`.
Поле `model` есть у всех событий, кроме `SessionEnd`.

### 5.2 Сценарии

| Сценарий (действие выполнено) | Событие получено |
|---|---|
| Новый тред: «New chat» в проекте и первый промпт | `SessionStart` `source: startup`, через ≈1 с `UserPromptSubmit`. Хук срабатывает на первом промпте, а не при открытии пустого чата. `transcript_path` уже указывает на созданный rollout. |
| Следующий промпт в загруженном треде | только `UserPromptSubmit` |
| Возврат к старому треду, пока он загружен в app-server, и промпт в нём | только `UserPromptSubmit`; `SessionStart` `resume` не пришёл, потому что треду не требовалась загрузка |
| Перезапуск приложения, открыть тред F (без промпта) | ничего |
| Первый промпт в F после перезапуска | `SessionStart` `source: resume` (`transcript_path` — актуальный сегмент `_<rollout-id>`), затем `UserPromptSubmit`. Перед ними пришёл `SessionEnd` `reason: other` с `transcript_path: null` для треда, чей `SessionStart` не приходил. |
| Правка своего сообщения (карандаш → Send) | `SessionEnd` `reason: other` со старым `transcript_path`, затем `SessionStart` `source: resume` с тем же `session_id` и `transcript_path` на **новый сегмент**, затем `UserPromptSubmit` (см. 7.4) |
| Пассивное переключение между тредами в боковой панели (без промпта) | ничего |
| Открыть тред во втором окне, закрыть второе окно | ничего |
| Форк: меню сообщения → «Branch in new chat» → «Use this worktree» | Сам форк события не дал. На первом промпте в нём пришли `SessionStart` `source: fork` (`session_id` = id форка, `transcript_path` = rollout форка) и `UserPromptSubmit`. |
| Меню треда → Fork → «Fork chat in same worktree» (дважды) | Видимый форк не появился, rollout не создан, событий нет. Следующий промпт ушёл в исходный тред. |
| `/Compact` (встроенная команда Desktop) | Сама компактификация события не дала. На следующем промпте пришли `SessionStart` `source: compact` (тот же `session_id`) и `UserPromptSubmit`. |
| `/clear` | Такой команды в Desktop нет (есть «New chat»). Событие `source: clear` в Desktop **не установлено**. |
| Архивирование треда (меню треда → Archive) | Сразу `SessionEnd` `reason: other`. `transcript_path` ещё указывает в `sessions\`, затем файл переезжает в `archived_sessions\`. |
| Подсказка следующего сообщения (Desktop делает её сам после некоторых ходов) | Эфемерный форк: `SessionStart` `source: fork` с `transcript_path: null`, `UserPromptSubmit` со служебным промптом («app-generated request for a suggested next user message»), через 4–10 с `SessionEnd` `reason: other`. Rollout не создаётся. |
| Черновой тред (текст набран на главной, промпт не отправлен) | Создан rollout с одним `session_meta`. `SessionStart` не было; через ≈10 мин app-server выгрузил тред («no subscribers and is idle») — пришёл `SessionEnd` `reason: other`. |
| Субагент (модель вызвала `spawn_agent`) | `SubagentStart`: `session_id` и `turn_id` родителя, `transcript_path` = rollout субагента, `agent_id` = id треда субагента, `agent_type: "default"`. `SubagentStop`: `transcript_path` = rollout родителя, плюс `agent_transcript_path`. Своего `SessionStart` и `UserPromptSubmit` у субагента не было. |
| Закрыть тред | Отдельного действия «закрыть тред» в Desktop нет. Меню треда (Rename, Pin, New side chat, Fork, Add scheduled task, Share, Copy, Open in new window, Open in, Archive) и команды `/` предлагают только Archive и Permanently delete chat; вкладок с крестиком нет. Ближайшие действия: уйти с треда (пассивное переключение — ничего) и Archive (`SessionEnd`, строка выше). Permanently delete chat не проверялся. |
| Закрыть последнее окно Desktop | Ничего. Процессы `ChatGPT.exe` (16) и `codex.exe` (2) живы; за 15 минут ни одного `SessionEnd`. |
| Простой без окон | За 34 минуты после закрытия последнего окна (36 мин без активности в тредах) ни одного `SessionEnd`; в логах app-server ни одной выгрузки треда. Выгрузка через 30 мин простоя **не установлена**. |
| Выход из приложения (File → Quit ChatGPT) | Ни одного `SessionEnd`. В логах нет записей о завершении тредов. |
| Запуск приложения | ничего |

Следствия для #252:

- `SessionStart` `fork` с `transcript_path: null` — признак служебного эфемерного треда. Его
  `UserPromptSubmit` и `SessionEnd` тоже не относятся к работе пользователя.
- `SessionEnd` приходит при архивировании, при выгрузке треда из app-server и перед новым
  сегментом. При закрытии окна и при выходе из приложения он не приходит. Сигналом «последнее
  окно закрыто» он не является — как и предполагала спека. Сигналом «тред закончен навсегда» тоже:
  за `SessionEnd` может прийти `SessionStart resume` того же `session_id`.
- Бывает `SessionEnd` без предшествующего `SessionStart`. Так было у чернового треда и у треда с
  `transcript_path: null` сразу после перезапуска приложения. Что это за второй тред, не
  установлено.
- `resume` приходит лениво, на первом промпте после загрузки треда, а не при его открытии.

### 5.3 Доверие (Review hooks)

**Подтверждено:**

- Установка плагина не включает хуки: Settings → Hooks показывает «5 need review», хуки выключены.
  Отдельного предупреждения в окне треда не появилось.
- Доверие хранится в `~/.codex/config.toml`:

  ```toml
  [hooks.state."<plugin>@<marketplace>:hooks/hooks.json:<event>:<group>:<index>"]
  trusted_hash = "sha256:…"
  trusted_content_hash = "sha256:…"
  ```

  Версии плагина в ключе нет.
- Изменение команды вместе с повышением версии (0.0.1 → 0.0.2 → 0.0.3 → 0.0.4) каждый раз снова
  требовало Review: «5 need review», переключатели выключены.
- Изменение файлов плагина без повышения версии Desktop не подхватывает: кэш остаётся старым, хуки
  выполняют старую команду.
- Кэш обновляется кнопкой Upgrade у marketplace. Старая версия в кэше удаляется, остаётся одна папка
  `<version>`.
- Повышение **только версии** (0.0.4 → 0.0.5, `hooks.json` байт в байт тот же) тоже снимает доверие:
  «5 need review», переключатели выключены. Отправленный после этого промпт не запустил ни одного
  хука: нет ни записи зонда, ни строки трассировки `cmd`. Каждый релиз плагина требует Review hooks
  заново (§4.4 п. 7). Пока пользователь его не прошёл, хуки молча не выполняются.
- Удаление плагина (Settings → Plugins → Uninstall) убирает `[plugins."…"]` и папку кэша, но записи
  `[hooks.state."…"]` остаются в `config.toml`.

## 6. Окно Desktop

**Подтверждено** (`EnumWindows` по процессам `ChatGPT`/`Codex`, видимые top-level окна без владельца):

| | Одно окно | Два окна |
|---|---|---|
| Процесс | `ChatGPT` (главный процесс Electron) | оба окна у одного главного процесса `ChatGPT` |
| Класс | `Chrome_WidgetWin_1` | `Chrome_WidgetWin_1` |
| Заголовок | `ChatGPT` | `ChatGPT` у обоих |
| Видимых окон | 1 | 2 |
| Всего top-level окон процесса | 19 | 21 |
| Закрыто последнее окно | 0 видимых, приложение работает | |

- Заголовок не содержит ни проекта, ни `cwd`, ни названия треда: он остаётся `ChatGPT` и с открытым
  тредом проекта. Совет §3.3 сверять `cwd` хука с заголовком на этой версии неприменим.
- Desktop — общее приложение «ChatGPT» с переключателем ChatGPT/Codex. Процесс `Codex.exe` на этой
  машине не встречался. Список `codexProcessNames` по умолчанию (`Codex`, `ChatGPT`) окна находит,
  но имя `ChatGPT` есть и у отдельного приложения ChatGPT. Отличить их можно только по пути к
  исполняемому файлу (`…\WindowsApps\OpenAI.Codex_…\app\ChatGPT.exe`) или по пакету.
- Остальные top-level окна процесса невидимы (служебные окна Chromium). Правило «visible top-level»
  из §3.3 их отсекает.

### 6.1 Оверлей на живом окне (#253)

Проверено 2026-11-07 на одном мониторе 1920×1200, 100% (второй монитор был отключён), запуском
`CodexSessionPulse.exe --overlay --stub` и чтением состояния окон через Win32 из отдельного процесса.

- Детектор по умолчанию находит одно окно (`processes [Codex, ChatGPT]` + путь
  `\WindowsApps\OpenAI.Codex_`); из 19 видимых top-level окон без владельца других совпадений нет.
  Одно перечисление — около 0,6 мс.
- Оверлей встаёт в z-order прямо над окном Codex и не становится topmost: окно поверх Codex закрывает и
  его. Ex-style живого окна `0x08080080` (`WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOOLWINDOW`), владелец —
  скрытое окно WPF в том же процессе, не Codex.
- Следует за окном при snap (Win+←, Win+↑), максимизации, сворачивании и восстановлении: позиция совпадает
  с расчётом (центр по ширине, нижний край на `offsetY` выше нижнего края окна).
- За 30 с с шестью сменами содержимого и перестановками foreground и фокус клавиатуры (`GetGUIThreadInfo`)
  ни разу не уходили с Codex (402 замера).
- Второе окно (File → New Window): детектор видит 2 окна, оверлей переходит к новому foreground-окну,
  `confirmed` показывается как `inferred`. После закрытия второго окна — снова 1 окно.
- **Активация без события.** Когда другой процесс выводил Codex на передний план, `EVENT_SYSTEM_FOREGROUND`
  не приходил (отдельный логгер видел событие только для Notepad). Поэтому опрос раз в 300 мс идёт, пока
  окно Codex существует, а не только пока оно в фокусе (spec §7.2).
- **Чужое сворачивание.** Внешний инструмент (скриншоты computer-use) свернул окно оверлея (`IsIconic`).
  Оверлей при следующей расстановке восстанавливается через `SW_SHOWNOACTIVATE`, не активируясь. Делает ли
  то же Win+D, не проверялось.
- **Поле ввода.** Карточка ввода Codex (поле и строка чипов) занимает около 157 DIP от нижнего края окна;
  `offsetY = -72` клал строку на текст поля, поэтому по умолчанию `-170`.

Не проверено здесь: mixed DPI и перенос между мониторами (один монитор), клик по самому оверлею (доступ
к окну оверлея для автоматизации не выдан), Alt+Tab визуально (исключение следует из `WS_EX_TOOLWINDOW` и
владельца), Win+D и Task View.

## 7. Rollout

Источник: 809 rollout из `~/.codex/sessions` (64 `.jsonl` и 745 `.jsonl.zst`), `cli_version` от 0.75.0
до 0.167.0-alpha.3.2; плюс треды, созданные в этих сценариях. Чтение только на чтение.

### 7.1 Общая форма записи

**Подтверждено:**

- Каждая строка — `{"timestamp", "type", "ordinal", "payload"}`. `ordinal` растёт на 1. В 808
  разобранных файлах корпуса он начинается с 0. В форке Desktop 0.167 он продолжает нумерацию
  родителя: первая запись форка имеет `ordinal` = `history_base.end_ordinal_exclusive` (33).
- Один файл из 809 не разобрался: строка с некорректным `\u`-escape.
- Codex пишет строки с LF.
- `LastWriteTime` дописываемого rollout не меняется, пока Codex держит файл открытым. У треда A длина
  выросла с 337 484 до 342 024 байт, а время осталось временем создания. Хвост нельзя искать по
  mtime — только по длине.

### 7.2 `session_meta`

Общие поля: `id`, `session_id`, `timestamp`, `cwd`, `originator`, `cli_version`, `source`,
`thread_source`, `history_mode` (`"paginated"` во всех файлах), `model_provider`,
`base_instructions`, `context_window`, иногда `git`, `memory_mode`, `runtime_workspace_roots`,
`creator_*`. **Модели в `session_meta` нет** — она есть в `turn_context.payload.model` каждого хода и
в stdin хуков.

| Вид | Отличающие поля (**подтверждено**) |
|---|---|
| Главный тред Desktop | `originator: "Codex Desktop"`, `source: "vscode"`, `thread_source: "user"`, `session_id == id` |
| Форк Desktop (0.167) | дополнительно `forked_from_id`, `forked_from_ordinal_exclusive` и `history_base: {thread_id, end_ordinal_exclusive, end_byte_offset}`. `session_id == id` (собственный, не корневой). Префикс родителя **не копируется**: сразу после форка в файле 2 записи. `end_byte_offset` равен длине файла родителя на момент форка. |
| Субагент Desktop (0.167) | `thread_source: "subagent"`, `parent_thread_id`, `source: {"subagent": {"thread_spawn": {parent_thread_id, depth, agent_path, agent_nickname, agent_role}}}`, `agent_nickname`, `multi_agent_version: "v2"`. `session_id` = id корневого треда. `forked_from_id` нет. Свои `token_count` — в собственном rollout. |
| Субагент с унаследованной историей (22 файла `codex_exec` 0.154.0 и 1 файл `Codex Desktop` 0.165.0-alpha.1.2) | Два `session_meta`: первый — свой (`forked_from_id` = родитель, `subagent_history_start_ordinal`), второй — копия `session_meta` родителя. За ними скопированный префикс родителя до `subagent_history_start_ordinal`, дальше собственные записи. |
| Служебный тред проверки (`thread_source: "guardian_review"`) | `source: {"subagent": {"other": "…"}}`. 157 файлов `Codex Desktop` и `codex_work_desktop`. |
| Выгрузка `codex exec` | `originator: "codex_exec"`, `source: "exec"` |

Правило спеки §5.3 «каждый `token_count` относится к ближайшему предшествующему `session_meta`» в
файлах с унаследованной историей **неверно**: собственные запросы субагента идут после копии meta
родителя и по этому правилу ушли бы родителю. Правильная граница — `ordinal`:

- записи с `ordinal >= subagent_history_start_ordinal` принадлежат треду из первого `session_meta`;
- только записи внутри скопированного префикса принадлежат родителю.

В корпусе внутри скопированного префикса нашлось 2 `token_count`. `jsonl-inspect` так и считает
(фикстура `…000000000053.jsonl`).

### 7.3 `token_count` и `token_usage_record`

**Подтверждено:**

- `token_count`: `payload` = `{type, info, rate_limits}`. В `info`:
  - `last_token_usage` и `total_token_usage` с `input_tokens`, `cached_input_tokens`,
    `cache_write_input_tokens`, `output_tokens`, `reasoning_output_tokens`, `total_tokens`;
  - `model_context_window`;
  - иногда `last_usage_includes_reasoning`.

  Ключи в snake_case во всех версиях; camelCase не встречался.
- `info: null` — в 12 файлах `codex_exec` (среди них 0.147.0, 0.153.2, 0.154.0), всегда у первого
  `token_count`. В пяти самых маленьких из них он единственный.
- После компактификации пишется `token_count`, у которого все счётчики `last_token_usage` равны 0, а
  `total_token_usage.total_tokens` > 0. Это не запрос: его нельзя показывать как «последний запрос».
  Найдено 131 такое событие.
- `token_usage_record` — отдельный top-level `type`. `payload`: `response_id`, `turn_id`,
  `root_turn_id`, `thread_id`, `session_id`, `usage`, `turn_token_usage`, `thread_token_usage`.
  Его нет ни в одном файле 0.145.0 (10) и 0.147.0 (55). Начиная с 0.153 он есть почти везде.
  Исключения — 4 файла 0.153.2 и 5 файлов 0.155.0-alpha.16 без него; почему, не установлено.
- Дублирует ли он `token_count`:
  - у 29 689 из 29 817 записей есть `token_count` с теми же четырьмя числами в `last_token_usage`.
    Он почти всегда идёт **после** `token_usage_record`, через 1–5 записей;
  - оставшиеся 128 — запросы компактификации: сразу за ними идёт `compacted`, своего `token_count` у
    них нет;
  - в треде этой проверки запись компактификации показала 51 387 input / 51 200 cached.
- `token_count` повторяется: 119 событий — точные повторы предыдущего (тот же `last_token_usage` и
  тот же `total_token_usage`). Без дедупликации один запрос посчитается дважды.
- `thread_id` и `session_id` в `token_usage_record` всегда совпадали с `id` и `session_id` первого
  `session_meta` (29 817 из 29 817).
- `rate_limits` есть почти в каждом `token_count`: `limit_id`, `limit_name`, `plan_type`,
  `primary{used_percent, window_minutes, resets_at}`, `secondary`, `credits{balance, has_credits,
  unlimited}`, `individual_limit`, `rate_limit_reached_type`, `spend_control_reached`. `primary` и
  `plan_type` бывают null.
- Время хода: `task_complete` содержит `duration_ms`, часто `time_to_first_token_ms`, `started_at`,
  `completed_at`. В `task_started` есть `started_at` и `model_context_window`.

### 7.4 Сегменты и `.zst`

- На 2026-11-07 00:15 в `sessions` и `archived_sessions` лежало 1148 файлов: 809 файлов корпуса,
  8 тредов этой проверки в `sessions` и 331 в `archived_sessions`, включая 1 тред этой проверки.
  Все имена имеют вид
  `rollout-<yyyy-mm-ddThh-mm-ss>-<thread-id>.jsonl[.zst]`: суффикса нет. Компактификация и форк
  нового сегмента не создают.
- **Подтверждено:** правка своего сообщения в Desktop создаёт новый сегмент того же треда:
  - имя `rollout-<время правки>-<thread-id>_<rollout-id>.jsonl` (папка — день правки, у нас
    `2026\11\07`, хотя первый сегмент лежит в `2026\11\06`);
  - `session_meta` с тем же `id` и `session_id`, без `forked_from_id`;
  - `history_base: {thread_id: <тот же тред>, end_ordinal_exclusive: 24, end_byte_offset: 315870}` —
    точка в первом сегменте, до которой история сохранена;
  - `ordinal` продолжается с 24;
  - старый сегмент остаётся на диске без изменений (длина 326 824 байт до и после).

  Дальше Codex пишет только в новый сегмент; новые промпты, в том числе после перезапуска,
  попадали в него. Фикстура — `…-000000000001_…000000000101.jsonl`.
- При архивировании треда в `archived_sessions` переезжают оба его сегмента.
- `.zst`: сжаты все файлы, последняя запись которых старше ≈7 дней (самый новый `.zst` — 30.10 21:56,
  самый старый `.jsonl` — 30.10 23:10; проверка 07.11). Ни у одного треда нет обоих вариантов сразу:
  файл сжимается на месте. В `archived_sessions`: 328 `.zst`, 3 `.jsonl`.
- Материализация `.zst` обратно при resume **не установлена**: старый сжатый тред в этой проверке не
  возобновлялся.

### 7.5 Порядок доставки

- **Подтверждено:** в одном файле `token_usage_record` пишется раньше парного `token_count`.
- **Подтверждено** (6 ходов в двух тредах): `task_started` хода попадает в rollout раньше, чем
  стартуют его хуки. Например, `task_started` 21:50:36.563, вход в `SessionStart` 21:50:38.274, в
  `UserPromptSubmit` 21:50:39.187. `token_usage_record` и `token_count` хода появляются через 2–4 с
  после `UserPromptSubmit` (короткие промпты). Значит, на `UserPromptSubmit` в файле ещё нет
  `token_count` этого хода.
- **Подтверждено:** `SessionStart` (`startup`, `fork`, `compact`) приходит до `UserPromptSubmit` того
  же промпта; между ними 0,5–1,3 с.
- **Подтверждено:** ход компактификации (`/Compact`) пишет `task_started`/`task_complete` и
  `token_usage_record`, но хука `UserPromptSubmit` у него нет.
- **Не установлено:**
  - успевает ли `transcript_path` на `SessionStart` указывать на уже дописанный файл: у `startup` файл
    существовал, у форка тоже, гарантий никто не давал;
  - порядок событий хуков относительно записи `token_count` предыдущего хода.

## 8. Не установлено и ограничения

- `SessionStart` `source: clear`: в Desktop нет команды `/clear`; в CLI не проверялось.
- Выгрузка треда и `SessionEnd` через 30 мин простоя: за 34 мин без окон не наблюдались. Дольше не
  ждали.
- Холодный старт `--hook` после перезагрузки; замеры сделаны на машине, где параллельно работали
  другие сессии (#254 повторяет на простаивающей машине).
- Кто держит job хука живым после выхода из приложения (раздел 4).
- Поведение на машине без PowerShell 7 (какую оболочку выберет Codex).
- Материализация `.jsonl.zst` при resume старого треда.
- Процесс `Codex.exe`: на этой машине его нет, есть только `ChatGPT.exe` из пакета `OpenAI.Codex`.
- Меню треда → Fork → «Fork chat in same worktree» видимого результата не дало (дважды); форк
  проверялся через меню сообщения «Branch in new chat».
- Все наблюдения — на одной машине, с одной версией Desktop (26.1103) и app-server
  (0.167.0-alpha.3.2).

## 9. `jsonl-inspect`

Консольная утилита `tools/CodexSessionPulse.JsonlInspect` (`jsonl-inspect.exe`). Читает rollout с
`FileShare.ReadWrite | FileShare.Delete`, ничего не пишет. На каждый `token_count` печатает одну
строку:

- `timestamp`;
- поток (по правилу из 7.2);
- модель из последнего `turn_context`;
- `input`, `cached`, `cacheWrite`, `output`;
- `timing`: что сообщил `task_complete` хода — `ttft+duration`, `duration`, `none` или `open`, пока ход
  не закончился.

`info: null` печатается как `info=null`, нулевая запись после компактификации — с `note=all-zero`.
Текста сообщений она не печатает. `.jsonl.zst` не читает: сообщает об этом и выходит с кодом 1.

```powershell
dotnet run --project plugins/codex-session-pulse/tools/CodexSessionPulse.JsonlInspect -- <rollout.jsonl>
```

## 10. Фикстуры

`tests/CodexSessionPulse.Infrastructure.Tests/Fixtures/`, описание — в `Fixtures/README.md`.

Как обезличено:

- id заменены синтетическими (`00000000-0000-7000-8000-0000000000NN`) согласованно во всех файлах.
  Хук-фикстуры тредов 1, 2 и 3 ссылаются на rollout-фикстуры тех же тредов. У служебного треда-
  подсказки (66) и архивированного треда (67) rollout-фикстуры нет;
- пути заменены на `C:\Users\user\…` и `C:\work\project`;
- весь свободный текст заменён на `"redacted"`;
- записи без нужных #251 данных сведены к `{type}`-заглушкам с прежним `ordinal`;
- числа, временные метки и структура сохранены;
- `history_base.end_byte_offset` форка пересчитан на обезличенный файл родителя.

Папка `rollouts/` помечена в `.gitattributes` как `-text`, чтобы LF и смещения доходили до тестов
байт в байт.

Фикстуры stdin для `SessionStart` `source: resume` нет. Событие наблюдалось (раздел 5.2), но
захваты stdin были удалены вместе с зондом раньше, чем дошло до фикстур. Восстанавливать его по
памяти не стали.

## 11. Остатки проверки

Удалено: временный плагин из Desktop (Uninstall), его папка кэша, проект `PulseProbe250` в Desktop,
временная запись в `.agents/plugins/marketplace.json`, `plugin.json`, `hooks.json` и `bin/` в рабочей
копии, все файлы и процессы зонда, стенд замеров.

Осталось и перечислено точно (это `~/.codex`, в эту проверку только для чтения):

- `~/.codex/config.toml`:
  - `[projects.'<путь рабочей копии>']` с `trust_level = "trusted"` — записан Desktop при «Trust
    this folder»;
  - пять записей `[hooks.state."codex-session-pulse@dlin10-ai-coding-plugins:hooks/hooks.json:<event>:0:0"]`
    для `session_start`, `session_end`, `user_prompt_submit`, `subagent_start`,
    `subagent_stop` — записаны Desktop при Trust all.
- `~/.codex/archived_sessions/`: 9 rollout тестовых тредов от 2026-11-06 23:41 до 2026-11-07 00:41
  (заархивированы через Desktop, видны в Settings → Archived chats).
- `~/.codex/sessions/2026/11/06/rollout-2026-11-06T23-40-01-<thread-id>.jsonl`: черновой тред с
  одним `session_meta`, без сообщений.
- `~/.codex/logs_2.sqlite`: строки логов app-server за время проверки.

## 12. Хуки, IPC и жизненный цикл (#252)

Проверено 2026-11-07 на этой же машине: тесты — на Debug-сборке, замеры — на Release
framework-dependent single-file (`-r win-x64 --self-contained false -p:PublishSingleFile=true`).
Параллельно работали другие сессии, как и в разделе 3.5.

### 12.1 Тёплый старт `--hook`

**Подтверждено** вне Desktop: порог 150 мс выполняется. Замер — от `Process.Start` до выхода, из
pwsh, n=25 на строку, stdin около 500 Б (`UserPromptSubmit`) или около 120 Б (`SessionEnd`).
Launcher читает версию из манифеста и запускает exe из `<home>\bin\0.1.0\`; корень перенесён через
`CODEX_SESSION_PULSE_HOME`.

| Путь | JIT: медиана / мин | ReadyToRun: медиана / мин |
|---|---|---|
| `cmd.exe /d /c launcher --hook`, overlay работает (доставка с подтверждением) | 129 / 127 мс | 121 / 119 мс |
| `exe --hook`, overlay работает | 97 / 96 мс | 90 / 89 мс |
| `cmd` + launcher `--hook --no-start`, overlay работает | 125 / 123 мс | 118 / 117 мс |
| `cmd` + launcher `--hook --no-start`, overlay нет | 116 / 114 мс | 113 / 111 мс |
| `exe --hook --no-start`, overlay нет | 85 / 84 мс | 82 / 81 мс |
| `cmd` + launcher `--hook`, холодный: запуск overlay и доставка (n=1) | 218 мс | 205 мс |

- Против пустого зонда из 3.5 (exe 77 мс, cmd + launcher 104 мс) разбор stdin, имена канала и
  pipe добавляют около 20 мс. Запас до порога — 21 мс (JIT) и 29 мс (ReadyToRun), так что отдельный
  маленький hook-бинарь (§4.4 п. 1б) не нужен.
- Для #254: `PublishReadyToRun` даёт ещё около 8 мс; exe растёт с 0,47 до 0,87 МБ.
- В цепочке Desktop добавляется pwsh, около 300 мс (3.5). Хуки `SessionStart` и `UserPromptSubmit`
  асинхронны, поэтому Codex их не ждёт.
- **Не установлено**: холодный старт после перезагрузки и время внутри Desktop (12.4).

### 12.2 Pipe, mutex и запуск overlay

- Имена: mutex `Local\CodexSessionPulse.<SID>.1`, pipe `CodexSessionPulse.<SID>.1`, где `1` —
  мажорная версия протокола. При перенесённом `CODEX_SESSION_PULSE_HOME` к обоим добавляется хэш
  корня, поэтому тесты не задевают настоящий overlay.
- Сообщение — одна строка JSON с `\n` в конце: `v`, `event`, `thread` (из имени файла
  `transcript_path`), `source`, `at`. Prompt, `cwd`, пути и `session_id` не передаются. Сервер
  отвечает одним байтом подтверждения.
- **Подтверждено** тестом: подтверждение обязательно. Без него из 16 параллельных клиентов часть
  сообщений терялась. Клиент успевал записать и закрыть соединение раньше, чем сервер вызывал
  `ConnectNamedPipe`: тот получал `ERROR_NO_DATA`, и данные пропадали.
- Пределы: connect 300 мс для работающего overlay, запись и подтверждение — 200 мс. После запуска
  overlay hook ждёт pipe до 1000 мс.
- Overlay берёт mutex и открывает pipe до загрузки WPF. **Подтверждено**: при холодном старте
  событие доходит за 205–218 мс от начала hook, а запуск через hook занимает около 13 мс.
- Hook запускает overlay через `CreateProcess` с `CREATE_BREAKAWAY_FROM_JOB`, без наследуемых
  handles, рабочая папка — папка exe. При `ERROR_ACCESS_DENIED` повторяет запуск внутри job. Сам
  hook играет роль короткоживущего стартера из ADR 0010: он сразу выходит, и у overlay не остаётся
  родителя.
- **Подтверждено** тестами с job, собранными как у Codex:
  - `0x2800` (kill on close и breakaway): overlay оказывается вне job и переживает его закрытие;
  - `0x2000` (без breakaway): overlay остаётся в job, пишет в лог `exitsWithJob True` и
    завершается при закрытии job.
- Тестовый процесс этой машины сам работает в job без breakaway. Hook, запущенный из него без
  тестового job, честно пишет `inside the hook's job (breakaway refused)`. Из pwsh overlay попадал в
  job с флагами `0x0000`, `exitsWithJob False`.
- Видимость mutex и pipe между процессом из контекста пакета MSIX (hook из Desktop) и процессом,
  запущенным вручную, **подтверждена** в обе стороны (12.4).

### 12.3 Тесты жизненного цикла

`tests/CodexSessionPulse.Win32.Tests/HookLifecycleTests.cs` запускает настоящий exe с отдельным
home и `CODEX_HOME`:

- холодный параллельный `SessionStart` и `UserPromptSubmit` поднимают ровно один overlay, и оба
  события доходят, `source` тоже;
- `SessionEnd --no-start` без overlay завершается быстрее 3 с и ничего не создаёт. С работающим
  overlay событие доставляется, а overlay продолжает работать;
- `UserPromptSubmit`, когда mutex уже занят, а pipe ещё нет: hook выходит с кодом 0 за время
  connect. Overlay без единого hook подхватывает тред из хвоста JSONL как `Inferred`;
- job `0x2800` и `0x2000` — как в 12.2;
- окна, которые overlay принимает за окна Codex (имя процесса теста и уникальный заголовок):
  - запущенный вручную без окна overlay не выходит;
  - сворачивание не завершает его;
  - закрытие одного из двух окон не завершает его;
  - закрытие последнего окна завершает его через 3–4 с с кодом 0, по тому же пути, что Exit в
    трее.

Правила `SessionEnd` проверяются в `PulseEngineTests`:

- очищается ровно один тред;
- повторное, неизвестное и устаревшее событие ничего не ломают;
- `SessionEnd` для subagent игнорируется;
- tombstone не даёт старому JSONL воскресить тред;
- pruning после 24 ч простоя и сверх 64 тредов оставляет tombstone.

### 12.4 Проверка в Desktop

Проверено 2026-11-07 в 04:45–05:00 UTC.

Стенд:

- Codex Desktop 26.1103.3517.0, окно развёрнуто на мониторе 2560×1440 при 125%;
- плагин установлен из рабочей копии #252 (Settings → Plugins показывал `Source` на её папку; `hooks.json`
  в кэше совпадал с рабочим);
- Release single-file exe лежал в `%USERPROFILE%\.codex-session-pulse\bin\0.1.0\`.

«Действие» — что сделано в Desktop; «получено» — что записали логи overlay и хука.

| Действие | Получено | Итог |
|---|---|---|
| Plugins → установка | «3 hooks, 3 need review» в Settings → Hooks. До Trust all prompt хуков не дал. | **Подтверждено** (5.3) |
| Trust all, новый тред, первый prompt; overlay не запущен | Два хука одновременно запустили два overlay. Второй записал `another overlay holds the single-instance mutex; exiting`. Первый получил `SessionStart (startup)` и `UserPromptSubmit` с тредом. | **Подтверждено**: холодный параллельный старт даёт один overlay. `UserPromptSubmit`, пришедший до готовности overlay, не потерян. |
| То же | Overlay из хука: `job 0x0000, exitsWithJob False`, hook: `outside the hook's job`. | **Подтверждено**: breakaway из job хука работает. |
| То же | `SessionStart (fork)` и `UserPromptSubmit` с `thread none` (служебный тред-подсказка). | **Подтверждено**: только посчитаны, на экран не попали. |
| Ручной `--hook --no-start` и ручной `--overlay` из обычного процесса при overlay из хука | `hook SessionEnd` доставлен. Ручной overlay вышел по mutex. | **Подтверждено**: pipe и mutex видны из обычного процесса в процесс из контекста пакета. |
| Overlay закрыт через `WM_CLOSE`, запущен вручную; prompt в Desktop | Хук доставил `UserPromptSubmit` в ручной overlay, нового процесса нет. `cache Warm, selection Confirmed`. | **Подтверждено**: видны и в обратную сторону, поэтому запасной путь из 12.2 не нужен. |
| Archive треда | `hook SessionEnd, thread from transcript_path`. Выбор перешёл на другой тред (`Inferred`), overlay работает. | **Подтверждено** |
| File → New Window, затем закрытие второго окна | `codex windows 2`, overlay перешёл к новому окну. После закрытия — `codex windows 1`, overlay работает. | **Подтверждено** |
| Закрытие последнего окна | `anchor NotFound` в 04:53:14.632, выход в 04:53:18.565 (3,9 с) через общий путь выхода. 15 процессов ChatGPT остались, видимых окон 0. | **Подтверждено** |
| Открыть Codex снова, новый тред | Снова один overlay: второй вышел по mutex. Оба события дошли. | **Подтверждено**: следующий `SessionStart` поднимает overlay заново. |
| Archive последнего треда при остановленном overlay | Overlay не появился. | **Подтверждено**: `--no-start` ничего не запускает. Само событие в этом случае не логируется. |

Не установлено и замечено:

- **Время хука внутри Desktop.** В `logs_2.sqlite` на текущем уровне логирования нет событий
  `hook/started` и `hook/completed`. Время exe и launcher — в 12.1.
- **Строки `hook.log` от одновременно запущенных хуков.** Из двух строк «started the overlay» в файле
  оказалась одна: два процесса одновременно дописывали `hook.log`, и одна запись упала на нарушении
  совместного доступа, которое молча проглатывается. На доставку событий это не влияет.
- **Overlay сместился в левый нижний угол** (#253, для #254). Трижды overlay оказался у левого края
  рабочей области: прямоугольник 0,1077–159,1104 в логических пикселях, ширина около 199 физических
  вместо 625:
  - при двух открытых окнах;
  - после архивирования показанного треда (дважды).

  В других запусках позиция была верной: 774,894–1274,934 логических, по центру окна, на 170 DIP
  выше нижнего края. В прогоне с диагностическим логом `Reposition` расчёт был верным (967,1118
  физических), и смещение не повторилось. Причина не найдена; кандидаты — порядок `SizeToContent`
  против `SetWindowPos` и ширина содержимого при смене состояния.
- **Кто доставляет сообщение после выхода хука.** Из-за подтверждения в протоколе хук ждёт, пока
  сервер прочтёт сообщение. В Desktop каждое событие дошло до overlay, но время ожидания отдельно
  не замерялось.

Осталось после проверки (это `~/.codex`, в эту проверку только для чтения, кроме записей Desktop):

- `~/.codex/config.toml`:
  - `[projects.'<путь рабочей копии #252>']` — Trust this folder;
  - `[hooks.state."codex-session-pulse@dlin10-ai-coding-plugins:hooks/hooks.json:<event>:0:0"]` —
    Trust all перезаписал хэши для трёх событий; записи `subagent_*` остались с #250.
- Проект `csp-252-check` в боковой панели Desktop и шесть заархивированных тестовых тредов.
- `%USERPROFILE%\.codex-session-pulse\logs\` — логи прогона, без текста prompt.

Удалено: плагин из Desktop (Uninstall, кэш плагина исчез), exe и папка `bin\`, процессы overlay.
Окно Codex вернулось на прежний монитор.

## 13. Пакет 0.1.0 (#254)

Проверено 2026-11-08 на этой же машине (Windows 11 Enterprise 26300, два монитора: основной 125 %,
второй 100 % слева, с отрицательными координатами), SDK 10.0.403, runtime 10.0.14. Пакет —
`build/package.ps1`: framework-dependent win-x64 single-file, ReadyToRun, символы внутри exe, один файл
около 0,94 МБ. Параллельно работали другие сессии.

### 13.1 Тёплый `--hook` и цена launcher

**Подтверждено**: порог 150 мс выполняется с запасом около 38 мс. Метод — `build/measure-hook.ps1`.
Скрипт запускает `cmd.exe /d /c "<plugin>\bin\codex-session-pulse-launcher.cmd" --hook` с payload
`UserPromptSubmit` в stdin. Overlay того же exe работает во временном home, поэтому каждый хук
подключается и доставляет событие. Время — от `Process.Start` до выхода, n=25, первый прогон не
считается. Все 26 событий доставлены в каждой строке. Два раунда:

| Вариант | Медиана, мс | Минимум, мс |
|---|---|---|
| ReadyToRun, launcher 0.1.0 | 112,0 / 111,6 | 109,6 / 109,3 |
| JIT, launcher 0.1.0 | 114,5 / 114,6 | 112,5 / 112,4 |
| ReadyToRun, launcher до #254 (`4f71c503`, только быстрый путь) | 111,9 / 111,6 | 109,1 / 109,3 |

- **ReadyToRun оставлен**: около 3 мс за 0,4 МБ (0,54 → 0,94 МБ). В 12.1 разница была около 8 мс.
- **Медленный путь и проверка runtime на быстром пути ничего не стоят.** Первая версия разбора
  манифеста вызывала `call :version` на каждую строку. Это стоило около 5,5 мс: 118,6 против
  113,0 мс медианы с прежним разбором. Разбор переписан на локальный `EnableDelayedExpansion` без
  `call`. Вариант без `DOTNET_DISABLE_GUI_ERRORS` и без проверки кода выхода оказался *медленнее*
  (128,5–129,2 мс при прочих равных). Причина не найдена; это не довод против проверки.
- Все числа — без pwsh. В Desktop к ним добавляется около 300 мс pwsh (3.5); `SessionStart` и
  `UserPromptSubmit` асинхронны.

**Overlay в покое** (`build/measure-overlay.ps1`). Overlay запущен хуком, окно Codex открыто, на
переднем плане другое окно. 5 мин, 29 отсчётов:

| Метрика | Значение | Цель §10 |
|---|---|---|
| CPU | 0,472 % одного ядра (0,029 % машины) | < 1 % — **выполнено** |
| Сеть | ни одного TCP-соединения и UDP-конечной точки | нет — **выполнено** |
| Working set | 112,8–118,1 МБ | ≤ 80 МБ — **не выполнено** |
| Private bytes | 56,0 МБ | — |

Откуда working set: четыре варианта по 45 с, каждый в своём временном home, при открытом окне Codex.

| Вариант | Working set | Private bytes |
|---|---|---|
| Настоящие rollout (19 файлов, 31 МБ) | 96,2 МБ | 59,0 МБ |
| `--stub`, без чтения rollout и движка | 95,5 МБ | 57,5 МБ |
| `DOTNET_gcConcurrent=0` | 96,0 МБ | 59,0 МБ |
| `DOTNET_GCConserveMemory=5` | 96,3 МБ | 60,1 МБ |

Чтение rollout и движок добавляют около 1–2 МБ, настройки GC ничего не меняют. Собственная память
процесса — 56–60 МБ. Остальное в working set — разделяемые образы WPF и .NET, шрифты и графика. За
часы работы working set растёт до ~118 МБ без роста private bytes. В Windows Sandbox (программная
отрисовка, без окна Codex) было 74,3 МБ. Цель ≤ 80 МБ для WPF-процесса без искусственной обрезки
страниц (`EmptyWorkingSet`) не достигается. Её стоит пересмотреть: например, мерить private bytes
или поднять порог.

### 13.2 Нет .NET 10 Desktop Runtime

**Подтверждено** на этой машине подменой `DOTNET_ROOT`. Каталог с `host\` и
`shared\Microsoft.NETCore.App\10.0.14` (junction-ы на настоящие), но без
`Microsoft.WindowsDesktop.App`, — это «.NET есть, Desktop Runtime нет». Пустой каталог — «.NET нет
совсем».

| Случай | Без `DOTNET_DISABLE_GUI_ERRORS` | С `DOTNET_DISABLE_GUI_ERRORS=1` |
|---|---|---|
| Нет Desktop Runtime | Apphost показывает окно `CodexSessionPulse.exe` («You must install or update .NET…») и ждёт клика; через 6 с процесс ещё жив | Выход за 324 мс с кодом `0x80008096`, текст ошибки в stderr |
| Нет .NET | — | Выход с кодом `0x80008083`, «You must install .NET…» в stderr |

- Launcher задаёт `DOTNET_DISABLE_GUI_ERRORS=1` перед запуском exe. Код `0x80008000`–`0x800080FF`
  (хост упал до приложения) он превращает в сообщение
  `CodexSessionPulse.exe needs the .NET 10 Desktop Runtime x64…` со ссылкой на загрузку. Для `--hook`
  выходит с 0, для ручного `--overlay` — с 1.
- `build/test-launcher.ps1` повторяет оба случая. Против launcher до #254 оба висят до таймаута 60 с:
  это и есть диалог.
- В чистой Windows Sandbox — раздел 13.4.

### 13.3 Проверка в Desktop

Проверено 2026-11-08, 20:33–21:24 UTC, на Codex Desktop 26.1103. Плагин установлен из каталога
рабочей копии #254: проект `csp-254-smoke` на её папке, Settings → Plugins. Хуки доверены через
Settings → Hooks. Exe лежал в `%USERPROFILE%\.codex-session-pulse\bin\0.1.0\`: сборка
`build/package.ps1` этой ветки, release ещё не существует. Окнами управляли computer-use и
Windows-MCP. Положение overlay снято Win32-зондом из отдельного процесса: PerMonitorV2,
`DWMWA_EXTENDED_FRAME_BOUNDS`, `GetForegroundWindow`, `GetGUIThreadInfo`, соседнее окно в z-order.

| Проверка | Получено | Итог |
|---|---|---|
| Реальные хуки, новый тред | `SessionStart (startup)` и `UserPromptSubmit` пришли одновременно и запустили два overlay; второй вышел по mutex. Оба события дошли, `selection Confirmed (hook-session-start)`; после ответа `Warm, Confirmed (hook-user-activity)`. | **Подтверждено** |
| 125 %, окно 1651×1067 | Overlay 625×50 (WaitingForUsage), затем 905×50 (Warm), по центру Codex: x = 1491,5 у обоих. 212 px = 170 DIP над нижним краем, в z-order сразу над Codex. Foreground и фокус клавиатуры — у Codex. | **Подтверждено** |
| Клик по overlay (синтетический, `mouse_event`) | Foreground и фокус остались у Codex. | **Подтверждено** |
| Alt+Tab (Ctrl+Alt+Tab, снимок) | В переключателе 11 окон, CodexSessionPulse среди них нет; ex-style `0x08080080`. | **Подтверждено** |
| Win+D и обратно | Скрыт вместе с Codex, вернулся на место. | **Подтверждено** |
| Task View (Win+Tab) | `anchor Background`, overlay скрыт. | **Подтверждено** |
| Свернуть и развернуть Codex | `Minimized`, затем `Attached`; overlay вернулся на место, не активируясь. | **Подтверждено** |
| Второе окно (File → New Window) | `codex windows 2`; overlay перешёл к новому развёрнутому окну (2560×1380): центр x = 1280, 170 DIP. Выбор не выше `inferred`. | **Подтверждено** |
| Перенос на второй монитор 100 % (Win+Shift+←) | Codex −1920,138 1920×1152, overlay −1312,1080 704×40 при 96 dpi: те же DIP, центр x = −960, отступ 170 px. Перенос обратно — снова 882×50 при 120 dpi. | **Подтверждено**, отрицательные координаты тоже |
| 100 % как единственный монитор | Основной монитор пропал из конфигурации посреди проверки, Codex переехал на 1920×1200 при 96 dpi; overlay 706×40 по центру, 170 px. | **Подтверждено** |
| 150 % | Не проверено: пользователь не стал менять масштаб. | **Не сделано** |
| Трей → Copy diagnostics | `app: 0.1.0`, детектор, кэш, `[lifecycle]`, настройки. Имени пользователя и пути профиля нет. Был полный GUID треда в имени rollout — исправлено (`Redact` оставляет 8 символов). | **Подтверждено** после исправления |
| Трей → Settings | Окно со всеми полями; Cancel закрыл без сохранения, Save — `settings saved and applied`. Esc вживую не нажимали: глобальный Escape прервал бы сессию агента; покрыт `SettingsWindowTests`. | **Подтверждено** (Esc — тестом) |
| Tooltip источника | При бампе версии, без хуков: «Hooks not observed: after a plugin update, open Review hooks in Codex». | **Подтверждено** |
| Ручной `--overlay` по README | Launcher из кэша плагина запустил overlay; `Auto · inferred` до первого хука. | **Подтверждено** |

Найдено и исправлено в этой ветке:

- **Глюк размещения при 125 % из 12.4 — это свёрнутый overlay.** Значок свёрнутого окна —
  160×27 DIP, при 125 % ровно 199×34 физических, в левом нижнем углу рабочей области. В этой
  проверке его воспроизвело одно нажатие клавиши через computer-use: инструмент сворачивает окна
  приложений, которых нет в его списке, и overlay оказался `IsIconic` в `0,1346 199×34`. Проверка
  #252 тоже шла через computer-use. `PlaceAbove` разворачивал overlay, но только при следующем
  событии anchor. Теперь секундный таймер переставляет свёрнутый overlay сам. Свёрнутый из другого
  процесса overlay вернулся через 267 мс, фокус остался у Codex.
- **Codex с `WS_EX_TOPMOST` закрывал overlay.** После Win+D под инструментом автоматизации окно
  Codex осталось topmost, а обычный overlay Windows держит под всеми topmost-окнами. Теперь overlay
  живёт в полосе Codex: topmost вместе с ним, обычный — без него. Таймер ловит закрепление и
  открепление, которые не двигают окно. Вживую переход в обе стороны занял не больше 1,5 с.

- **Обычный overlay под Codex наверху обычной полосы** (найдено на ревью PR #388, воспроизведено
  тестом на настоящих окнах; в Desktop не воспроизводилось). Над Codex сразу стоит topmost-окно,
  overlay — ниже Codex. Ни `HWND_NOTOPMOST`, ни `HWND_TOP` его не поднимают. Первый не двигает
  окно, которое уже обычное. Второй не поднимает окно фонового процесса, а overlay фоновый, пока
  фокус у Codex. Работает пара вызовов: `HWND_TOPMOST` на миг, затем `HWND_NOTOPMOST`. В Desktop
  случай маскирует скрытое IME-окно Codex: как принадлежащее Codex, оно всегда стоит прямо над ним,
  и overlay встаёт после него. `IsPlacedFor` теперь проверяет, что overlay выше Codex.

Замечено, без изменений:

- Установка из локальной рабочей копии копирует папку плагина целиком, вместе с `bin/obj` и
  `artifacts` сборки (33 МБ). При установке из GitHub-каталога в кэш попадут только файлы из git.
- Пока в Desktop открыт проект на рабочей копии, её `.agents/plugins/marketplace.json` перекрывает
  одноимённый каталог для всех плагинов dlin10. Обновление кэша `plan-forge-flow` из неё упало с
  `Access is denied`.
- Время хука внутри Desktop по-прежнему не измерено (12.4).

### 13.4 Windows Sandbox

Проверено 2026-11-08 в Windows Sandbox. Гость — Windows 10.0.26100, пользователь
`WDAGUtilityAccount`, сеть включена. Папка с exe, `bin/` и манифестом смонтирована только для
чтения. Установщик `windowsdesktop-runtime-10.0.14-win-x64.exe` скачан с builds.dotnet.microsoft.com
(60 035 296 байт, подпись Microsoft, SHA256 `91C2795A…7330`) и смонтирован отдельно. Сценарий
выполнялся в PowerShell 5.1 гостя.

| Шаг | Получено | Итог |
|---|---|---|
| .NET нет, exe нет, настоящий URL релиза | Хук: «downloading … from https://github.com/…/codex-session-pulse-v0.1.0/CodexSessionPulse.exe», `curl: (22) … 404`, «could not prepare …», exit 0 за 3,8 с. Релиза пока нет — это ожидаемо. | **Подтверждено** (путь скачивания) |
| .NET нет, exe в кэше | Хук: текст apphost и строка launcher «needs the .NET 10 Desktop Runtime x64 … install it from https://dotnet.microsoft.com/download/dotnet/10.0», exit 0 за 70 мс. `--overlay`: то же, exit 1. Процессов не осталось. | **Подтверждено** |
| Тот же exe напрямую, без launcher | Через 6 с процесс жив, видно окно `CodexSessionPulse.exe` — диалог apphost. Именно от него защищает launcher. | **Подтверждено** |
| Установка Runtime (`/install /quiet /norestart`) | Exit 0 за 8 мин. Runtimes: NETCore.App и WindowsDesktop.App 10.0.14; SDK нет. | — |
| Хук `{}` | Exit 0, WPF не загружен. | **Подтверждено** |
| Тёплый `--hook --no-start`, n=10 | Медиана 101–104 мс, минимум 96–100 мс. | **Подтверждено** |
| `SessionStart` через launcher | В первом прогоне хук молча ничего не делал. Причина: консоль гостя в UTF-8, и `StandardInput` PowerShell 5.1 пишет перед JSON BOM `EF BB BF` даже через `BaseStream`. Исправлено: пропуск BOM в `HookPayloadReader`, запись сбоя хука в `hook.log`. С исправленным exe: overlay запущен за 277 мс, `hook SessionStart (startup)` дошёл. | **Подтверждено** после исправления |
| Второй `--overlay` | Вышел за 103 мс по mutex. | **Подтверждено** |
| Overlay без окна Codex, 120 с | Жив, тих: CPU 0,52 % одного ядра, working set 74,3 МБ, сетевых соединений нет. | **Подтверждено** |
| WM_CLOSE в окно трея | Overlay вышел. | **Подтверждено** |
| Copy diagnostics | Трей внутри Sandbox недоступен инструментам автоматизации. | **Не сделано** (проверено на хосте, 13.3) |

### 13.5 Обновление версии и доверие хукам

Проверено 2026-11-08 в Desktop. Временный бамп 0.1.0 → 0.1.1 в рабочей копии, не коммитился.

- После Marketplace → Upgrade кэш стал `…\codex-session-pulse\0.1.1\`, папка 0.1.0 удалена.
  Settings → Hooks: «Codex Session Pulse — 3 hooks — 3 need review». В композере Codex появилась
  кнопка «Review hooks».
- Промпт в треде: ни одного хука. `hook.log` не изменился, overlay не запустился.
- Ручной запуск по README (launcher из `…\0.1.1\bin\`, exe в `bin\0.1.1\`): overlay работает,
  `Auto · inferred`. В tooltip источника — «Hooks not observed: after a plugin update, open Review
  hooks in Codex». Тот же текст Copy diagnostics показывает, пока хуков не было (13.3).
- **Новое:** откат на 0.1.0 и Upgrade снова дали «3 need review». Доверие привязано к содержимому
  плагина (`trusted_content_hash` сменился с `f39d8370…` на `5abbb1a9…`). Повторного Review
  требует любое обновление кэша, в котором поменялись файлы, а не только смена версии.
- После Trust all уже открытый тред хуки не вызывал; новый тред — сразу:
  `SessionStart`/`UserPromptSubmit` → `Confirmed`.

### 13.6 Холодный старт после перезагрузки

**Не измерен**: машину не перезагружали. Воспроизводимая команда: перезагрузиться, войти, не
запускать Codex и первым делом выполнить в pwsh строки ниже. Первый прогон — холодный, второй —
тёплый для сравнения. `--no-start` не запускает overlay, поэтому замер не зависит от Codex.

```powershell
$launcher = "$env:USERPROFILE\.codex\plugins\cache\dlin10-ai-coding-plugins\codex-session-pulse\0.1.0\bin\codex-session-pulse-launcher.cmd"
foreach ($run in 'cold', 'warm') { $ms = (Measure-Command { '{"hook_event_name":"SessionEnd"}' | cmd /d /c "`"$launcher`" --hook --no-start" }).TotalMilliseconds; "$run $([int]$ms) ms" }
```
