#Requires -Version 7.0
<#
.SYNOPSIS
The gates of the phase-3 forge run (execution model): one entry per task, the run-wide check and the host-run check.

.DESCRIPTION
Written by the orchestrator after the plan was approved; each block checks exactly what the plan's gate for that task says.
Paths resolve against the plugins root, so the script runs from any directory. -Task 1..9 is a task gate, -Final is G2,
-Host is G3 and the gate of task 10. The demo hashes are pinned after task 1, once the orchestrator has checked the
hand-written expectations; until then every gate from task 2 on fails.
#>
[CmdletBinding(DefaultParameterSetName = 'Task')]
param(
    [Parameter(ParameterSetName = 'Task', Mandatory)]
    [ValidateRange(1, 9)]
    [int]$Task,
    [Parameter(ParameterSetName = 'Final', Mandatory)]
    [switch]$Final,
    [Parameter(ParameterSetName = 'HostRun', Mandatory)]
    [Alias('Host')]
    [switch]$HostRun,
    [Parameter(ParameterSetName = 'PrintDemoHashes', Mandatory)]
    [switch]$PrintDemoHashes
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$BASE_COMMIT = '99d49faaff059ab2a64f3988c68d78e426e70dab'
$APPROVED_AT = [DateTimeOffset]'2026-09-17T12:10:00Z'
$SOLUTION = 'concurrency-hunter/src/ConcurrencyHunter.slnx'
$DEMO = 'concurrency-hunter/demo'
$DEMO_GROUP_EXCLUDED = 'FullyQualifiedName!~DemoExpectationTests&FullyQualifiedName!~Demo_measurement_matches_the_snapshot&FullyQualifiedName!~EShopSnapshotTests&FullyQualifiedName!~PublishedExecutableEndToEndTests'
$PHASE_2B_GATE = 'Demo_matches_every_phase_2b_expectation_on_three_runs'
$PHASE_3_GATE = 'Demo_matches_every_phase_3_expectation_on_three_runs'
$TEMPORARY_TEST = 'Demo_phase_3_cases_give_no_finding_before_phase_3'

# ADRs and CONTEXT.md as the user approved them with the plan.
$PINNED_DOCS = [ordered]@{
    'concurrency-hunter/docs/adr/0001-the-skill-drives-the-run.md' = '4566d6898d21220a76e67de9de913ce8837c7536ef283055fd4176129914c763'
    'concurrency-hunter/docs/adr/0002-points-to-smt-and-a-normalized-ir-are-in-the-first-version.md' = 'b29e5899413329b571f39ef8a9c7b29646b279872dda219410fb19fc9aac14a2'
    'concurrency-hunter/docs/adr/0003-the-server-renders-the-report-and-the-ai-writes-only-narrative.md' = '3fa1a3a82357a0aba4d2b53c78418a04be0706a89ec21ec46fecae6750676d44'
    'concurrency-hunter/docs/adr/0004-z3-ships-inside-the-executable-and-degrades-to-unknown.md' = 'e92af2619cb67a6757486010889f2bf439199fb81a26188f714a850cbbe99f24'
    'concurrency-hunter/docs/adr/0005-a-process-scope-is-an-executable-and-the-projects-it-loads.md' = 'e314dca9d6dacf42dc908796cda48d9da40f4641593fe6be474196b18b315ba5'
    'concurrency-hunter/docs/adr/0006-a-construction-belongs-to-the-execution-that-triggers-it.md' = 'aaaa492b195b49365cb430cc76e99dee1540704d0e669cff63153c195272ab20'
    'concurrency-hunter/docs/adr/0007-a-finding-is-a-pair-of-access-sites.md' = '05cf5e03109932118923cfbf90fe997cc9e961fbbefa79a8252de23776969a32'
    'concurrency-hunter/docs/adr/0008-ordering-is-a-happens-before-graph-trusted-inside-one-instance-tree.md' = '5b114ce2d9965322f1edb6518b8207e67b0e15a29602e36c3f4f11dfd8ce6ca5'
    'concurrency-hunter/CONTEXT.md' = '4e87565a1b3ae33c977a487fb15ded04fb882871de27d5a0ee97264809f23e65'
}

# The demo after task 1, pinned by the orchestrator once the expectations were checked (-PrintDemoHashes prints them).
$PINNED_DEMO = @(
    'concurrency-hunter/demo/Demo.Application/Cases/RmwThreeLayers.cs=dd9b512681ecd99ec27fb84ee93ead7bf33caf0ed6d81f5f158563b4ec1aa97c'
    'concurrency-hunter/demo/Demo.Application/Demo.Application.csproj=4535be9b32177d68bc9250a6cf915134c5a1d59bc8b3a0f89ebc06d170848957'
    'concurrency-hunter/demo/Demo.Domain/Cases/RmwThreeLayers.cs=277679a47773bec760f61a248efa449e95d86da4c44689a80c2063958b099e78'
    'concurrency-hunter/demo/Demo.Domain/Cases/SharedLibraryStatic.cs=a006fbf75e5ae5f472ecc28e0981a9bcf33559fd280f753027dafbf7e9ef795f'
    'concurrency-hunter/demo/Demo.Domain/Demo.Domain.csproj=641d0895a243f84b699107ac32ada4549310b5eeb499e22ab8a077a5de55519e'
    'concurrency-hunter/demo/Demo.Tests/Cases/TestProjectNotAScope.cs=1fbf8cc9980215078e3a25f2b81833e5c3c21c3ca2bd35d56599c15868e026e4'
    'concurrency-hunter/demo/Demo.Tests/Demo.Tests.csproj=9532119d7b30f63df8c138fc6ee060ddfbe2912d13eeb9c9dbb15ed5f2d4e808'
    'concurrency-hunter/demo/Demo.Web/Cases/ActionSelfOverlap.cs=f29837c67fa97f0437e2864f99d3fb9cc62387660160ab1296f5427283b1650c'
    'concurrency-hunter/demo/Demo.Web/Cases/AliasTwoFields.cs=c255e687b4fd9701483373fa81077cb23f8797c40a327e518783c60b3b0bef0d'
    'concurrency-hunter/demo/Demo.Web/Cases/AmbiguousRegistrationScopedWins.cs=f4a504020f80c4765e4ba95f3ccb3fc16dafe65039c7e1aa6a79d53441271cec'
    'concurrency-hunter/demo/Demo.Web/Cases/AsyncVoidCall.cs=d8e958b421c9b2cf0073c50c1a2f16f4e80ec84f8e390e0dff6f50538503ffe5'
    'concurrency-hunter/demo/Demo.Web/Cases/BackgroundStopReadsOwnField.cs=564eb453f1f6b5c895ed89f7827fb6367ff03183d9a0e08a3c212bb3b45787be'
    'concurrency-hunter/demo/Demo.Web/Cases/ConstructionOtherState.cs=9ae3b3ab87c346f2829b97a1f37e29760020fd217257d05427d8de3ea6dea37a'
    'concurrency-hunter/demo/Demo.Web/Cases/ConstructorLeaksThis.cs=73eb6238b949afad3eebdff4f9898c34447b50ec5f43eb37081059d11143066e'
    'concurrency-hunter/demo/Demo.Web/Cases/ContinueWith.cs=07925989c23054c9aa1fb79f23479b1aeee7afde7e3123d8dc838a9864eb12dc'
    'concurrency-hunter/demo/Demo.Web/Cases/ControllerConstructorStaticCounter.cs=d3e26fdb3855c70dc4368a20c4416ddccb1db53366c4814c2aea71d96039c8dc'
    'concurrency-hunter/demo/Demo.Web/Cases/CustomLockByName.cs=94f338586c1fd64faa095dc75ab27893db466814251a25fa76c9f17b6d0339e5'
    'concurrency-hunter/demo/Demo.Web/Cases/DeepAccessPathWildcard.cs=7c5563e79d92bdc9bc4de046f6fd96c14c01f3d815dcd7b66a32d89ce264963e'
    'concurrency-hunter/demo/Demo.Web/Cases/DelegateField.cs=6d722a2ec4e42bf4baba51aad59ae193479f8f5023e4b8beea7d1eadbde5668d'
    'concurrency-hunter/demo/Demo.Web/Cases/DiFactoryRegistration.cs=684c2b5b44c6a0448f4e167283afd846fedc3eb2ed1590b1ef8425a18d274ef3'
    'concurrency-hunter/demo/Demo.Web/Cases/DiInstanceRegistration.cs=5f3ca566643b771cc5e86dda01031a42662574a7384f007cdf64aa98222cf39e'
    'concurrency-hunter/demo/Demo.Web/Cases/DiScopedPerRequest.cs=d91f134736241d49708ef6791c60c875958f63e70a6e5d49580b782eb809965a'
    'concurrency-hunter/demo/Demo.Web/Cases/DiSingletonControllerVsWorker.cs=b4516bd9d1be159966ad1f790528e994ca538ca03e7b0876569a4dc2cab4de2a'
    'concurrency-hunter/demo/Demo.Web/Cases/DiSingletonSameLockOnInstance.cs=4314706b19b2d48ff16bcaae0cffd9c73b22b6be60eb9a2195ded95507fe4771'
    'concurrency-hunter/demo/Demo.Web/Cases/DistinctAllocationSites.cs=e4e8925fc4bfa207bd59962196f0dfe8270c7eab926ac3a0215d50f6c7849b2c'
    'concurrency-hunter/demo/Demo.Web/Cases/DiTransient.cs=3293f226fd0c7f6d7ea16bf7911273358aeed7731045993597e0a33ca683e755'
    'concurrency-hunter/demo/Demo.Web/Cases/EscapeIntoSingleton.cs=784c9dbf9185a96c94c4d15ab18732410ba3d8dfeb365dea3b3fd4292b267ca8'
    'concurrency-hunter/demo/Demo.Web/Cases/EscapeViaArrayElement.cs=1294232e4a59de02a67ca26dc3b4117154647fb529b94495713f7525d1fdf2b0'
    'concurrency-hunter/demo/Demo.Web/Cases/EscapeViaCapturedClosure.cs=0200b8a8fb3ce7fca37c079c61533f4bac137bf936117d8145cb503742935272'
    'concurrency-hunter/demo/Demo.Web/Cases/EscapeViaOutParameter.cs=a5bd6087bcf4819bd0a92e22ea7fb11bf6f77e31157479b5f595e353c2cdc81a'
    'concurrency-hunter/demo/Demo.Web/Cases/EscapeViaStaticAssignment.cs=641d30b141775220b68f526e8f2bbbc6ebe5aaf4e2fb8598515f92c78523b46f'
    'concurrency-hunter/demo/Demo.Web/Cases/EventWaitNotOrdering.cs=e7029467e54b370a5d0737187f73da9ac2e7066af3f4885b5968734b9910f1d4'
    'concurrency-hunter/demo/Demo.Web/Cases/FactoryDistinctCallSites.cs=42ac8821f493ba3cf5b3864c9498f4d96713d8018343a71f9af5c2a442664118'
    'concurrency-hunter/demo/Demo.Web/Cases/FactoryInterfaceDispatch.cs=7563d08f8e20389b2420e74c5139980c35f9f311de0f6dcb2650d7bf0cf62c67'
    'concurrency-hunter/demo/Demo.Web/Cases/FactoryResolvesOtherService.cs=a21b9b32fcef0ae949adab66180a14f2510a8b9642b983ea4ba55edeb75a6984'
    'concurrency-hunter/demo/Demo.Web/Cases/FactoryReturnsSharedStatic.cs=f6d22aa7ed8a1fa1b17a00fe772ce5fd2a077864fbef9870171a96a4a9c881d4'
    'concurrency-hunter/demo/Demo.Web/Cases/FactoryScopedPerRequest.cs=c8efabf7ca1805482b384c6ad0b2c8a0931b93a52738a36b82153f0b368f3afa'
    'concurrency-hunter/demo/Demo.Web/Cases/FireAndForgetVsAwaited.cs=11b39d57453624cf6d94beb72c97c926912efea934af4f29597d82b3d2116774'
    'concurrency-hunter/demo/Demo.Web/Cases/FromServicesActionParameter.cs=06f1a1ecdf20836be2a0d79be4dc009cae9408136f2c63a0f21fe9283a0ce111'
    'concurrency-hunter/demo/Demo.Web/Cases/GenericSingletonPerTypeArgument.cs=4ba3d4b34e9ff75b68abe2fa4ac9ce90ba007b8e1a777eaae9ed21cc9bb6177b'
    'concurrency-hunter/demo/Demo.Web/Cases/GroupSharedHelperManyCallers.cs=7bb8bc614057faf936d756bbd0792c4f422e0196ad21f0d0e1b38769c48d96ba'
    'concurrency-hunter/demo/Demo.Web/Cases/GrpcServiceMethod.cs=c1048ab1e7f54b1e5c2d462666a0a3761449243ee70d073b20967b60fab09ad9'
    'concurrency-hunter/demo/Demo.Web/Cases/HostedConstructorBeforeRoots.cs=3eda60480905ac4692c470394eef54e1f4b81521f402f5d9f29a6c0ae5861140'
    'concurrency-hunter/demo/Demo.Web/Cases/HostedServiceRegisteredTwice.cs=c6fe3000201b2c5630931f236a0aa2a24131387c21d13835a048ed6dd8fea4ef'
    'concurrency-hunter/demo/Demo.Web/Cases/HostedStartVsAction.cs=7725e6d3abaf8adef8f78d9788a8df55abd2b07df1de1be0538469a57478ae11'
    'concurrency-hunter/demo/Demo.Web/Cases/InstanceRegistrationTouchesStatic.cs=ce0ef57541142c89489324d75bbfad5fecc4663a6ac1be761273ba4620694278'
    'concurrency-hunter/demo/Demo.Web/Cases/InterfaceDispatchDi.cs=c6babe46675360f4d5114302a3c2d4f96f610d2336ed6d1c9808335520fe04f2'
    'concurrency-hunter/demo/Demo.Web/Cases/JoinSkippedOnException.cs=567a8463388bcdff17ad8c7683b58bcb962c14a2fefa9a563a09d55db6f3a436'
    'concurrency-hunter/demo/Demo.Web/Cases/LambdaAndLocalFunction.cs=5b947d1370086aa7441fdfb9af690f375173281ff39888f7bf71f69e03171c9d'
    'concurrency-hunter/demo/Demo.Web/Cases/LocatorScopedViaCreateScope.cs=d84250aa1c17987de8d8a1707e13732079f5d81467610b7b2827f80553946a89'
    'concurrency-hunter/demo/Demo.Web/Cases/LocatorSingletonVsWorker.cs=ae06a4c3b395c5c7adf10f3bf065f6ad05dc5bb5f420d45d6ef1983508038626'
    'concurrency-hunter/demo/Demo.Web/Cases/LocatorTransientDistinct.cs=3b2630712e782bcd4a032b8eef43264a0de97605632a696f3aae2cd0c4f29b37'
    'concurrency-hunter/demo/Demo.Web/Cases/LocatorUnregisteredOpaque.cs=b4fbc32749e3678a1301cfa0694650bb61ea68b6e98f4a2062c97ca558eaab10'
    'concurrency-hunter/demo/Demo.Web/Cases/LockHeldByCaller.cs=3b59f043f40d3ceeb0cecfe2ac3f75161ccbc33153fa76db8ac0d54f7dc47395'
    'concurrency-hunter/demo/Demo.Web/Cases/LockIdentityThroughAlias.cs=b1a7b700f563908e8e014e6e73c415f0d05c372e218e31b1f72f1cbc76aa5eed'
    'concurrency-hunter/demo/Demo.Web/Cases/MinimalApiLambdaHandler.cs=6442615229f926b67fc84d1259a5db1119a7c80ac6e6f6c8730c50deb80eec23'
    'concurrency-hunter/demo/Demo.Web/Cases/MinimalApiReadWrite.cs=2446dc053ca3ca623bd87cd376c854f5b69154c3b78200b9d9f3f6cd31ada31a'
    'concurrency-hunter/demo/Demo.Web/Cases/MonitorEnterExitSameGate.cs=ed34da0434ab0cfe9b06b3eecd7cd54277f7211e0796a0e1e76a652bb2d5c36d'
    'concurrency-hunter/demo/Demo.Web/Cases/NonActionPublicMethod.cs=475bd49f5bc750b332180f123fd9c3bd04118d351cb4f956ec4b8d24569a8f67'
    'concurrency-hunter/demo/Demo.Web/Cases/OwnedLocalAllocation.cs=2c96f0d3a9e1764cf14cbef4e153a33793b6cbe044c68ee0c6fbbb34b72fe69a'
    'concurrency-hunter/demo/Demo.Web/Cases/ParallelForeach.cs=abdaedebc272f635105c893058a08f133d272244337cee0c3c1fa5814a63558e'
    'concurrency-hunter/demo/Demo.Web/Cases/ParallelForeachAsync.cs=61b67b27fd61ecfdce8d13ea5d52e7f1a3e4ec8f04bcec27de940dd679708b55'
    'concurrency-hunter/demo/Demo.Web/Cases/ParallelForSharedTotal.cs=ea3da4820d59ccd11e440797128c02db4748beb60bbf97b5c54481fdcb6809e9'
    'concurrency-hunter/demo/Demo.Web/Cases/PeriodicTimerLoop.cs=e32f7f8f4692c40c3a41035528f860e39df9933f7b037242fe364b2409038604'
    'concurrency-hunter/demo/Demo.Web/Cases/PocoControllerSelfOverlap.cs=90a9368d93e8d312582de22cff634b6b7a5a36ace23eafd27d232c871b23f68d'
    'concurrency-hunter/demo/Demo.Web/Cases/PrimaryConstructorInjection.cs=6c23cbf886e955b6b33866bd786ae33f3235aa05d4a740370f4804d79c0d1f35'
    'concurrency-hunter/demo/Demo.Web/Cases/ReceiverSensitivity.cs=f3dad181bcf53aea9171a2f098c4d7c61e47bf0e3acffa58b0af6edc391b988b'
    'concurrency-hunter/demo/Demo.Web/Cases/RecursiveSummary.cs=0781436af8201cc6e93a620d5178fd122c116d02bf821b3198cdee9fc8561f20'
    'concurrency-hunter/demo/Demo.Web/Cases/RmwForms.cs=3f6309e234cb8431fbc836be40016a5a4445be33758bff7e29c081fc2d5693fd'
    'concurrency-hunter/demo/Demo.Web/Cases/RmwSingletonCounter.cs=b75ef3a433f1ce07b26c5597a887a9cb3189a46fb770a43f5dd298978a5fab56'
    'concurrency-hunter/demo/Demo.Web/Cases/RmwThreeLayers.cs=fcc2d7ce0ea983f71e4204418be1efcec33a20c426cc549242533f557c9596fa'
    'concurrency-hunter/demo/Demo.Web/Cases/SameLockViaField.cs=d9ffa3050b5f7fdb6cfdeb78397768bc1ea2f2a725823b477d409af501ac4f9c'
    'concurrency-hunter/demo/Demo.Web/Cases/SharedLibraryStatic.cs=c338d94e212b82777f3cc5096659c8da5dd5eb51807f781e169347c35ed69548'
    'concurrency-hunter/demo/Demo.Web/Cases/SingletonConfiguredInConstructor.cs=866273b806b6888cd6711791f3ae8030cd1b9b60b0beef980d005e0f69ac7b3c'
    'concurrency-hunter/demo/Demo.Web/Cases/StaleReadWithoutDependency.cs=762866db4a2bf5b0cb4f70cf6da0bde93ca336f020b100290ef848f965e41ec0'
    'concurrency-hunter/demo/Demo.Web/Cases/StartupWriteBeforeRun.cs=713276175ce5772b42645c2ed1a9e8250614eb4308ff0bfa0174877a9fa4f93d'
    'concurrency-hunter/demo/Demo.Web/Cases/StaticConstructorInitialization.cs=bea289e5ee63438de40716106d5f9a000d293cb74d34c659450283a280eced7b'
    'concurrency-hunter/demo/Demo.Web/Cases/StaticFieldHttpVsWorker.cs=c7d9457208e5b9700e1624239cc18ae031a7349638e6a7532d3a4f90c691a76d'
    'concurrency-hunter/demo/Demo.Web/Cases/StaticFieldSameLock.cs=0359b9894186ecfdb13ec491893d033ceb719e8721e22b79af1f5a145258e7b3'
    'concurrency-hunter/demo/Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs=ab7cf2eb5ee31abbc103bf4dcf2eb682ef0ceee6d403320edb5fda1daf1277c7'
    'concurrency-hunter/demo/Demo.Web/Cases/TaskFactoryStartNew.cs=f88f296cd005fc91fd75398ef5bcfbe7b481ea92349450916a7321970774e0a9'
    'concurrency-hunter/demo/Demo.Web/Cases/TaskHandleAwaitedElsewhere.cs=c865a04e9d59d46b0caf1a3ef162d479a1339c43783ebb16b6e3214d9b12c112'
    'concurrency-hunter/demo/Demo.Web/Cases/TaskHandleJoinOrder.cs=7ad7946cabf457eb1bb0db03091c37e376e6ea8c8db27f21b64d5d7ed1809cff'
    'concurrency-hunter/demo/Demo.Web/Cases/TaskRunVsParent.cs=b5583832cf05f93c78ae6f61ecfe5ad32956f306cfbc74efe5ba2c9ca72cbb89'
    'concurrency-hunter/demo/Demo.Web/Cases/TaskWaitJoin.cs=d4f1829b4bf3da930bf3de8d8284566315621133b30d7ad48eb19d821cdb27f3'
    'concurrency-hunter/demo/Demo.Web/Cases/ThreadingTimerSelfOverlap.cs=764d7a0bab26786a0c5064d1eeacc2c03357df613739d863663999ba7b632192'
    'concurrency-hunter/demo/Demo.Web/Cases/ThreadingTimerVsAction.cs=6022698d69257f631c474de7a305d324aaede0ebc8c48dcd3ef031bed67952c4'
    'concurrency-hunter/demo/Demo.Web/Cases/ThreadPoolQueueUserWorkItem.cs=184bd143aa918945366b00006664cac0c6fd36cd59c48cfba0e4bdb271121be4'
    'concurrency-hunter/demo/Demo.Web/Cases/ThreadStartJoin.cs=5e0288384fa0d7004fb20f4f80594a6bf2221834203b22ae41a09399a220a781'
    'concurrency-hunter/demo/Demo.Web/Cases/TimerCapturedAlias.cs=e28a7e3cb7ee65f485a56052f2802a2e1123699977c5367043c2eb58ca6b62b0'
    'concurrency-hunter/demo/Demo.Web/Cases/TimerChangeReactivates.cs=15b12fc9c04bca41795dadf2c4b6b8d321e7753da944282bd070db95b633d224'
    'concurrency-hunter/demo/Demo.Web/Cases/TimerDisposeAsyncAwaited.cs=351e404af2296d6deb6694c78bbb6c44295f2e8063e775320732fcebf1a9f1ac'
    'concurrency-hunter/demo/Demo.Web/Cases/TimerDisposeDoesNotJoin.cs=f87b2a7305882ef8f0f4201ca633d2d3619bbe478d14313cb1086326352e196b'
    'concurrency-hunter/demo/Demo.Web/Cases/TimerDisposeWaitHandle.cs=9aa6c7eab4b338bd863e95a1a7c9e103881c9e663007c1b69ddf72ed0d2053bc'
    'concurrency-hunter/demo/Demo.Web/Cases/TimerNeverActivated.cs=7521fd47595296708524f64a5d1bf92e1e3a5bd8524d80ba83813371f8efb268'
    'concurrency-hunter/demo/Demo.Web/Cases/TimerOneShot.cs=14eb751a4647a8ed3b5d6d874d6908b4a78c614eef25935baa4242bd28ec7021'
    'concurrency-hunter/demo/Demo.Web/Cases/TimerStateSharing.cs=2b0097fc003931f92b05531d4aba5735b7306ad12afffebe540f3e73bcec395f'
    'concurrency-hunter/demo/Demo.Web/Cases/TimersTimerElapsed.cs=1da83eb39ed2af742ad148e1ff8055c00d8dad8cf23e90b69745fffb00343d2c'
    'concurrency-hunter/demo/Demo.Web/Cases/UnreachableStaticWriter.cs=5cedae6471273d9f3e83d889736d20e0f17a4853b67165a9c0602fd566ebd456'
    'concurrency-hunter/demo/Demo.Web/Cases/VirtualDispatchPointsTo.cs=630941031d71a64befd6330dcd9f9633f400b9e58a54802d2b20188c563e4119'
    'concurrency-hunter/demo/Demo.Web/Cases/WhenAllSiblings.cs=5f195b043cf63449cc681edc7e0235d22ea33db29f9c088f5fbe4833cc26dd86'
    'concurrency-hunter/demo/Demo.Web/Cases/WhenAllSynchronousPrefix.cs=266625a1491b1c973317d81135ee2689b8185662cff87fcd6fe1d05ecbc9ed34'
    'concurrency-hunter/demo/Demo.Web/Cases/WhenAnyNoJoin.cs=342b176be17ad7ec723c7db2ea7e7200d1e4204a9734cb82c561f128eae9269f'
    'concurrency-hunter/demo/Demo.Web/Demo.Web.csproj=7f4570a5ec5a1d2bde2c1786c906237c9e806d52b6687173ac0d5e0163008a04'
    'concurrency-hunter/demo/Demo.Web/Program.cs=7602ab66c377d4cb1f5e8497a600b18165594c7119683ab2affd1a9e7724c2da'
    'concurrency-hunter/demo/Demo.Web/Protos/grpc_service_method.proto=44e0cebc4b5659160a67be3e13df9e3d01222b80cf1ee17015391982c82e857c'
    'concurrency-hunter/demo/Demo.Worker/Cases/SharedLibraryStatic.cs=3105ce89f2164e378eaf09dc91edf06e16ae226ae9e98e7c567997f49ebe305d'
    'concurrency-hunter/demo/Demo.Worker/Demo.Worker.csproj=c9639a16081097ee78a0c92e96c14354a903868d513d57b0e623d0084190dfdc'
    'concurrency-hunter/demo/Demo.Worker/Program.cs=1d6405baa04c559f29496167d13c98da9a003e1637f41841e82675a1ead49d58'
    'concurrency-hunter/demo/expected-findings.json=61fd07783efc334505af5037fafe85dad0828737e190f0c854ed849a89fa6bd4'
    'concurrency-hunter/demo/SCENARIOS.md=b255f0615b4b1d103bae3442beccc25b16f465023d886dbb986b0a1cd2f5ed73'
)

$CASES = @(
    'TaskRunVsParent', 'TaskFactoryStartNew', 'TaskHandleJoinOrder', 'TaskHandleAwaitedElsewhere', 'TaskWaitJoin', 'ContinueWith',
    'ThreadPoolQueueUserWorkItem', 'ThreadStartJoin', 'ParallelForSharedTotal', 'ParallelForeach', 'ParallelForeachAsync', 'WhenAllSiblings',
    'WhenAllSynchronousPrefix', 'WhenAnyNoJoin', 'FireAndForgetVsAwaited', 'AsyncVoidCall', 'JoinSkippedOnException', 'EventWaitNotOrdering',
    'ThreadingTimerVsAction', 'ThreadingTimerSelfOverlap', 'TimerStateSharing', 'TimerCapturedAlias', 'TimerNeverActivated', 'TimerOneShot',
    'TimerChangeReactivates', 'TimerDisposeDoesNotJoin', 'TimerDisposeAsyncAwaited', 'TimerDisposeWaitHandle', 'TimersTimerElapsed',
    'PeriodicTimerLoop', 'GrpcServiceMethod')

$REQUIRED_FINDINGS = [ordered]@{
    'task-run-vs-parent' = 'DCA1001'; 'task-factory-start-new' = 'DCA1001'; 'task-handle-join-order/before-await' = 'DCA1001'
    'task-wait-join/before-wait' = 'DCA1001'; 'continue-with/vs-parent' = 'DCA1001'; 'thread-pool-queue-user-work-item/queue' = 'DCA1001'
    'thread-pool-queue-user-work-item/unsafe-queue' = 'DCA1001'; 'thread-start-join/before-join' = 'DCA1001'
    'parallel-for-shared-total' = 'DCA1002'; 'parallel-foreach' = 'DCA1001'; 'parallel-foreach-async' = 'DCA1002'
    'when-all-siblings/siblings' = 'DCA1001'; 'when-all-synchronous-prefix/after-first-await' = 'DCA1001'; 'when-any-no-join' = 'DCA1001'
    'fire-and-forget-vs-awaited/dropped' = 'DCA1001'; 'async-void-call' = 'DCA1001'; 'join-skipped-on-exception' = 'DCA1001'
    'event-wait-not-ordering' = 'DCA1001'; 'threading-timer-vs-action' = 'DCA1001'; 'threading-timer-self-overlap' = 'DCA1002'
    'timer-state-sharing' = 'DCA1001'; 'timer-captured-alias' = 'DCA1001'; 'timer-one-shot/vs-action' = 'DCA1001'
    'timer-change-reactivates' = 'DCA1002'; 'timer-dispose-does-not-join' = 'DCA1001'; 'timer-dispose-async-awaited/detached-work' = 'DCA1001'
    'timer-dispose-wait-handle/before-wait' = 'DCA1001'; 'timers-timer-elapsed/self' = 'DCA1002'; 'timers-timer-elapsed/vs-action' = 'DCA1001'
    'periodic-timer-loop/vs-action' = 'DCA1001'; 'grpc-service-method' = 'DCA1001'
}

$REQUIRED_NOT_DEFECTS = @(
    'task-handle-join-order/after-await', 'task-handle-awaited-elsewhere', 'task-wait-join/after-wait', 'continue-with/vs-antecedent',
    'thread-start-join/after-join', 'when-all-siblings/after-when-all', 'when-all-synchronous-prefix/prefix', 'fire-and-forget-vs-awaited/awaited',
    'timer-never-activated', 'timer-one-shot/self', 'timer-dispose-async-awaited/after-dispose', 'timer-dispose-wait-handle/after-wait',
    'periodic-timer-loop/iterations')

# Tests whose asserted contract phase 3 changes; they may be renamed or rewritten (G2).
$REWRITABLE = @(
    'ConcurrencyHunter.Core.Tests.Engine.MethodSummaryTests.Opaque_call_records_its_callee_and_the_delegate_passed_to_it'
    'ConcurrencyHunter.Core.Tests.Engine.ReachableSetTests.Lambda_passed_to_an_opaque_call_is_not_reached_through_it'
    'ConcurrencyHunter.Core.Tests.Engine.InterproceduralAccessTests.Lambda_passed_to_an_opaque_call_gives_no_access'
    'ConcurrencyHunter.Core.Tests.Engine.InterproceduralAccessTests.Coverage_counters_add_up_on_a_mixed_fixture'
    'ConcurrencyHunter.Core.Tests.Engine.DiSemanticsTests.Instance_registration_is_a_startup_construction_whose_static_write_is_dropped_and_counted'
    'ConcurrencyHunter.Core.Tests.Engine.DiSemanticsTests.Registration_body_accesses_are_startup_accesses'
    'ConcurrencyHunter.Core.Tests.Engine.OwnershipAndConstructionTests.Hosted_service_constructor_static_write_is_dropped_and_counted'
    'ConcurrencyHunter.Core.Tests.Engine.OwnershipAndConstructionTests.Hosted_service_type_initializer_with_no_other_reference_is_a_dropped_startup_construction'
    'ConcurrencyHunter.Core.Tests.Engine.OwnershipAndConstructionTests.Type_initializer_used_at_startup_and_by_an_action_is_a_dropped_startup_construction'
    'ConcurrencyHunter.Core.Tests.ReportSkeletonTests.Coverage_lists_each_scope_with_roots_diagnostics_registrations_and_skips'
    'ConcurrencyHunter.Core.Tests.ReportSkeletonTests.Coverage_names_what_is_not_analyzed_in_this_version_with_or_without_analysis'
    "ConcurrencyHunter.Core.Tests.DemoExpectationTests.$PHASE_2B_GATE")

# Minimum sizes of the classes the tasks add.
$NEW_CLASS_MINIMUMS = [ordered]@{
    'ConcurrencyHunter.Core.Tests.IrSpawnLoweringTests' = 48
    'ConcurrencyHunter.Core.Tests.Engine.SpawnHeapTests' = 25
    'ConcurrencyHunter.Core.Tests.Engine.SpawnExecutionTests' = 27
    'ConcurrencyHunter.Core.Tests.Engine.HappensBeforeTests' = 55
    'ConcurrencyHunter.Core.Tests.Engine.TimerTests' = 37
    'ConcurrencyHunter.Core.Tests.Engine.GrpcEndToEndTests' = 1
}

function Fail([string]$message) {
    Write-Host "check-phase3: $message"
    exit 1
}

function Invoke-Native([string]$what, [scriptblock]$command) {
    $output = @(& $command 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($code -ne 0) {
        $output | Select-Object -Last 60 | Write-Host
        Fail "$what exited $code"
    }
    return $output
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

function Test-ChangedSinceBase([string]$path) {
    & git diff --quiet $BASE_COMMIT -- $path
    $code = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    return $code -ne 0
}

function Get-BaseText([string]$path) {
    $text = @(& git show "${BASE_COMMIT}:plugins/$path" 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($code -ne 0) { Fail "cannot read $path at $BASE_COMMIT" }
    return ($text -join "`n")
}

function Get-MethodName([string]$name) {
    $open = $name.IndexOf('(')
    if ($open -ge 0) { return $name.Substring(0, $open) }
    return $name
}

function Get-ClassName([string]$method) {
    return $method.Substring(0, $method.LastIndexOf('.'))
}

function Get-ListedTests([string]$configuration) {
    $output = Invoke-Native 'listing the tests' { dotnet test $SOLUTION -c $configuration --nologo --no-build --list-tests }
    return @($output | ForEach-Object { $_.Trim() } | Where-Object { $_ -like 'ConcurrencyHunter.*' } |
        ForEach-Object { Get-MethodName $_ } | Sort-Object -Unique)
}

function Get-BaseTests {
    $lines = (Get-BaseText 'concurrency-hunter/build/test-baseline.txt') -split "`n"
    return @($lines | Where-Object { $_ -match "`tPassed$" } | ForEach-Object { Get-MethodName (($_ -split "`t")[0]) } | Sort-Object -Unique)
}

function Get-ClassCount([string[]]$tests, [string]$class) {
    return @($tests | Where-Object { (Get-ClassName $_) -eq $class }).Count
}

function Assert-ClassAtLeast([string[]]$tests, [string]$class, [int]$minimum) {
    $count = Get-ClassCount $tests $class
    if ($count -lt $minimum) { Fail "$class has $count tests, expected at least $minimum" }
}

function Assert-ClassNotBelowBase([string[]]$tests, [string[]]$baseTests, [string]$class) {
    Assert-ClassAtLeast $tests $class (Get-ClassCount $baseTests $class)
}

function Invoke-SuiteWithoutDemoGroup {
    Invoke-Native 'the suite without the demo group' { dotnet test $SOLUTION --nologo --no-build --filter $DEMO_GROUP_EXCLUDED } | Out-Null
}

function Build-Solution([string]$configuration) {
    Invoke-Native "the $configuration build" { dotnet build $SOLUTION -c $configuration --nologo } | Out-Null
}

function Get-DemoFiles {
    $files = @(
        "$DEMO/expected-findings.json"
        "$DEMO/SCENARIOS.md"
        "$DEMO/Demo.Web/Program.cs"
        "$DEMO/Demo.Worker/Program.cs")
    $files += @(Get-ChildItem -Path $DEMO -Recurse -File -Filter '*.csproj' | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { [IO.Path]::GetRelativePath($script:root, $_.FullName) -replace '\\', '/' })
    $folders = @(Get-ChildItem -Path $DEMO -Directory | ForEach-Object { "$DEMO/$($_.Name)/Cases" }) + "$DEMO/Demo.Web/Protos"
    foreach ($folder in $folders) {
        if (Test-Path -LiteralPath $folder) {
            $files += @(Get-ChildItem -Path $folder -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($script:root, $_.FullName) -replace '\\', '/' })
        }
    }
    return @($files | Sort-Object -Unique)
}

function Get-DemoHashLines {
    return @(Get-DemoFiles | ForEach-Object { "$_=$(Get-Sha256 $_)" })
}

function Assert-DemoPinned {
    if ($PINNED_DEMO.Count -eq 0) { Fail 'the demo hashes are not pinned yet; the orchestrator pins them after task 1' }
    $current = Get-DemoHashLines
    $difference = @(Compare-Object -ReferenceObject $PINNED_DEMO -DifferenceObject $current)
    if ($difference.Count -ne 0) {
        $difference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" } | Write-Host
        Fail 'the demo files differ from the ones pinned after task 1'
    }
}

function Assert-DocsPinned {
    foreach ($path in $PINNED_DOCS.Keys) {
        if (-not (Test-Path -LiteralPath $path) -or (Get-Sha256 $path) -ne $PINNED_DOCS[$path]) { Fail "$path differs from the approved version" }
    }
}

function Get-Expectations([string]$json) {
    return $json | ConvertFrom-Json -Depth 50
}

function Assert-OtherPhasesUnchanged {
    $current = Get-Expectations (Get-Content -Raw "$DEMO/expected-findings.json")
    $baseExpectations = Get-Expectations (Get-BaseText "$DEMO/expected-findings.json")
    foreach ($section in 'findings', 'notDefects') {
        $now = ConvertTo-Json -InputObject @($current.$section | Where-Object { $_.phase -ne '3' }) -Depth 50 -Compress
        $then = ConvertTo-Json -InputObject @($baseExpectations.$section | Where-Object { $_.phase -ne '3' }) -Depth 50 -Compress
        if ($now -ne $then) { Fail "entries of other phases in $section differ from $BASE_COMMIT" }
    }
}

function Get-Section([string[]]$lines, [string]$startPattern, [string]$endPattern) {
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($start -lt 0 -and $lines[$i] -match $startPattern) { $start = $i; continue }
        if ($start -ge 0 -and $lines[$i] -match $endPattern) { return ($lines[$start..($i - 1)] -join "`n") }
    }
    if ($start -lt 0) { return $null }
    return ($lines[$start..($lines.Count - 1)] -join "`n")
}

function Assert-DocumentationChecks {
    $spec = @(Get-Content "concurrency-hunter/docs/SPEC.md")
    $td065 = @($spec | Where-Object { $_.StartsWith('**TD-065.**') })
    if ($td065.Count -ne 1 -or $td065[0] -notmatch 'antecedent' -or $td065[0] -notmatch 'Parallel') { Fail 'SPEC TD-065 does not name the antecedent and Parallel' }
    $td061 = @($spec | Where-Object { $_.StartsWith('**TD-061.**') })
    if ($td061.Count -ne 1 -or $td061[0] -notmatch 'AutoReset') { Fail 'SPEC TD-061 does not name AutoReset' }
    $section47 = Get-Section $spec '^### 4\.7\.' '^### '
    if ($null -eq $section47 -or $section47 -notmatch 'ADR 0008') { Fail 'SPEC 4.7 does not reference ADR 0008' }
    $boundaries = Get-Section $spec 'Временные границы фазы 2a' '^Порядок последовательный'
    if ($null -eq $boundaries) { Fail 'SPEC 14.3 lost the list of phase-2a boundaries' }
    if ($boundaries.Contains('(3)') -or $boundaries.Contains('(3/5)')) { Fail 'SPEC 14.3 still lists a boundary phase 3 lifts' }
    $section141 = Get-Section $spec '^### 14\.1\.' '^### 14\.2\.'
    $baseSection141 = Get-Section ((Get-BaseText 'concurrency-hunter/docs/SPEC.md') -split "`n") '^### 14\.1\.' '^### 14\.2\.'
    if ($section141 -ne $baseSection141) { Fail 'SPEC 14.1 changed' }

    $composing = 'concurrency-hunter/skills/hunt/composing.md'
    if (-not (Test-ChangedSinceBase $composing)) { Fail "$composing is unchanged" }
    $paragraph = Get-Section @(Get-Content $composing) '(?i)^#+\s.*spawn' '^#+\s'
    if ($null -eq $paragraph) { Fail "$composing has no heading about spawn" }
    if ($paragraph -notmatch '(?i)happens-before' -or $paragraph -notmatch '(?i)\bjoin') { Fail "the spawn paragraph of $composing does not mention happens-before and join" }
}

function Invoke-DemoMetrics {
    Invoke-Native 'the Release build of the CLI' { dotnet build concurrency-hunter/src/ConcurrencyHunter.Cli -c Release --nologo } | Out-Null
    $out = Join-Path ([IO.Path]::GetTempPath()) "check-phase3-demo-$PID.json"
    Invoke-Native 'metrics on the demo' { dotnet run --project concurrency-hunter/src/ConcurrencyHunter.Cli -c Release --no-build -- metrics --target "$DEMO/Demo.slnx" --out $out } | Out-Null
    $measurement = Get-Content -Raw $out | ConvertFrom-Json -Depth 50
    Remove-Item -LiteralPath $out -Force
    return $measurement
}

function Get-FingerprintHash([string[]]$fingerprints) {
    $sorted = [string[]]@($fingerprints)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($sorted -join '|'))
    return ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))).Substring(0, 16).ToLowerInvariant()
}

