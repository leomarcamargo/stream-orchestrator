using StreamOrchestrator.Web.Components;
using StreamOrchestrator.Web.Orchestration;
using StreamOrchestrator.Web.Services;
using StreamOrchestrator.Web.Services.Interfaces;
using StreamOrchestrator.Web.Settings;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(SessionOrchestrator.TelemetryName))
    .WithTracing(tracing => tracing.AddSource(SessionOrchestrator.TelemetryName));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOptions<ExecutionSettings>()
    .BindConfiguration(nameof(ExecutionSettings))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<YouTubeSettings>()
    .BindConfiguration(nameof(YouTubeSettings))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IPlaybackService, PlaybackService>();
builder.Services.AddSingleton<SessionOrchestrator>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
