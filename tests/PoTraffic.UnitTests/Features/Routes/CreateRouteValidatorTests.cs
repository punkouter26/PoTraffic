using FluentAssertions;
using FluentValidation.TestHelper;
using NSubstitute;
using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;

namespace PoTraffic.UnitTests.Features.Routes;

/// <summary>
/// Tests for <see cref="CreateRouteValidator"/> input validation rules.
/// FR-014: origin and destination must not resolve to identical coordinates.
/// </summary>
public sealed class CreateRouteValidatorTests
{
    private readonly CreateRouteValidator _validator = new();

    [Fact]
    public void Validator_WhenOriginIsEmpty_ShouldHaveValidationError()
    {
        var command = new CreateRouteCommand(
            UserId: UserId.New(),
            OriginAddress: "",
            DestinationAddress: "10 Downing Street, London",
            Provider: RouteProvider.GoogleMaps);

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.OriginAddress);
    }

    [Fact]
    public void Validator_WhenDestinationIsEmpty_ShouldHaveValidationError()
    {
        var command = new CreateRouteCommand(
            UserId: UserId.New(),
            OriginAddress: "1 Infinite Loop, Cupertino",
            DestinationAddress: "",
            Provider: RouteProvider.GoogleMaps);

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.DestinationAddress);
    }

    [Fact]
    public void Validator_WhenOriginExceedsMaxLength_ShouldHaveValidationError()
    {
        string longAddress = new('A', ValidationConstants.AddressMaxLength + 1);
        var command = new CreateRouteCommand(
            UserId: UserId.New(),
            OriginAddress: longAddress,
            DestinationAddress: "Valid Destination",
            Provider: RouteProvider.GoogleMaps);

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.OriginAddress);
    }

    [Fact]
    public void Validator_WhenProviderIsInvalidEnum_ShouldHaveValidationError()
    {
        var command = new CreateRouteCommand(
            UserId: UserId.New(),
            OriginAddress: "Valid Origin",
            DestinationAddress: "Valid Destination",
            Provider: (RouteProvider)99); // invalid enum value

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Provider);
    }

    [Fact]
    public void Validator_WhenStartTimeIsInvalidFormat_ShouldHaveValidationError()
    {
        var command = new CreateRouteCommand(
            UserId: UserId.New(),
            OriginAddress: "Baker Street, London",
            DestinationAddress: "Waterloo Station, London",
            Provider: RouteProvider.GoogleMaps,
            StartTime: "7:00"); // missing leading zero

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.StartTime);
    }

    [Fact]
    public void Validator_WhenEndTimeEqualsStartTime_ShouldHaveValidationError()
    {
        var command = new CreateRouteCommand(
            UserId: UserId.New(),
            OriginAddress: "Baker Street, London",
            DestinationAddress: "Waterloo Station, London",
            Provider: RouteProvider.GoogleMaps,
            StartTime: "09:00",
            EndTime: "09:00"); // zero-length window — never fires

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.EndTime);
    }

    /// <summary>
    /// Times cross the wire as UTC, so an ordinary local window (15:00–21:00 EDT) arrives as
    /// 19:00–01:00 UTC. PollRouteJob.IsWithinWindow evaluates that correctly, so the validator
    /// must not reject it — the "start now, end six hours later" default depends on this.
    /// </summary>
    [Fact]
    public void Validator_WhenWindowWrapsMidnight_ShouldNotHaveValidationError()
    {
        var command = new CreateRouteCommand(
            UserId: UserId.New(),
            OriginAddress: "Baker Street, London",
            DestinationAddress: "Waterloo Station, London",
            Provider: RouteProvider.GoogleMaps,
            StartTime: "19:00",
            EndTime: "01:00");

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.EndTime);
    }

    [Fact]
    public void Validator_WhenDaysOfWeekMaskIsZero_ShouldHaveValidationError()
    {
        var command = new CreateRouteCommand(
            UserId: UserId.New(),
            OriginAddress: "Baker Street, London",
            DestinationAddress: "Waterloo Station, London",
            Provider: RouteProvider.GoogleMaps,
            DaysOfWeekMask: 0); // no days selected

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.DaysOfWeekMask);
    }

    [Fact]
    public void Validator_WhenScheduleIsValid_ShouldNotHaveValidationErrors()
    {
        var command = new CreateRouteCommand(
            UserId: UserId.New(),
            OriginAddress: "Baker Street, London",
            DestinationAddress: "Waterloo Station, London",
            Provider: RouteProvider.GoogleMaps,
            StartTime: "07:30",
            EndTime: "09:00",
            DaysOfWeekMask: 0x1F); // Mon–Fri

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
