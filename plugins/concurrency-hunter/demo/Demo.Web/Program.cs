// Only a list of calls: every registration, endpoint and startup write lives in its case file, so no
// two cases share a line here that the analysis could mistake for shared state.
using Demo.Web.Cases.ActionSelfOverlap;
using Demo.Web.Cases.AliasTwoFields;
using Demo.Web.Cases.AmbiguousRegistrationScopedWins;
using Demo.Web.Cases.BackgroundStopReadsOwnField;
using Demo.Web.Cases.ConstructionOtherState;
using Demo.Web.Cases.ConstructorLeaksThis;
using Demo.Web.Cases.ControllerConstructorStaticCounter;
using Demo.Web.Cases.CustomLockByName;
using Demo.Web.Cases.DeepAccessPathWildcard;
using Demo.Web.Cases.DelegateField;
using Demo.Web.Cases.DiFactoryRegistration;
using Demo.Web.Cases.DiInstanceRegistration;
using Demo.Web.Cases.DiScopedPerRequest;
using Demo.Web.Cases.DiSingletonControllerVsWorker;
using Demo.Web.Cases.DiSingletonSameLockOnInstance;
using Demo.Web.Cases.DistinctAllocationSites;
using Demo.Web.Cases.DiTransient;
using Demo.Web.Cases.EscapeIntoSingleton;
using Demo.Web.Cases.EscapeViaArrayElement;
using Demo.Web.Cases.EscapeViaCapturedClosure;
using Demo.Web.Cases.EscapeViaOutParameter;
using Demo.Web.Cases.EscapeViaStaticAssignment;
using Demo.Web.Cases.FactoryDistinctCallSites;
using Demo.Web.Cases.FactoryInterfaceDispatch;
using Demo.Web.Cases.FactoryResolvesOtherService;
using Demo.Web.Cases.FactoryReturnsSharedStatic;
using Demo.Web.Cases.FactoryScopedPerRequest;
using Demo.Web.Cases.FromServicesActionParameter;
using Demo.Web.Cases.GenericSingletonPerTypeArgument;
using Demo.Web.Cases.GroupSharedHelperManyCallers;
using Demo.Web.Cases.HostedConstructorBeforeRoots;
using Demo.Web.Cases.HostedServiceRegisteredTwice;
using Demo.Web.Cases.HostedStartVsAction;
using Demo.Web.Cases.InstanceRegistrationTouchesStatic;
using Demo.Web.Cases.InterfaceDispatchDi;
using Demo.Web.Cases.LambdaAndLocalFunction;
using Demo.Web.Cases.LocatorScopedViaCreateScope;
using Demo.Web.Cases.LocatorSingletonVsWorker;
using Demo.Web.Cases.LocatorTransientDistinct;
using Demo.Web.Cases.LocatorUnregisteredOpaque;
using Demo.Web.Cases.LockHeldByCaller;
using Demo.Web.Cases.LockIdentityThroughAlias;
using Demo.Web.Cases.MinimalApiLambdaHandler;
using Demo.Web.Cases.MinimalApiReadWrite;
using Demo.Web.Cases.MonitorEnterExitSameGate;
using Demo.Web.Cases.NonActionPublicMethod;
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
using Demo.Web.Cases.VirtualDispatchPointsTo;

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
                .AddFactoryScopedPerRequest();

var app = builder.Build();

app.ConfigureStartupWriteBeforeRun();
app.MapControllers();
app.MapMinimalApiReadWrite();
app.MapMinimalApiLambdaHandler();

app.Run();
