using System.Net.Http.Json;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.E2EAPI;

/// <summary>Creates cookie-authenticated HttpClients against the running Testing instance.</summary>
public static class ApiSessionFactory
{
    public static HttpClient CreateAnonymous() => new()
    {
        BaseAddress = new Uri(SkipUnlessApiReadyAttribute.BaseUrl)
    };

    /// <summary>Guest-logs-in; the handler's cookie container holds the BFF session.</summary>
    public static async Task<(HttpClient Client, AuthMeResponse Me)> CreateGuestSessionAsync()
    {
        HttpClient client = CreateAnonymous();
        HttpResponseMessage resp = await client.PostAsync("/api/auth/guest-login", content: null);
        resp.EnsureSuccessStatusCode();
        AuthMeResponse me = (await resp.Content.ReadFromJsonAsync<AuthMeResponse>())!;
        return (client, me);
    }
}
