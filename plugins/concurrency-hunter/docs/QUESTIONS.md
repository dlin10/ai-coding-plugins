# Вопросы: Concurrency Hunter

| Поле | Значение |
|---|---|
| План | [PLAN](PLAN.md) |
| Каталог demo | [`demo/SCENARIOS.md`](../demo/SCENARIOS.md) |

Решаются в плане указанной фазы до написания кейса.

Закрытый вопрос переносит решение в SPEC, выбор между альтернативами с последствиями — в ADR, смысл термина — в `CONTEXT.md`, правило корпуса — в правила каталога demo; от строки остаётся ссылка на это место: «Закрыт → SPEC <раздел>» или «Закрыт → ADR <номер>». Номера не переиспользуются и не меняются: на них ссылаются код, тесты, evals и документы. Закрытая строка, чьё решение ещё не записано туда целиком, несёт его сама, пока его не перенесут.

Открытые вопросы перенесены в GitHub Issues ([проект Concurrency Hunter](https://github.com/users/dlin10/projects/3)), и от строки осталось «Перенесён → #N»: номер вопроса по-прежнему ведёт к нему, а текст, кейсы и решение живут в issue. Новых строк таблица не принимает: новый вопрос заводится issue.

| # | Вопрос | Кейсы | Фаза |
|---|---|---|---|
| 1 | Закрыт → SPEC 12.2, TD-043; ADR 0010. | selector-кейсы, `dictionary-disjoint-keys-structural`, `static-list-add` | 4 |
| 2 | Закрыт → ADR 0010; SPEC TD-070, TD-052. | `static-list-add`, `dictionary-disjoint-keys-structural` | 4 |
| 3 | Закрыт → SPEC TD-065; ADR 0008. | `continue-with` | 3 |
| 4 | Закрыт → SPEC TD-085; ADR 0010. | `concurrent-dictionary-enumerate-while-mutate` | 4 |
| 5 | Закрыт → SPEC TD-071, TD-072, TD-082, раздел 7; CONTEXT.md, «Atomic operation». | `interlocked-mixed-with-plain-write` | 4 |
| 6 | Закрыт → ADR 0009; SPEC TD-086. | `custom-async-lock-releaser` | 4 |
| 7 | Закрыт → CONTEXT.md, «Channel<T>»; SPEC TD-086. | `channel-handoff` | 5b |
| 8 | Закрыт → ADR 0006; SPEC TD-062b; thread-local ownership — PLAN, временные границы (5g). | `static-constructor-initialization` и кейсы «Фаза 2 — конструирование»; thread-local кейсы в разделе 5g | 5g |
| 9 | Закрыт → SPEC TD-034c; CONTEXT.md, «Repository root»; SCENARIOS, правило 7. | `project-model-overrides-built-in`, `suppress-file-fingerprint` | 5c |
| 10 | Закрыт → SPEC TD-092, поле одного объекта на процесс. | `mutually-exclusive-paths`, `unsupported-guard-kept` | 4 |
| 11 | Закрыт → SCENARIOS, «Колонка «До фазы»». | все ⚠ | план каждой фазы |
| 12 | Закрыт → CONTEXT.md, «Unknown effect»; SPEC 3, «Unknown-call model»; TD-025. | `unknown-call-model-not-noop` | 5b |
| 13 | Перенесён → [#281](https://github.com/dlin10/ai-coding-plugins/issues/281). | | |
| 14 | Перенесён → [#282](https://github.com/dlin10/ai-coding-plugins/issues/282). | | |
| 15 | Закрыт → SPEC TD-067, handle, переданный параметром. | `callee-joins-on-all-paths`, `callee-joins-on-a-parameter` | 4 |
| 16 | Закрыт → SPEC TD-034, TD-039, TD-108. | `opaque-task-source`, `mixed-source-timer` | 5b |
| 17 | Закрыт → SPEC TD-090, TD-092. | `index-guard-at-call-site`, `index-from-field-guarded`, `constant-argument-cells` | 4b |
| 18 | Закрыт → SPEC TD-012, TD-043. | `custom-slice-offsets`, `ref-local-write`, `ref-return-write`, `out-and-ref-arguments` | 4b |
| 19 | Закрыт → SPEC TD-060a, TD-060b, 4.9; ADR 0011; ADR 0009. | `iterator-created-under-lock`, `iterator-enumerated-under-lock`, `iterator-escapes-to-field`, `iterator-held-monitor` | 4b |
| 20 | Перенесён → [#283](https://github.com/dlin10/ai-coding-plugins/issues/283). | | |
| 21 | Перенесён → [#284](https://github.com/dlin10/ai-coding-plugins/issues/284). | | |
| 22 | Закрыт → SPEC TD-060a, TD-086. | `async-tail-acquisition` | 4b |
| 23 | Закрыт → SPEC TD-034, `out` вызова и создания без тела. | без кейса demo: `OpaqueLeftoverTests` | 5b |
| 24 | Закрыт → SPEC TD-060b, побег через делегат в поле; ADR 0011. | без кейса demo: `OpaqueLeftoverTests` | 5b |
| 25 | Закрыт → SPEC 7. | `ref-local-split-read-modify-write` | 5b |
| 26 | Закрыт → SPEC TD-034a, держатель коллекции-аргумента. | без кейса demo: `KnownCallShapeTests` | 5a |
| 27 | Закрыт → SPEC TD-034a, `elements(v)`. Последовательность из метаданных вне ADR 0010 — вопрос 28. | без кейса demo: `ArgumentWriteTests` | 5a |
| 28 | Закрыт → ADR 0010; SPEC TD-070; SPEC 3, «Unknown-call model». | без кейса demo: `CollectionTableExtensionTests` | 5b |
| 29 | Закрыт → SPEC TD-034a; ADR 0012; ADR 0010. | `linq-take-keeps-elements`; без кейса demo: `LibrarySequenceTests`, `LinqBuiltInResultTests` | 5c |
| 30 | Закрыт → ADR 0010; SPEC TD-052, TD-070, TD-085. | `list-element-field-write`, `foreach-over-list-element-write`, `dictionary-pair-key-and-value` | 5b |
| 31 | Закрыт → ADR 0010; SPEC TD-034, TD-070. | `interface-count-reads-only`, `interface-add-and-count` | 5b |
| 32 | Закрыт → ADR 0010; SPEC TD-070. | `interface-add-and-count`, `array-element-increment`, `dictionary-indexer-compound`, `property-accessor-increment` | 5b |
| 33 | Закрыт → SPEC TD-012. | `array-element-increment`, `dictionary-indexer-compound`, `property-accessor-increment` | 5b |
| 34 | Закрыт → SPEC TD-070: прямой доступ к элементу массива, пришедшего не прямым путём; SPEC TD-034b, «Эффекты»: `writes-cells` для массива аргумента, каким бы путём он ни дошёл. | `CollectionRouteMatrixTests`, `GenerationCauseTests`, `ReferenceAccessTests` | 5g |
| 35 | Закрыт → SPEC TD-034a, члены формы массива. | `array-element-increment`, `array-length-loop-bound` | 5c |
| 36 | Закрыт → SPEC TD-023; SPEC 6. | `late-receiver-base-call`; `LateReceiverTests` | 5d run A1 |
| 37 | Закрыт → SPEC TD-012, присваивание цели с ref-возвратом. | `ref-call-target-assignment` | 5c |
| 38 | Закрыт → SPEC 4.10, patterns и проверка точного типа. | `pattern-binds-tested-object`, `deconstruct-calls-source-method`, `type-test-fast-path`; `PatternLoweringTests`, `PatternImpliedCallTests`, `DeconstructionCallTests`, `TypeSafetyTests` | 5d run A1 |
| 39 | Закрыт → SPEC 4.10, интерфейсный вызов; TD-060b; ADR 0011. | `interface-call-reaches-override`, `enumerator-method-runs-at-movenext`, `explicit-enumerator-runs-body`; `InterfaceOverrideDispatchTests`, `ExplicitEnumerationTests` | 5d run A1 |
| 40 | Перенесён → [#285](https://github.com/dlin10/ai-coding-plugins/issues/285). | | |
| 41 | Закрыт → SPEC TD-034b, implementation-сборка. | без кейса demo: `ImplementationAssemblyTests`, model evals | 5d run B1 |
| 42 | Перенесён → [#262](https://github.com/dlin10/ai-coding-plugins/issues/262). | | |
| 43 | Закрыт → SPEC TD-034a, `keeps`, `kept:K` и результат `new`; TD-052. | `deserialize-returns-new-object`, `project-model-kept-object`, `project-model-try-get-value`; `NewResultTests`, `KeptObjectTests` | 5d run A2 |
| 44 | Закрыт → SPEC TD-034a, грамматика declaration id. | без кейса demo: `DeclarationIdGrammarTests` | 5c |
| 45 | Перенесён → [#286](https://github.com/dlin10/ai-coding-plugins/issues/286). | | |
| 46 | Закрыт → SPEC TD-034a, `writes-cells` и `stores`; TD-070. | `array-sort-writes-cells`; `ArrayCellWriterFamilyTests`, `ModelCellWriteTests` | 5d run A2 |
| 47 | Закрыт → SPEC TD-034a, `calls`; SPEC TD-034b: вывод `calls` из свидетеля. Встроенные ручные семейства LINQ и коллекций ADR 0010 `calls` пока не несут. | `ModelCallTests`, `CellVocabularyTests`, `ModelEntryWriterTests`, `GenerationCauseTests` | до генерации CoreLib |
| 48 | Перенесён → [#287](https://github.com/dlin10/ai-coding-plugins/issues/287). | | |
| 49 | Перенесён → [#288](https://github.com/dlin10/ai-coding-plugins/issues/288). | | |
| 50 | Перенесён → [#289](https://github.com/dlin10/ai-coding-plugins/issues/289). | | |
| 51 | Перенесён → [#290](https://github.com/dlin10/ai-coding-plugins/issues/290). | | |
| 52 | Закрыт → SPEC TD-034b (`unnamed-member`), TD-034c (цели генерации в ране). | `DeclarationIdGrammarTests`, `FunctionPointerMatrixTests`, `GenerateCommandTests` | 5d run B3 |
| 53 | Перенесён → [#291](https://github.com/dlin10/ai-coding-plugins/issues/291). | | |
| 54 | Перенесён → [#292](https://github.com/dlin10/ai-coding-plugins/issues/292). | | |
| 55 | Перенесён → [#293](https://github.com/dlin10/ai-coding-plugins/issues/293). | | |
| 56 | Перенесён → [#294](https://github.com/dlin10/ai-coding-plugins/issues/294). | | |
| 57 | Перенесён → [#295](https://github.com/dlin10/ai-coding-plugins/issues/295). | | |
| 58 | Перенесён → [#296](https://github.com/dlin10/ai-coding-plugins/issues/296). | | |
| 59 | Перенесён → [#297](https://github.com/dlin10/ai-coding-plugins/issues/297). | | |
| 60 | Перенесён → [#298](https://github.com/dlin10/ai-coding-plugins/issues/298). | | |
| 61 | Перенесён → [#299](https://github.com/dlin10/ai-coding-plugins/issues/299). | | |
| 62 | Закрыт → SPEC TD-012, события; SPEC 3, «Unknown-call model»; ADR 0014. | `field-like-event-raised-by-worker`, `field-like-event-raised-in-request` | 5d run B2b |
| 63 | Перенесён → [#300](https://github.com/dlin10/ai-coding-plugins/issues/300). | | |
| 64 | Перенесён → [#301](https://github.com/dlin10/ai-coding-plugins/issues/301). | | |
| 65 | Перенесён → [#302](https://github.com/dlin10/ai-coding-plugins/issues/302). | | |
| 66 | Перенесён → [#303](https://github.com/dlin10/ai-coding-plugins/issues/303). | | |
| 67 | Перенесён → [#304](https://github.com/dlin10/ai-coding-plugins/issues/304). | | |
| 68 | Закрыт → SPEC TD-012: присваивание, цель которого Roslyn берёт захватом до значения (`t ??= v`, `x = f ? a : b`, `x = a ?? b`, `x = new T { … }`), пишет цель тем же путём, что полная запись; лямбда дерева выражений отдаёт захваты операндами своего unknown. Исполнение тела дерева выражений — вопрос 154. | `IrLoweringTests`, `ValueProvenanceTests`, `PointsToTests` | 5g |
| 69 | Перенесён → [#305](https://github.com/dlin10/ai-coding-plugins/issues/305). | | |
| 70 | Перенесён → [#306](https://github.com/dlin10/ai-coding-plugins/issues/306). | | |
| 71 | Перенесён → [#307](https://github.com/dlin10/ai-coding-plugins/issues/307). | | |
| 72 | Перенесён → [#308](https://github.com/dlin10/ai-coding-plugins/issues/308). | | |
| 73 | Перенесён → [#309](https://github.com/dlin10/ai-coding-plugins/issues/309). | | |
| 74 | Перенесён → [#310](https://github.com/dlin10/ai-coding-plugins/issues/310). | | |
| 75 | Перенесён → [#311](https://github.com/dlin10/ai-coding-plugins/issues/311). | | |
| 76 | Перенесён → [#312](https://github.com/dlin10/ai-coding-plugins/issues/312). | | |
| 77 | Закрыт → SPEC TD-034a: цель `writes-cells` — массив любого ранга или `System.Array`; на span и коллекции ADR 0010 словарь не расширяется. | `CellVocabularyTests` | до генерации CoreLib |
| 78 | Перенесён → [#313](https://github.com/dlin10/ai-coding-plugins/issues/313). | | |
| 79 | Перенесён → [#314](https://github.com/dlin10/ai-coding-plugins/issues/314). | | |
| 80 | Закрыт → SPEC TD-034a: `reads-deep` на `this` допустим, `writes-arg` на `this` запрещён; SPEC TD-034b: чтение пользовательского объекта, который держит получатель, — `reads-deep` на `this`, запись в него — `receiver-object-write`. | `CellVocabularyTests`, `KeptObjectTests`, `GenerationCauseTests`, `GeneratedEntryTests`, `AccessorEntryMatrixTests` | до генерации CoreLib |
| 81 | Перенесён → [#315](https://github.com/dlin10/ai-coding-plugins/issues/315). | | |
| 82 | Перенесён → [#316](https://github.com/dlin10/ai-coding-plugins/issues/316). | | |
| 83 | Перенесён → [#317](https://github.com/dlin10/ai-coding-plugins/issues/317). | | |
| 84 | Перенесён → [#318](https://github.com/dlin10/ai-coding-plugins/issues/318). | | |
| 85 | Закрыт → SPEC TD-034a, TD-034b, встроенные модели `System.Private.CoreLib`; ADR 0017, ADR 0019; состав — перепись членов, которые вызывают demo и корпуса, а не каждый член сборки (ADR 0012, поправка 2026-10-17). Построение — PLAN, временные границы (5d run C); граница замыкания, порядок генерации и major-версии — PLAN, раздел 4, п. 14. Его ждут члены эталона судеб `CancellationToken.Register` и `AssemblyName.Name`; в снимке генерации 2026-10-16 оба — `closure-bound` (вопрос 142); после вопросов 144 и 142 оба доходят до анализа, `AssemblyName.Name` — до заместителей культуры (вопрос 178). `CancellationToken.Register` получает ручную встроенную запись (ADR 0012, поправка 2026-10-17). | кейс не написан; model evals, `CoreLibBenchmarkTests` | 5d run C |
| 86 | Закрыт → ADR 0012: генератор читает декомпилированный код, а не исходники библиотеки. | `generator-snapshot.json` | 5d run B3 |
| 87 | Закрыт → SPEC TD-034a, `keeps.result`. | model evals | 5d run B2 |
| 88 | Закрыт → SPEC TD-034b, драйвер; TD-090. | кейс не написан | 5d run B2 |
| 89 | Закрыт → SPEC TD-034b, поля и seeds; ADR 0013. | кейс не написан | 5d run B2 |
| 90 | Закрыт → SPEC TD-034a, аксессоры и судьба `not-run`; TD-034b; ADR 0015. | `AccessorModelIdTests`, `NotRunFateTests`, `AccessorEvalTests` | 5d run B2b |
| 91 | Перенесён → [#263](https://github.com/dlin10/ai-coding-plugins/issues/263). | | |
| 92 | Закрыт → SPEC TD-034b, границы решений. | model evals | 5d run B2 |
| 93 | Закрыт → SPEC TD-012, ref-локации без предела цепочки. Ограничение сбора accesses в рекурсии — вопрос 107. | `ref-parameter-chain-beyond-depth`, `ref-return-chain-beyond-depth`; `RefChainTests` | 5d run A3 |
| 94 | Закрыт → SPEC TD-034a, `task(…)` и `completion(…)`; TD-034b; CONTEXT.md, «Completion value». | `TaskResultVocabularyMatrixTests`, `TaskResultRunMatrixTests`, `CompletionObservationMatrixTests`, model evals | 5d run A4 |
| 95 | Перенесён → [#264](https://github.com/dlin10/ai-coding-plugins/issues/264). | | |
| 96 | Закрыт → SPEC TD-034b, подтверждение держателя и происхождение; TD-034a, `holder-arg:N`. | кейс не написан; model evals | 5d run B2 |
| 97 | Перенесён → [#319](https://github.com/dlin10/ai-coding-plugins/issues/319). | | |
| 98 | Закрыт → SPEC TD-034b, свидетель члена получателя: исполнение override `Sub_Recv` вне setup — `unknown-touch` (`receiver-member-unseen`), кроме геттера, `ToString`, `Equals`, `GetHashCode` и членов сравнения; точного получателя словарь не описывает. Судьбы `FindFirst`, `Execute` и `ExecuteAsync` остаются `unknown-execution`, шире эталона. | `GenerationCauseTests`, `EffectReaderTests`, model evals | до генерации CoreLib |
| 99 | Перенесён → [#320](https://github.com/dlin10/ai-coding-plugins/issues/320). | | |
| 100 | Перенесён → [#321](https://github.com/dlin10/ai-coding-plugins/issues/321). | | |
| 101 | Перенесён → [#265](https://github.com/dlin10/ai-coding-plugins/issues/265). | | |
| 102 | Перенесён → [#322](https://github.com/dlin10/ai-coding-plugins/issues/322). | | |
| 103 | Закрыт → SPEC TD-034b, границы решений; CONTEXT.md, «Completion value». | `FateMatrixTests`, `TaskProducerTests` | 5d run A4 |
| 104 | Закрыт → SPEC TD-034b, `iterator` через задачу; CONTEXT.md, «Completion value». | `FateMatrixTests`, `CompletionValueHeapTests` | 5d run A4 |
| 105 | Закрыт → SPEC 4.7, completion value после TD-067; CONTEXT.md, «Completion value». Остаток — вопрос 133. | `awaited-result-shared`, `task-run-result-shared`, `completion-source-handoff`, `awaited-fresh-result`, `CompletionValueMatrixTests` | 5d run A4 |
| 106 | Закрыт → SPEC TD-034b, компиляция библиотеки; ADR 0023. | `LibraryCompilationTests` | 5d run B3b |
| 107 | Закрыт → SPEC TD-090: путь входит снова в instance, который уже стоит на пути, при новой привязке ссылок и коллекций. | `RefChainTests` | будущий ран движка |
| 108 | Закрыт → ADR 0017; SPEC TD-062b. Замеры — PLAN, раздел 4, п. 12. | кейс не написан; `CoreLibBenchmarkTests` | 5d run A5 |
| 109 | Закрыт → SPEC 6, шаг 9: сбор accesses; SPEC 9.7, бюджет и счётчики стадии. Замеры — `skills/hunt/evals/metrics/corelib-bench.json`, точка `accesses`. | `CoreLibBenchmarkTests`, `EngineCancellationTests`, `BenchmarkWatchdogTests` | 5d run B3 |
| 110 | Закрыт → ADR 0016; SPEC 6, шаг 4. | без кейса demo: `HeapFreezeTests`, `WorklistTests` | 5d |
| 111 | Перенесён → [#323](https://github.com/dlin10/ai-coding-plugins/issues/323). | | |
| 112 | Перенесён → [#324](https://github.com/dlin10/ai-coding-plugins/issues/324). | | |
| 113 | Перенесён → [#325](https://github.com/dlin10/ai-coding-plugins/issues/325). | | |
| 114 | Перенесён → [#266](https://github.com/dlin10/ai-coding-plugins/issues/266). | | |
| 115 | Закрыт → SPEC TD-034b, компиляция библиотеки, 12.1 п. 8a; ADR 0023. | `EffectsEvalTests`, `LibraryCompilationTests`; остаток CS0104 — вопрос 182 | 5d run B3b |
| 116 | Закрыт → SPEC TD-034b, драйвер: кандидатура по результату. | `CandidacyMatrixTests`, `LinqOracleTests` | 5d run B3c |
| 117 | Закрыт → SPEC TD-034b, драйвер: `IQueryable<T>`, `Expression<TDelegate>` и свидетели generic-методов. | `GenericMethodWitnessMatrixTests`, `DriverSynthesisTests`, `LinqOracleTests` | 5d run B3c |
| 118 | Закрыт → SPEC TD-034b, происхождение: `outputs` присвоенного `ref`. | GeneratedEntryMatrixTests, ValueProvenanceTests | 5d run B2 |
| 119 | Закрыт → SPEC TD-034b, происхождение: свежая коллекция-результат. | GeneratedEntryMatrixTests, ValueProvenanceTests | 5d run B4 |
| 120 | Перенесён → [#326](https://github.com/dlin10/ai-coding-plugins/issues/326). | | |
| 121 | Закрыт → SPEC TD-034b, удержание: объекты, созданные вызовом. | GeneratedEntryMatrixTests, ValueProvenanceTests | 5d run B4 |
| 122 | Закрыт → SPEC TD-034b, происхождение. | GeneratedEntryMatrixTests (клетка `Probe/StoreInArrayArg/TaskRun` точна и вне `Wider` с коммита `2c75b85`) | 5d run B4 |
| 123 | Закрыт → SPEC TD-034b, драйвер: значение конкретной коллекции ADR 0010. | CollectionValueMatrixTests, ParameterKindsTests, GeneratedEntryTests | 5d run B4 |
| 124 | Закрыт → SPEC TD-034b, кандидаты: параметр `Task<T>`. | ParameterKinds, ModelGenerator; `ParameterKindsTests`, `CandidacyTests`, `GeneratedEntryTests` | 5d run A4 |
| 125 | Перенесён → [#327](https://github.com/dlin10/ai-coding-plugins/issues/327). | | |
| 126 | Закрыт → SPEC TD-034b, драйвер: init-аксессор на подготовленном получателе и CS8852. | `AccessorDriverTests`, `IrLoweringTests`, `AutoAccessorGenerationMatrixTests` | 5d run B3c |
| 127 | Перенесён → [#328](https://github.com/dlin10/ai-coding-plugins/issues/328). | | |
| 128 | Перенесён → [#329](https://github.com/dlin10/ai-coding-plugins/issues/329). | | |
| 129 | Закрыт → SPEC TD-034b, удержание: одна `this` одного прогона тела. | `AccessorEvalTests.BackgroundWorker_event_accessors_match_their_gold`, `GeneratedEntryTests` | 5d run B4 |
| 130 | Закрыт → SPEC TD-034b, компиляция библиотеки, 12.1 п. 8a; ADR 0023. | `AccessorEvalTests`, `LibraryCompilationTests` | 5d run B3b |
| 131 | Закрыт → SPEC TD-034b, держатель через задачу; ADR 0015. | `FateMatrixTests` | 5d run A4 |
| 132 | Закрыт → SPEC TD-034b, эффекты: собственный путь доступа к seed-у получателя. | `AccessorEntryMatrixTests`, `ReceiverSeedAccessMatrixTests`, `GeneratedEntryTests` | 5d run B4 |
| 133 | Перенесён → [#330](https://github.com/dlin10/ai-coding-plugins/issues/330). | | |
| 134 | Перенесён → [#331](https://github.com/dlin10/ai-coding-plugins/issues/331). | | |
| 135 | Перенесён → [#332](https://github.com/dlin10/ai-coding-plugins/issues/332). | | |
| 136 | Перенесён → [#333](https://github.com/dlin10/ai-coding-plugins/issues/333). | | |
| 137 | Закрыт → SPEC TD-034b, «Происхождение»: чтение хранилища наблюдаемо, только когда наблюдаемо каждое значение, записанное в читаемый слот. | `DelegateReturnObservationMatrixTests`, `ValueProvenanceTests`, `CompletionProducerFormTests` | 5g |
| 138 | Перенесён → [#334](https://github.com/dlin10/ai-coding-plugins/issues/334). | | |
| 139 | Закрыт → ADR 0018; SPEC TD-062b и TD-090. Замеры — PLAN, раздел 4, п. 12. | `InterproceduralAccessTests.Write_to_an_object_from_a_construction_it_starts_is_construction_local`; `CoreLibBenchmarkTests` | 5d run A6 |
| 140 | Закрыт → ADR 0019; SPEC TD-034b. | `CoreLibGenerationSetMatrixTests`, `CoreLibGenerationEvalTests` | 5d run A7 |
| 141 | Перенесён → [#335](https://github.com/dlin10/ai-coding-plugins/issues/335). | | |
| 142 | Закрыт → ADR 0019; SPEC TD-008, TD-034b (диагностика в генерации CoreLib выключена; загрузки прохода, после которого меняются пробы, не засеваются; неизменяемый объект под пробой и скрытое состояние библиотеки — не касание). Ядро globalization остаётся за границей замыкания; заместители культуры в драйвере — вопрос 178. | `CoreLibDiagnosticsOffTests`, `GenerationCauseTests`, model evals (`NullableComparer`) | после генерации CoreLib |
| 143 | Закрыт → ADR 0019; SPEC TD-008, TD-034b. | `ReachabilityRuleSelectionTests`, `PackageReachabilityTests`, `LibraryStaticHandoffMatrixTests` | 5d run B4b |
| 144 | Закрыт → ADR 0019; SPEC TD-034b; поправка ADR 0010 (член распознанного типа вне таблицы). | `CoreLibRecognizedTypeTests`, `RecognizedTypeDefaultTests` | после генерации CoreLib |
| 145 | Закрыт → SPEC 3, «Unknown-call model»; CONTEXT.md, «Opaque call». | `BodylessWorkTests`, `CoreLibGenerationSetMatrixTests`, `spawn-bodyless-work` | 5d run A8 |
| 146 | Перенесён → [#267](https://github.com/dlin10/ai-coding-plugins/issues/267). | | |
| 147 | Закрыт → SPEC TD-034b, свидетель: значение каждого `out` строится рецептом, как возврат. | `WitnessTests`, `GenerationCauseTests` | до генерации CoreLib |
| 148 | Закрыт → SPEC TD-034b, компиляция библиотеки: тела `Unsafe` становятся `extern`, и адрес, переданный `Unsafe.AsPointer`, уходит невидимому коду. | `GenerationCauseTests`, `LibraryCompilationTests`, `GeneratedFateTests`, `GeneratedEntryTests` | до генерации CoreLib |
| 149 | Закрыт → SPEC TD-034b, «Драйвер»: член, которого объявляет только открытая копия, — `member-not-found`; список генерации CoreLib читает доступность по неоткрытой сборке. | `CoreLibGenerationTests`, `AccessorDriverTests` | до генерации CoreLib |
| 150 | Закрыт → SPEC TD-034b, `argument-member-unseen`. Эффект таблицы ADR 0010 вместо отказа — вопрос 151. | `GenerationCauseTests`, `WitnessTests`, `GeneratedEntryMatrixTests` | до генерации CoreLib |
| 151 | Перенесён → [#336](https://github.com/dlin10/ai-coding-plugins/issues/336). | | |
| 152 | Закрыт → SPEC TD-034b, «Происхождение»: результату типа, который объекта не держит, имя не нужно, и запись его не описывает. | `ValueProvenanceTests` | до генерации CoreLib |
| 153 | Закрыт → SPEC TD-012: чтение своей переменной, которую присваивает вложенное тело, из ячейки захвата; SPEC TD-034b, «Происхождение»: `result-unresolved`. | `IrLoweringTests`, `ValueProvenanceTests`, `PointsToTests` | до генерации CoreLib |
| 154 | Перенесён → [#337](https://github.com/dlin10/ai-coding-plugins/issues/337). | | |
| 155 | Закрыт → SPEC TD-030: вызов члена, который объявляет struct, — direct call, а не dispatch. Делегат на метод struct без объекта остаётся dispatch без получателя (`no-receiver-object`). | `InterfaceOverrideDispatchTests`, `GenerationCauseTests` | до генерации CoreLib |
| 156 | Закрыт → SPEC TD-034b, «Прогон движка»: прогон, в котором вызов драйвера не исполнил тело самого члена, — `analysis-failed`. | `GenerationCauseTests` | до генерации CoreLib |
| 157 | Закрыт → SPEC TD-030: пользовательский оператор — бинарный, унарный, преобразование, составное присваивание, `++` и `--` — это вызов его метода. | `InterfaceOverrideDispatchTests` | до генерации CoreLib |
| 158 | Закрыт → SPEC TD-034b, «Полнота и причины модели»: касание инициализатором типа статики собственного типа — не касание пробы в чужом исполнении. | `GenerationCauseTests` | до генерации CoreLib |
| 159 | Закрыт → SPEC TD-034b, «Драйвер» (проба-struct `Probe_<T>` для `where T : struct`) и «Поля и seeds» (struct-входы засеваются вдоль своих путей). | `StructSeedTests`, `ProbeStructTests`, `ProbeStructWitnessMatrixTests`, `GenerationCauseTests` | 5d run A9 |
| 160 | Закрыт → SPEC TD-012 и TD-034b, «Полнота и причины модели»: ссылка, которую возвращает вызов без тела, указывает на место, которое ничто не доказывает; аргумент-ссылка такого вызова передаёт объект, в котором лежит её хранилище. | `GenerationCauseTests`, `EShopSnapshotTests`, `MetricsCommandTests` | до генерации CoreLib |
| 161 | Закрыт → SPEC TD-034b, «Полнота и причины модели» и «Эффекты»: передача изменяемого входа вызову без тела или операции, которую lowering не выражает, — касание; исключения перечислены там. | `GenerationCauseTests`, `EffectReaderTests`, `GeneratedEntryTests`, `GeneratedEntryMatrixTests`, `AccessorEntryMatrixTests` | до генерации CoreLib |
| 162 | Перенесён → [#338](https://github.com/dlin10/ai-coding-plugins/issues/338). | | |
| 163 | Перенесён → [#339](https://github.com/dlin10/ai-coding-plugins/issues/339). | | |
| 164 | Перенесён → [#340](https://github.com/dlin10/ai-coding-plugins/issues/340). | | |
| 165 | Закрыт → SPEC TD-012, TD-030, TD-041, TD-042, TD-043, TD-062b, TD-124, TD-034b («Драйвер», «Поля и seeds», «Эффекты»), 5, 8.1, 8.2, 12.2; ADR 0020. Остаток: ссылка в struct-хранилище, которую возвращает член библиотеки, и запись в хранилище вызывающего через `ref`-, `out`- или `in`-параметр модели — вопросы 162 и 164; локали, которые поднимает async-метод или итератор, — только через захват; `fixed`-буферы, указатели и `unsafe`; join через задачу в поле struct не доказывается; результат модели `new` в поле struct ничего не держит (TD-034a). | `struct-field-of-singleton`, `struct-in-array-cell`, `static-struct-field`, `nested-struct-field`, `struct-mutating-method`, `struct-through-reference`, `struct-torn-write`, `struct-copy-not-shared`, `readonly-struct-defensive-copy`, `captured-struct`, `captured-struct-copied`; `StructPlaceLoweringTests`, `StructStoragePointsToTests`, `NullableBoxingTests`, `StructAccessTests`, `PathOverlapMatrixTests`, `StructPlaceMatrixTests`, `UnresolvedBaseCoverageTests`, `GeneratedStructEffectMatrixTests` | 5d run A9 |
| 166 | Закрыт → SPEC TD-012, TD-050, TD-051, TD-053, TD-060, TD-065, TD-070, 8.1, 8.2, 12.2; ADR 0020. Вердикт `escape-via-captured-closure/note-cell` — каталог demo. | `captured-counter`, `captured-struct`, `captured-struct-copied`, `capture-per-invocation`, `captured-counter-one-task`, `spawned-work-own-object`, `spawned-work-own-object-twice`, `spawned-work-in-a-loop`, `spawned-work-object-published`, `escape-via-captured-closure/note-cell`; `CaptureCellTests`, `TreeConfinedTests`, `OncePerCreatorRunMatrixTests`, `TreeConfinedMatrixTests` | 5d run A9 |
| 167 | Закрыт → SPEC TD-041, TD-034b («Эффекты»), 12.1 п. 8a. | `struct-cell-written-by-ref-foreach`, `struct-boxed-by-pattern`, `struct-deconstructed-in-place`, `struct-deconstructed-through-interface`, `pair-value-read-from-field`, `struct-with-copies-captured-variable`, `struct-copied-through-type-parameter`; `StructPlaceMatrixTests`, `StructPlaceMatrixCellTests`, `StructAccessTests`, `GeneratedStructEffectMatrixTests` | 5d run A10 |
| 168 | Закрыт → SPEC TD-070; CONTEXT.md, «Capture cell». | `lambda-variable-captured-by-local-function`; `CaptureOwnerMatrixTests` | 5d run A10 |
| 169 | Закрыт → SPEC TD-012. | `RefVariableVersionTests`, `IrConstructionTests` | 5d run A10 |
| 170 | Закрыт → SPEC TD-124. | без кейса demo: `UnresolvedBaseCoverageTests`, `CreatedArgumentArrayMatrixTests` | 5d run A10 |
| 171 | Закрыт → SPEC TD-012. | `captured-target-evaluated-before-value`; `CapturedTargetOrderMatrixTests` | 5d run A10 |
| 172 | Закрыт → SPEC TD-041, TD-034a, TD-034b; CONTEXT.md, «Unknown effect». | `nullable-filled-by-unseen-write`; `NullableUnseenWriteMatrixTests` | 5d run A10 |
| 173 | Закрыт → ADR 0021; SPEC TD-093, TD-094, 10.5. | `SolverResourceLimitTests`, `SolverTests`, `DemoExpectationTests` | 5d run A10 |
| 174 | Закрыт → SPEC TD-034b («Драйвер», «Прогон движка»), TD-041; ADR 0019. | без кейса demo: `DefaultComparerSelectionMatrixTests`, `ComparisonWitnessMatrixTests`, `ConstructedValueTypeMatrixTests`, `NullableHeldValueMatrixTests`, `CoreLibGenerationEvalTests` | 5d run A10 |
| 175 | Перенесён → [#341](https://github.com/dlin10/ai-coding-plugins/issues/341). | | |
| 176 | Перенесён → [#342](https://github.com/dlin10/ai-coding-plugins/issues/342). | | |
| 177 | Перенесён → [#343](https://github.com/dlin10/ai-coding-plugins/issues/343). | | |
| 178 | Перенесён → [#344](https://github.com/dlin10/ai-coding-plugins/issues/344). | | |
| 179 | Перенесён → [#268](https://github.com/dlin10/ai-coding-plugins/issues/268). | | |
| 180 | Перенесён → [#345](https://github.com/dlin10/ai-coding-plugins/issues/345). | | |
| 181 | Закрыт → SPEC TD-034b, TD-034c, 6, 10.6, 12.1 п. 10; ADR 0023. | `CancellationCoverageTests`, `WorkCancellationTests`, `StageCancellationTests` | 5d run B3b |
| 182 | Закрыт → SPEC TD-034b, компиляция библиотеки: артефакты CS0104 и CS0051. | `LibraryCompilationTests`, `ModelEvalTests.The_compile_fix_makes_no_compiled_body_extern` | 5d run B4 |
| 183 | Закрыт → SPEC TD-034b; ADR 0028. | `EmittedAssemblyOptionsTests`, `LibraryIdentityTests`, `NonCompilerErrorTests`, `ReferenceFloorTests`, `ReferenceSetMatrixTests`, `UnprovidedReferenceTests`, `ImplementationAssemblyTests`, `LibraryCompileAcceptanceTests` | 5d run L1, закрыт |
| 184 | Закрыт → SPEC TD-034b, драйвер: вызов тела целевого аксессора auto-property. | `AutoAccessorGenerationMatrixTests`, `AccessorEvalTests` | 5d run B3c |
| 185 | Закрыт → SPEC TD-034b, драйвер: отменённая работа не остаётся в исходнике. | `DriverRollbackMatrixTests`, клетки `Seed` в `ValueSourceMatrixTests` | 5d run B4 |
| 186 | Перенесён → [#346](https://github.com/dlin10/ai-coding-plugins/issues/346). | | |
| 187 | Перенесён → [#347](https://github.com/dlin10/ai-coding-plugins/issues/347). | | |
| 188 | Закрыт → ADR 0029. | — | 5d run H1 |
| 189 | Закрыт → SPEC TD-060, join своего прохода; ADR 0008. | `own-pass-join`, `OwnPassJoinTests`, `OwnPassJoinMatrixTests` | 5d run H2a |
| 190 | Перенесён → [#348](https://github.com/dlin10/ai-coding-plugins/issues/348). | | |
| 191 | Перенесён → [#349](https://github.com/dlin10/ai-coding-plugins/issues/349). | | |
| 192 | Закрыт → SPEC TD-034c, «Бюджет генерации», предел записи; ADR 0031. | — | Tooling, [#244](https://github.com/dlin10/ai-coding-plugins/issues/244) |
| 193 | Перенесён → [#270](https://github.com/dlin10/ai-coding-plugins/issues/270). | | |
| 194 | Перенесён → [#248](https://github.com/dlin10/ai-coding-plugins/issues/248). | | |
| 195 | Перенесён → [#350](https://github.com/dlin10/ai-coding-plugins/issues/350). | | |
| 196 | Перенесён → [#351](https://github.com/dlin10/ai-coding-plugins/issues/351). | | |
| 197 | Перенесён → [#352](https://github.com/dlin10/ai-coding-plugins/issues/352). | | |
| 198 | Перенесён → [#353](https://github.com/dlin10/ai-coding-plugins/issues/353). | | |
| 199 | Перенесён → [#354](https://github.com/dlin10/ai-coding-plugins/issues/354). | | |
| 200 | Перенесён → [#355](https://github.com/dlin10/ai-coding-plugins/issues/355). | | |
| 201 | Перенесён → [#356](https://github.com/dlin10/ai-coding-plugins/issues/356). | | |
| 202 | Перенесён → [#357](https://github.com/dlin10/ai-coding-plugins/issues/357). | | |
| 203 | Перенесён → [#358](https://github.com/dlin10/ai-coding-plugins/issues/358). | | |
| 204 | Закрыт → SPEC TD-060; CONTEXT.md, «Join». | `AwaitLeavingWorkMatrixTests`, `PollyRetryAttemptMatrixTests`, `AwaitExitFlowTests`, `BlockingWaitAssumptionMatrixTests.An_await_of_a_task_leaving_before_its_child_join_keeps_the_child_pair_past_the_catch` | 5d run H2b |
| 205 | Перенесён → [#359](https://github.com/dlin10/ai-coding-plugins/issues/359). | | |
| 206 | Перенесён → [#360](https://github.com/dlin10/ai-coding-plugins/issues/360). | | |
| 207 | Перенесён → [#361](https://github.com/dlin10/ai-coding-plugins/issues/361). | | |
| 208 | Перенесён → [#362](https://github.com/dlin10/ai-coding-plugins/issues/362). | | |
| 209 | Перенесён → [#363](https://github.com/dlin10/ai-coding-plugins/issues/363). | | |
| 210 | Закрыт → SPEC TD-034a, TD-065, TD-060; ADR 0008; CONTEXT.md, «Site pass». | `PerPassAdmissionTests`, `PerPassAdmissionMatrixTests`, `AwaitedRunsMatrixTests`, `AwaitedRunsTests`, `H2EShopAcceptanceTests`, `polly-nested-retry` | 5d run H2c, закрыт |
| 211 | Перенесён → [#364](https://github.com/dlin10/ai-coding-plugins/issues/364). | | |
| 212 | Перенесён → [#365](https://github.com/dlin10/ai-coding-plugins/issues/365). | | |
| 213 | Перенесён → [#366](https://github.com/dlin10/ai-coding-plugins/issues/366). | | |
| 214 | Перенесён → [#367](https://github.com/dlin10/ai-coding-plugins/issues/367). | | |
| 215 | Перенесён → [#368](https://github.com/dlin10/ai-coding-plugins/issues/368). | | |
| 216 | Перенесён → [#369](https://github.com/dlin10/ai-coding-plugins/issues/369). | | |
| 217 | Перенесён → [#370](https://github.com/dlin10/ai-coding-plugins/issues/370). | | |

**Как закрывать остаток.** Четыре раунда код-ревью фазы 4 дали 20, 8, 8 и 7 находок, и около половины каждого раунда были следствиями правок предыдущего: правило вводилось в одном слое и не применялось в соседнем, либо две независимо добавленные фичи фазы не сочетались друг с другом — атомарность на элементе и `Span` не знали друг о друге, пока это не проверили попарно. Мутационный тест доказывает, что правка что-то меняет, и не доказывает, что она не открыла дыру рядом. Поэтому остаток этого хвоста дешевле закрывать систематическим проходом — перечислить все распознаватели, все места, где решает каждое правило, и все пары фич, обязанных сочетаться, — чем новыми раундами ревью.

**CoreLib generation prerequisites.** Что сделать до генерации моделей `System.Private.CoreLib`, которые станут встроенными (5d run C), чтобы потом их не перегенерировать: перегенерацию решает владелец по коммиту последней генерации (ADR 0022), а полная генерация CoreLib идёт около двух часов (`build/generate-corelib.ps1`). Член без модели безопасен — это opaque call с unknown effect, — а модель уже истины прячет доступы, поэтому до генерации идут корректность, формат моделей и объём генерации, а точность добирается после неё ценой перегенерации. Корректность, формат, объём, аудит пустых моделей и struct-хранилище сделаны (до 5d run A10 включительно): решения — в SPEC TD-012, TD-030, TD-034a, TD-034b, TD-041, TD-070 и ADR 0019–0021, сдвиги ответов генератора — в [`runs/`](runs/). Всё сделано: снимок генерации записан в [`corelib-generation.tsv`](../skills/hunt/evals/models/corelib-generation.tsv) с коммитом, на котором он сгенерирован, и когда перегенерировать CoreLib, решает владелец по этому коммиту ([ADR 0022](adr/0022-the-owner-regenerates-the-built-in-corelib-models-from-a-recorded-commit.md)). Генерация каждого члена снята с run C замером 2026-10-17 ([ADR 0012](adr/0012-library-semantics-are-data-the-analysis-derives-from-decompiled-code.md), поправка): встроенными станут модели переписи членов, которые вызывают demo и корпуса, а снимок каждого члена остаётся мерой того, что сдвинуло изменение генератора или движка.

После генерации, ценой перегенерации, можно добирать точность — модели станут полнее, но не неверными; вопрос решают, когда в него упирается член переписи, нужный demo или корпусу (ADR 0012, поправка 2026-10-17): вопросы 40, 55, 57, 58, 70 (`ValueTuple.Create` от 2 до 7 аргументов: в снимке 2026-10-16 — `unknown-touch` с `probe-in-unsupported-operation`; `Create` от 1 и 8 там тоже без модели — `vocabulary`, `result-several-objects`), 97, 99, 111, 112 (span из `out`), 113, 114 (абстрактные члены, `String.Join` и `String.Split`), 119, 121, 132, 141, 178, 120 и 122 (к CoreLib применимы с закрытием вопроса 144), 146, 151, 162, 163 и 176. Вопрос 164 отложен сюда же, хотя его 12 моделей `{}` уже истины: решено 2026-10-12. Вопрос 50, вероятно, уже исправлен в 5d run A8: закрыть после проверки примеров строки. На генерацию CoreLib не влияют — это потребители моделей, другие пакеты и пользовательский код: 13, 14, 20, 21, 42, 45, 48, 49, 51–54, 56, 59–61, 65–67, 71–76, 78, 81–84, 91, 95, 100–102, 123, 125, 127, 129, 133–136, 138, 143 и 154, а также вопросы 111, 112 и 114 в остальных частях. Вопросы 106, 115, 116, 117, 126, 130 и 184 закрыты; остаток компиляции библиотек — вопросы 182 и 183.
