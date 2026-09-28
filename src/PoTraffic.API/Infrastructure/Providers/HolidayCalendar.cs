using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace PoTraffic.API.Infrastructure.Providers;

/// <summary>Public holidays, so a Thanksgiving-empty motorway doesn't pass for a normal Thursday.</summary>
public interface IHolidayCalendar
{
    /// <summary>The nationwide public holiday on <paramref name="date"/>, or null (also when unknown).</summary>
    Task<string?> HolidayOnAsync(string countryCode, DateOnly date, CancellationToken ct = default);
}

/// <summary>
/// Nager.Date (date.nager.at): free, no API key. One request per country per year, cached a
/// day. Only nationwide holidays count — a state holiday elsewhere says nothing about this
/// commute. A failed fetch caches "no holidays" so it never costs a poll.
/// </summary>
public sealed class NagerHolidayCalendar(HttpClient http, HybridCache cache, ILogger<NagerHolidayCalendar> logger)
    : IHolidayCalendar
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(1);

    public async Task<string?> HolidayOnAsync(string countryCode, DateOnly date, CancellationToken ct = default)
    {
        Dictionary<DateOnly, string> holidays = await cache.GetOrCreateAsync(
            $"holidays:{countryCode}:{date.Year}",
            (Calendar: this, Country: countryCode, date.Year),
            static (s, token) => s.Calendar.FetchAsync(s.Country, s.Year, token),
            new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor },
            cancellationToken: ct);
        return holidays.GetValueOrDefault(date);
    }

    private async ValueTask<Dictionary<DateOnly, string>> FetchAsync(string country, int year, CancellationToken ct)
    {
        try
        {
            NagerHoliday[]? rows = await http.GetFromJsonAsync<NagerHoliday[]>(
                $"https://date.nager.at/api/v3/PublicHolidays/{year}/{Uri.EscapeDataString(country)}", ct);
            return (rows ?? [])
                .Where(h => h.Global && DateOnly.TryParse(h.Date, out _))
                .GroupBy(h => DateOnly.Parse(h.Date))
                .ToDictionary(g => g.Key, g => g.First().Name);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            // 404 for a country Nager.Date doesn't cover lands here too.
            logger.LogWarning(ex, "Holiday lookup failed for {Country} {Year}", country, year);
            return [];
        }
    }

    private sealed record NagerHoliday(string Date, string Name, bool Global);
}

/// <summary>Mock mode and tests: never a holiday, never a network call.</summary>
public sealed class NoHolidayCalendar : IHolidayCalendar
{
    public static readonly NoHolidayCalendar Instance = new();
    public Task<string?> HolidayOnAsync(string countryCode, DateOnly date, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
