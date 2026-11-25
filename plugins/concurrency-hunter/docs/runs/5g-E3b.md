# 5g run E3b — ожидания и исключительные выходы по вхождениям на корпусах

## Причина

Ран [#436](https://github.com/dlin10/ai-coding-plugins/issues/436) фазы 5g
([#274](https://github.com/dlin10/ai-coding-plugins/issues/274)) берёт вопросы об ожиданиях, исключительных выходах и допущениях
окружения ADR 0030, у которых есть вхождение на корпусе или пример пропущенного race в коде программы. Целиком ран ещё не открыт:
запись начата работой, выделенной из рана E-scale. [#503](https://github.com/dlin10/ai-coding-plugins/issues/503) — остаток #498,
закрытого коммитом `47f97194`; раздел #498 лежит в [`5g-E-scale.md`](5g-E-scale.md), и его текст сюда не переносится.

## #503 — проверка tail проходит wait вызывающего мимо среднего helper, который покидает тело до своего `await`

**Вход.** Hosted worker один раз ждёт три async helper, каждый — задачу следующего:

```csharp
// A: дочерняя работа, необязательный блокирующий join, await
var t = Task.Run(() => { First(); return 1; });
if (probe) { try { t.Wait(); } catch (Exception) { } }
await t;

// B: может бросить до await задачи A
var a = A(Environment.TickCount % 3 == 0);
if (Environment.TickCount % 2 == 0) throw new InvalidOperationException();
await a;

// C: вызов до try, await в try, запись после Task.Yield в catch
var b = B();
try { await b; } catch (InvalidOperationException) { await Task.Yield(); After(); }

// worker
var c = C(); await c;
```

Когда `probe` ложно, а B бросает, C получает задачу с fault, пока `First` ещё может работать: race `First` / `After`. Finding не
было — пару упорядочивал completion proof. Без необязательного join в A пара есть: рамку работы держит непустой именно он.

**Второй вход** нашли фикстуры: B не бросает, а делает `return` до `await a` (`if (…) return; await a;`), C — `var b = B(); await b;
await Task.Yield(); After();`. C получает задачу, завершённую обычно, пока `First` ещё может работать; finding не было тоже.

**Где ошибка.** Proof спрашивает, возобновляется ли tail (`After` после `Yield` идёт в async-продолжении) только после работы:
`TailCompletes` начинает с waits, которые подъём записал для старта (`await t` и `t.Wait()` в A), и `TailFollows` поднимается от тела
к вызывающим, ставя wait вызывающего за задачей тела на место waits тела. Trace уровней (зонд, не сдаётся) на вершине:

| Уровень | Waits | Что проверено | Итог |
|---|---|---|---|
| A | `t.Wait()`, `await t` | suspensions A только через waits | вверх по свежему join |
| B | `await a` | suspensions B только через `await a` (других нет) | вверх по свежему join, `faultCompletion` как пришёл |
| C | `await b` | `Yield` в `catch` — через исключительный выход `await b`, который читается после работы | tail — свой у C: упорядочено |

У B обычные выходы проходят `await a`, а исключительные нет: в потоке `throw` до `await a` выходит из вызова `B()` исключением
мимо `try` в C. Во втором входе мимо `await a` идёт уже обычный выход B. Ни того, ни другого `TailFollows` не спрашивала — проверяла
только suspensions тела, а единственная suspension B, `await a`, сама wait.

**Правка.** На уровне с waits `TailFollows` после проверки suspensions читает выходы тела, как `Climb` читает их для рамки (`bodyWaits`
и `faultFollows`): каждый обычный выход проходит waits, и, при `faultCompletion`, каждый выход, исключительные включая. Три ветви,
где wait вызывающего встаёт на место waits тела — свежий join tail-а, `await` вызова сразу, wait допущенного вызова, — берут его
только там, где проходит обычный выход, и несут выше исключительную половину как `faultCompletion` уровня выше. Уровень без waits
(работа готова, когда вызов возвращает) гарантирует обе. Проверка «tail — этого тела» и ветвь тела, вошедшего целиком, не тронуты:
вторая читает выходы такого тела в потоке вызывающего сама.

**Фикстуры.** Зонды `Probe503` и `Probe503b` (не сдаются), ранний случай и контроль, где B покидает тело только после `await a`:

| Вход | Вершина `47f97194` | С правкой |
|---|---|---|
| Форма issue: `var b = B(); try { await b; } catch { Yield; After }` | PassCompletion, пары нет | не упорядочена, пара |
| B делает `return` до `await a`; `var b = B(); await b; Yield; After` | PassCompletion, пары нет | не упорядочена, пара |
| `try { await B(); } catch { Yield; After }` | PassCompletion, пары нет | не упорядочена, пара |
| B делает `return`; `await B(); Yield; After` | PassCompletion, пары нет | не упорядочена, пара |
| Средний — делегат допущенного вызова `Runs.Lib.Run`, `throw` или `return` | не упорядочена, пара | то же |

Контроли первых четырёх форм упорядочены completion proof без assumptions — и на вершине, и с правкой. Где вызов `B()` стоит в `try`
вызывающего (`await B()` сразу), ранний `throw` в потоке идёт в `catch` и дальше обычным путём, и его ловит обычная половина; форма
с `return` — тоже только она. У форм с допущенным вызовом ранний случай верен и без правки, а контроль даёт пару из-за второго
execution instance `First` в исполнении run — это не #503.

**Тесты.** `BlockingWaitAssumptionMatrixTests.Three_helpers_the_middle_one_leaving_before_its_await_keep_the_pair`: `kept` (форма
issue) и `at-once` × `throw` и `return` × ранний случай и контроль, ветвь, assumptions и пара; второй вход — строки с `return`. Тест #498
`A_catch_after_an_optional_blocking_join_of_work_leaving_its_child_keeps_the_pair` мог пройти вхолостую: для записи сразу в `catch`
он теперь требует ровно один access `After` в `ROOT` и ровно один в async-продолжении, каждый со своей ветвью и assumptions, для
записи после `Yield` — что единственный access `After` в продолжении.

**Мутации** на итоговом коде и тестах (четыре класса completion proof, 1 601 тест, 25.11 07:18–07:28), каждая возвращает одно
место; красный всегда ранний случай на ветви, контроли не краснеют ни разу:

| Мутация | Что возвращает | Красные |
|---|---|---|
| M-G | всю правку | 4: все ранние |
| M-H | исключительную половину | 1: форма issue |
| M-I | обычную половину | 2: формы с `return` |
| M-J1 | ветвь свежего join | 2: `kept` |
| M-J2 | ветвь `await` сразу | 2: `at-once` |
| M-J3 | ветвь допущенного вызова | 0 — вход, где она решает, не найден |

**Гейты** по правилу 23.11, один раз на коде поверх `47f97194` (`src` как у `5ac61677`, где сверху только запись рана E-scale),
под corpus-lock 25.11 07:28–07:43: findings, groups, pairs и coverage demo и eShop побайтно те же, что на `47f97194`;
`check-test-baseline` со snapshot eShop без `CH_ACCESS_WORKERS` — Cli 178/178, Core 17 537 passed, 39 skipped, 0 failed, каждый
случай baseline проходит; `record-demo-models -Check` и `check-docs` — выход 0.

**Не проверено.** Вход, где решает ветвь допущенного вызова. Большие корпуса. Demo-случай по SPEC §12.3 для формы #498 — следующим
шагом, отдельно.
