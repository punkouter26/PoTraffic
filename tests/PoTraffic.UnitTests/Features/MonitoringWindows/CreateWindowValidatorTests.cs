// filepath: tests/PoTraffic.UnitTests/Features/MonitoringWindows/CreateWindowValidatorTests.cs
using FluentAssertions;
using FluentValidation.TestHelper;
using PoTraffic.API.Features.MonitoringWindows;
using Xunit;

namespace PoTraffic.UnitTests.Features.MonitoringWindows;

/// <summary>
/// Rules that gate <c>POST /api/routes/{routeId}/windows</c> before the handler runs.
/// Catches the "Failed to save schedule" regression where Eastern-time users picking
/// local business hours (e.g. 09:21–21:21) sent a UTC-converted window whose end-time
/// preceded its start-time (wrap-around midnight UTC) and the validator rejected it.
/// </summary>
public sealed class CreateWindowValidatorTests
{
    private readonly CreateWindowValidator _sut = new();

    private static CreateWindowCommand BuildCommand(TimeOnly start, TimeOnly end, byte mask = 0x1F) =>
        new(RouteId.New(), UserId.New(), start, end, mask);

    [Fact]
    public void Allows_WrapAroundMidnightUtc_SoEasternTimeWindowsPersist()
    {
        // Eastern 09:21–21:21 → UTC 13:21–01:21 (wraps midnight UTC).
        // This is the exact case the deployed app refused before the fix.
        CreateWindowCommand cmd = BuildCommand(new TimeOnly(13, 21), new TimeOnly(1, 21));

        TestValidationResult<CreateWindowCommand> result = _sut.TestValidate(cmd);

        result.IsValid.Should().BeTrue(
            "a window that wraps midnight UTC must be accepted — the client converts local to UTC, " +
            "and Eastern-time users picking daytime hours legitimately produce such wraps");
    }

    [Fact]
    public void Allows_ConventionalSameDayWindow()
    {
        CreateWindowCommand cmd = BuildCommand(new TimeOnly(9, 0), new TimeOnly(17, 0));

        _sut.TestValidate(cmd).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "midnight-to-midnight is a zero-width window")]
    [InlineData(13, 21, 13, 21, "13:21–13:21 — the exact regressed UTC pair")]
    public void Rejects_StartEqualsEnd(int sh, int sm, int eh, int em, string because)
    {
        CreateWindowCommand cmd = BuildCommand(new TimeOnly(sh, sm), new TimeOnly(eh, em));

        _sut.TestValidate(cmd).IsValid.Should().BeFalse(because);
    }

    [Fact]
    public void Rejects_WhenNoDaySelected()
    {
        CreateWindowCommand cmd = BuildCommand(new TimeOnly(9, 0), new TimeOnly(17, 0), mask: 0);

        _sut.TestValidate(cmd).IsValid.Should().BeFalse("at least one day must be enabled");
    }
}