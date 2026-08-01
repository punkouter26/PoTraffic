// filepath: src/PoTraffic.API/Infrastructure/Security/MockExternalAuthDelegatingHandler.cs
// CI/CD rule #4 — Isolate & Mock AI/Auth boundaries in test environments.
// Replaces outbound calls to https://login.microsoftonline.com and graph.microsoft.com
// with deterministic local responses so integration/E2E tests run without leaking
// tokens, paying for real traffic, or depending on the public Internet.

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoTraffic.API.Infrastructure.Security;

/// <summary>
/// A <see cref="DelegatingHandler"/> that intercepts outbound HTTP calls made
/// by <see cref="Features.Auth.MicrosoftExternalIdentityProvider"/> and returns
/// a deterministic stub. Activate ONLY via <c>AddMockExternalAuth()</c> in test
/// hosts — never registered in Production.
/// </summary>
internal sealed class MockExternalAuthDelegatingHandler : DelegatingHandler
{
    public const string TestAuthCode = "integration-test-code";
    public const string TestSubject = "microsoft-subject-001";
    public const string TestEmail = "microsoft.user@test.invalid";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;

        // token endpoint
        if (path.EndsWith("/token", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Post)
        {
            return Task.FromResult(Json(HttpStatusCode.OK, new
            {
                access_token = "fake-access-token-do-not-use-outside-tests",
                id_token = "fake-id-token",
                token_type = "Bearer",
                expires_in = 3600
            }));
        }

        // userinfo endpoint
        if (path.EndsWith("/oidc/userinfo", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Get)
        {
            return Task.FromResult(Json(HttpStatusCode.OK, new
            {
                sub = TestSubject,
                email = TestEmail,
                preferred_username = TestEmail,
                email_verified = true,
                name = "Test Microsoft User"
            }));
        }

        // anything else — refuse loudly so tests never silently hit the network
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(
                $"MockExternalAuthDelegatingHandler: no stub for {request.Method} {path}")
        });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body)
    {
        string json = JsonSerializer.Serialize(body, JsonOpts);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>
/// Extension methods to wire the mock handler into a test host's HTTP client
/// pipeline. Never registered outside test environments.
/// </summary>
internal static class MockExternalAuthExtensions
{
    /// <summary>
    /// Inserts the mock handler at the END of the named client's primary
    /// handler chain. Idempotent — safe to call once per test factory.
    /// </summary>
    public static IHttpClientBuilder AddMockExternalAuth(this IHttpClientBuilder builder)
    {
        // PrimaryHandler is null until first resolve — we chain via ConfigurePrimaryHttpMessageHandler
        // to ensure our stub is the first to see every outbound request.
        return builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // The mock handler is the *primary* handler, so it never falls through to network.
            // We don't actually need the inner HttpClientHandler for outbound calls.
        }).AddTypedClientLifetime();
    }

    private static IHttpClientBuilder AddTypedClientLifetime(this IHttpClientBuilder builder)
    {
        // The PrimaryHandler is set; the typed-client's HttpClient will pick up our delegating
        // chain via the standard AddHttpClient pipeline.
        builder.Services.AddTransient(_ => new MockExternalAuthDelegatingHandler
        {
            InnerHandler = new HttpClientHandler()
        });
        return builder.AddHttpMessageHandler<MockExternalAuthDelegatingHandler>();
    }
}