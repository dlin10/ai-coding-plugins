# 5d run B4b

База сравнения — `fb8866273a037a4720fb6bb4814a3d51904de95a` (`HEAD` перед раном). Ран строит reachable set генерации каждой библиотеки по сконструированным типам (поправка ADR 0019), строит span-аргументы триггеров из засеянного массива, не берёт в триггеры члены `System.Object` у держателя точного типа и собирает accesses подтверждающего прогона держателя только в действиях драйвера (ADR 0025). Держатель фабрики `MessageParser<T>` теперь покрыт, но не подтверждается, поэтому `High`-находка кейса `protobuf-parser-factory-at-parse` переезжает в ран 5d B4c вместе с остатком вопроса 101, а четыре границы сгенерированного слоя из раздела 3 `docs/PLAN.md` — в ран 5d B4d. Вопрос 143 закрыт, вопрос 101 сужен, вопрос 187 открыт.

## R1

Генерация любой библиотеки, не только `System.Private.CoreLib`, строит reachable set по сконструированным типам (`ModelGenerator.Reachability`, `DispatchRule.ConstructedTypes`): виртуальный и интерфейсный вызов доходит только до override-ов типов, которые набор конструирует, а вызов, который куча разрешает в тело вне набора, — opaque-вызов. Тела `System.SR`, типы распознавателей и выключенная диагностика остаются правилами только CoreLib (`CoreLibLeftOutTypeKeys`, `CoreLibSilentTypeKeys`); сборка, чей метод забирает распознаватель, по-прежнему отказывается целиком. Тесты: `ReachabilityRuleSelectionTests` (новый; какое правило получает генерация по сборке и анализ программы, факты `Every_cell_gets_its_expected_rule`, `Every_axis_value_occurs_in_a_cell`) и `PackageReachabilityTests` (новый; `A_package_interface_call_reaches_only_the_overrides_of_constructed_types`, `A_package_call_into_a_body_outside_the_set_is_an_opaque_call`, `A_package_whose_method_a_recognizer_claims_is_refused_whole`). Из прежних тестов это правило сдвинуло ответы `GeneratedStructEffectMatrixTests`: обобщённые маршруты на struct `S`, которую ни одно достигнутое тело не боксит, стали opaque-вызовами (ниже, «Тесты правила»).

Правило статики библиотеки у невидимого кода (решения рана D-0019, D-0021). Статическое поле сборки библиотеки, которое вне setup получает по ссылке код, невидимый анализу, — получателем или аргументом `ref`, `in` или `out` неизвестного вызова (члена без тела) или вызова без разрешённой цели, — запись в состояние библиотеки: `EffectReader.ReadStaticsHandedToUnseen` записывает state store статической области типа поля с действием места, и ответ — `library-state` с причиной `library-state-store` (`static:<сборка>:<тип> in V_Call`), если его не отказали раньше по `unknown-touch` или `incomplete`. Поле находится через получателя (хранилище struct и его держатель — статическая область, или сама статическая область) и через каждое статическое поле, которое называет ссылка по своим привязкам (`ValueProvenance.ReferenceStatics`): ссылка на скалярное или ссылочное статическое поле, которое никто не загружает и не пишет, области в куче не имеет и видна только в `ReferenceCell`. Статика сборки драйвера, значение статики, отданное по значению, и операнды операции, которую lowering не выражает, под правило не попадают; место, которое выполняется только в дереве setup, тоже.

Фикс код-ревью F-0016 — правило: статика библиотеки, отданная невидимому коду по ссылке, — запись в состояние библиотеки в каждом исполнении места вне setup, а не только в деревьях `V_Call`/`V_Enum`. Деление мест на setup / вне setup — одно, у `GenerationHandoffs.ExecutionsOutsideSetup` (по нему же `File` и `FileWitness` строят `HandoffsOutsideSetup`); `EffectReader.ReadStaticsHandedToUnseen` берёт исполнения места оттуда, а не решает второй раз через `ActionOf`, поэтому место в неизвестном исполнении (делегат библиотеки, отданный невидимому коду), в инициализаторе типа, в ленивом построении и при запуске тоже даёт `library-state-store`; корень записи — действие дерева исполнения, а вне дерева действия — само исполнение (`static:<сборка>:<тип> in unknown-delegate-call:…`, `… in type-initializer:…`). Места: `UnknownCall` и `UnresolvedTarget` — оба из `UnseenCalls`, fixed; `File`/`FileWitness`/`OutsideSetup` в `GenerationHandoffs` — already agreed, теперь через одну функцию. Ни один ответ demo и model evals это не сдвинуло: перезаписанные файлы demo, снимок demo и снимок evals равны прежним.

Фикс код-ревью F-0020 — правило: каждый цикл, который ран добавляет в `src/`, в тестах тоже, — тело-блок с `WorkCancellation.Check();` первым или пометка `// bounded: <причина>`. `LibraryStaticHandoffMatrixTests.Source` — fixed (пометка); новые циклы `SpanTriggerArgumentMatrixTests.A_namesake_span_leaves_its_member_uncovered` — с пометкой; циклы `ConfirmationAccessesTests`, `ReachabilityRuleSelectionTests` и тел генерации рана — already agreed.

`GenerationHandoffs` вместе с этим отдаёт невидимому коду то же, что называет ссылка, на обоих путях: вызов без разрешённой цели отдаёт хранилище своих ссылочных аргументов (`ValueProvenance.ReferenceStorage`), как неизвестный вызов (вопрос 160), а получатель — хранилище struct (`this` по ссылке) — на обоих путях отдаёт объект, в котором это хранилище лежит (`HeapSolution.Located`), как ссылочный аргумент: без этого поле объекта-аргумента, отданное получателем, не отдавало бы сам аргумент.

Тесты правила:

