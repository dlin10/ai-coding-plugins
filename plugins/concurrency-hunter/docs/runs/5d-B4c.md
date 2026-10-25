# 5d run B4c — инициализаторы типов в генерации библиотек

База сравнения — `4da13b0` (`4da13b0555851a233b7b90e91cb62aaae67dd4b5`, `HEAD` при записи). Вопрос 101 остаётся открытым: держатель подтверждён в режиме B, модели конструктора нет из-за незасеянного `JsonParser.NullValueDescriptor`, находка перенесена в `5d-C`. Решение рана: [ADR 0026](../adr/0026-library-generation-type-initializers.md), SPEC TD-008, TD-034b, TD-034c и G-6. Анализ программы и генерация CoreLib используют `run`; изменена генерация библиотек вне CoreLib. Ниже — реализованные требования и проверяющие их тесты; фактические прогоны evals, demo и базиса записаны в соответствующих разделах, замеры — отдельно.

Задача 7 выполнена по D-0023 и D-0024: записаны model evals с единственным разрешённым снижением `fateGoldEntries` 4 → 3 и новые замеры текущего генератора. Результаты и сдвиги — в разделах ниже. Исполняемый гейт задачи оставлен серверу.

## R1 — три обращения с инициализатором

`run` сохраняет исполнение; `inert` сохраняет reachable set, lowering, эффекты кучи и исполнение в общем обходе, но не доступы, срабатывания, discovery, code flow, construction-ребро и semantic gap через него; `off` исключает тело и активацию. Инертное исполнение сохраняет членство, родителя запущенной работе, передачу делегатов невидимому коду и признак однократности. Работа получает обычные исполнения. Обобщённый тип, содержащий на любой глубине тип драйвера или открытый параметр, исполняет инициализатор во всех режимах; слитый экземпляр исполняется, если исполняется хотя бы одно закрытие. Проверяют `TypeInitializerTreatmentMatrixTests`, `InertInitializerWorkMatrixTests`; прежние тесты движка на обращении по умолчанию сохранены.

## R2 — читатели инертного членства

Только происхождение аллокации и передача статики библиотеки невидимому коду по ссылке читают инертное исполнение как `TypeInitializer` при `run`; смешанное членство сохраняет происхождение и `library-state-store`. Пробы и свидетели, unresolved receivers, чужие доступы, незасеянные чтения, эффекты, деревья действий, setup-only, судьбы и подтверждение его исключают. Проверяют `InertInitializerSharingMatrixTests`, `LibraryStaticHandoffMatrixTests`, `GenerationHandoffsTests`, `ConfirmationAccessesTests` и `AllocationsTests`.

## R3 — переключение и проверка исключённого замыкания

Генерация вне CoreLib начинает с `inert` (режим A); переполнение последнего прохода судеб или подтверждения повторяет весь член с `off` (режим B). Промежуточный остановленный проход, после которого свидетель сравнения сужает следующий, сам не переключает режим. Такая ситуация сейчас бывает только в CoreLib, где переключения нет; решение проверено тестом функции выбора, без искусственной фикстуры (D-0012, D-0018). Граница 1500 тел и 16 контекстов сохранены. По рёбрам исходного обхода A проверяются все тела, достигаемые выключенным инициализатором, даже достигнутые также в B: статические handoff дают `library-state`, возможная работа — `incomplete` / `initializer-work-not-run`, с именем инициализатора. Конструктор Thread без запуска и async-итератор без перечисления работы не дают; callback-таймер без видимого запуска даёт. Проверяют `InitializerModeSwitchTests`, `InitializerReachedBodyScanMatrixTests`, `InitializerWorkStartMatrixTests` и `ReachableSetTests`.

## R4 — значение рецепта закрытой статики

Попытка B использует свою открытую копию библиотеки: holding-статика и auto-property записываемы для драйвера, а lowering и индекс сохраняют readonly-факты обычной копии. До Seed-ов закрытые загруженные хранилища с неисполняемым инициализатором получают значения рецепта; новые поля и закрытия из судеб или подтверждения повторяют синтез и оба прогона до неподвижной точки. На любой holding-глубине допускается только источник библиотеки (конструктор, фабрика, internal-конструктор, реализация интерфейса/абстрактного класса) либо статика другой сборки, без пробы, подкласса драйвера, делегата, дерева выражения, контейнера или собственной статики библиотеки; struct со ссылками не получает `default`. Кандидат откатывается целиком. Незаписываемое или непостроенное хранилище остаётся незасеянным, доступ вне setup к нему прямо или через ref — `unseeded-read`; отдельное засеянное закрытие не страдает, слитое пересекается. Граф рецепта — `LibraryBefore`, все читатели происхождения сохраняют семантику смешанных создателей; прочий setup — драйвер. Значение рецепта не Seed. Проверяют `RecipeStorageOpeningMatrixTests`, `RecipeValueMatrixTests`, `RecipeValueDiscoveryMatrixTests`, `RecipeValueUnseededMatrixTests` и `RecipeValueProvenanceMatrixTests`.

## R5 — согласованность форм статики

`LibraryStaticFormMatrixTests` сравнивает классификацию, причины, модель и коды причин `run`/A/B во всех допускаемых языком клетках. Исключены с причиной в `Allowed` только невозможные клетки. Разрешённые отличия B — отказ для незасеваемой статики и возможной работы инициализатора. Исключение D-0020: область статики, отданная по ссылке инициализатором, содержит Seed другого поля того же типа; `run` видит пробу в куче без порядка и отвечает `unknown-touch` / `probe-handed-to-unseen`, A и B отвечают `library-state` / `library-state-store`. Инициализатор на деле исполняется раньше Seed, и модели нет во всех трёх ответах. Обычный выбор значений драйвера не менялся.

## R6 — запись генерации

G-6 — схема 4: режим ответа, упорядоченные попытки с телами судеб и подтверждения, секунды стадий до первой попытки и внутри каждой попытки. Повторы стадий суммируются, `reachedBodies` относится к попытке ответа, сумма стадий равна общему времени с точностью округления до 0,1. Снимок evals хранит `mode`, без времён; файлы сгенерированных моделей сохраняют схему 2. Проверяют `GenerationRecordTests`, `GeneratedDocumentTests`, `GenerateCommandTests`; запись файлов — `RunGenerationTests`.

## R7 — единый предел члена

В `GenerationLimits` одна константа 120 с для рана, model evals и `generate`, общая для обеих попыток. Предел рана 900 с и различение timeout/budget/отмены сохранены; ран не хранит запись до среза. Команда и evals возвращают `timeout` с частичной записью, включая прерванную стадию; команда пишет JSON и завершает процесс кодом 0, внешняя отмена сохраняет поведение. CoreLib-тул сохраняет свои 5 минут. Проверяют `GenerationRecordTests`, `GenerateCommandTests`, `RunGenerationTests`; отрицательный прогон timeout-теста и проверки стадий записаны в `scratch/b4c-timeout-negative.log`, `scratch/b4c-limit-regressions.log`, `scratch/b4c-stage-regressions.log` задачей 6.

## Исправления F-0045–F-0047

- F-0045, R3: строка `TypeInitializers` сохраняет все закрытые ключи; `InitializerReachedBodies` проверяет каждый ключ предикатом `RunsTypeInitializer` для `off`. Наличие исполняемого закрытия, в том числе в слитом экземпляре, не исключает неисполняемое закрытие из синтаксической проверки. Регрессии: `InitializerModeSwitchTests.A_mixed_generic_initializer_is_scanned_after_the_automatic_switch`, `Initializer_scanning_uses_each_closed_key` (только исполняемые, только выключенные, смешанные и вложенные аргументы), `TypeInitializerTreatmentMatrixTests.A_merged_running_initializer_still_has_a_non_running_closure_to_scan`.
- F-0046, R3: записанное ребро хранит свой вид. Достижимость и причины её построения не меняются; ребро переданного callback, фабрики, моделируемого вызова пользовательского члена или spawn не становится целью исходного вызова. Оба читателя — `EffectReader.ReadInitializerBodies` и `InitializerWork.Starts` — выбирают собственные цели через `InitializerWork.CallTargets`. Регрессии: `InitializerModeSwitchTests.An_argument_delegate_does_not_resolve_a_static_handoff_after_the_automatic_switch`, `Only_the_calls_own_targets_resolve_a_static_handoff` и `Argument_delegates_leave_each_unseen_call_kind_unresolved` покрывают lambda, method group, async-lambda, разрешённые и неразрешённые вызовы, static/virtual/interface/constrained/constructor/delegate invocation.
- F-0047, R11: SPEC TD-034b и ADR 0026 описывают одну ленивую копию `off` на результат компиляции библиотеки, общую для попыток всех её членов и освобождаемую вместе с результатом. CONTEXT и Driver synthesis не ограничивают её срок одной попыткой. Контракт `LibraryCompilation.RecipeCopy` и его существующий тест не меняются.

Регрессии воспроизведены до исправления: `scratch/b4c-review-red.log` и `scratch/b4c-review-red-handoff.log` содержат отказы смешанных закрытий, callback-передачи статики и ложное срабатывание async-аргумента. Окончательный целевой прогон — 337 Passed, 0 Failed, 0 Skipped (`scratch/b4c-review-final-targeted.log`): классы `InitializerModeSwitchTests`, `ReachableSetTests`, `InitializerWorkStartMatrixTests`, `TypeInitializerTreatmentMatrixTests`, `InitializerReachedBodyScanMatrixTests`, `CancellationCoverageTests`. Точечная проверка окончательной сборки под `CH_MODEL_EVALS=1` — 6 Passed, 0 Failed, 0 Skipped (`scratch/b4c-review-evals.log`): `ModelEvalTests.Message_parser_holder_is_covered_and_confirmed_with_initializers_left_out` и `EffectsEvalTests`. Снимок evals не изменён исправлениями; все 21 ответа файлов demo, кроме отпечатка генератора, и весь срез `counts`/`coverage` совпадают с состоянием перед исправлениями. Исполняемые G1–G3 и `check-baseline-rerecord.ps1` окончательного базиса оставлены серверу.

## Исправления F-0049–F-0051

