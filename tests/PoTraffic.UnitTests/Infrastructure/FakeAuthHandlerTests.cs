using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PoTraffic.API.Infrastructure.Security;

namespace PoTraffic.UnitTests.Infrastructure;

/// <summary>
/// Covers the Rule 3 guardrail (fake auth must never exist in Production) and the
/// header contract the Dev/Test tiers rely on.
/// </summary>
public sealed class FakeAuthHandlerTests
{
    private static IWebHostEnvironment Env(string environmentName)
    {
        IWebHostEnvironment env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);
        return env;
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(
        string environmentName,
        Action<HttpContext> configureRequest)
    {
        IOptionsMonitor<AuthenticationSchemeOptions> options =
            Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        FakeAuthHandler handler = new(
            options, NullLoggerFactory.Instance, UrlEncoder.Default, Env(environmentName));

        DefaultHttpContext context = new();
        configureRequest(context);

        await handler.InitializeAsync(
            new AuthenticationScheme(FakeAuthHandler.SchemeName, null, typeof(FakeAuthHandler)),
            context);

        return await handler.AuthenticateAsync();
    }

    [Fact]
    public void GuardNotProduction_InProduction_Throws()
    {
        Action act = () => FakeAuthHandler.GuardNotProduction(Env("Production"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must never be active in Production*");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void GuardNotProduction_OutsideProduction_DoesNotThrow(string environmentName)
    {
        Action act = () => FakeAuthHandler.GuardNotProduction(Env(environmentName));

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_InProduction_Throws()
    {
        IOptionsMonitor<AuthenticationSchemeOptions> options =
            Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();

        Action act = () => _ = new FakeAuthHandler(
            options, NullLoggerFactory.Instance, UrlEncoder.Default, Env("Production"));

        act.Should().Throw<InvalidOperationException>(
            "constructing the handler must fail even if registration somehow let it through");
    }

    [Fact]
    public async Task WithoutUserHeader_ReturnsNoResult()
    {
        AuthenticateResult result = await AuthenticateAsync("Testing", _ => { });

        result.None.Should().BeTrue("an unauthenticated request must fall through to the cookie scheme");
    }

}
