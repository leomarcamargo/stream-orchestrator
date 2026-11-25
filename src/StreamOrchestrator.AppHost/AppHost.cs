var builder = DistributedApplication.CreateBuilder(args);

builder.AddDotnetProject("stream-orchestrator-web", "../StreamOrchestrator.Web/StreamOrchestrator.Web.csproj")
    .WithHttpHealthCheck("/health");

builder.Build().Run();