- F-0049, правило R3/D-0021: записанное ребро reachable set не доказывает разрешённый получатель. В `ReadInitializerBodies` одна функция `HandoffArguments` считает virtual/interface/constrained-вызов передачей невидимому коду; только статический или невиртуальный вызов с телом прослеживается по сводке ссылочных параметров. `InitializerWork.CallTargets` сохраняет своё правило рёбер для запуска async-работы.
- F-0051, правило R3/D-0021: ссылка на статику сохраняет смысл передачи через `ref`/`in`/`out`-параметры помощников. Тот же скан вычисляет сводки до неподвижной точки; прямой путь, один и два помощника используют одно решение. Сохраняются сканирование каждого закрытого ключа и исходные виды рёбер обхода.
- F-0050, правило R4: выражение фабрики становится значением рецепта только с источником объекта в завершённой куче, в том числе для вложенного значения, держащего объекты. `ReadRecipeSources` дополняет набор незасеянных закрытых хранилищ; `MatchesUnseeded` читает его для проекции доступов, сводок и ссылочных доступов. Подтверждающий прогон проверяет тот же набор, а `Driver.UnseededRecipeStatics` и запись `Unseeded` включают найденные хранилища. Круг `Box.Make() => State` без аллокации получает `incomplete / unseeded-read`, без модели.

`InitializerReachedBodyScanMatrixTests.Off_refuses_wherever_running_initializers_hand_a_static_to_unseen_code` сравнивает Run и Off в 168 клетках: восемь видов получателя, четыре канала (ref/in/out/struct receiver), прямой путь и пути через одного/двух помощников, без callback и с lambda. Недопустимое boxing-преобразование struct-получателя исключено `Allowed`. Список known-wider содержит только известные virtual/interface-получатели, разрешённые кучей Run; constrained-получатель этой фикстуры не разрешён и в Run. Ни в одной клетке Off не уже Run. Тест `Only_the_calls_own_targets_resolve_a_static_handoff` переименован в `Only_the_calls_own_targets_leave_virtual_handoffs_unresolved`: D-0021 меняет утверждение для разрешённого в Run interface-получателя на отказ в Off.

`RecipeObjectSourceTests` проверяет свежий объект, фабрику только из статики, фабрику с обоими источниками, вложенную фабрику, автоматический переход после 1500 тел и обнаружение хранилища подтверждением. Свежий и смешанный источник сохраняют `library-state`, пустой источник даёт `incomplete / unseeded-read`. Усиленная регрессия автоматического перехода в `InitializerModeSwitchTests` конструирует override, но берёт получателя через extern.

Обратная проверка: при временном снятии двух правил источника и передачи девять регрессий упали (`scratch/b4c-review2-red.log`, 9 Failed, 5 Passed); после восстановления целевой прогон дал 299 Passed, 0 Failed, 0 Skipped (`scratch/b4c-review2-targeted.log`). Он включает классы InitializerReachedBodyScanMatrixTests, RecipeObjectSourceTests, InitializerModeSwitchTests, RecipeValueUnseededMatrixTests, RecipeValueDiscoveryMatrixTests, RecipeValueProvenanceMatrixTests и CancellationCoverageTests. Roslyn проверил Core и зависимые проекты: 0 errors, 0 warnings. SPEC TD-034b и ADR 0026 описывают эти правила в готовом виде. Точечные model evals с `CH_MODEL_EVALS=1`: 6 Passed, 0 Failed, 0 Skipped (`scratch/b4c-review2-evals.log`), обе версии MessageParser<T> и EffectsEvalTests. Снимок eShop, CoreLib TSV и файл ожиданий demo совпадают с HEAD (проверка `git diff --quiet HEAD` по этим трём файлам, exit 0).

