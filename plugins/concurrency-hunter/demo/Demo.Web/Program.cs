// Only a list of calls: every registration, endpoint and startup write lives in its case file, so no
// two cases share a line here that the analysis could mistake for shared state.
using Demo.Web.Cases.ActionSelfOverlap;
using Demo.Web.Cases.AliasTwoFields;
using Demo.Web.Cases.DelegateField;
using Demo.Web.Cases.DiScopedPerRequest;
using Demo.Web.Cases.DiSingletonControllerVsWorker;
using Demo.Web.Cases.DiSingletonSameLockOnInstance;
using Demo.Web.Cases.DistinctAllocationSites;
using Demo.Web.Cases.DiTransient;
using Demo.Web.Cases.EscapeIntoSingleton;
using Demo.Web.Cases.FactoryDistinctCallSites;
using Demo.Web.Cases.FactoryReturnsSharedStatic;
using Demo.Web.Cases.InterfaceDispatchDi;
using Demo.Web.Cases.LambdaAndLocalFunction;
using Demo.Web.Cases.LockHeldByCaller;
using Demo.Web.Cases.MinimalApiReadWrite;
using Demo.Web.Cases.ReceiverSensitivity;
using Demo.Web.Cases.RmwSingletonCounter;
using Demo.Web.Cases.RmwThreeLayers;
using Demo.Web.Cases.SameLockViaField;
using Demo.Web.Cases.SingletonConfiguredInConstructor;
using Demo.Web.Cases.StartupWriteBeforeRun;
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
                .AddSingletonConfiguredInConstructor();

var app = builder.Build();

app.ConfigureStartupWriteBeforeRun();
app.MapControllers();
app.MapMinimalApiReadWrite();

app.Run();
