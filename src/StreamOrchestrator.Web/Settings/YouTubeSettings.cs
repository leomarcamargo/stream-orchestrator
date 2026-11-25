using System.ComponentModel.DataAnnotations;

namespace StreamOrchestrator.Web.Settings;

public sealed class YouTubeSettings
{
    /// <summary>Id of an owned or authorised video. Empty means the demo is not configured.</summary>
    [RegularExpression(
        "^([A-Za-z0-9_-]{11})?$",
        ErrorMessage = "VideoId must be an 11-character YouTube video id or empty."
    )]
    public string VideoId { get; set; } = "";
}