Итог проверки demo после F-0049–F-0051 принят решением D-0023, которое переопределило R8: конструктор `MessageParser<T>` подтверждает `factory = holder result` в режиме `off`, но модели нет — `incomplete` с причиной `unseeded-read`. Диагностическая публикация прошла (exit 0, `scratch/b4c-review2-publish.log`), затем `record-demo-models.ps1` записал 21 файл и подтвердил, что чтение ничего не генерирует (exit 0, `scratch/b4c-review2-record-demo.log`). Сравнение с сохранённым состоянием `scratch/review2-artifacts-before.json` без поля generator выявило единственный сдвиг ответа Google.Protobuf 3.30.2: `M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0})`, model → refusal `incomplete`, cause `unseeded-read` (R4). В диагностическом `scratch/demo-read.json` не было добавленных отпечатков, исчез только `626883f8e2808cb2` — находка `protobuf-parser-factory-at-parse/factory-runs-at-parse`. По D-0023 находка перенесена в `5d-C`, а точка `5d-B4c` сохраняет находки и отпечатки `HEAD` и получает notDefect `/descriptor-unseeded`. Окончательные файлы моделей и снимок demo записаны задачей 8 в разделе [Demo](#demo); снимок evals, включая единственную потерянную модель конструктора, записан задачей 7 в разделе [Model evals](#model-evals).

### F-0050 — диагностика D-0022

Диагностический `Trace` Google.Protobuf 3.30.2 с переопределением `Off` завершён текущим генератором (лог `scratch/b4c-review3-probe.log`); изучены исходник открытой копии, драйвер, сводки и завершённые кучи судеб и подтверждения. Причина — `unseeded-read: Google.Protobuf:Google.Protobuf.MessageParser.Extensions in body:Google.Protobuf:M:Google.Protobuf.MessageParser`1.ParseFrom(System.Byte[])`.

1. Предположение о прямом чтении через пустую статику не подтвердилось. `MessageParser.Extensions` — **instance** auto-property (`IrFieldRef.IsStatic = false`, readonly). Получатель загрузки в `ParseFrom(byte[])` — аллокация `new MessageParser<Probe_T>(...)` драйвера, не статический регион. Публичный конструктор `MessageParser<T>(Func<T>)` вызывает внутренний конструктор с `false, (ExtensionRegistry)null`; базовый конструктор присваивает `Extensions = extensions`. Это обычное допустимое null-значение свойства.
2. Символ Extensions попал в `Driver.UnseededMembers` по другой ветке Seed: `static TypeRegistry.Empty → TypeRegistry.fullNameToMessageMap.Values → MessageDescriptor.Parser → MessageParser.Extensions`. Есть также пути через `MessageDescriptor.ContainingType` и `NestedTypes`. Идентичность незасеянного Seed-пути — определение поля, как до этого исправления; она не различает эти экземпляры парсера и парсер, который создаёт драйвер для триггера. Новое чтение **всех** `UNSEEDED_READ` подтверждающим EffectReader ошибочно распространило этот Seed-отказ на триггер. Это не результат проверки `UnseededRecipeStatics` и не пустой источник загрузки Extensions.
3. `TypeRegistry.Empty` сам не получил присваивания рецепта: в драйвере есть только цикл засева по `TypeRegistry.Empty.fullNameToMessageMap.Values`. Публичного либо internal-конструктора TypeRegistry нет: единственный конструктор `private TypeRegistry(Dictionary<string, MessageDescriptor>)`. Публичные фабрики `FromFiles(FileDescriptor[])`, `FromFiles(IEnumerable<FileDescriptor>)`, `FromMessages(MessageDescriptor[])`, `FromMessages(IEnumerable<MessageDescriptor>)` требуют контейнер, запрещённый для значения рецепта. Чтение собственного shared-свойства Empty также запрещено. Существующие допустимые кандидаты поэтому не строят реестр; Empty остаётся незасеянным. Указанное в предыдущем сообщении «recipe-assigned closed storage без объекта, через которое читается Extensions» отсутствует.
4. В исходнике Google.Protobuf 3.30.2 инициализатор TypeRegistry присваивает `Empty = new TypeRegistry(new Dictionary<string, MessageDescriptor>())`: при `run` реестр и пустой словарь создаются, а цикл засева по Values не находит дескрипторов и не получает MessageParser через эту ветку. Инициализатор не присваивает Extensions — это instance-свойство, которое присваивает конструктор парсера.
5. До F-0050 проверка unseeded Seed-путей не расширялась на подтверждающий прогон. `off` действительно не строил TypeRegistry.Empty и цикл по Values не находил объектов; однако подтверждение держателя не зависело от парсера из этой статики: оно строило свой MessageParser для каждого триггера и вызывало фабрику непосредственно из его ParseFrom. Сами fates после F-0050 остаются `factory = holder result`; теряется модель из-за нового Seed-отказа. Требование R4 об unseeded **recipe storage** подтверждения не требует этого расширения проверки Seed-путей (они вне объёма рана). Для загрузки Extensions новый источник не нужен: конструктор намеренно записывает null.

Дальнейшее исправление ограничивает подтверждающее чтение незасеянностью значений рецепта, сохраняя прежние читатели Seed-путей; правило отката кандидатов без источника объекта применяется отдельно, в существующем порядке источников. Частные конструкторы, собственная статика библиотеки и default struct-with-references источниками не становятся.

### F-0050 — результат отката кандидатов и оставшаяся граница R8

Реализовано по R4/D-0022: `TryCandidate` использует один набор отвергнутых выражений рецепта, который получается из завершённой off-кучи (`ReadRecipeCandidates` / `HasObjectSource`). При отсутствии источника выбранное выражение откатывается существующей транзакцией, а прежний порядок выбирает следующую фабрику либо internal-конструктор. Генерация повторяет синтез и прогоны, пока растёт набор отказов или закрытых хранилищ. Вложенный пустой кандидат отвергается отдельно, поэтому внешний конструктор может получить следующий пригодный внутренний источник. Других источников, открытия private-конструкторов и значений default для struct-with-references не добавлено. Подтверждение читает только незасеянные хранилища рецепта; обычные Seed-пути сохраняют прежних читателей.

Проверки: `scratch/b4c-review3-targeted.log` — 308 Passed, 0 Failed, 0 Skipped (RecipeObjectSourceTests, RecipeValueMatrixTests, RecipeValueUnseededMatrixTests, RecipeValueDiscoveryMatrixTests, RecipeValueProvenanceMatrixTests, InitializerReachedBodyScanMatrixTests, InitializerModeSwitchTests, CancellationCoverageTests). При временном снятии отката и ограничения Seed-читателя пять регрессий упали (`scratch/b4c-review3-red.log`, 5 Failed, 6 Passed); фиксы восстановлены. Новый тест проверяет откат аргумента Marker при выборе следующей фабрики и internal-конструктора на верхнем и вложенном уровнях; другой — instance-чтение Extensions на новом объекте при наличии одноимённого незасеянного Seed-пути из иной статики. Исходный круг Box.Make() → State остаётся incomplete / unseeded-read. В `A_factory_static_cycle_discovered_by_confirmation_is_unseeded` число подтверждений 2 → 3: обнаружение статик, проверка первого кандидата и повтор после его отката; утверждение о незасеянности сохранено.

Повтор R8 усилен проверкой наличия модели в `ModelEvalTests.Message_parser_holder_is_covered_and_confirmed_with_initializers_left_out`. Обе версии, 3.30.2 и 3.21.9, проходят проверки режима off, границы подтверждения и судьбы holder result, но падают на модели (`scratch/b4c-review3-parser-causes.log`, 2 Failed). Причина теперь **не Extensions**: `unseeded-read: Google.Protobuf:Google.Protobuf.JsonParser.NullValueDescriptor in body:Google.Protobuf:M:Google.Protobuf.JsonParser.IsGoogleProtobufNullValueField(Google.Protobuf.Reflection.FieldDescriptor)`.

Диагностический Trace 3.30.2 (`scratch/b4c-review3-chain.log`; данные `scratch/review3-null-descriptor-facts.txt`, исходник `scratch/review3-null-descriptor-source.txt`, драйвер `scratch/review3-final-driver.cs.txt`) подтвердил полный путь: `ModelDriver.T19 → MessageParser.ParseJson(string) → JsonParser.Merge(IMessage,string) → Merge(IMessage,TextReader) → Merge(IMessage,JsonTokenizer) → MergeField → IsGoogleProtobufNullValueField`. ParseJson получает реально построенный `JsonParser.defaultInstance = new JsonParser(new JsonParser.Settings(default(int)))`. В `IsGoogleProtobufNullValueField` есть прямое чтение `NullValueDescriptor.FullName` для сравнения с `field.EnumType.FullName`. Закрытое хранилище `(Google.Protobuf:Google.Protobuf.JsonParser, NullValueDescriptor)` в окончательном наборе unseeded; присваивания рецепта ему нет. Старый off-прогон действительно молча не проверял это чтение подтверждения; модель держателя зависела от отсутствия этой проверки, хотя сама судьба holder result подтверждается и сейчас.

Причина отсутствия кандидата: тип NullValueDescriptor — EnumDescriptor. Публичных фабрик библиотеки, результат которых неявно приводится к EnumDescriptor, **нет** (проверено по символам скомпилированной копии). Единственный конструктор — `internal EnumDescriptor(EnumDescriptorProto, FileDescriptor, MessageDescriptor, int, System.Type)`. EnumDescriptorProto строится публичным конструктором, но FileDescriptor требует своего private-конструктора либо публичной фабрики `FromGeneratedCode(byte[], FileDescriptor[], GeneratedClrTypeInfo)`; она требует запрещённых контейнеров. Private-конструктор FileDescriptor также требует ByteString, FileDescriptorProto, IList<FileDescriptor>, DescriptorPool и GeneratedClrTypeInfo. Объект System.Type — ещё один объектный аргумент. Поэтому разрешённые источники не строят EnumDescriptor; повтор выбора это не исправляет.

При run исходник JsonParser задаёт `NullValueDescriptor = StructReflection.Descriptor.EnumTypes.Single(ed => ed.ClrType == typeof(NullValue))`. Инициализатор StructReflection строит descriptor через `FileDescriptor.FromGeneratedCode` с настоящими base64-данными struct.proto, пустым массивом зависимостей и GeneratedClrTypeInfo с typeof(NullValue) и сведениями о сообщениях. Это библиотечный граф дескрипторов, не один объект, который можно получить простым чтением выключенной статики. Самый прямой единичный новый источник — элемент StructReflection.Descriptor.EnumTypes для typeof(NullValue), **честно построенный штатной инициализацией**. Одного разрешения читать ту же выключенную статику недостаточно: объекта всё равно нет. Такой источник требует решения о выполнении/воспроизведении графа инициализатора (включая массивы, metadata Type и callbacks), либо нескольких новых источников аргументов конструктора; в этом ходе он не введён.

Стоимость, которую подтверждает текущий Trace: inert — 21 тело судеб, 2006 тел подтверждения; off после откатов — 29 и 413 тел, три подтверждающих прохода. Таким образом, простое возвращение полного исходного инертного подтверждения превышает прежнюю границу 1500 тел на 506. Стоимость отдельного честного среза StructReflection не измерена: такого разрешённого источника в реализации нет, и эти числа не доказывают размер его среза. Запуск диагностики не является R10-замером generate.

Итог D-0022 закреплён решениями D-0023/D-0024: R8 требует подтверждённого держателя без модели, поскольку цепочка `ParseJson(string) → JsonParser.IsGoogleProtobufNullValueField` читает `JsonParser.NullValueDescriptor`, а допустимые источники не строят его `EnumDescriptor` без графа сгенерированной рефлексии `FileDescriptor`. Новых источников и изменения границы не потребовалось; диагностические 2006 тел подтверждения `inert` и 413 тел `off` объясняют переключение и сохранённую границу. Находка перенесена в `5d-C`, вопрос 101 остаётся открытым. По D-0024 пол `fateGoldEntries` вручную снижен ровно с 4 до 3 для этой единственной потерянной модели; механизм записи снимка сохранён. Задача 7 записала снимок и замер текущего генератора в разделах [Model evals](#model-evals) и [Замеры](#замеры), задача 8 — опубликованный exe, файлы моделей, снимок demo и базис в разделе [Demo](#demo). G1–G3 остаются серверу.

## Model evals

Снимок `skills/hunt/evals/models/generator-snapshot.json` перезаписан в Release под `CH_MODEL_EVALS=1` и `CH_MODEL_EVALS_RECORD=1`. До записи вручную изменён только `fateGoldEntries`: 4 → 3 — R4, D-0024. Механизм `ModelEvals.RecordSnapshot` не изменён. Классы запущены последовательно, каждый в собственном процессе `pwsh -NoProfile`; логи — `scratch/b4c-task7/record-<class>.log`.

- `ModelEvalTests`: Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21, Duration: 6 m 12 s - ConcurrencyHunter.Core.Tests.dll (net10.0)
- `EffectsEvalTests`: Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 2 m - ConcurrencyHunter.Core.Tests.dll (net10.0)
- `LinqOracleTests`: Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 35 s - ConcurrencyHunter.Core.Tests.dll (net10.0)
- `AccessorEvalTests`: Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6, Duration: 18 s - ConcurrencyHunter.Core.Tests.dll (net10.0)

Проверки опасного сужения судеб и эффектов прошли, `MessageParserCtor` точен по gold. Набор ответов сохранён: members — 18, effects — 18, linq — 129, accessors — 12. Набор пакетов, их `bodies`, `externBodies` и реализации (`assemblyVersion`, `fileVersion`, `mvid`) совпадают с `HEAD`. Единственная потеря модели среди members — конструктор `MessageParser<T>` с `modelReason = incomplete` и `unseeded-read`; это единственное разрешённое снижение порога R9. Снимок хранит `mode`, но не секунды (R6). Режимы ответов: inert — 169, off — 2, null — 6.

`ModelEvalTests.Message_parser_holder_is_covered_and_confirmed_with_initializers_left_out` использует `TraceWithin` и проверяет Google.Protobuf 3.21.9 и 3.30.2: 22 триггера, оба `ParseFrom(ReadOnlySpan<byte>)`, ни одного члена `System.Object`, непокрытых членов нет. Ответ — `off`; подтверждение `inert` больше 1500 тел, `off` не больше 1500, `factory = holder result`. Модели нет: `modelReason = incomplete`, причина `unseeded-read` называет `Google.Protobuf.JsonParser.NullValueDescriptor` (R4, R8, D-0023).

Сдвиги ответов относительно `HEAD` (отсутствующее поле записано как `null`; для структур названы все сдвинувшиеся поля):

- ``` M:Google.Protobuf.FieldCodec.ForMessage``1(System.UInt32,Google.Protobuf.MessageParser{``0}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0}) ``` (members): `modelReason` null → incomplete, `classified`, `model`, `causes`, `reachedBodies` 21 → 29, `mode` null → off — R2, R3, R4, R6 (D-0023).
- ``` M:Polly.AsyncPolicy.ExecuteAsync(System.Func{System.Threading.Tasks.Task}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:Polly.AsyncRetrySyntax.WaitAndRetryAsync(Polly.PolicyBuilder,System.Int32,System.Func{System.Int32,System.TimeSpan},System.Action{System.Exception,System.TimeSpan,System.Int32,Polly.Context}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:Polly.AsyncRetrySyntax.WaitAndRetryForeverAsync(Polly.PolicyBuilder,System.Func{System.Int32,System.TimeSpan},System.Action{System.Exception,System.Int32,System.TimeSpan}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:Polly.Policy.Execute(System.Action) ``` (members): `mode` null → inert — R3, R6.
- ``` M:Polly.RetrySyntax.WaitAndRetry(Polly.PolicyBuilder,System.Int32,System.Func{System.Int32,System.TimeSpan},System.Action{System.Exception,System.TimeSpan,System.Int32,Polly.Context}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.All``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.FirstOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.GroupBy``4(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2},System.Func{``1,System.Collections.Generic.IEnumerable{``2},``3}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.OrderBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SelectMany``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Collections.Generic.IEnumerable{``1}}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SingleOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.ToDictionary``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Where``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:System.Security.Claims.ClaimsPrincipal.FindFirst(System.Predicate{System.Security.Claims.Claim}) ``` (members): `mode` null → inert — R3, R6.
- ``` M:AutoMapper.IMapperBase.Map``1(System.Object) ``` (effects): `mode` null → inert — R3, R6.
- ``` M:AutoMapper.IMapperBase.Map``2(``0,``1) ``` (effects): `mode` null → inert — R3, R6.
- ``` M:FluentValidation.AbstractValidator`1.Validate(`0) ``` (effects): `mode` null → inert — R3, R6.
- ``` M:FluentValidation.AbstractValidator`1.ValidateAsync(`0,System.Threading.CancellationToken) ``` (effects): `mode` null → inert — R3, R6.
- ``` M:FluentValidation.IRuleBuilder`2.SetValidator(FluentValidation.IValidator{`1},System.String[]) ``` (effects): `mode` null → inert — R3, R6.
- ``` M:FluentValidation.IRuleBuilder`2.SetValidator(FluentValidation.Validators.IPropertyValidator{`0,`1}) ``` (effects): `mode` null → inert — R3, R6.
- ``` M:Google.Protobuf.Collections.RepeatedField`1.Add(`0) ``` (effects): `mode` null → inert — R3, R6.
- ``` M:Polly.RetrySyntax.WaitAndRetry(Polly.PolicyBuilder,System.Collections.Generic.IEnumerable{System.TimeSpan}) ``` (effects): `mode` null → inert — R3, R6.
- ``` M:System.Console.Write(System.Object) ``` (effects): `mode` null → inert — R3, R6.
- ``` M:System.Console.WriteLine(System.Object) ``` (effects): `mode` null → inert — R3, R6.
- ``` M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions) ``` (effects): `reason` closure-bound → timeout, `detail`, `reachedBodies` 1635 → 0, `mode` null → off — R3, R6, R7.
- ``` M:System.Threading.Channels.ChannelWriter`1.TryWrite(`0) ``` (effects): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Aggregate``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``0,``0})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Aggregate``2(System.Collections.Generic.IEnumerable{``0},``1,System.Func{``1,``0,``1})~``1 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Aggregate``3(System.Collections.Generic.IEnumerable{``0},``1,System.Func{``1,``0,``1},System.Func{``1,``2})~``2 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.All``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Boolean ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Any``1(System.Collections.Generic.IEnumerable{``0})~System.Boolean ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Any``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Boolean ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.AsEnumerable``1(System.Collections.Generic.IEnumerable{``0})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Decimal})~System.Decimal ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Double})~System.Double ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32})~System.Double ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int64})~System.Double ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Decimal}})~System.Nullable{System.Decimal} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Double}})~System.Nullable{System.Double} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int32}})~System.Nullable{System.Double} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int64}})~System.Nullable{System.Double} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Single}})~System.Nullable{System.Single} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Average``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Single})~System.Single ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Cast``1(System.Collections.IEnumerable)~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Contains``1(System.Collections.Generic.IEnumerable{``0},``0)~System.Boolean ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Count``1(System.Collections.Generic.IEnumerable{``0})~System.Int32 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Count``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Int32 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.DistinctBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Empty``1~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.ExceptBy``2(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.FirstOrDefault``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.FirstOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.FirstOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean},``0)~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.First``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.First``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.GroupBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{System.Linq.IGrouping{``1,``0}} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.GroupBy``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2})~System.Collections.Generic.IEnumerable{System.Linq.IGrouping{``1,``2}} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.GroupBy``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``1,System.Collections.Generic.IEnumerable{``0},``2})~System.Collections.Generic.IEnumerable{``2} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.GroupBy``4(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2},System.Func{``1,System.Collections.Generic.IEnumerable{``2},``3})~System.Collections.Generic.IEnumerable{``3} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.GroupJoin``4(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``2},System.Func{``1,``2},System.Func{``0,System.Collections.Generic.IEnumerable{``1},``3})~System.Collections.Generic.IEnumerable{``3} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.IntersectBy``2(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Join``4(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``2},System.Func{``1,``2},System.Func{``0,``1,``3})~System.Collections.Generic.IEnumerable{``3} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.LastOrDefault``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.LastOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.LastOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean},``0)~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Last``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Last``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.LeftJoin``4(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``2},System.Func{``1,``2},System.Func{``0,``1,``3})~System.Collections.Generic.IEnumerable{``3} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.LongCount``1(System.Collections.Generic.IEnumerable{``0})~System.Int64 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.LongCount``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Int64 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.MaxBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Decimal})~System.Decimal ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Double})~System.Double ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32})~System.Int32 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int64})~System.Int64 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Decimal}})~System.Nullable{System.Decimal} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Double}})~System.Nullable{System.Double} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int32}})~System.Nullable{System.Int32} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int64}})~System.Nullable{System.Int64} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Single}})~System.Nullable{System.Single} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Max``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Single})~System.Single ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Max``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~``1 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.MinBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Decimal})~System.Decimal ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Double})~System.Double ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32})~System.Int32 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int64})~System.Int64 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Decimal}})~System.Nullable{System.Decimal} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Double}})~System.Nullable{System.Double} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int32}})~System.Nullable{System.Int32} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int64}})~System.Nullable{System.Int64} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Single}})~System.Nullable{System.Single} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Min``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Single})~System.Single ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Min``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~``1 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.OfType``1(System.Collections.IEnumerable)~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.OrderByDescending``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Linq.IOrderedEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.OrderBy``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Linq.IOrderedEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.RightJoin``4(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``2},System.Func{``1,``2},System.Func{``0,``1,``3})~System.Collections.Generic.IEnumerable{``3} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SelectMany``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Collections.Generic.IEnumerable{``1}})~System.Collections.Generic.IEnumerable{``1} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SelectMany``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,System.Collections.Generic.IEnumerable{``1}})~System.Collections.Generic.IEnumerable{``1} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SelectMany``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Collections.Generic.IEnumerable{``1}},System.Func{``0,``1,``2})~System.Collections.Generic.IEnumerable{``2} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SelectMany``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,System.Collections.Generic.IEnumerable{``1}},System.Func{``0,``1,``2})~System.Collections.Generic.IEnumerable{``2} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,``1})~System.Collections.Generic.IEnumerable{``1} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{``1} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SequenceEqual``1(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``0})~System.Boolean ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SingleOrDefault``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SingleOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SingleOrDefault``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean},``0)~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Single``1(System.Collections.Generic.IEnumerable{``0})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Single``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SkipWhile``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.SkipWhile``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Skip``1(System.Collections.Generic.IEnumerable{``0},System.Int32)~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Decimal})~System.Decimal ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Double})~System.Double ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32})~System.Int32 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int64})~System.Int64 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Decimal}})~System.Nullable{System.Decimal} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Double}})~System.Nullable{System.Double} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int32}})~System.Nullable{System.Int32} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Int64}})~System.Nullable{System.Int64} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Nullable{System.Single}})~System.Nullable{System.Single} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Sum``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Single})~System.Single ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.TakeWhile``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.TakeWhile``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Take``1(System.Collections.Generic.IEnumerable{``0},System.Int32)~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Take``1(System.Collections.Generic.IEnumerable{``0},System.Range)~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.ThenByDescending``2(System.Linq.IOrderedEnumerable{``0},System.Func{``0,``1})~System.Linq.IOrderedEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.ThenBy``2(System.Linq.IOrderedEnumerable{``0},System.Func{``0,``1})~System.Linq.IOrderedEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.ToArray``1(System.Collections.Generic.IEnumerable{``0})~``0[] ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.ToDictionary``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Collections.Generic.Dictionary{``1,``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.ToDictionary``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2})~System.Collections.Generic.Dictionary{``1,``2} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.ToList``1(System.Collections.Generic.IEnumerable{``0})~System.Collections.Generic.List{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.ToLookup``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Linq.ILookup{``1,``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.ToLookup``3(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1},System.Func{``0,``2})~System.Linq.ILookup{``1,``2} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.UnionBy``2(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Union``1(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``0})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Where``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Where``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Int32,System.Boolean})~System.Collections.Generic.IEnumerable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Enumerable.Zip``3(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``1,``2})~System.Collections.Generic.IEnumerable{``2} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.Any``1(System.Linq.IQueryable{``0})~System.Boolean ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.Any``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~System.Boolean ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.OrderBy``2(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,``1}})~System.Linq.IOrderedQueryable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.OrderBy``2(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,``1}},System.Collections.Generic.IComparer{``1})~System.Linq.IOrderedQueryable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}},``0)~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0},``0)~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.Single``1(System.Linq.IQueryable{``0})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.Single``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~``0 ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.Skip``1(System.Linq.IQueryable{``0},System.Int32)~System.Linq.IQueryable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.Take``1(System.Linq.IQueryable{``0},System.Int32)~System.Linq.IQueryable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.Take``1(System.Linq.IQueryable{``0},System.Range)~System.Linq.IQueryable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.Where``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~System.Linq.IQueryable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Linq.Queryable.Where``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Int32,System.Boolean}})~System.Linq.IQueryable{``0} ``` (linq): `mode` null → inert — R3, R6.
- ``` M:System.Collections.ObjectModel.ObservableCollection`1.add_CollectionChanged(System.Collections.Specialized.NotifyCollectionChangedEventHandler) ``` (accessors): `mode` null → inert — R3, R6.
- ``` M:System.Collections.ObjectModel.ObservableCollection`1.remove_CollectionChanged(System.Collections.Specialized.NotifyCollectionChangedEventHandler) ``` (accessors): `mode` null → inert — R3, R6.
- ``` M:System.Collections.Specialized.ListDictionary.get_Item(System.Object) ``` (accessors): `mode` null → inert — R3, R6.
- ``` M:System.Collections.Specialized.ListDictionary.set_Item(System.Object,System.Object) ``` (accessors): `mode` null → inert — R3, R6.
- ``` M:System.ComponentModel.BackgroundWorker.add_DoWork(System.ComponentModel.DoWorkEventHandler) ``` (accessors): `mode` null → inert — R3, R6.
- ``` M:System.ComponentModel.BackgroundWorker.add_RunWorkerCompleted(System.ComponentModel.RunWorkerCompletedEventHandler) ``` (accessors): `mode` null → inert — R3, R6.
- ``` M:System.ComponentModel.Component.set_Site(System.ComponentModel.ISite) ``` (accessors): `mode` null → inert — R3, R6.
- ``` M:System.Console.add_CancelKeyPress(System.ConsoleCancelEventHandler) ``` (accessors): `mode` null → inert — R3, R6.
- ``` M:System.Diagnostics.Process.add_Exited(System.EventHandler) ``` (accessors): `mode` null → inert — R3, R6.
- ``` M:System.IO.FileSystemWatcher.add_Changed(System.IO.FileSystemEventHandler) ``` (accessors): `mode` null → inert — R3, R6.
- ``` M:System.IO.FileSystemWatcher.remove_Changed(System.IO.FileSystemEventHandler) ``` (accessors): `mode` null → inert — R3, R6.
- ``` M:System.Net.Security.SslClientAuthenticationOptions.set_RemoteCertificateValidationCallback(System.Net.Security.RemoteCertificateValidationCallback) ``` (accessors): `mode` null → inert — R3, R6.

Сдвиги порогов и чисел пакетов:

- `exact`: 11 → 12 — R3, R4: подтверждён factory = holder result.
- `fateGoldEntries`: 4 → 3 — R4, D-0024: подтверждённый holder result не даёт модели при чтении незасеянного NullValueDescriptor.

## Замеры

Команда -Measure повторена на итоговом генераторе после F-0057–F-0060; лог scratch/b4c-gate/r4-measure.log, источник scratch/b4c-gate/measure.json. Каждый член — отдельный процесс CLI Release; порогов нет.

Отпечаток генератора (ADR 0023): `38b1e51e38b7f49a4fd59450ac310853571100a4bd15c794c25d037473168051`. Все времена — секунды.

- ``` M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions) ```: mode off, reason timeout; до попыток: resolve=0.0, compileLibrary=12.8, engineClaims=0.1; попытка inert: reachedBodies 1635, confirmationBodies null; стадии: synthesize=1.1, analyze=90.9; попытка off: reachedBodies 0, confirmationBodies null; стадии: synthesize=0.1, compileLibrary=1.5, analyze=13.5; генерация 120.0 с, процесс 120.5 с; implementation: assemblyVersion 10.0.0.0, fileVersion 10.0.1326.50504, mvid f305635c-7d90-4597-8b9d-e051338bb803.
- ``` M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0}) ```: mode off, reason null; до попыток: resolve=0.1, compileLibrary=5.4, engineClaims=0.1; попытка inert: reachedBodies 21, confirmationBodies 2006; стадии: synthesize=0.2, analyze=4.6, classify=0.1, confirmHolder=4.8, readEffects=0.1, buildModel=0.0; попытка off: reachedBodies 29, confirmationBodies 409; стадии: synthesize=0.6, compileLibrary=1.0, analyze=3.4, classify=0.0, confirmHolder=3.3, readEffects=1.0, buildModel=0.0; генерация 24.7 с, процесс 24.9 с; implementation: assemblyVersion 3.30.2.0, fileVersion 3.30.2.0, mvid 601a02d3-9487-45ce-a28b-99b5cb103efd.

Реализация System.Text.Json сверяется со снимком evals по assemblyVersion, fileVersion и mvid; Google.Protobuf — assemblyVersion/fileVersion 3.30.2.0.

| member | reachedBodies | HEAD reachedBodies |
| --- | --- | --- |
| ``` M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0}) ``` | 29 | 21 |
| ``` M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions) ``` | 0 | 1635 |

MessageParser<T> confirmation: 409 vs 402

Значение рецепта не возвращает подтверждение держателя MessageParser<T> к замыканию больше 1500 тел. Модели конструктора нет из-за unseeded-read. Для timeout SerializeToUtf8Bytes число reachedBodies не описывает завершённое замыкание; остальные вопросы роста значений рецепта остаются открытыми.

- timeout: M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions)

## Demo

Точка сравнения — `5d-B4c`.

У всех пяти проектов demo и всех 30 проектов eShop проверено наличие `obj/project.assets.json`; восстановление не потребовалось. Выполнена публикация exe в Release: `dotnet publish src/ConcurrencyHunter.Cli/ConcurrencyHunter.Cli.csproj -c Release -r win-x64 --self-contained false -o bin/win-x64 --nologo` (код 0, лог `scratch/b4c-gate/r3-final-publish.log`). Затем `./build/record-demo-models.ps1` завершился с кодом 0, вывод (лог `scratch/b4c-gate/r3-final-record-demo.log`):

```text
21 generated file(s) below demo/.concurrency-hunter/models/generated; a run that only reads them generates nothing.
```

Во всех 21 файле один текущий отпечаток генератора — `38b1e51e38b7f49a4fd59450ac310853571100a4bd15c794c25d037473168051`, совпадающий с повторными замерами R10 в `scratch/b4c-gate/measure.json`. Новых файлов нет; схема 2 сохранена. Сравнение всех 21 файлов с `HEAD` даёт ровно три изменившихся ответа (перечень ниже); остальные ответы совпадают, кроме отпечатка генератора. Ответов `timeout` и `budget` нет, в том числе у `Polly.ResiliencePipeline.Execute<T>`; повторный прогон `--generation read` не оставил `not-generated`.

Конструктор ``` M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0}) ``` (Google.Protobuf 3.30.2) подтверждает судьбу `factory` `holder` `result` в режиме B (`off`), но модели нет: `modelReason` `incomplete`, причина `unseeded-read`. Триггер `ParseJson(string)` читает `JsonParser.NullValueDescriptor`; допустимые источники значения рецепта не строят этот `EnumDescriptor`, которому нужен `FileDescriptor` сгенерированной рефлексии (R4, R8, D-0023). Ответ ``` M:Google.Protobuf.MessageParser`1.ParseFrom(System.Byte[]) ``` — `unknown-touch` с причинами `receiver-member-unseen`, `result-several-objects`, `unseeded-read`, без модели.

