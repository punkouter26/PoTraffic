var builder = DistributedApplication.CreateBuilder(args);

// SQL Server — use azure-sql-edge (ARM64-compatible) to match docker-compose.yml.
// ACCEPT_EULA=1 is required by azure-sql-edge (Aspire's default "Y" is for mssql/server).
// ConnectionStrings:Default is injected into the API, matching EF Core and Hangfire config.
var db = builder
    .AddSqlServer("sqlserver")
    .WithImageRegistry("mcr.microsoft.com")
    .WithImage("azure-sql-edge")
    .WithImageTag("latest")
    .WithEnvironment("ACCEPT_EULA", "1")
    .WithDataVolume("sqldata")
    .AddDatabase("Default");

// API project — Blazor WASM client is hosted by the API and served from its wwwroot,
// so no separate client entry is needed here.
builder.AddProject<Projects.PoTraffic_Api>("api")
    .WithReference(db)
    .WaitFor(db)
    .WithExternalHttpEndpoints();

builder.Build().Run();
