using System.ComponentModel.DataAnnotations;

namespace StreamOrchestrator.Web.Settings;

public sealed class ExecutionSettings
{
    [Range(1, 3600)]
    public int DefaultDurationSeconds { get; set; } = 30;

    [Range(1, 5000)]
    public int DefaultSessions { get; set; } = 100;

    [Range(1, 3600)]
    public int MaxDurationSeconds { get; set; } = 300;

    [Range(1, 5000)]
    public int MaxSessions { get; set; } = 1000;
}