Изменившиеся ответы сгенерированных файлов относительно `HEAD`:

- ``` M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0}) ```: модель с `factory: unknown-execution` → отказ `incomplete` с `unseeded-read` — R3, R4: держатель подтверждён после повторения без инициализаторов, но значение рецепта `JsonParser.NullValueDescriptor` не строится.
- `M:Polly.ResiliencePipeline.Execute(System.Action)`: из `causes` удалена `witness-value-accessed-elsewhere`; причина `unknown-touch`, отсутствие модели и остальные коды причин сохранены — R2: при проверке чужих доступов членство инертного исполнения не читается.
- ``` M:Polly.ResiliencePipeline.Execute``1(System.Func{``0}) ```: из `causes` удалена `witness-value-accessed-elsewhere`; причина `unknown-touch`, отсутствие модели и остальные коды причин сохранены — R2: при проверке чужих доступов членство инертного исполнения не читается.

`CURRENT_POINT` равен `5d-B4c`. В `demo/expected-findings.json` относительно `HEAD` ровно две правки R8: `protobuf-parser-factory-at-parse/factory-runs-at-parse` перенесён в `5d-C`; добавлен `notDefect` `protobuf-parser-factory-at-parse/descriptor-unseeded` с `phase` `5d-B4c`, `until` `5d-C` и прежним ресурсом кейса. `factory-fires-in-type-initializers` по-прежнему кончается на `5d-B4c`. Строка кейса в `demo/SCENARIOS.md` описывает эти ответы и вердикты.

