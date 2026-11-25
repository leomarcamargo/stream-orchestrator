using System.ComponentModel.DataAnnotations;
using StreamOrchestrator.Web.Settings;

namespace StreamOrchestrator.UnitTests.Settings;

public class YouTubeSettingsTests
{
    private static bool IsValid(YouTubeSettings settings) =>
        Validator.TryValidateObject(
            settings,
            new ValidationContext(settings),
            [],
            validateAllProperties: true
        );

    [Fact]
    public void Constructor_WhenCreated_ShouldLeaveTheDemoUnconfigured()
    {
        var settings = new YouTubeSettings();

        Assert.Equal(string.Empty, settings.VideoId);
        Assert.True(IsValid(settings));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("YE7VzlLtp-4", true)]
    [InlineData("a_B-c1D2e3F", true)]
    [InlineData("short", false)]
    [InlineData("YE7VzlLtp-44", false)]
    [InlineData("YE7VzlLtp-!", false)]
    [InlineData(" YE7VzlLtp-4", false)]
    public void Validate_WhenVideoIdIsProvided_ShouldAcceptOnlyElevenCharacterIdsOrEmpty(
        string videoId,
        bool expectedValid
    )
    {
        var settings = new YouTubeSettings
        {
            VideoId = videoId
        };

        var isValid = IsValid(settings);

        Assert.Equal(expectedValid, isValid);
    }
}
