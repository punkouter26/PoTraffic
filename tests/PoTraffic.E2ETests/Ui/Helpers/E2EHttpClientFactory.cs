// filepath: tests/PoTraffic.E2ETests/Ui/Helpers/E2EHttpClientFactory.cs
using System;
using System.Net.Http;

namespace PoTraffic.E2ETests.Ui.Helpers;

/// <summary>
/// Factory for the simple <see cref="HttpClient"/> instances used by the
/// Playwright scenarios to seed the testing-only API. Centralises the
/// base-address wiring so individual scenarios stay terse.
/// </summary>
public static class E2EHttpClientFactory
{
    /// <summary>Creates a one-shot <see cref="HttpClient"/> pointed at the live API.</summary>
    public static HttpClient CreateApiClient(string baseUrl) =>
        new() { BaseAddress = new Uri(baseUrl) };
}