Снимок `src/ConcurrencyHunter.Cli.Tests/Snapshots/demo.json` записан из `scratch/demo-read.json` — результата `metrics --target demo/Demo.slnx --generation read` внутри `record-demo-models.ps1`, той же сборкой Release. Сохранён исходный срез `counts` и `coverage` через `[IO.File]::WriteAllText`, без завершающего перевода строки. Все `counts`, в том числе 267 находок и каждый их отпечаток, равны `HEAD`: находки не появились, не исчезли и не сменили confidence. Все сдвиги снимка относительно `HEAD`:

- `coverage[Demo.Web].counters.delegate-to-opaque`: 10 → 12 — R4, R8: оба вызова конструктора `MessageParser<T>` не имеют модели из-за незасеянного дескриптора; переданные фабрики учитываются как делегаты невидимому коду.
- `coverage[Demo.Web].counters.known-call`: 333 → 331 — R4, R8: два вызова конструктора перестают быть известными вызовами сгенерированной модели.
- `coverage[Demo.Web].counters.known-call-generated`: 5 → 3 — R4, R8: модель конструктора не применяется в двух закрытиях `MessageParser<T>`.
- `coverage[Demo.Web].counters.opaque-call`: 1306 → 1308 — R4, R8: два вызова конструктора остаются opaque при отказе `incomplete`, `unseeded-read`.
- `coverage[Demo.Web].counters.semantic-gap`: 17 → 19 — R4, R8: каждый из двух opaque-вызовов конструктора даёт semantic gap.
- ``` coverage[Demo.Web].generationRefusals[M:Google.Protobuf.MessageParser`1.#ctor(System.Func{`0})] ```: added — R4, R8: записан отказ генерации конструктора `incomplete` вместо модели.
- `coverage[Demo.Web].semanticGaps[Google.Protobuf.MessageParser<Demo.Web.Cases.GrpcServiceMethod.GreetReply>..ctor(Func<GreetReply>)]`: added — R4, R8: opaque-вызов конструктора для `GreetReply`.
- `coverage[Demo.Web].semanticGaps[Google.Protobuf.MessageParser<Demo.Web.Cases.ProtobufParserFactoryAtParse.Msg>..ctor(Func<Msg>)]`: added — R4, R8: opaque-вызов конструктора для `Msg`.

Первый полный прогон `./build/check-test-baseline.ps1 -Record -KeepResults scratch/b4c-task8-baseline-results` с `CH_ESHOP_ROOT=C:\Dev\eShopOnContainers`, `CONCURRENCYHUNTER_REQUIRE_E2E=1` и без `CH_MODEL_EVALS`/`CH_MODEL_EVALS_RECORD` прошёл тесты: CLI — 152 Passed, 0 Failed, 0 Skipped; Core — 11648 Passed, 0 Failed, 39 Skipped. Запись базиса в этом прогоне не состоялась: `Set-Content` сообщил, что `build/test-baseline.txt` занят другим процессом; лог `scratch/b4c-task8-baseline.log`, TRX сохранены. Повторный полный прогон `./build/check-test-baseline.ps1 -Record -KeepResults scratch/b4c-task8-baseline-retry-results` выполнен в отдельном `pwsh -NoProfile` с тем же окружением, код 0 (лог `scratch/b4c-task8-baseline-retry.log`): CLI — 152 Passed, 0 Failed, 0 Skipped; Core — 11648 Passed, 0 Failed, 39 Skipped. Скрипт записал 11644 уникальных кейса, 11605 Passed. Все семь классов гейта задачи 8 и все четыре eShop-теста прошли, включая сравнение снимка eShop, аудит worklist, census и перестановки обходов. `DemoGeneratedModelsTests.Polly_execute_of_T_is_no_timeout` прошёл. Команда `./build/check-baseline-rerecord.ps1` завершилась с кодом 0 (лог `scratch/b4c-task8-baseline-rerecord.log`): 11605 Passed против 11463 в `HEAD`, потеряно 0.

`src/ConcurrencyHunter.Cli.Tests/Snapshots/eshop.json` и `skills/hunt/evals/models/corelib-generation.tsv` равны `HEAD` по `git diff --quiet HEAD`. Исполняемый гейт задачи 8 оставлен серверу.

## Документы