function Assert-Measurements([string]$engineVersion) {
    if (-not $env:CH_ESHOP_ROOT) { Fail 'CH_ESHOP_ROOT is not set' }
    $metrics = 'concurrency-hunter/skills/hunt/evals/metrics'
    $eshopRevision = @(Invoke-Native 'the eShop revision' { git -C $env:CH_ESHOP_ROOT rev-parse HEAD })[0].Trim()
    $demoRevision = @(Invoke-Native 'the repository revision' { git rev-parse HEAD })[0].Trim()
    $expected = @{
        'demo' = @{ Target = "$DEMO/Demo.slnx"; Revision = $demoRevision }
        'eshop' = @{ Target = 'src/eShopOnContainers-ServicesAndWebApps.sln'; Revision = $eshopRevision }
    }
    $revisions = @{}
    foreach ($corpus in 'demo', 'eshop') {
        $path = "$metrics/$corpus.json"
        $measurement = Get-Content -Raw $path | ConvertFrom-Json -Depth 50
        $baseMeasurement = Get-BaseText $path | ConvertFrom-Json -Depth 50
        if ([DateTimeOffset]$measurement.recordedAt -le $APPROVED_AT) { Fail "$path was recorded before the plan was approved" }
        if ($measurement.engineVersion -ne $engineVersion) { Fail "$path has engine $($measurement.engineVersion), current is $engineVersion" }
        if ((ConvertTo-Json -InputObject $measurement.limits -Compress) -ne (ConvertTo-Json -InputObject $baseMeasurement.limits -Compress)) { Fail "$path has other limits than at $BASE_COMMIT" }
        if ($measurement.target -ne $expected[$corpus].Target) { Fail "$path targets $($measurement.target)" }
        if ($measurement.revision -ne $expected[$corpus].Revision) { Fail "$path has revision $($measurement.revision), expected $($expected[$corpus].Revision)" }
        $revisions[$corpus] = $measurement.revision
    }
    $manifest = Get-Content -Raw "$metrics/manifest.json" | ConvertFrom-Json -Depth 50
    if ($manifest.phase -ne '3') { Fail "manifest.json has phase $($manifest.phase)" }
    if ([DateTimeOffset]$manifest.recordedAt -le $APPROVED_AT) { Fail 'manifest.json was recorded before the plan was approved' }
    foreach ($corpus in 'demo', 'eshop') {
        if ($manifest.corpora.$corpus.revision -ne $revisions[$corpus]) { Fail "manifest.json has another $corpus revision than $corpus.json" }
    }
    foreach ($snapshot in 'demo', 'eshop') {
        $path = "concurrency-hunter/src/ConcurrencyHunter.Cli.Tests/Snapshots/$snapshot.json"
        if (-not (Test-ChangedSinceBase $path)) { Fail "$path was not rerecorded" }
        if ((Get-Content -Raw $path).Contains('startup-construction-access')) { Fail "$path still carries startup-construction-access" }
    }
}

