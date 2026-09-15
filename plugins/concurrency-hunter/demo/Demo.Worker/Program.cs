using Demo.Worker.Cases.SharedLibraryStatic;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedService<SyncWorker>();

builder.Build().Run();