Задача 9 согласовала ADR 0026, SPEC (TD-008, TD-034b, TD-034c, G-6 и 12.1 п. 8a), CONTEXT, Driver synthesis, PLAN, QUESTIONS и README model evals с итоговыми ответами. Вопрос 101 остаётся открытым: держатель `MessageParser<T>` подтверждён, но модели конструктора нет без значения рецепта `JsonParser.NullValueDescriptor`; находка перенесена в `5d-C`. Пол снимка `fateGoldEntries` — 3, ручное снижение 4 → 3 ограничено этим отказом (D-0024), механизм записи не менялся. Строка 178 QUESTIONS, раздел 3 PLAN и правила движка TD-060a, TD-061 и TD-065 не менялись.

`./build/check-docs.ps1` выполнен отдельным процессом `pwsh -NoProfile`, код выхода 0; лог — `scratch/b4c-task9-docs.log`. SPEC и CONTEXT не называют фазы, раны, вопросы, интервью или даты разработки. Проверки задачи не включали повторный прогон движка или evals; исполняемый гейт задачи 9 оставлен серверу. Повторный вывод `generate-corelib.ps1 -Since` ниже совпал с прежней записью; лог последнего повторения — `scratch/b4c-gate/r3-final-corelib-since.log`.

## Генерация CoreLib

`./build/generate-corelib.ps1 -Since` выполнен отдельным процессом `pwsh -NoProfile`, код выхода 0; полный вывод — `scratch/b4c-task9-corelib-since.log` и ниже. Команда ничего не генерирует и перечисляет изменения генератора, движка и входов сборки относительно коммита таблицы. Генерация CoreLib и её тул не менялись: обращение `run`, предел тула 5 минут; `skills/hunt/evals/models/corelib-generation.tsv` сохранён равным `HEAD`. Полная перегенерация остаётся решением владельца по ADR 0022.

```text
Recorded on 24669ef55059c8053292b745f1bd258dc787caf3; HEAD is 4da13b0555851a233b7b90e91cb62aaae67dd4b5.

generator: 11 commit(s)
  93fed8a concurrency-hunter: ValueObservation visits each value once per query instead of every simple path
  c40b4a3 concurrency-hunter: driver synthesis skips inapplicable factory producers before a candidate's snapshot and rolls back names and unseeded paths by journal
  d882b66 concurrency-hunter: ValueProvenance reads keepers through a store index instead of rescanning the call's instances
  4948f34 concurrency-hunter: 5d run B4b - every library's generation reaches only the types it constructs; the MessageParser<T> holder is covered (ADR 0019 amendment, ADR 0025)
  a70d961 concurrency-hunter: 5d run B4 - the generator's answers get precise: rollback leaves no trace, concrete collections, value provenance, own access paths (ADR 0024)
  3a23477 concurrency-hunter: 5d run B3c - the generator's driver builds the calls it used to refuse
  5d2e28b concurrency-hunter: 5d run B3b - generator version, one cancellation owner, decompiled-library fixes
  784822c concurrency-hunter: the run generates the models of the library members it reaches, within a budget, and keeps them in the repository
  b9d60fc concurrency-hunter: CoreLib's generation runs with diagnostics off, and code a user may have written reaches only what a user can name
  c59f48a concurrency-hunter: CoreLib's generation reads a type a recognizer owns as a program reads it from metadata
  52135a4 concurrency-hunter: the model driver compiles where it overrides protected internal members, seeds delegates over internal types and calls members with pointers
   .../LibraryModels/Generation/Allocations.cs        |  53 +-
   .../Generation/ComparisonWitnesses.cs              |   5 +
   .../Generation/CompilationOwnership.cs             |  57 ++
   .../LibraryModels/Generation/CoreLibGeneration.cs  |   6 +
   .../LibraryModels/Generation/DriverExecutions.cs   |  13 +-
   .../LibraryModels/Generation/DriverRootProvider.cs |   2 +
   .../LibraryModels/Generation/DriverSynthesizer.cs  | 966 +++++++++++++++++----
   .../LibraryModels/Generation/EffectReader.cs       | 451 ++++++++--
   .../LibraryModels/Generation/EngineClaims.cs       |  36 +-
   .../LibraryModels/Generation/FateClassifier.cs     | 129 ++-
   .../LibraryModels/Generation/FieldSymbols.cs       |   9 +
   .../LibraryModels/Generation/GeneratedDocument.cs  |  11 +
   .../LibraryModels/Generation/GenerationHandoffs.cs | 217 +++--
   .../LibraryModels/Generation/GenerationReasons.cs  |  18 +-
   .../LibraryModels/Generation/GenerationStages.cs   |  92 ++
   .../LibraryModels/Generation/GenerationTargets.cs  | 297 +++++++
   .../LibraryModels/Generation/HeapReachability.cs   | 114 ++-
   .../Generation/ImplementationAssemblies.cs         | 275 ++++--
   .../LibraryModels/Generation/LibraryCompilation.cs | 479 +++++++++-
   .../Generation/LibraryFieldOpening.cs              |  11 +
   .../LibraryModels/Generation/ModelGenerator.cs     | 264 +++++-
   .../LibraryModels/Generation/PackageVersions.cs    |   7 +
   .../LibraryModels/Generation/ParameterKinds.cs     |  64 +-
   .../LibraryModels/Generation/SeedableFields.cs     |  26 +-
   .../LibraryModels/Generation/ValueObservation.cs   | 356 ++++----
   .../LibraryModels/Generation/ValueProvenance.cs    | 749 +++++++++++++---
   26 files changed, 3875 insertions(+), 832 deletions(-)

engine: 8 commit(s)
  4948f34 concurrency-hunter: 5d run B4b - every library's generation reaches only the types it constructs; the MessageParser<T> holder is covered (ADR 0019 amendment, ADR 0025)
  a70d961 concurrency-hunter: 5d run B4 - the generator's answers get precise: rollback leaves no trace, concrete collections, value provenance, own access paths (ADR 0024)
  3a23477 concurrency-hunter: 5d run B3c - the generator's driver builds the calls it used to refuse
  5d2e28b concurrency-hunter: 5d run B3b - generator version, one cancellation owner, decompiled-library fixes
  784822c concurrency-hunter: the run generates the models of the library members it reaches, within a budget, and keeps them in the repository
  b9d60fc concurrency-hunter: CoreLib's generation runs with diagnostics off, and code a user may have written reaches only what a user can name
  c59f48a concurrency-hunter: CoreLib's generation reads a type a recognizer owns as a program reads it from metadata
  f7047cb concurrency-hunter: a member of a recognized type no recognizer models has an unknown effect, by family
   .../Accesses/AccessModel.cs                        |  12 +
   .../Accesses/AccessPathParts.cs                    |   4 +
   .../Accesses/CollectionObjects.cs                  |  68 +-
   .../Accesses/GapDecisions.cs                       |  51 +-
   .../Accesses/InterproceduralAccesses.cs            | 597 +++++++++++---
   .../Accesses/MustHeldLocks.cs                      | 175 +++-
   .../Accesses/PairIdentity.cs                       |  23 +
   .../Accesses/ReturnSummaries.cs                    | 139 +++-
   .../Accesses/SemanticGaps.cs                       |  36 +-
   .../Accesses/UnknownCalls.cs                       |  82 +-
   .../Analysis/AnalysisModel.cs                      |  29 +
   .../Analysis/ConflictFindings.cs                   |  47 +-
   .../Analysis/PathConditions.cs                     |   9 +-
   .../CallGraph/ProgramIndex.cs                      |  45 +-
   .../CallGraph/ReachableSet.cs                      | 159 +++-
   .../src/ConcurrencyHunter.Analysis/Di/DiIndex.cs   |   1 +
   .../Execution/ExecutionModel.cs                    | 168 +++-
   .../Execution/ExecutionSet.cs                      |  80 +-
   .../Execution/HappensBefore.cs                     | 173 ++--
   .../Execution/TimerSteps.cs                        |   4 +
   .../Heap/MethodSummary.cs                          |  78 +-
   .../Heap/MethodSummaryBuilder.cs                   | 270 +++++--
   .../Heap/SolverWorklist.cs                         |  16 +
   .../Heap/TrackedState.cs                           | 140 +++-
   .../ConcurrencyHunter.Analysis/Heap/TypeSafety.cs  |   2 +
   .../Heap/UnseenWrites.cs                           |  13 +-
   .../Heap/WholeProgram.cs                           | 371 +++++++--
   .../ConcurrencyHunter.Analysis/Ir/IrOperations.cs  |  71 +-
   .../src/ConcurrencyHunter.Analysis/Ir/IrPrinter.cs |  12 +-
   .../ConcurrencyHunter.Analysis/Ir/IrValidator.cs   |   9 +
   .../Reporting/ReportRenderer.cs                    |   8 +
   .../Solving/SolverRefinement.cs                    |  53 +-
   .../ConcurrencyHunter.Analysis/WorkCancellation.cs |  58 ++
   .../Analysis/GenerationCauses.cs                   |  99 +++
   .../Analysis/GenerationPass.cs                     | 226 ++++++
   .../Analysis/ModelGeneration.cs                    |  17 +
   .../Analysis/PhaseOneAnalyzer.cs                   | 265 +++++-
   .../Analysis/RunGeneration.cs                      | 337 ++++++++
   .../Analysis/ScopePipeline.cs                      |  44 +-
   .../ConcurrencyHunter.Core.csproj                  |  20 +
   .../Frontend/DelegateCombination.cs                |   7 +-
   .../Frontend/EffectiveFlowGraph.cs                 |  12 +
   .../ConcurrencyHunter.Core/Frontend/IrLowering.cs  | 900 +++++++++++++++++----
   .../Frontend/ProgramIndexBuilder.cs                | 125 ++-
   .../src/ConcurrencyHunter.Core/Frontend/SsaPlan.cs |  63 +-
   .../ConcurrencyHunter.Core/Frontend/SymbolNames.cs |  18 +-
   .../Providers/AspNetCoreRootProvider.cs            |  33 +
   .../Providers/DiIndexBuilder.cs                    |  45 +-
   .../Providers/ExactSymbols.cs                      |   1 +
   .../Providers/HostingRootProvider.cs               |   9 +
   .../Providers/InjectionBindings.cs                 |  51 +-
   .../Providers/LibraryModels/BuiltInModelReader.cs  |  11 +
   .../Providers/LibraryModels/DeclarationId.cs       | 111 ++-
   .../Providers/LibraryModels/FunctionPointers.cs    |  27 +
   .../Providers/LibraryModels/GeneratedLayer.cs      | 107 +++
   .../Providers/LibraryModels/GeneratedModelFiles.cs | 830 +++++++++++++++++++
   .../Providers/LibraryModels/GeneratedModelTable.cs | 154 ++++
   .../Providers/LibraryModels/LibraryModels.cs       | 141 +++-
   .../Providers/LibraryModels/LibraryVocabulary.cs   |  96 +++
   .../Providers/LibraryModels/ModelEntryWriter.cs    |  57 +-
   .../Providers/LibraryModels/ModelLock.cs           |  13 +-
   .../Providers/LibraryModels/ProjectModelReader.cs  |  19 +
   .../LibraryModels/ProjectModelResolver.cs          |  24 +-
   .../Providers/LibraryModels/RepositoryRoot.cs      |   1 +
   .../Providers/LibraryModels/TaskTypes.cs           |   6 +
   .../Providers/ProcessScopes.cs                     |   2 +
   .../Providers/ProviderRegistry.cs                  |  10 +-
   .../Providers/ProviderSupport.cs                   |   7 +
   .../Providers/SourceSpans.cs                       |   6 +-
   69 files changed, 6056 insertions(+), 841 deletions(-)

build: unchanged
```

