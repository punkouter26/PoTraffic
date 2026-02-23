using FluentAssertions;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.UnitTests.Features.Auth;

public sealed class ExternalAuthServiceTests
{
    [Fact]
    public void BuildCompletionRedirectPath_WithSuccessResult_IncludesAccessTokenAndReturnUrl()
    {
        var authResponse = new AuthResponse(
            AccessToken: "access-token-123",
            RefreshToken: "refresh-token-123",
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            UserId: Guid.NewGuid(),
            Role: "Commuter");

        var result = new ExternalAuthCompletionResult(
            IsSuccess: true,
            ReturnPath: "/dashboard",
            Response: authResponse,
            ErrorCode: null);

        string redirect = ExternalAuthService.BuildCompletionRedirectPath(result);

        redirect.Should().StartWith("/auth/external-complete#");
        redirect.Should().Contain("accessToken=");
        redirect.Should().Contain("returnUrl=%2Fdashboard");
    }

    [Fact]
    public void BuildCompletionRedirectPath_WithErrorResult_IncludesErrorCode()
    {
        var result = new ExternalAuthCompletionResult(
            IsSuccess: false,
            ReturnPath: "/dashboard",
            Response: null,
            ErrorCode: "INVALID_STATE");

        string redirect = ExternalAuthService.BuildCompletionRedirectPath(result);

        redirect.Should().Be("/auth/external-complete#error=INVALID_STATE&returnUrl=%2Fdashboard");
    }
}
