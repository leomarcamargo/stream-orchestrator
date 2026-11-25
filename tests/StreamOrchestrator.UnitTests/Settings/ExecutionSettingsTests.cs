using System.ComponentModel.DataAnnotations;
using StreamOrchestrator.Web.Settings;

namespace StreamOrchestrator.UnitTests.Settings;

public class ExecutionSettingsTests
{
    private static bool IsValid(ExecutionSettings settings) =>
        Validator.TryValidateObject(
            settings,
            new ValidationContext(settings),
            [],
            validateAllProperties: true
        );

    [Fact]
    public void Constructor_WhenCreated_ShouldUseValidDefaults()
    {
        var settings = new ExecutionSettings();

        Assert.Equal(30, settings.DefaultDurationSeconds);
        Assert.Equal(100, settings.DefaultSessions);
        Assert.Equal(300, settings.MaxDurationSeconds);
        Assert.Equal(1000, settings.MaxSessions);
        Assert.True(IsValid(settings));
    }

    [Theory]
    [InlineData(nameof(ExecutionSettings.DefaultDurationSeconds), 0, false)]
    [InlineData(nameof(ExecutionSettings.DefaultDurationSeconds), 1, true)]
    [InlineData(nameof(ExecutionSettings.DefaultDurationSeconds), 3600, true)]
    [InlineData(nameof(ExecutionSettings.DefaultDurationSeconds), 3601, false)]
    [InlineData(nameof(ExecutionSettings.DefaultSessions), 0, false)]
    [InlineData(nameof(ExecutionSettings.DefaultSessions), 1, true)]
    [InlineData(nameof(ExecutionSettings.DefaultSessions), 5000, true)]
    [InlineData(nameof(ExecutionSettings.DefaultSessions), 5001, false)]
    [InlineData(nameof(ExecutionSettings.MaxDurationSeconds), 0, false)]
    [InlineData(nameof(ExecutionSettings.MaxDurationSeconds), 1, true)]
    [InlineData(nameof(ExecutionSettings.MaxDurationSeconds), 3600, true)]
    [InlineData(nameof(ExecutionSettings.MaxDurationSeconds), 3601, false)]
    [InlineData(nameof(ExecutionSettings.MaxSessions), 0, false)]
    [InlineData(nameof(ExecutionSettings.MaxSessions), 1, true)]
    [InlineData(nameof(ExecutionSettings.MaxSessions), 5000, true)]
    [InlineData(nameof(ExecutionSettings.MaxSessions), 5001, false)]
    public void Validate_WhenValueIsAtTheBoundary_ShouldAcceptOnlyValuesInRange(
        string property,
        int value,
        bool expectedValid
    )
    {
        var settings = new ExecutionSettings();
        typeof(ExecutionSettings).GetProperty(property)!.SetValue(settings, value);

        var isValid = IsValid(settings);

        Assert.Equal(expectedValid, isValid);
    }
}