## Остаток

- Вопрос 178: этот ран только измерил R10 — 29 против 21 тела судеб конструктора MessageParser<T>, 409 против 402 тел подтверждения; timeout SerializeToUtf8Bytes не даёт размера завершённого замыкания. Замеры выше не решают остальной вопрос о заместителях культуры и других источниках значений CoreLib; строка 178 в QUESTIONS сохранена.
- Вопрос 187: держатель `FieldCodec.ForMessage` и ref struct в триггерах остаются рану 5d B5.

## Исправления ревью F-0053–F-0056

F-0053/F-0054, rule: кандидат рецепта имеет источник ровно тогда, когда R4 разрешает его; чужая аллокация не доказывает источник текущего выражения, а выбранная общая статика другой сборки не требует Allocation в куче.

- fixed: `EffectReader.HasObjectSource` — единая проверка выбранного выражения и его собственного пути построения; ссылки проверяются по самому значению, объект только в его полях источником ссылки не считается. У struct со ссылками проверяются её вложенные значения.
- fixed: `Allocations.CreatedByRecipe` — чтение тех же создателей, что у происхождения; операция `new` в setup должна принадлежать проверяемому выражению рецепта, библиотечный создатель — достигаться из вызовов этого выражения. Объекты другого выражения рецепта, обычного setup, Seed, пробы или подкласса драйвера не делают кандидат построенным.
- fixed: `ReadRecipeCandidates` — откат кандидата использует общую проверку, включая выбранные внешние статические поля и свойства.
- fixed: `ReadRecipeSources` — верхнее значение и каждый вложенный аргумент используют ту же проверку, в прогоне судеб и в каждом подтверждающем прогоне обнаружения статики.
- already agreed: `DriverSynthesizer.Value`, `LibraryValue`, `SharedValue` — набор допустимых источников R4 уже ограничен; выбор источников и откат D-0022 сохранены.

`RecipeObjectSourceTests.A_seed_is_not_the_object_source_of_a_recipe` воспроизводит F-0053 в явном off, при автоматическом переходе inert → off, во вложенном аргументе и при обнаружении статики подтверждением. Основной случай даёт `incomplete` / `unseeded-read`, модели нет. Во вложенной клетке неразрешённый интерфейсный вызов даёт приоритетный `unknown-touch`, вместе с `unseeded-read`, без модели. `An_object_from_other_setup_or_another_recipe_is_not_a_candidates_own_source` отдельно исключает аллокации обычного setup и другого выражения рецепта. `Another_assembly_shared_static_is_a_recipe_source_through_the_final_answer` проверяет F-0054 для свойства canned-типа StringComparer и поля Missing.Value, на верхнем уровне, во вложенном аргументе, при автоматическом переключении и при подтверждении; ответы явного off равны run и inert по classified, modelReason и модели. Клетка OtherAssemblySharedStatic в `RecipeValueMatrixTests.Every_cell_gets_its_expected_recipe_value` теперь проверяет окончательный ответ и сохранение присваивания, а не только текст синтеза.

Запись model evals повторена через `ModelEvalTests.Answers_match_the_recorded_snapshot` с `CH_MODEL_EVALS=1`, `CH_MODEL_EVALS_RECORD=1`: 1 Passed, 0 Failed, 0 Skipped (лог `scratch/b4c-gate/r3-final-record-evals.log`). Все ответы, пакеты и пороги побайтно совпадают с началом этого хода; опасного сужения нет. Относительно HEAD единственное снижение — `fateGoldEntries` 4 → 3, а `exact` вырос 11 → 12. Остальные пороги сохранены: effectsExact 8, accessorsExact 8, linqExact.System.Linq 17, linqExact.System.Linq.Queryable 0.

Повторная публикация exe, запись всех 21 файлов demo и пересъёмка снимка завершены. Снимок demo совпадает с началом хода, все counts, находки и fingerprints равны HEAD; различия coverage относительно HEAD перечислены в разделе Demo. Отказ R8 сохранён: factory = holder result, off, incomplete / unseeded-read JsonParser.NullValueDescriptor; в замере 29 тел судеб и 413 тел подтверждения. Таблица CoreLib и снимок eShop равны HEAD. `generate-corelib.ps1 -Since` выполнен без генерации, код 0, лог `scratch/b4c-gate/r3-final-corelib-since.log`; вывод совпадает с разделом Генерация CoreLib.

F-0055, rule: запись Demo называет каждый изменившийся ответ сгенерированных файлов и его требование R1–R7.
- fixed: раздел Demo — после сравнения всех 21 файлов убрано утверждение об их равенстве HEAD, перечислены конструктор Protobuf (R3, R4) и два ответа Polly.Core (R2).

F-0056, rule: README называет фактические движения порогов, сохраняя запрет R9 на прочие снижения.
- fixed: README model evals — явно указан рост exact 11 → 12 и единственное снижение fateGoldEntries 4 → 3; остальные пороги сохранены.

Полные исполняемые G1–G3 оставлены серверу; команды гейтов не запускались. `-Measure` выполнен как предписанная операция записи R10, без порога.

Проверка регрессий со старым правилом HasObjectSource (любая Allocation, включая достижимую через поля): 15 Failed, 0 Passed — все 14 новых строк теорий и итоговая ячейка матрицы; лог `scratch/b4c-gate/r3-all-regressions-red.log`. Восстановление исправленного EffectReader выполнено файловой записью с новым LastWriteTime; содержимое побайтно сверено с сохранённым исправлением. Отдельный прогон Recipe* и CancellationCoverageTests дал 328 Passed, 0 Failed, 0 Skipped (лог `scratch/b4c-gate/r3-final-recipes.log`), ещё два случая чужого setup/рецепта — 2 Passed (лог `scratch/b4c-gate/r3-other-sources.log`).

Дополнительная клетка F-0054 — struct со ссылками `Pair(StringComparer)`, получающая InvariantCulture, — сначала упала в `RecipeValueMatrixTests.Every_cell_gets_its_expected_recipe_value` (Debug, лог `scratch/b4c-gate/r3-struct-shared.log`). Конструктор struct теперь признаётся источником её значения, а вложенные аргументы по-прежнему проверяются тем же HasObjectSource. После этой правки `RecipeObjectSourceTests`, `RecipeValueMatrixTests` и `RecipeValueProvenanceMatrixTests` в Debug дали 31 Passed, 0 Failed, 0 Skipped (лог `scratch/b4c-gate/r3-struct-shared-green.log`).

Первый прогон записи базиса этого исправления завершился с кодом 1: CLI — 152 Passed, 0 Failed, 0 Skipped; Core — 11661 Passed, 1 Failed, 39 Skipped. Единственный отказ — `SolverResourceLimitTests.A_query_over_its_limit_answers_unknown_for_the_limit_and_is_no_safeguard_stop`: ожидался ResourceLimit, получен Safeguard; лог `scratch/b4c-gate/r3-record-baseline.log`. Скрипт отказался записывать базис из неуспешного прогона. Изолированный повтор того же теста на той же Release-сборке прошёл (1 Passed за 559 мс, лог `scratch/b4c-gate/r3-resource-isolated.log`); код движка и его пределы не менялись.

Повторная запись базиса на итоговых исходниках выполнена последовательно после записей demo/evals и замера, без параллельных Builder-сборок: `pwsh -NoProfile -File ./build/check-test-baseline.ps1 -Record -KeepResults scratch/b4c-gate/r3-final-baseline-results`, с CH_ESHOP_ROOT=C:\Dev\eShopOnContainers, CONCURRENCYHUNTER_REQUIRE_E2E=1 и снятыми CH_MODEL_EVALS/CH_MODEL_EVALS_RECORD. Код выхода 0, лог `scratch/b4c-gate/r3-final-record-baseline.log`: CLI — 152 Passed, 0 Failed, 0 Skipped; Core — 11663 Passed, 0 Failed, 39 Skipped. Записаны 11659 уникальных кейсов, 11620 Passed. Все новые случаи источников, клетка struct с общей внешней статикой и добавленный отрицательный случай фабрики, возвращающей null, прошли. Тест SolverResourceLimitTests, отказавший в первом прогоне, тоже Passed; его ожидание и код не менялись. Все четыре eShop-теста прошли; снимок eShop остался равен HEAD. Проверка нового базиса против HEAD входит в серверный G1 и отдельно Builder-ом не запускалась.


## Исправления ревью F-0057–F-0060

F-0057, rule: ключ кандидата инициализатора — закрытие после подстановки параметров достигающего метода и типа, согласованное с кучей (R1, R3).
- fixed: ReachableSet.Reference регистрирует закрытия через существующие BindClosed/Watch/WatchOpen, включая поздние привязки, вложенные типы и открытый/слитый контекст; путь run сохраняет прежнее поведение анализа программы.
- fixed: регистрации из загрузки/записи статического поля, вызова статического метода/свойства, создания объекта и статического method group проходят через этот общий путь.
- already agreed: AddTypeInitializerTrigger получает закрытый тип construction от разрешения контейнера; повторной подстановки у читателей нет.
- already agreed: Candidate, TypeInitializerCandidate.ClosedTypeKeys, RunsTypeInitializer и ModelGenerator.InitializerReachedBodies используют записанные ключи. Склеенный экземпляр по-прежнему исполняется, если исполняется хотя бы одно закрытие.
- fixed: InitializerGenericHelperTests проверяет оба воспроизведения (Ext.Take(ref Value), Task.Run) с принудительным off и автоматическим переключением после переполнения, отсутствие State<int>..cctor в наборе off и двойники run/inert. InitializerClosedContextTests проверяет пять триггеров, метод/обобщённый тип/вложенное закрытие/два помощника, тип библиотеки, драйвера, открытый/слитый контекст и смешанные закрытия.