function Assert-BaselineWithEShop {
    if (-not $env:CH_ESHOP_ROOT) { Fail 'CH_ESHOP_ROOT is not set' }
    $env:CONCURRENCYHUNTER_REQUIRE_E2E = '1'
    Invoke-Native 'the test baseline check' { & ./concurrency-hunter/build/check-test-baseline.ps1 -Configuration Release } | Out-Null
    $line = 'ConcurrencyHunter.Cli.Tests.EShopSnapshotTests.EShop_measurement_matches_the_snapshot' + "`t" + 'Passed'
    if (@(Get-Content 'concurrency-hunter/build/test-baseline.txt') -notcontains $line) { Fail 'the baseline does not record EShopSnapshotTests as Passed' }
}

function Assert-HostRun {
    $runJson = 'concurrency-hunter/skills/hunt/evals/claude-code/run.json'
    if (-not (Test-ChangedSinceBase $runJson)) { Fail "$runJson was not rerecorded" }
    foreach ($other in 'codex', 'cursor') {
        if (Test-ChangedSinceBase "concurrency-hunter/skills/hunt/evals/$other/run.json") { Fail "the $other run.json changed" }
    }
    $run = Get-Content -Raw $runJson | ConvertFrom-Json -Depth 50
    if ($run.status -ne 'complete') { Fail "the host run status is $($run.status)" }
    if ($run.digest.analysisStatus -ne 'CompleteWithFindings') { Fail "the host analysis status is $($run.digest.analysisStatus)" }
    $measurement = Invoke-DemoMetrics
    if ($run.digest.engineVersion -ne $measurement.engineVersion) { Fail "the host ran engine $($run.digest.engineVersion), current is $($measurement.engineVersion)" }
    if ([int]$run.digest.findings -ne [int]$measurement.counts.findings) { Fail "the host found $($run.digest.findings), the demo gives $($measurement.counts.findings)" }
    $hash = Get-FingerprintHash @($measurement.counts.fingerprints)
    if ($run.digest.fingerprints -ne $hash) { Fail "the host fingerprint hash $($run.digest.fingerprints) differs from the demo's $hash" }
    $demoMeasurement = Get-Content -Raw 'concurrency-hunter/skills/hunt/evals/metrics/demo.json' | ConvertFrom-Json -Depth 50
    if ([DateTimeOffset]$run.invokedAt -le [DateTimeOffset]$demoMeasurement.recordedAt) { Fail 'the host run is older than the demo measurement' }
    if (-not $run.bundle -or -not (Test-Path -LiteralPath (Join-Path $run.bundle 'run-metadata.json'))) { Fail "the bundle $($run.bundle) has no run-metadata.json" }
    $metadata = Get-Content -Raw (Join-Path $run.bundle 'run-metadata.json') | ConvertFrom-Json -Depth 50
    if ($metadata.engineVersion -ne $run.digest.engineVersion -or $metadata.status -ne $run.digest.analysisStatus -or
        [int]$metadata.counts.findings -ne [int]$run.digest.findings) { Fail 'the bundle metadata disagrees with the digest' }
}