- `LibraryStaticHandoffMatrixTests` (новый): оси Site (`UnknownCall`, `UnresolvedTarget`), Channel (`Receiver`, `Ref`, `In`, `Out`, `ByValue`), FieldType (`Struct`, `Scalar`, `Reference`), Owner (`Library`, `Driver`, `Input`) и Where (`Call`, `SetupOnly`, `SetupAndCall`, `UnknownExecution` — место в статическом методе библиотеки, чей делегат член отдаёт `extern`-методу, `TypeInitializer` — место, которое зовёт статический конструктор вложенного типа библиотеки, запущенный вызовом), 168 клеток; `Allowed` исключает `Receiver` со `Scalar` и `Reference`, `ByValue` с `Reference` и `Driver`/`Input` вне `Call`. `Owner = Library`, `Channel ≠ ByValue`, `Where ≠ SetupOnly` ⇔ `library-state` с `library-state-store`, названной по статической области типа клетки; `Owner = Input`, `Channel ≠ ByValue` ⇒ `unknown-touch` с `probe-handed-to-unseen`, названной по объекту-аргументу; в остальных клетках state store на статической области нет. Факты `Every_cell_gets_its_expected_answer`, `Every_axis_value_occurs_in_a_cell`.
- `GeneratedStructEffectMatrixTests`: обобщённые маршруты (`GenericRef`, `GenericIn`, `GenericByValue`) отвечают как opaque-вызов, который делает constrained-вызов на `S` (R1): `unknown-touch` (`probe-handed-to-unseen`) во всех клетках, где невидимый код получает вход драйвера или то, до чего вход достаёт (получатель, в котором лежит хранилище, хранилище параметра в статике драйвера, засеянный массив статики, копию с засеянным seed статики, которую маршрут загружает), и `library-state` (`library-state-store`) в `StaticField/GenericRef/MutatingCall` и `StaticField/GenericRef/TouchReference`, где невидимый код получает по ссылке статику библиотеки, которую никто не загружает. Копирующий маршрут сравнивается с копией, отданной невидимому коду (`ByValueParameter/GenericByValue`), а не с копией `ByValueParameter/Directly`, и обобщённые маршруты не входят в сравнение «маршрут отвечает как хранилище». Имена фактов прежние; `No_cell_gets_a_model_that_misses_its_effect` проходит — до правила две клетки `StaticField/GenericRef` получали модель `effects: {}`.

## R2

Аргумент триггера типа `Span<T>` или `ReadOnlySpan<T>` — по значению, `in`, `ref readonly` или `ref` — строится в вызове триггера из статического поля `T[]` (`TArg_<вариант>_<номер>`), которое setup засевает тем же рецептом, что аргумент `T[]` на том же месте; аргумент по ссылке — локальная переменная, объявленная перед вызовом (`DriverSynthesizer`, `SpanElement`). Такой триггер покрывает свой член. Значение другого ref-like или pointer типа драйвер не строит, и член остаётся непокрытым членом держателя с прежним текстом; span-параметр самого генерируемого члена по-прежнему даёт `driver-not-synthesized`.

Тесты:

- `SpanTriggerArgumentMatrixTests` (новый): клетки по элементу, способу передачи и ожиданию результата; факты `Every_cell_gets_its_expected_trigger`, `Every_axis_value_occurs_in_a_cell`, `No_cell_is_left_out_by_Allowed`, `A_ref_struct_other_than_a_span_leaves_its_member_uncovered`, `A_namesake_span_leaves_its_member_uncovered`, `A_pointer_argument_leaves_its_member_uncovered`.

Фикс код-ревью F-0018 — правило: рецепт массива получают только `System.Span<T>` и `System.ReadOnlySpan<T>` среды выполнения — объявленные вне типа в пространстве `System` прямо под глобальным сборкой, которая объявляет `System.Object`; любой другой ref-like тип, одноимённый тоже, оставляет член непокрытым с прежним текстом. Место одно — `DriverSynthesizer.SpanElement`, fixed. Тест `A_namesake_span_leaves_its_member_uncovered` (новый): одноимённые `Span<T>` и `ReadOnlySpan<T>`, вложенные в тип пространства `System`, и объявленные самой библиотекой в `System` — непокрытые члены, а span среды выполнения рядом — триггер.

Фикс код-ревью F-0019 — правило: шаг 5 «Driver synthesis» в `docs/research/library-models.md` описывает готовое поведение R2 без противоречия: ограничение «without `ref`/`in`/ref-like parameters» заменено тем, что аргумент — засеянная статика, переданная так, как берёт параметр, `out` отбрасывается, а из ref-like и pointer типов строятся только span-ы; там же — что одноимённый span не span. Место одно, fixed.
- `GeneratedFateTests.A_holder_member_the_driver_cannot_call_leaves_the_holder_unconfirmed`: фикстура `Scan(ReadOnlySpan<int>)` стала бы триггером (R2), поэтому член, которого драйвер не может позвать, теперь принимает собственный `ref struct` библиотеки `Cursor`; утверждение то же.

## R3

Член, реализация которого для типа держателя — собственная реализация `System.Object` (`ToString`, `Equals(object)`, `GetHashCode`, не переопределённые ни типом держателя, ни его базами), не триггер и не занимает места среди 24, когда тип держателя запечатан или держатель — результат конструктора (`HolderMembers(holder, exact)`); override, в том числе `System.ValueType` и записи, — триггер, как раньше. Предел 24 и его общий счёт для двух держателей не менялись.

Тесты:

- `ObjectMemberTriggerMatrixTests` (новый): факты `Every_cell_gets_its_expected_trigger_set`, `Every_axis_value_occurs_in_a_cell`, `A_message_parser_shaped_holder_fits_the_24_triggers`; ось Declarer (`Object`, `Interface` — тип держателя реализует ещё интерфейс `IO`, который объявляет три члена `object`) и значение `ExplicitInterface` оси реализатора (член клетки — явная реализация члена `IO`; `Allowed` исключает его без интерфейса).

