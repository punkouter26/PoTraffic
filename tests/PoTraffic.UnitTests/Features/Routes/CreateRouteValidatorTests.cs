using FluentAssertions;
using FluentValidation.TestHelper;
using NSubstitute;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure.Providers;
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
            UserId: Guid.NewGuid(),
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
            UserId: Guid.NewGuid(),
            OriginAddress: "1 Infinite Loop, Cupertino",
            DestinationAddress: "",
            Provider: RouteProvider.GoogleMaps);

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.DestinationAddress);
    }

    [Fact]
    public void Validator_WhenBothAddressesEmpty_ShouldHaveValidationErrors()
    {
        var command = new CreateRouteCommand(
            UserId: Guid.NewGuid(),
            OriginAddress: "",
            DestinationAddress: "",
            Provider: RouteProvider.GoogleMaps);

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.OriginAddress);
        result.ShouldHaveValidationErrorFor(x => x.DestinationAddress);
    }

    [Fact]
    public void Validator_WhenOriginExceedsMaxLength_ShouldHaveValidationError()
    {
        string longAddress = new('A', ValidationConstants.AddressMaxLength + 1);
        var command = new CreateRouteCommand(
            UserId: Guid.NewGuid(),
            OriginAddress: longAddress,
            DestinationAddress: "Valid Destination",
            Provider: RouteProvider.GoogleMaps);

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.OriginAddress);
    }

    [Fact]
    public void Validator_WhenDestinationExceedsMaxLength_ShouldHaveValidationError()
    {
        string longAddress = new('B', ValidationConstants.AddressMaxLength + 1);
        var command = new CreateRouteCommand(
            UserId: Guid.NewGuid(),
            OriginAddress: "Valid Origin",
            DestinationAddress: longAddress,
            Provider: RouteProvider.GoogleMaps);

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.DestinationAddress);
    }

    [Fact]
    public void Validator_WhenProviderIsInvalidEnum_ShouldHaveValidationError()
    {
        var command = new CreateRouteCommand(
            UserId: Guid.NewGuid(),
            OriginAddress: "Valid Origin",
            DestinationAddress: "Valid Destination",
            Provider: (RouteProvider)99); // invalid enum value

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Provider);
    }

    [Theory]
    [InlineData(RouteProvider.GoogleMaps)]
    [InlineData(RouteProvider.TomTom)]
    public void Validator_WhenCommandIsValid_ShouldNotHaveValidationErrors(RouteProvider provider)
    {
        var command = new CreateRouteCommand(
            UserId: Guid.NewGuid(),
            OriginAddress: "Baker Street, London",
            DestinationAddress: "Waterloo Station, London",
            Provider: provider);

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    /// <summary>
    /// FR-014: same-coordinates check is enforced inside the handler (after geocoding), not in the validator.
    /// This test documents that the validator does NOT reject same origin/destination strings at validation time —
    /// only the handler rejects identical geocoded coordinates.
    /// </summary>
    [Fact]
    public void Validator_WhenOriginAndDestinationTextAreSame_ShouldNotHaveValidationError()
    {
        // Same text strings are syntactically valid; the SAME_COORDINATES check happens
        // after geocoding in CreateRouteCommandHandler (FR-014).
        var command = new CreateRouteCommand(
            UserId: Guid.NewGuid(),
            OriginAddress: "Buckingham Palace",
            DestinationAddress: "Buckingham Palace",
            Provider: RouteProvider.TomTom);

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.OriginAddress);
        result.ShouldNotHaveValidationErrorFor(x => x.DestinationAddress);
    }

    [Fact]
    public void Validator_WhenStartTimeIsInvalidFormat_ShouldHaveValidationError()
    {
        var command = new CreateRouteCommand(
            UserId: Guid.NewGuid(),
            OriginAddress: "Baker Street, London",
            DestinationAddress: "Waterloo Station, London",
            Provider: RouteProvider.GoogleMaps,
            StartTime: "7:00"); // missing leading zero

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.StartTime);
    }

    [Fact]
    public void Validator_WhenEndTimeIsBeforeStartTime_ShouldHaveValidationError()
    {
        var command = new CreateRouteCommand(
            UserId: Guid.NewGuid(),
            OriginAddress: "Baker Street, London",
            DestinationAddress: "Waterloo Station, London",
            Provider: RouteProvider.GoogleMaps,
            StartTime: "09:00",
            EndTime: "07:00"); // before start

        TestValidationResult<CreateRouteCommand> result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.EndTime);
    }

    [Fact]
    public void Validator_WhenDaysOfWeekMaskIsZero_ShouldHaveValidationError()
    {
        var command = new CreateRouteCommand(
            UserId: Guid.NewGuid(),
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
            UserId: Guid.NewGuid(),
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