$script:root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $script:root
try {
    if ($PrintDemoHashes) {
        Get-DemoHashLines | ForEach-Object { "    '$_'" }
        exit 0
    }

    if ($HostRun) {
        Assert-HostRun
        Write-Output 'check-phase3: host run ok'
        exit 0
    }

    if ($Final) {
        Assert-BaselineWithEShop
        $tests = Get-ListedTests 'Release'
        $baseTests = Get-BaseTests
        $missing = @($baseTests | Where-Object { $tests -notcontains $_ -and $REWRITABLE -notcontains $_ })
        if ($missing.Count -ne 0) { $missing | Write-Host; Fail 'tests that passed at the base are gone' }
        foreach ($class in @($baseTests | ForEach-Object { Get-ClassName $_ } | Sort-Object -Unique)) {
            Assert-ClassNotBelowBase $tests $baseTests $class
        }
        foreach ($class in $NEW_CLASS_MINIMUMS.Keys) { Assert-ClassAtLeast $tests $class $NEW_CLASS_MINIMUMS[$class] }
        $grpc = @($tests | Where-Object { $_ -like 'ConcurrencyHunter.Core.Tests.Providers.AspNetCoreRootProviderTests.AspNetCore_Grpc*' }).Count
        if ($grpc -lt 8) { Fail "only $grpc AspNetCore_Grpc tests" }
        $report = 'ConcurrencyHunter.Core.Tests.ReportSkeletonTests', 'ConcurrencyHunter.Core.Tests.ReportRendererTests', 'ConcurrencyHunter.Core.Tests.NarrativeValidatorTests'
        $reportNow = ($report | ForEach-Object { Get-ClassCount $tests $_ } | Measure-Object -Sum).Sum
        $reportThen = ($report | ForEach-Object { Get-ClassCount $baseTests $_ } | Measure-Object -Sum).Sum
        if ($reportNow -lt $reportThen + 8) { Fail "the report classes grew from $reportThen to $reportNow, expected at least 8 more" }
        $tools = 'ConcurrencyHunter.Cli.Tests.RunToolsTests', 'ConcurrencyHunter.Cli.Tests.RunLifecycleTests'
        $toolsNow = ($tools | ForEach-Object { Get-ClassCount $tests $_ } | Measure-Object -Sum).Sum
        $toolsThen = ($tools | ForEach-Object { Get-ClassCount $baseTests $_ } | Measure-Object -Sum).Sum
        if ($toolsNow -lt $toolsThen + 1) { Fail 'RunToolsTests and RunLifecycleTests did not grow' }
        Assert-DocsPinned
        Assert-OtherPhasesUnchanged
        Assert-DemoPinned
        Assert-DocumentationChecks
        $engine = (Invoke-DemoMetrics).engineVersion
        Assert-Measurements $engine
        if (Test-ChangedSinceBase 'Common') { Fail 'plugins/Common changed' }
        $untracked = @(Invoke-Native 'untracked files under Common' { git ls-files --others --exclude-standard -- Common })
        if ($untracked.Count -ne 0) { $untracked | Write-Host; Fail 'plugins/Common has new files' }
        Write-Output 'check-phase3: G2 ok'
        exit 0
    }

    if ($Task -eq 1) {
        Invoke-Native 'the demo build' { dotnet build "$DEMO/Demo.slnx" --nologo } | Out-Null
        foreach ($case in $CASES) {
            $files = @("$DEMO/Demo.Web/Cases/$case.cs", "$DEMO/Demo.Worker/Cases/$case.cs" | Where-Object { Test-Path -LiteralPath $_ })
            if ($files.Count -ne 1) { Fail "expected exactly one Cases/$case.cs, found $($files.Count)" }
            $source = Get-Content -Raw $files[0]
            if ([regex]::Matches($source, '(?m)^\s*namespace\s').Count -ne 1) { Fail "$($files[0]) must declare exactly one namespace" }
            if ($source -notmatch "(?:Add|Map)$case\s*\(\s*this\s") { Fail "$($files[0]) declares no Add$case or Map$case extension" }
            $program = Join-Path (Split-Path (Split-Path $files[0])) 'Program.cs'
            if ((Get-Content -Raw $program) -notmatch "\.(?:Add|Map)$case\s*\(") { Fail "$program does not call Add$case or Map$case" }
        }
        $expectations = Get-Expectations (Get-Content -Raw "$DEMO/expected-findings.json")
        $findings = @($expectations.findings | Where-Object { $_.phase -eq '3' })
        $notDefects = @($expectations.notDefects | Where-Object { $_.phase -eq '3' })
        if ($findings.Count -lt 31 -or $notDefects.Count -lt 13) { Fail "phase-3 entries: $($findings.Count) findings, $($notDefects.Count) notDefects" }
        foreach ($id in $REQUIRED_FINDINGS.Keys) {
            if (@($findings | Where-Object { $_.id -eq $id -and $_.rule -eq $REQUIRED_FINDINGS[$id] }).Count -ne 1) { Fail "missing finding $id with rule $($REQUIRED_FINDINGS[$id])" }
        }
        foreach ($id in $REQUIRED_NOT_DEFECTS) {
            if (@($notDefects | Where-Object { $_.id -eq $id }).Count -ne 1) { Fail "missing notDefects $id" }
        }
        Assert-OtherPhasesUnchanged
        if ((Test-ChangedSinceBase "$DEMO/Demo.Web/Cases") -or (Test-ChangedSinceBase "$DEMO/Demo.Worker/Cases")) { Fail 'an existing case file changed or was deleted' }
        $scenarios = @(Get-Content "$DEMO/SCENARIOS.md")
        if ($scenarios -notcontains '## Фаза 3 — написаны') { Fail 'SCENARIOS.md has no section "Фаза 3 — написаны"' }
        $question = @($scenarios | Where-Object { $_ -match '^\|\s*3\s*\|' })
        if ($question.Count -ne 1 -or $question[0] -notmatch 'Да') { Fail 'SCENARIOS.md question 3 is not closed' }
        Build-Solution 'Debug'
        $tests = Get-ListedTests 'Debug'
        foreach ($name in $PHASE_2B_GATE, $TEMPORARY_TEST) {
            if ($tests -notcontains "ConcurrencyHunter.Core.Tests.DemoExpectationTests.$name") { Fail "DemoExpectationTests.$name does not exist" }
        }
        Invoke-Native 'the two demo tests' { dotnet test $SOLUTION --nologo --no-build --filter "FullyQualifiedName~DemoExpectationTests.$PHASE_2B_GATE|FullyQualifiedName~DemoExpectationTests.$TEMPORARY_TEST" } | Out-Null
        Write-Output 'check-phase3: task 1 ok'
        exit 0
    }

    Assert-DemoPinned

    if ($Task -eq 9) {
        Assert-BaselineWithEShop
        $tests = Get-ListedTests 'Release'
        if ($tests -notcontains "ConcurrencyHunter.Core.Tests.DemoExpectationTests.$PHASE_3_GATE") { Fail "DemoExpectationTests.$PHASE_3_GATE does not exist" }
        foreach ($name in $PHASE_2B_GATE, $TEMPORARY_TEST) {
            if ($tests -contains "ConcurrencyHunter.Core.Tests.DemoExpectationTests.$name") { Fail "DemoExpectationTests.$name still exists" }
        }
        $grid = Get-Content -Raw 'concurrency-hunter/skills/hunt/evals/metrics/run-limits-grid.ps1'
        if (-not $grid.Contains($PHASE_3_GATE) -or $grid.Contains($PHASE_2B_GATE)) { Fail 'run-limits-grid.ps1 does not call the phase-3 gate test' }
        $engine = (Invoke-DemoMetrics).engineVersion
        Assert-Measurements $engine
        Write-Output 'check-phase3: task 9 ok'
        exit 0
    }

    Build-Solution 'Debug'
    $tests = Get-ListedTests 'Debug'
    $baseTests = Get-BaseTests
    switch ($Task) {
        2 {
            Assert-ClassAtLeast $tests 'ConcurrencyHunter.Core.Tests.IrSpawnLoweringTests' 48
            if ((Get-Content -Raw 'concurrency-hunter/src/ConcurrencyHunter.Analysis/Ir/IrSchema.cs') -notmatch 'VERSION\s*=\s*"1\.2"') { Fail 'IrSchema.VERSION is not 1.2' }
        }
        3 {
            Assert-ClassAtLeast $tests 'ConcurrencyHunter.Core.Tests.Engine.SpawnHeapTests' 25
            foreach ($class in 'MethodSummaryTests', 'ReachableSetTests', 'InterproceduralAccessTests') {
                Assert-ClassNotBelowBase $tests $baseTests "ConcurrencyHunter.Core.Tests.Engine.$class"
            }
        }
        4 {
            Assert-ClassAtLeast $tests 'ConcurrencyHunter.Core.Tests.Engine.SpawnExecutionTests' 27
        }
        5 {
            Assert-ClassAtLeast $tests 'ConcurrencyHunter.Core.Tests.Engine.HappensBeforeTests' 55
            Assert-ClassNotBelowBase $tests $baseTests 'ConcurrencyHunter.Core.Tests.Engine.OwnershipAndConstructionTests'
            $leftovers = @(Get-ChildItem -Path 'concurrency-hunter/src' -Recurse -File -Include '*.cs', '*.json', '*.md' |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.FullName -notmatch '\.Tests[\\/]' } |
                Select-String -SimpleMatch 'startup-construction-access')
            if ($leftovers.Count -ne 0) { $leftovers | ForEach-Object { "$($_.Path):$($_.LineNumber)" } | Write-Host; Fail 'startup-construction-access is still in the product code' }
        }
        6 {
            Assert-ClassAtLeast $tests 'ConcurrencyHunter.Core.Tests.Engine.TimerTests' 37
        }
        7 {
            $grpc = @($tests | Where-Object { $_ -like 'ConcurrencyHunter.Core.Tests.Providers.AspNetCoreRootProviderTests.AspNetCore_Grpc*' }).Count
            if ($grpc -lt 8) { Fail "only $grpc AspNetCore_Grpc tests" }
            Assert-ClassAtLeast $tests 'ConcurrencyHunter.Core.Tests.Engine.GrpcEndToEndTests' 1
            Assert-ClassNotBelowBase $tests $baseTests 'ConcurrencyHunter.Core.Tests.Providers.AspNetCoreRootProviderTests'
            Invoke-Native 'the demo build' { dotnet build "$DEMO/Demo.slnx" --nologo } | Out-Null
        }
        8 {
            $renderer = Get-Content -Raw 'concurrency-hunter/src/ConcurrencyHunter.Analysis/Reporting/ReportRenderer.cs'
            if ($renderer.Contains('spawn sites and ordering') -or -not $renderer.Contains('pairs ordered by happens-before')) { Fail 'ReportRenderer.cs coverage lines are not the phase-3 ones' }
            $ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path 'concurrency-hunter/skills/hunt/evals/run-hosts.ps1').Path, [ref]$null, [ref]$null)
            if ($null -eq $ast.ParamBlock -or @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Hosts' }).Count -ne 1) { Fail 'run-hosts.ps1 declares no Hosts parameter' }
            Assert-DocumentationChecks
            $report = 'ConcurrencyHunter.Core.Tests.ReportSkeletonTests', 'ConcurrencyHunter.Core.Tests.ReportRendererTests', 'ConcurrencyHunter.Core.Tests.NarrativeValidatorTests'
            $reportNow = ($report | ForEach-Object { Get-ClassCount $tests $_ } | Measure-Object -Sum).Sum
            $reportThen = ($report | ForEach-Object { Get-ClassCount $baseTests $_ } | Measure-Object -Sum).Sum
            if ($reportNow -lt $reportThen + 8) { Fail "the report classes grew from $reportThen to $reportNow, expected at least 8 more" }
            $tools = 'ConcurrencyHunter.Cli.Tests.RunToolsTests', 'ConcurrencyHunter.Cli.Tests.RunLifecycleTests'
            $toolsNow = ($tools | ForEach-Object { Get-ClassCount $tests $_ } | Measure-Object -Sum).Sum
            $toolsThen = ($tools | ForEach-Object { Get-ClassCount $baseTests $_ } | Measure-Object -Sum).Sum
            if ($toolsNow -lt $toolsThen + 1) { Fail 'RunToolsTests and RunLifecycleTests did not grow' }
        }
    }
    Invoke-SuiteWithoutDemoGroup
    Write-Output "check-phase3: task $Task ok"
    exit 0
}
finally {
    Pop-Location
}