F-0058, rule: async-делегат, кроме async-итератора, переданный аргументом вызову с невидимым скану телом, — возможный запуск работы, независимо от ожидания самого вызова (R3, D-0025).
- fixed: InitializerWork.Starts читает ArgumentDelegate отдельно от собственных целей вызова; CallBody — общий ответ о видимости тела для проверки работы и передачи статики.
- already agreed: CallTargets исключает ArgumentDelegate; он по-прежнему не разрешает вызов для передачи статики. Вызов с видимым телом решает запуск по собственному вызову делегата в этом теле.
- fixed: EffectReader.ReadInitializerBodies/HandoffArguments использует CallBody; скан сохраняет library-state-store для синтаксически установленной передачи статики при одновременном initializer-work-not-run. Обычная запись в статическое поле рядом с запуском работы такой причины не добавляет. Для остальных incomplete правило раннего отказа сохранено.
- fixed: переименованные Argument_delegates_leave_each_unseen_call_kind_unresolved_and_async_arguments_may_start_work и Only_the_calls_own_targets_leave_virtual_handoffs_unresolved_and_async_arguments_may_start_work сохраняют утверждения о library-state-store; async-клетки теперь требуют initializer-work-not-run. Добавлены лямбды и method group для extern, virtual, constrained, конструктора и делегатного вызова.
- fixed: InitializerAsyncArgumentTests проверяет List.ForEach/Array.ForEach с async-лямбдой и method group, также с автоматическим переключением; run/inert создают дочернюю работу. Двойники Run помечают extern с async-лямбдой/method group как known-wider: off учитывает возможное невидимое выполнение. Видимые тела с вызовом/без вызова, немедленное ожидание, синхронный делегат и async-итератор задают границу правила.

F-0059, rule: один кеш lowering принадлежит попытке и сохраняется между всеми её проходами, повторным синтезом, подтверждениями и откатами кандидатов рецепта.
- fixed: Attempt владеет словарём lowered, единственным экземпляром LibraryModels и накопленными диагностическими сообщениями dropped; AttemptPass получает их. Попытки inert/off создают разные словари. Диагностики сохраняются вместе с кешем, поскольку повторное чтение кеша их не выдаёт.
- already agreed: ключ кеша включает Compilation, BodyId и LibraryModels; новые драйверы не смешиваются, библиотечные тела переиспользуются. Ответы от переноса кеша не меняются, меняется только время. Существующие проверки повторного подтверждения и отката кандидатов сохранены.

F-0060, rule: обозначения режимов A/B используются только в PLAN, QUESTIONS и записи рана.
- fixed: demo/SCENARIOS.md, строка protobuf-parser-factory-at-parse, использует «при обращении off»; 5d-B4c, /descriptor-unseeded, 5d-C и ADR 0026 сохранены.
- already agreed: SPEC, ADR 0026, CONTEXT, README evals и research/library-models проверены поиском; других обозначений режимов A/B в них нет. SPEC и ADR 0026 уточняют согласованное правило async-аргумента D-0025.

Диагностика регрессий: scratch/b4c-gate/r4-red-confirmed.log — при временном снятии F-0057/F-0058 14 Failed, 7 Passed; исходники восстановлены файловой записью. F-0057 падает на наличии State<int>..cctor в reachable set off, F-0058 — на отсутствующей причине initializer-work-not-run. С исправлениями первоначальный расширенный прогон scratch/b4c-gate/r4-diagnostics.log дал 356 Passed, 0 Failed, 0 Skipped (включая CancellationCoverageTests и проверки рецептов). Последующее общее определение CallBody не меняет предикат видимости; итоговая сборка и добавленный случай смешанных закрытий проверяются при записи базиса. Проверка Roslyn затронутых проектов завершилась без ошибок и предупреждений. Перед eShop у всех csproj проверено наличие obj/project.assets.json, пропусков нет.


Итоговая диагностика кода: scratch/b4c-gate/r4-final-diagnostics-fixed.log — 365 Passed, 0 Failed, 0 Skipped, включая InitializerReachedBodyScanMatrixTests и LibraryStaticFormMatrixTests. Предыдущий диагностический прогон r4-final-diagnostics.log выявил два отказа этих матриц из-за слишком широкой проверки обычных статических записей рядом с работой; исправление оставляет совместную причину только для синтаксически доказанной передачи статики, ожидания матриц не менялись. Отдельная проверка ожидания невидимого вызова r4-awaited-argument.log — 2 Passed: async-аргумент считается возможной работой даже при ожидании вызова; синхронный Task-returning method group не считается async. Опубликован итоговый Release exe, r4-publish-final.log, exit 0.


Итоговая запись demo F-0057–F-0060: r4-record-demo-final.log, exit 0; 21 файлов, повторный read ничего не генерирует. Число находок, occurrences, groups и fingerprints равны HEAD. Срез counts/coverage записан из scratch/demo-read.json без завершающего перевода строки.


После F-0057–F-0060 повторён generate-corelib.ps1 -Since без генерации: exit 0, scratch/b4c-gate/r4-corelib-since.log; перечень относительно коммита таблицы совпадает с разделом «Генерация CoreLib». Снимок eShop и таблица CoreLib повторно побайтно сверены с HEAD с учётом CRLF checkout, отличий нет. Итоговый Roslyn get_diagnostics по пяти затронутым производственным файлам и зависимым проектам: complete=true, errorCount=0, warningCount=0.


Снимок evals на итоговом генераторе записан единственным тестом ModelEvalTests.Answers_match_the_recorded_snapshot с CH_MODEL_EVALS=1 и CH_MODEL_EVALS_RECORD=1; r4-record-evals.log. Число сдвинувшихся ответов относительно входа этого исправления: 0. Порог вручную не менялся.


Model evals F-0057–F-0060: тест записи снимка завершился с exit 0 (1 Passed, 0 Failed, 0 Skipped). Снимок полностью равен входу этого исправления: ни одна судьба, модель, reason/modelReason, detail, causes, reachedBodies или mode не сдвинулись; реализации и пакеты неизменны, новых initializer-work-not-run нет. Полы: exact 12, fateGoldEntries 3, effectsExact 8, linqExact.System.Linq 17, linqExact.System.Linq.Queryable 0, accessorsExact 8. Ручного снижения нет. Конструктор MessageParser<T> 3.21.9 сохраняет off, factory=holder result, incomplete/unseeded-read и отсутствие модели (R8). Перенос кеша F-0059 не сдвинул ответы, меняется только время.


Замер R10 после F-0057: MessageParser<T> 3.30.2, off.confirmationBodies 413 → 409 (против 402 варианта C); R1/R3, закрытые ключи достигаемых через обобщённые помощники типов подставляются до решения об исключении инициализатора, поэтому off не удерживает ошибочно открытые тела. Судьба factory=holder result и отказ incomplete/unseeded-read не изменились. Прогон судеб остаётся 29 тел; inert — 21 тело судеб и 2006 подтверждения. Evals на версии 3.21.9 не сдвинулись. Перенос кеша F-0059 влияет только на время; число тел меняет исправление ключа F-0057.

Проверка R10 выполнена отдельно от гейта: все 21 generator файлов demo совпадают с measure.json; реализации SerializeToUtf8Bytes совпадают со снимком evals по assemblyVersion/fileVersion/mvid, версии сборки и файла MessageParser — 3.30.2.0. check-docs.ps1 завершился с exit 0, r4-docs.log.


Первый полный прогон записи базиса F-0057–F-0060 (scratch/b4c-gate/r4-record-baseline.log) завершился exit 1: Core — 11707 Passed, 1 Failed, 39 Skipped. Единственный отказ — StageCancellationTests.Decompilation_stops_every_worker_after_the_cancellation(workers: 4, enclosing: False), за 10 с: ожидался OperationCanceledException, получен AggregateException. Скрипт отказался записывать базис из неуспешного прогона. Изолированный прогон всех четырёх строк этой теории на той же Release-сборке без пересборки прошёл: 4 Passed за 512 мс, scratch/b4c-gate/r4-cancellation-isolated.log. Код декомпиляции, отмены и ожидания теста не менялись; точная причина отказа под полной нагрузкой не установлена.


Итоговая запись базиса F-0057–F-0060 завершилась exit 0: pwsh -NoProfile с check-test-baseline.ps1 -Record -KeepResults scratch/b4c-gate/r4-final-baseline-results; лог scratch/b4c-gate/r4-final-record-baseline.log. CH_ESHOP_ROOT=C:\Dev\eShopOnContainers, CONCURRENCYHUNTER_REQUIRE_E2E=1; CH_MODEL_EVALS/CH_MODEL_EVALS_RECORD сняты. CLI — 152 Passed, 0 Failed, 0 Skipped; Core — 11708 Passed, 0 Failed, 39 Skipped. Записаны 11704 уникальных кейса, 11665 Passed. Все четыре теста eShop в сохранённых TRX имеют Passed; тест StageCancellationTests, отказавший в первом полном прогоне, также Passed без изменения кода или ожидания. Новые и переименованные тесты включены. Исполняемые G1–G3 и check-baseline-rerecord.ps1 отдельно Builder-ом не запускались: независимая проверка принадлежит серверу.


После записи базиса усилены только утверждения в двух уже переименованных теориях InitializerModeSwitchTests: Run-двойники для virtual/interface, extern, constrained, конструктора и делегатного вызова подтверждают отсутствие наблюдаемого запуска работы; соответствующие async-клетки off явно названы known-wider по D-0025. Список строк теорий и имена тестов не менялись. Отдельный прогон этих окончательных утверждений — 26 Passed, 0 Failed, 0 Skipped, scratch/b4c-gate/r4-run-twins.log. Производственные исходники после публикации, записей demo/evals, замера и полного прогона базиса не менялись.