Фикс код-ревью F-0017 — правило: член, реализация которого для типа держателя — собственная реализация `System.Object`, не триггер держателя точного типа, кто бы его ни объявлял — сам `Object` или интерфейс держателя, который член `Object` реализует неявно. Места в `DriverSynthesizer.HolderMembers`: обход членов `Object` и интерфейсная ветка — fixed, через одну проверку `ObjectsOwn`; интерфейс, реализованный override-ом в типе или базе, — already agreed (член интерфейса стоит за override-ом); явная реализация интерфейса — already agreed (член интерфейса — триггер); неточный держатель — already agreed (члены `Object` и интерфейса — триггеры, как раньше).
- Ответы, которые сдвинуло правило (у запечатанного держателя без override-ов членов `object` больше нет): `DriverSynthesisTests.A_carried_probe_is_built_by_setup_once_per_variant_with_that_variants_probe` — триггеров два вместо пяти, без `Equals`, `GetHashCode`, `ToString` `object`; `GeneratedFateTests.Holder_whose_triggers_run_the_delegate_in_place_stays_a_holder` — триггеры `Bus` — `Fire`, `On` и `Twice`, без членов `object`; `DriverRollbackMatrixTests.A_rebuild_keeps_the_members_past_the_trigger_cap_uncovered` — 25 собственных членов держателя (`IOld.Try` и 24 члена `IMany`) вместо 21 и трёх членов `object`, и за пределом остаётся `M:Lib.IMany.M23`, а не `M:System.Object.GetHashCode`.
- Только фикстура, утверждения те же: `Fluent` в `DriverSynthesisTests` получил `F10` и `F11`, чтобы держатели `result` и `this` у `Fluent.Chain` вместе по-прежнему не укладывались в 24 триггера (`The_result_and_the_receiver_holders_share_the_24_triggers`), а `Plain` в `AccessorDriverTests` — `Fire`, чтобы у держателя без членов `object` остался триггер (`A_non_virtual_field_like_events_accessors_are_not_triggers`).

## R4

Подтверждающий прогон держателя ограничен той же границей замыкания 1500 тел (`CLOSURE_BOUND`) и тем же отказом `closure-bound`, что прогон судеб; своей границы нет. Его стадия accesses собирает только исполнения, которые `DriverExecutions.TreeActionOf` относит к дереву setup, `V_Call` или `V_Enum` (`ModelGenerator.ConfirmationCollects`, фильтр `accessesFilter` в `ScopePipeline.Run` и `InterproceduralAccesses`); стадия executions полная. Анализ программы и прогон судеб собирают accesses как раньше.

Тесты:

- `ConfirmationAccessesTests` (новый): `Each_execution_role_is_collected_as_its_readers_read_it`, `The_confirmation_collects_no_access_in_a_triggers_execution`, `Holder_inputs_are_the_same_as_with_every_execution_collected` — подтверждение держателя и его входы те же, что у прогона, собирающего всё (`confirmationCollectsEverything`).
- `ScopePipelineTests.An_execution_filter_leaves_out_the_accesses_of_the_executions_it_refuses` (новый).
- `GeneratedFateTests.Holder_whose_confirmation_run_passes_the_bound_is_unknown_execution` и `ModelEvalTests.Message_parser_holder_is_covered_and_its_confirmation_passes_the_bound` (новый, под `CH_MODEL_EVALS=1`, версии 3.21.9 и 3.30.2): держатель `result` конструктора `MessageParser<T>` покрыт — 22 триггера, оба `ParseFrom(ReadOnlySpan<byte>)` среди них, членов `System.Object` нет, непокрытых членов нет, — подтверждающий прогон останавливается за границей 1500 тел, и судьба `factory` — `unknown-execution`.

## R5

Замер R5 (гейт задачи 4, `scratch/b4b-gate/r5-measure.json`): `generate` конструктора ``` M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0}) ``` пакета Google.Protobuf 3.30.2 в Release на этой машине — общее время процесса 15.9 с (из них генерация 15.5 с по записи генерации), пиковый working set процесса 361 МБ (378339328 байт), пиковая paged-память 239 МБ (250683392 байт), судьба `factory` — `unknown-execution` без держателя, у модели тоже `unknown-execution`; прогон судеб дошёл до 21 тела. Порога у замера нет (решение рана D-0015); разбивки по стадиям нет.

Замер первой попытки задачи 4, со своей границей подтверждающего прогона 3000 тел (отвергнута, ADR 0025): подтверждающий прогон конструктора (3.30.2), чей держатель покрыт span-триггерами и без членов `System.Object`, дошёл до 2006 тел и не остановился; генерация шла 45 с при 813 МБ. Держатель всё равно не подтвердился: проба фабрики сработала в собственном root execution каждого триггера `ParseFrom`, `ParseDelimitedFrom` и `ParseJson`, а ещё в восьми инициализаторах типов библиотеки — `WellKnownTypes.*Reflection`, `DescriptorReflection` и `FeatureSetDescriptor`, — которые строят дескрипторы собственными `MessageParser<T>` через общий с драйвером экземпляр `ParseFrom`/`CreateTemplate`.

## Model evals

Снимок `skills/hunt/evals/models/generator-snapshot.json` перезаписан прогоном классов `ModelEvalTests`, `EffectsEvalTests`, `LinqOracleTests` и `AccessorEvalTests` в Release под `CH_MODEL_EVALS=1` и `CH_MODEL_EVALS_RECORD=1`: 38 тестов прошли, ни один ответ не сужен опасно, ни один порог не ниже записанного, записи и пакеты те же, `bodies` и `externBodies` каждого пакета те же. Ни у одного члена не сдвинулись `reason`, `modelReason`, `classified`, модель и `causes`; у 150 членов сдвинулось только `reachedBodies`, у одного из них ещё `detail` отказа `closure-bound`.

