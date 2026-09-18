// Only a list of calls: every registration, endpoint and startup write lives in its case file, so no
// two cases share a line here that the analysis could mistake for shared state.
using Demo.Web.Cases.ActionSelfOverlap;
using Demo.Web.Cases.AliasTwoFields;
using Demo.Web.Cases.AmbiguousRegistrationScopedWins;
using Demo.Web.Cases.AsyncTailEntry;
using Demo.Web.Cases.AsyncVoidCall;
using Demo.Web.Cases.AwaitConditionalTask;
using Demo.Web.Cases.BackgroundStopReadsOwnField;
using Demo.Web.Cases.BranchingJoin;
using Demo.Web.Cases.CalleeJoinsOnAllPaths;
using Demo.Web.Cases.ConditionalJoinInsideWork;
using Demo.Web.Cases.ConstructionOtherState;
using Demo.Web.Cases.ConstructorLeaksThis;
using Demo.Web.Cases.ContinueWith;
using Demo.Web.Cases.ControllerConstructorStaticCounter;
using Demo.Web.Cases.CustomLockByName;
using Demo.Web.Cases.DeepAccessPathWildcard;
using Demo.Web.Cases.DelegateField;
using Demo.Web.Cases.DiFactoryRegistration;
using Demo.Web.Cases.DiInstanceRegistration;
using Demo.Web.Cases.DiScopedPerRequest;
using Demo.Web.Cases.DiSingletonControllerVsWorker;
using Demo.Web.Cases.DiSingletonSameLockOnInstance;
using Demo.Web.Cases.DisposeAsyncConfigureAwait;
using Demo.Web.Cases.DistinctAllocationSites;
using Demo.Web.Cases.DiTransient;
using Demo.Web.Cases.EscapeIntoSingleton;
using Demo.Web.Cases.EscapeViaArrayElement;
using Demo.Web.Cases.EscapeViaCapturedClosure;
using Demo.Web.Cases.EscapeViaOutParameter;
using Demo.Web.Cases.EscapeViaStaticAssignment;
using Demo.Web.Cases.EventWaitNotOrdering;
using Demo.Web.Cases.FactoryDistinctCallSites;
using Demo.Web.Cases.FactoryInterfaceDispatch;
using Demo.Web.Cases.FactoryResolvesOtherService;
using Demo.Web.Cases.FactoryReturnsSharedStatic;
using Demo.Web.Cases.FactoryScopedPerRequest;
using Demo.Web.Cases.FireAndForgetVsAwaited;
using Demo.Web.Cases.FromServicesActionParameter;
using Demo.Web.Cases.GenericSingletonPerTypeArgument;
using Demo.Web.Cases.GroupSharedHelperManyCallers;
using Demo.Web.Cases.GrpcServiceMethod;
using Demo.Web.Cases.HostedConstructorBeforeRoots;
using Demo.Web.Cases.HostedServiceRegisteredTwice;
using Demo.Web.Cases.HostedStartVsAction;
using Demo.Web.Cases.InstanceRegistrationTouchesStatic;
using Demo.Web.Cases.InterfaceDispatchDi;
using Demo.Web.Cases.JoinSkippedOnException;
using Demo.Web.Cases.LambdaAndLocalFunction;
using Demo.Web.Cases.LocatorScopedViaCreateScope;
using Demo.Web.Cases.LocatorSingletonVsWorker;
using Demo.Web.Cases.LocatorTransientDistinct;
using Demo.Web.Cases.LocatorUnregisteredOpaque;
using Demo.Web.Cases.LockHeldByCaller;
using Demo.Web.Cases.LockIdentityThroughAlias;
using Demo.Web.Cases.MaybeNullHandle;
using Demo.Web.Cases.MinimalApiLambdaHandler;
using Demo.Web.Cases.MinimalApiReadWrite;
using Demo.Web.Cases.MonitorEnterExitSameGate;
using Demo.Web.Cases.NonActionPublicMethod;
using Demo.Web.Cases.ParallelForeach;
using Demo.Web.Cases.ParallelForeachAsync;
using Demo.Web.Cases.ParallelForSharedTotal;
using Demo.Web.Cases.PeriodicTimerLoop;
using Demo.Web.Cases.PocoControllerSelfOverlap;
using Demo.Web.Cases.PrimaryConstructorInjection;
using Demo.Web.Cases.ReceiverSensitivity;
using Demo.Web.Cases.RecursiveSummary;
using Demo.Web.Cases.RmwForms;
using Demo.Web.Cases.RmwSingletonCounter;
using Demo.Web.Cases.RmwThreeLayers;
using Demo.Web.Cases.SameLockViaField;
using Demo.Web.Cases.SingletonConfiguredInConstructor;
using Demo.Web.Cases.StaleReadWithoutDependency;
using Demo.Web.Cases.StartupWriteBeforeRun;
using Demo.Web.Cases.StaticConstructorInitialization;
using Demo.Web.Cases.StaticFieldHttpVsWorker;
using Demo.Web.Cases.TaskFactoryStartNew;
using Demo.Web.Cases.TaskHandleAwaitedElsewhere;
using Demo.Web.Cases.TaskHandleJoinOrder;
using Demo.Web.Cases.TaskRunVsParent;
using Demo.Web.Cases.TaskWaitJoin;
using Demo.Web.Cases.ThreadingTimerSelfOverlap;
using Demo.Web.Cases.ThreadingTimerVsAction;
using Demo.Web.Cases.ThreadPoolQueueUserWorkItem;
using Demo.Web.Cases.ThreadStartJoin;
using Demo.Web.Cases.TimerCapturedAlias;
using Demo.Web.Cases.TimerChangeReactivates;
using Demo.Web.Cases.TimerDisposeAsyncAwaited;
using Demo.Web.Cases.TimerDisposeDoesNotJoin;
using Demo.Web.Cases.TimerDisposeWaitHandle;
using Demo.Web.Cases.TimerNeverActivated;
using Demo.Web.Cases.TimerOneShot;
using Demo.Web.Cases.TimerStateSharing;
using Demo.Web.Cases.TimersTimerElapsed;
using Demo.Web.Cases.VirtualDispatchPointsTo;
using Demo.Web.Cases.WhenAllContinueWith;
using Demo.Web.Cases.WhenAllSiblings;
using Demo.Web.Cases.WhenAllSynchronousPrefix;
using Demo.Web.Cases.WhenAnyNoJoin;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddDiSingletonControllerVsWorker()
                .AddMinimalApiReadWrite()
                .AddActionSelfOverlap()
                .AddStaticFieldHttpVsWorker()
                .AddDiScopedPerRequest()
                .AddDiTransient()
                .AddDiSingletonSameLockOnInstance()
                .AddStartupWriteBeforeRun()
                .AddRmwSingletonCounter()
                .AddRmwThreeLayers()
                .AddAliasTwoFields()
                .AddInterfaceDispatchDi()
                .AddVirtualDispatchPointsTo()
                .AddDelegateField()
                .AddLambdaAndLocalFunction()
                .AddEscapeIntoSingleton()
                .AddFactoryReturnsSharedStatic()
                .AddDistinctAllocationSites()
                .AddReceiverSensitivity()
                .AddFactoryDistinctCallSites()
                .AddSameLockViaField()
                .AddLockHeldByCaller()
                .AddSingletonConfiguredInConstructor()
                .AddHostedStartVsAction()
                .AddBackgroundStopReadsOwnField()
                .AddPocoControllerSelfOverlap()
                .AddPrimaryConstructorInjection()
                .AddFromServicesActionParameter()
                .AddMonitorEnterExitSameGate()
                .AddAmbiguousRegistrationScopedWins()
                .AddDiFactoryRegistration()
                .AddDiInstanceRegistration()
                .AddMinimalApiLambdaHandler()
                .AddNonActionPublicMethod()
                .AddHostedServiceRegisteredTwice()
                .AddRmwForms()
                .AddStaleReadWithoutDependency()
                .AddRecursiveSummary()
                .AddGenericSingletonPerTypeArgument()
                .AddEscapeViaArrayElement()
                .AddEscapeViaOutParameter()
                .AddEscapeViaCapturedClosure()
                .AddEscapeViaStaticAssignment()
                .AddLockIdentityThroughAlias()
                .AddDeepAccessPathWildcard()
                .AddCustomLockByName()
                .AddStaticConstructorInitialization()
                .AddGroupSharedHelperManyCallers()
                .AddControllerConstructorStaticCounter()
                .AddConstructionOtherState()
                .AddHostedConstructorBeforeRoots()
                .AddConstructorLeaksThis()
                .AddLocatorSingletonVsWorker()
                .AddLocatorScopedViaCreateScope()
                .AddLocatorTransientDistinct()
                .AddLocatorUnregisteredOpaque()
                .AddFactoryInterfaceDispatch()
                .AddFactoryResolvesOtherService()
                .AddInstanceRegistrationTouchesStatic()
                .AddFactoryScopedPerRequest()
                .AddTaskRunVsParent()
                .AddTaskFactoryStartNew()
                .AddTaskHandleJoinOrder()
                .AddTaskHandleAwaitedElsewhere()
                .AddTaskWaitJoin()
                .AddContinueWith()
                .AddThreadPoolQueueUserWorkItem()
                .AddThreadStartJoin()
                .AddParallelForSharedTotal()
                .AddParallelForeach()
                .AddParallelForeachAsync()
                .AddWhenAllSiblings()
                .AddWhenAllSynchronousPrefix()
                .AddWhenAnyNoJoin()
                .AddFireAndForgetVsAwaited()
                .AddAsyncVoidCall()
                .AddJoinSkippedOnException()
                .AddEventWaitNotOrdering()
                .AddThreadingTimerVsAction()
                .AddThreadingTimerSelfOverlap()
                .AddTimerStateSharing()
                .AddTimerCapturedAlias()
                .AddTimerNeverActivated()
                .AddTimerOneShot()
                .AddTimerChangeReactivates()
                .AddTimerDisposeDoesNotJoin()
                .AddTimerDisposeAsyncAwaited()
                .AddTimerDisposeWaitHandle()
                .AddTimersTimerElapsed()
                .AddPeriodicTimerLoop()
                .AddGrpcServiceMethod()
                .AddAsyncTailEntry()
                .AddAwaitConditionalTask()
                .AddBranchingJoin()
                .AddCalleeJoinsOnAllPaths()
                .AddConditionalJoinInsideWork()
                .AddDisposeAsyncConfigureAwait()
                .AddMaybeNullHandle()
                .AddWhenAllContinueWith();

var app = builder.Build();

app.ConfigureStartupWriteBeforeRun();
app.MapControllers();
app.MapMinimalApiReadWrite();
app.MapMinimalApiLambdaHandler();
app.MapGrpcServiceMethod();

app.Run();
