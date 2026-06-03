using FluentAssertions;
using FluentValidation.Results;
using NSubstitute;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Api.Infrastructure.Storage;

namespace PoTraffic.UnitTests.Features.Auth;

/// <summary>
/// Unit tests for <see cref="RegisterCommandValidator"/> and <see cref="LoginCommandValidator"/>.
/// FR-013: email uniqueness; validators must reject bad input before hitting the handler.
/// </summary>
public sealed class AuthValidatorTests
{
    [Fact]
    public async Task RegisterValidator_WithDuplicateEmail_ReturnsValidationError()
    {
        // Arrange — seed in-memory DB with existing user
        await using TableStorageContext db = new();
        db.Set<User>().Add(new User
        {
            Id = Guid.NewGuid(),
            Email = "existing@example.com",
            PasswordHash = "hash",
            Locale = "Europe/London",
            Role = "Commuter",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var validator = new RegisterCommandValidator(db);
        var command = new RegisterCommand("existing@example.com", "Password1!", "Europe/London");

        // Act
        ValidationResult result = await validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse("duplicate email must be rejected (FR-013)");
        result.Errors.Should().Contain(e => e.PropertyName == "Email");
    }

    [Fact]
    public async Task RegisterValidator_WithNewEmail_Passes()
    {
        await using TableStorageContext db = new();
        var validator = new RegisterCommandValidator(db);
        var command = new RegisterCommand("new@example.com", "Password1!", "en-US");

        ValidationResult result = await validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterValidator_WithEmptyPassword_Fails()
    {
        await using TableStorageContext db = new();
        var validator = new RegisterCommandValidator(db);
        var command = new RegisterCommand("user@example.com", "", "Europe/London");

        ValidationResult result = await validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse("empty password must be rejected");
        result.Errors.Should().Contain(e => e.PropertyName == "Password");
    }

    [Fact]
    public async Task LoginValidator_WithEmptyPassword_ReturnsValidationError()
    {
        var validator = new LoginCommandValidator();
        var command = new LoginCommand("user@example.com", "");

        ValidationResult result = await validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse("empty password must be rejected");
        result.Errors.Should().Contain(e => e.PropertyName == "Password");
    }

    [Fact]
    public async Task LoginValidator_WithValidCredentials_Passes()
    {
        var validator = new LoginCommandValidator();
        var command = new LoginCommand("user@example.com", "Password1!");

        ValidationResult result = await validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