Все сдвиги — R1. `reachedBodies` — тела прогона судеб (стадия analyze, без триггеров), так что span-аргументы триггеров (R2), члены `Object` у держателя точного типа (R3) и accesses подтверждающего прогона (R4) это число не двигают, а правило статики библиотеки у невидимого кода (R1) не сдвинуло ни одной причины. Число падает, потому что виртуальный и интерфейсный вызов в библиотеке вне CoreLib теперь доходит только до override-ов типов, которые набор конструирует, а вызов в тело вне набора — opaque-вызов. Ответ ``` M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0}) ``` (Google.Protobuf 3.21.9) остаётся записью с судьбой `factory` `unknown-execution` (R5): держатель покрыт, подтверждающий прогон останавливается на границе 1500 тел.

Ответы:

- ``` M:Google.Protobuf.FieldCodec.ForMessage``1(System.UInt32,Google.Protobuf.MessageParser{``0}) ``` (members): `reachedBodies` 49 → 45 — R1.
- ``` M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0}) ``` (members): `reachedBodies` 22 → 21 — R1.
- `M:Polly.AsyncPolicy.ExecuteAsync(System.Func{System.Threading.Tasks.Task})` (members): `reachedBodies` 219 → 49 — R1.
- `M:Polly.Policy.Execute(System.Action)` (members): `reachedBodies` 216 → 48 — R1.
- ``` M:System.Linq.Enumerable.All``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean}) ``` (members): `reachedBodies` 341 → 8 — R1.
- ``` M:System.Linq.Enumerable.FirstOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean}) ``` (members): `reachedBodies` 342 → 9 — R1.
- ``` M:System.Linq.Enumerable.GroupBy``4(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2},System.Func{``1,System.Collections.Generic.IEnumerable{``2},``3}) ``` (members): `reachedBodies` 357 → 36 — R1.
- ``` M:System.Linq.Enumerable.OrderBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1}) ``` (members): `reachedBodies` 352 → 42 — R1.
- ``` M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1}) ``` (members): `reachedBodies` 371 → 52 — R1.
- ``` M:System.Linq.Enumerable.SelectMany``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Collections.Generic.IEnumerable{``1}}) ``` (members): `reachedBodies` 358 → 27 — R1.
- ``` M:System.Linq.Enumerable.SingleOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean}) ``` (members): `reachedBodies` 350 → 17 — R1.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32}) ``` (members): `reachedBodies` 342 → 9 — R1.
- ``` M:System.Linq.Enumerable.ToDictionary``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2}) ``` (members): `reachedBodies` 355 → 22 — R1.
- ``` M:System.Linq.Enumerable.Where``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean}) ``` (members): `reachedBodies` 354 → 39 — R1.
- ``` M:FluentValidation.AbstractValidator`1.Validate(`0) ``` (effects): `reachedBodies` 469 → 84 — R1.
- ``` M:FluentValidation.AbstractValidator`1.ValidateAsync(`0,System.Threading.CancellationToken) ``` (effects): `reachedBodies` 469 → 82 — R1.
- ``` M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions) ``` (effects): `reachedBodies` 1741 → 1635, `detail` «the reachable set reached 1741 bodies, over 1500» → «the reachable set reached 1635 bodies, over 1500» — R1.
- ``` M:System.Linq.Enumerable.Aggregate``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``0,``0})~``0 ``` (linq): `reachedBodies` 400 → 19 — R1.
- ``` M:System.Linq.Enumerable.Aggregate``2(System.Collections.Generic.IEnumerable{``0},``1,System.Func{``1,``0,``1})~``1 ``` (linq): `reachedBodies` 392 → 11 — R1.
- ``` M:System.Linq.Enumerable.Aggregate``3(System.Collections.Generic.IEnumerable{``0},``1,System.Func{``1,``0,``1},System.Func{``1,``2})~``2 ``` (linq): `reachedBodies` 395 → 14 — R1.
- ``` M:System.Linq.Enumerable.All``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Boolean ``` (linq): `reachedBodies` 391 → 10 — R1.
- ``` M:System.Linq.Enumerable.Any``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Boolean ``` (linq): `reachedBodies` 391 → 10 — R1.
- ``` M:System.Linq.Enumerable.Any``1(System.Collections.Generic.IEnumerable{``0})~System.Boolean ``` (linq): `reachedBodies` 518 → 7 — R1.
- ``` M:System.Linq.Enumerable.AsEnumerable``1(System.Collections.Generic.IEnumerable{``0})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 391 → 7 — R1.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Decimal})~System.Decimal ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Double})~System.Double ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32})~System.Double ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int64})~System.Double ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Decimal}})~System.Nullable{System.Decimal} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Double}})~System.Nullable{System.Double} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int32}})~System.Nullable{System.Double} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int64}})~System.Nullable{System.Double} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Single}})~System.Nullable{System.Single} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Single})~System.Single ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Cast``1(System.Collections.IEnumerable)~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 397 → 26 — R1.
- ``` M:System.Linq.Enumerable.Contains``1(System.Collections.Generic.IEnumerable{``0},``0)~System.Boolean ``` (linq): `reachedBodies` 424 → 8 — R1.
- ``` M:System.Linq.Enumerable.Count``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Int32 ``` (linq): `reachedBodies` 391 → 10 — R1.
- ``` M:System.Linq.Enumerable.Count``1(System.Collections.Generic.IEnumerable{``0})~System.Int32 ``` (linq): `reachedBodies` 388 → 7 — R1.
- ``` M:System.Linq.Enumerable.DistinctBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 398 → 17 — R1.
- ``` M:System.Linq.Enumerable.Empty``1~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 389 → 6 — R1.
- ``` M:System.Linq.Enumerable.ExceptBy``2(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 398 → 16 — R1.
- ``` M:System.Linq.Enumerable.First``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `reachedBodies` 401 → 20 — R1.
- ``` M:System.Linq.Enumerable.First``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `reachedBodies` 518 → 18 — R1.
- ``` M:System.Linq.Enumerable.FirstOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean},``0)~``0 ``` (linq): `reachedBodies` 392 → 11 — R1.
- ``` M:System.Linq.Enumerable.FirstOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `reachedBodies` 392 → 11 — R1.
- ``` M:System.Linq.Enumerable.FirstOrDefault``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `reachedBodies` 518 → 9 — R1.
- ``` M:System.Linq.Enumerable.GroupBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{System.Linq.IGrouping{``1,``0}} ``` (linq): `reachedBodies` 397 → 38 — R1.
- ``` M:System.Linq.Enumerable.GroupBy``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2})~System.Collections.Generic.IEnumerable{System.Linq.IGrouping{``1,``2}} ``` (linq): `reachedBodies` 402 → 43 — R1.
- ``` M:System.Linq.Enumerable.GroupBy``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``1,System.Collections.Generic.IEnumerable{``0},``2})~System.Collections.Generic.IEnumerable{``2} ``` (linq): `reachedBodies` 402 → 45 — R1.
- ``` M:System.Linq.Enumerable.GroupBy``4(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2},System.Func{``1,System.Collections.Generic.IEnumerable{``2},``3})~System.Collections.Generic.IEnumerable{``3} ``` (linq): `reachedBodies` 407 → 50 — R1.
- ``` M:System.Linq.Enumerable.GroupJoin``4(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``2},System.Func{``1,``2},System.Func{``0,System.Collections.Generic.IEnumerable{``1},``3})~System.Collections.Generic.IEnumerable{``3} ``` (linq): `reachedBodies` 410 → 43 — R1.
- ``` M:System.Linq.Enumerable.IntersectBy``2(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 398 → 16 — R1.
- ``` M:System.Linq.Enumerable.Join``4(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``2},System.Func{``1,``2},System.Func{``0,``1,``3})~System.Collections.Generic.IEnumerable{``3} ``` (linq): `reachedBodies` 409 → 42 — R1.
- ``` M:System.Linq.Enumerable.Last``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `reachedBodies` 411 → 20 — R1.
- ``` M:System.Linq.Enumerable.Last``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `reachedBodies` 518 → 18 — R1.
- ``` M:System.Linq.Enumerable.LastOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean},``0)~``0 ``` (linq): `reachedBodies` 402 → 11 — R1.
- ``` M:System.Linq.Enumerable.LastOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `reachedBodies` 402 → 11 — R1.
- ``` M:System.Linq.Enumerable.LastOrDefault``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `reachedBodies` 518 → 9 — R1.
- ``` M:System.Linq.Enumerable.LeftJoin``4(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``2},System.Func{``1,``2},System.Func{``0,``1,``3})~System.Collections.Generic.IEnumerable{``3} ``` (linq): `reachedBodies` 409 → 41 — R1.
- ``` M:System.Linq.Enumerable.LongCount``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Int64 ``` (linq): `reachedBodies` 391 → 9 — R1.
- ``` M:System.Linq.Enumerable.LongCount``1(System.Collections.Generic.IEnumerable{``0})~System.Int64 ``` (linq): `reachedBodies` 389 → 7 — R1.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Decimal})~System.Decimal ``` (linq): `reachedBodies` 400 → 18 — R1.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Double})~System.Double ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32})~System.Int32 ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int64})~System.Int64 ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Decimal}})~System.Nullable{System.Decimal} ``` (linq): `reachedBodies` 391 → 9 — R1.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Double}})~System.Nullable{System.Double} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int32}})~System.Nullable{System.Int32} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int64}})~System.Nullable{System.Int64} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Single}})~System.Nullable{System.Single} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Single})~System.Single ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Max``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~``1 ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.MaxBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~``0 ``` (linq): `reachedBodies` 402 → 20 — R1.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Decimal})~System.Decimal ``` (linq): `reachedBodies` 400 → 18 — R1.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Double})~System.Double ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32})~System.Int32 ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int64})~System.Int64 ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Decimal}})~System.Nullable{System.Decimal} ``` (linq): `reachedBodies` 391 → 9 — R1.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Double}})~System.Nullable{System.Double} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int32}})~System.Nullable{System.Int32} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int64}})~System.Nullable{System.Int64} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Single}})~System.Nullable{System.Single} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Single})~System.Single ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.Min``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~``1 ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.MinBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~``0 ``` (linq): `reachedBodies` 402 → 20 — R1.
- ``` M:System.Linq.Enumerable.OfType``1(System.Collections.IEnumerable)~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 396 → 25 — R1.
- ``` M:System.Linq.Enumerable.OrderBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Linq.IOrderedEnumerable{``0} ``` (linq): `reachedBodies` 400 → 59 — R1.
- ``` M:System.Linq.Enumerable.OrderByDescending``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Linq.IOrderedEnumerable{``0} ``` (linq): `reachedBodies` 400 → 59 — R1.
- ``` M:System.Linq.Enumerable.RightJoin``4(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``2},System.Func{``1,``2},System.Func{``0,``1,``3})~System.Collections.Generic.IEnumerable{``3} ``` (linq): `reachedBodies` 409 → 41 — R1.
- ``` M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{``1} ``` (linq): `reachedBodies` 416 → 48 — R1.
- ``` M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,``1})~System.Collections.Generic.IEnumerable{``1} ``` (linq): `reachedBodies` 397 → 16 — R1.
- ``` M:System.Linq.Enumerable.SelectMany``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Collections.Generic.IEnumerable{``1}})~System.Collections.Generic.IEnumerable{``1} ``` (linq): `reachedBodies` 408 → 30 — R1.
- ``` M:System.Linq.Enumerable.SelectMany``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,System.Collections.Generic.IEnumerable{``1}})~System.Collections.Generic.IEnumerable{``1} ``` (linq): `reachedBodies` 409 → 22 — R1.
- ``` M:System.Linq.Enumerable.SelectMany``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Collections.Generic.IEnumerable{``1}},System.Func{``0,``1,``2})~System.Collections.Generic.IEnumerable{``2} ``` (linq): `reachedBodies` 414 → 27 — R1.
- ``` M:System.Linq.Enumerable.SelectMany``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,System.Collections.Generic.IEnumerable{``1}},System.Func{``0,``1,``2})~System.Collections.Generic.IEnumerable{``2} ``` (linq): `reachedBodies` 414 → 27 — R1.
- ``` M:System.Linq.Enumerable.SequenceEqual``1(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``0})~System.Boolean ``` (linq): `reachedBodies` 390 → 9 — R1.
- ``` M:System.Linq.Enumerable.Single``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `reachedBodies` 403 → 22 — R1.
- ``` M:System.Linq.Enumerable.Single``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `reachedBodies` 401 → 19 — R1.
- ``` M:System.Linq.Enumerable.SingleOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean},``0)~``0 ``` (linq): `reachedBodies` 401 → 20 — R1.
- ``` M:System.Linq.Enumerable.SingleOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `reachedBodies` 401 → 20 — R1.
- ``` M:System.Linq.Enumerable.SingleOrDefault``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `reachedBodies` 399 → 17 — R1.
- ``` M:System.Linq.Enumerable.Skip``1(System.Collections.Generic.IEnumerable{``0},System.Int32)~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 406 → 37 — R1.
- ``` M:System.Linq.Enumerable.SkipWhile``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 396 → 15 — R1.
- ``` M:System.Linq.Enumerable.SkipWhile``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 396 → 15 — R1.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Decimal})~System.Decimal ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Double})~System.Double ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32})~System.Int32 ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int64})~System.Int64 ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Decimal}})~System.Nullable{System.Decimal} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Double}})~System.Nullable{System.Double} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int32}})~System.Nullable{System.Int32} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int64}})~System.Nullable{System.Int64} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Single}})~System.Nullable{System.Single} ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Single})~System.Single ``` (linq): `reachedBodies` 392 → 10 — R1.
- ``` M:System.Linq.Enumerable.Take``1(System.Collections.Generic.IEnumerable{``0},System.Int32)~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 390 → 37 — R1.
- ``` M:System.Linq.Enumerable.Take``1(System.Collections.Generic.IEnumerable{``0},System.Range)~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 407 → 40 — R1.
- ``` M:System.Linq.Enumerable.TakeWhile``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 396 → 15 — R1.
- ``` M:System.Linq.Enumerable.TakeWhile``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 396 → 15 — R1.
- ``` M:System.Linq.Enumerable.ThenBy``2(System.Linq.IOrderedEnumerable{``0},System.Func{``0,``1})~System.Linq.IOrderedEnumerable{``0} ``` (linq): `reachedBodies` 418 → 27 — R1.
- ``` M:System.Linq.Enumerable.ThenByDescending``2(System.Linq.IOrderedEnumerable{``0},System.Func{``0,``1})~System.Linq.IOrderedEnumerable{``0} ``` (linq): `reachedBodies` 418 → 27 — R1.
- ``` M:System.Linq.Enumerable.ToArray``1(System.Collections.Generic.IEnumerable{``0})~``0[] ``` (linq): `reachedBodies` 389 → 18 — R1.
- ``` M:System.Linq.Enumerable.ToDictionary``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Collections.Generic.Dictionary{``1,``0} ``` (linq): `reachedBodies` 399 → 18 — R1.
- ``` M:System.Linq.Enumerable.ToDictionary``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2})~System.Collections.Generic.Dictionary{``1,``2} ``` (linq): `reachedBodies` 404 → 23 — R1.
- ``` M:System.Linq.Enumerable.ToList``1(System.Collections.Generic.IEnumerable{``0})~System.Collections.Generic.List{``0} ``` (linq): `reachedBodies` 439 → 10 — R1.
- ``` M:System.Linq.Enumerable.ToLookup``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Linq.ILookup{``1,``0} ``` (linq): `reachedBodies` 399 → 34 — R1.
- ``` M:System.Linq.Enumerable.ToLookup``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2})~System.Linq.ILookup{``1,``2} ``` (linq): `reachedBodies` 404 → 39 — R1.
- ``` M:System.Linq.Enumerable.Union``1(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``0})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 401 → 36 — R1.
- ``` M:System.Linq.Enumerable.UnionBy``2(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 398 → 16 — R1.
- ``` M:System.Linq.Enumerable.Where``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 402 → 38 — R1.
- ``` M:System.Linq.Enumerable.Where``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `reachedBodies` 396 → 15 — R1.
- ``` M:System.Linq.Enumerable.Zip``3(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``1,``2})~System.Collections.Generic.IEnumerable{``2} ``` (linq): `reachedBodies` 398 → 16 — R1.
- ``` M:System.Linq.Queryable.Any``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~System.Boolean ``` (linq): `reachedBodies` 30 → 9 — R1.
- ``` M:System.Linq.Queryable.Any``1(System.Linq.IQueryable{``0})~System.Boolean ``` (linq): `reachedBodies` 29 → 8 — R1.
- ``` M:System.Linq.Queryable.OrderBy``2(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,``1}},System.Collections.Generic.IComparer{``1})~System.Linq.IOrderedQueryable{``0} ``` (linq): `reachedBodies` 53 → 27 — R1.
- ``` M:System.Linq.Queryable.OrderBy``2(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,``1}})~System.Linq.IOrderedQueryable{``0} ``` (linq): `reachedBodies` 52 → 26 — R1.
- ``` M:System.Linq.Queryable.Single``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~``0 ``` (linq): `reachedBodies` 30 → 9 — R1.
- ``` M:System.Linq.Queryable.Single``1(System.Linq.IQueryable{``0})~``0 ``` (linq): `reachedBodies` 29 → 8 — R1.
- ``` M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0},``0)~``0 ``` (linq): `reachedBodies` 30 → 9 — R1.
- ``` M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}},``0)~``0 ``` (linq): `reachedBodies` 31 → 10 — R1.
- ``` M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~``0 ``` (linq): `reachedBodies` 30 → 9 — R1.
- ``` M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0})~``0 ``` (linq): `reachedBodies` 29 → 8 — R1.
- ``` M:System.Linq.Queryable.Skip``1(System.Linq.IQueryable{``0},System.Int32)~System.Linq.IQueryable{``0} ``` (linq): `reachedBodies` 42 → 16 — R1.
- ``` M:System.Linq.Queryable.Take``1(System.Linq.IQueryable{``0},System.Int32)~System.Linq.IQueryable{``0} ``` (linq): `reachedBodies` 42 → 16 — R1.
- ``` M:System.Linq.Queryable.Take``1(System.Linq.IQueryable{``0},System.Range)~System.Linq.IQueryable{``0} ``` (linq): `reachedBodies` 42 → 16 — R1.
- ``` M:System.Linq.Queryable.Where``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~System.Linq.IQueryable{``0} ``` (linq): `reachedBodies` 52 → 26 — R1.
- ``` M:System.Linq.Queryable.Where``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Int32,System.Boolean}})~System.Linq.IQueryable{``0} ``` (linq): `reachedBodies` 52 → 26 — R1.
- `M:System.Collections.Specialized.ListDictionary.get_Item(System.Object)` (accessors): `reachedBodies` 38 → 34 — R1.
- `M:System.Collections.Specialized.ListDictionary.set_Item(System.Object,System.Object)` (accessors): `reachedBodies` 38 → 34 — R1.
- `M:System.Diagnostics.Process.add_Exited(System.EventHandler)` (accessors): `reachedBodies` 29 → 25 — R1.
- `M:System.Net.Security.SslClientAuthenticationOptions.set_RemoteCertificateValidationCallback(System.Net.Security.RemoteCertificateValidationCallback)` (accessors): `reachedBodies` 85 → 54 — R1.

Числа пакетов и пороги не сдвинулись: `exact` 11, `fateGoldEntries` 4, `effectsExact` 8, `accessorsExact` 8, `linqExact.System.Linq` 17, `linqExact.System.Linq.Queryable` 0, как в `HEAD`; поля пакетов те же.

## Demo

Опубликован exe (`bin/win-x64`), затем `build/record-demo-models.ps1` перезаписал сгенерированные файлы demo генератором рана: «21 generated file(s) below demo/.concurrency-hunter/models/generated; a run that only reads them generates nothing.» Генерирующий прогон не оставил `budget`; новых файлов в `demo/.concurrency-hunter/models/generated` нет. Во всех 21 файле сдвинулся отпечаток `generator`; в трёх ещё и ответы:

- ``` M:Google.Protobuf.MessageParser`1.ParseFrom(System.Byte[]) ``` (Google.Protobuf 3.30.2): `closure-bound` («the reachable set reached 1571 bodies, over 1500») → `unknown-touch` с пустым detail и причинами `receiver-member-unseen`, `result-several-objects`, `unseeded-read` — R1: виртуальный и интерфейсный вызов доходит только до override-ов сконструированных типов, и замыкание укладывается в границу 1500 тел. Вызов по-прежнему без модели.
- `M:System.Net.Http.HttpClient.#ctor` (System.Net.Http 10.0): `closure-bound` («the reachable set reached 1825 bodies, over 1500») → `library-state` с пустым detail и причинами `library-state-store`, `unsupported-store`, `value-unobserved` — R1: то же сужение замыкания. Вызов по-прежнему без модели.
- `M:Polly.CircuitBreakerResiliencePipelineBuilderExtensions.AddCircuitBreaker(Polly.ResiliencePipelineBuilder,Polly.CircuitBreaker.CircuitBreakerStrategyOptions)` (Polly.Core 8.7.0): ответ `unknown-touch` тот же, из причин ушли `unseeded-read`, `witness-value-accessed-elsewhere` и `witness-value-handed` — R1: тела, через которые шли эти чтения и передачи, — override-ы несконструированных типов, и замыкание до них не доходит.

Правило статики раздела R1 ни одного ответа demo не сдвинуло: все три сдвига дала постройка reachable set по сконструированным типам.

Затем из `metrics --target demo/Demo.slnx --generation read` той же сборки Release (прогон чтения `record-demo-models.ps1`) взяты `counts` и `coverage`, без завершающего перевода строки, для `src/ConcurrencyHunter.Cli.Tests/Snapshots/demo.json`. Точка сравнения — `5d-B4b`.

Находки не сдвинулись: весь список `counts.fingerprints` и все поля `counts` равны `HEAD`, счётчики покрытия тоже. Изменений снимка два, элемент массива не появился и не исчез:

- ``coverage[Demo.Web].generationRefusals[M:Google.Protobuf.MessageParser`1.ParseFrom(System.Byte[])].cause``: closure-bound → unknown-touch — R1: замыкание `ParseFrom(byte[])` по сконструированным типам укладывается в границу, и ответ называет причины вместо границы.
- `coverage[Demo.Web].generationRefusals[M:System.Net.Http.HttpClient.#ctor].cause`: closure-bound → library-state — R1: замыкание конструктора `HttpClient` по сконструированным типам укладывается в границу.

Две правки `demo/expected-findings.json` (решение рана D-0017):

- `protobuf-parser-factory-at-parse/factory-runs-at-parse`: `phase` `5d-B4b` → `5d-B4c`, остальные поля прежние. Держатель фабрики `MessageParser<T>` покрыт, но не подтверждается: подтверждающий прогон конструктора выходит за границу замыкания 1500 тел, а проба фабрики срабатывает и в инициализаторах типов самой библиотеки через общий экземпляр `ParseFrom`/`CreateTemplate` (ADR 0025).
- `protobuf-parser-factory-at-parse/factory-fires-in-type-initializers` добавлена в `notDefects`: `phase` `5d-B4b`, `until` `5d-B4c`, ресурс `static:Demo.Web.Cases.ProtobufParserFactoryAtParse.Tally` `Created`, как у `/holder-unconfirmed`. Находки на `Tally.Created` нет: модель конструктора `MessageParser<T>` — без эффектов с судьбой `factory` `unknown-execution`, ответ `ParseFrom(byte[])` — `unknown-touch`, и вызов остаётся opaque.

## Генерация CoreLib

Ран CoreLib не перегенерирует: это решает владелец по коммиту последней генерации (ADR 0022); `skills/hunt/evals/models/corelib-generation.tsv` равен `HEAD`. Генерация CoreLib уже строила reachable set по сконструированным типам, а правила только CoreLib не менялись; её могут сдвинуть правило статики библиотеки у невидимого кода и передача хранилища ссылочным аргументом вызова без разрешённой цели (R1), span-аргументы триггеров (R2), члены `Object` у держателя точного типа (R3) и accesses подтверждающего прогона (R4). `./build/generate-corelib.ps1 -Since` сравнивает с коммитом генерации только закоммиченную историю; правки рана в дереве поверх `HEAD` добавляют к группе `generator` файлы `DriverExecutions.cs`, `DriverSynthesizer.cs`, `EffectReader.cs`, `GenerationHandoffs.cs`, `ModelGenerator.cs` и `ValueProvenance.cs` из `Providers/LibraryModels/Generation/`, а к группе `engine` — `Accesses/InterproceduralAccesses.cs` и `CallGraph/ReachableSet.cs` из `ConcurrencyHunter.Analysis` и `Analysis/ScopePipeline.cs` из `ConcurrencyHunter.Core`; группа `build` не меняется. Вывод на конец рана (код выхода 0):

```text
Recorded on 24669ef55059c8053292b745f1bd258dc787caf3; HEAD is fb8866273a037a4720fb6bb4814a3d51904de95a.

generator: 7 commit(s)
  a70d961 concurrency-hunter: 5d run B4 - the generator's answers get precise: rollback leaves no trace, concrete collections, value provenance, own access paths (ADR 0024)
  3a23477 concurrency-hunter: 5d run B3c - the generator's driver builds the calls it used to refuse
  5d2e28b concurrency-hunter: 5d run B3b - generator version, one cancellation owner, decompiled-library fixes
  784822c concurrency-hunter: the run generates the models of the library members it reaches, within a budget, and keeps them in the repository
  b9d60fc concurrency-hunter: CoreLib's generation runs with diagnostics off, and code a user may have written reaches only what a user can name
  c59f48a concurrency-hunter: CoreLib's generation reads a type a recognizer owns as a program reads it from metadata
  52135a4 concurrency-hunter: the model driver compiles where it overrides protected internal members, seeds delegates over internal types and calls members with pointers
   26 files changed, 3203 insertions(+), 626 deletions(-)

engine: 7 commit(s)
  a70d961 concurrency-hunter: 5d run B4 - the generator's answers get precise: rollback leaves no trace, concrete collections, value provenance, own access paths (ADR 0024)
  3a23477 concurrency-hunter: 5d run B3c - the generator's driver builds the calls it used to refuse
  5d2e28b concurrency-hunter: 5d run B3b - generator version, one cancellation owner, decompiled-library fixes
  784822c concurrency-hunter: the run generates the models of the library members it reaches, within a budget, and keeps them in the repository
  b9d60fc concurrency-hunter: CoreLib's generation runs with diagnostics off, and code a user may have written reaches only what a user can name
  c59f48a concurrency-hunter: CoreLib's generation reads a type a recognizer owns as a program reads it from metadata
  f7047cb concurrency-hunter: a member of a recognized type no recognizer models has an unknown effect, by family
   69 files changed, 6037 insertions(+), 833 deletions(-)

build: unchanged
```

Списки файлов обеих групп сокращены до итоговой строки `git diff --stat`.

## Остаток

- Вопрос 101 (5d run B4c): держатель `result` конструктора `MessageParser<T>` покрыт, но не подтверждается. Подтверждающий прогон конструктора (3.30.2) доходит до 2006 тел — за границей 1500, а без неё идёт 45 с — и видит срабатывания пробы фабрики в восьми инициализаторах типов библиотеки (`WellKnownTypes.*Reflection`, `DescriptorReflection`, `FeatureSetDescriptor`) через общий экземпляр `ParseFrom`/`CreateTemplate` (ADR 0025). Ран B4c отличает эти срабатывания от срабатываний действий драйвера и даёт подтверждающему прогону свою границу; `High`-находка кейса `protobuf-parser-factory-at-parse` (`/factory-runs-at-parse`) записана с `phase` `5d-B4c`.
- Вопрос 187 (5d run B5): держатель `FieldCodec<T>` у `FieldCodec.ForMessage` (Google.Protobuf 3.21.9) не покрыт членами с `ref struct`, которые программа получает только от библиотеки (`Read(ref ParseContext)`, `WriteTagAndValue(ref WriteContext, T)`); ответ — `incomplete` (`result-unnamed`, `value-unnamed`), судьба `parser` — `unknown-execution`.
- Четыре границы сгенерированного слоя из раздела 3 `docs/PLAN.md` — генерация по зависимостям; работа, разрешённая в библиотечный метод, и interface call на коллекции; `reached-through-model`; пересланный тип — ран 5d B4d.
