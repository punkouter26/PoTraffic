using System.Text.Json;
using System.Text.Json.Serialization;
using PoTraffic.Shared.DTOs.Account;
using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.DTOs.Alerts;
using PoTraffic.Shared.DTOs.Auth;
using PoTraffic.Shared.DTOs.History;
using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.Client.Infrastructure.Http;

// ── Client-side contracts (previously anonymous objects or page-private records) ──
public sealed record ProvidersResponse(List<string> Providers, bool GuestEnabled);
public sealed record FeatureFlags(bool UseMockProviders);
public sealed record CheckNowResponse(int DurationSeconds, int DistanceMetres);
public sealed record StopSessionRequest(SessionId SessionId);
public sealed record SaveWindowRequest(string StartTime, string EndTime, byte DaysOfWeekMask);

/// <summary>
/// Everything the dashboard renders, in one cacheable payload.
///
/// <para>
/// Persisted by <see cref="PoTraffic.Client.Infrastructure.ClientCache"/> so a return visit
/// paints real routes immediately instead of skeletons, and so the command palette can search
/// routes without waiting for — or duplicating — the dashboard's fetch.
/// </para>
/// </summary>
public sealed record DashboardSnapshot(
    List<RouteDto> Routes,
    QuotaDto? Quota,
    List<RouteInsight> Insights);

/// <summary>Per-route dashboard extras, kept as a list because the snapshot round-trips through JSON.</summary>
public sealed record RouteInsight(
    RouteId RouteId,
    OptimalDepartureDto? OptimalDeparture,
    List<PollRecordDto> RecentPolls);

/// <summary>Keys used with <see cref="PoTraffic.Client.Infrastructure.ClientCache"/>.</summary>
public static class ClientCacheKeys
{
    public const string Dashboard = "dashboard";
}

/// <summary>
/// Source-generated JSON metadata for every payload the WASM client sends or
/// receives. Required by the trim analyzer: reflection-based System.Text.Json
/// (IL2026) cannot be statically verified under trimming.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(QuotaDto))]
[JsonSerializable(typeof(UpdateProfileRequest))]
[JsonSerializable(typeof(List<UserDailyUsageDto>))]
[JsonSerializable(typeof(List<RecentVolatilityPointDto>))]
[JsonSerializable(typeof(List<ConnectionHealthDto>))]
[JsonSerializable(typeof(List<SystemConfigDto>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ProvidersResponse))]
[JsonSerializable(typeof(FeatureFlags))]
[JsonSerializable(typeof(AuthMeResponse))]
[JsonSerializable(typeof(PagedResult<RouteDto>))]
[JsonSerializable(typeof(RouteDto))]
[JsonSerializable(typeof(CreateRouteRequest))]
[JsonSerializable(typeof(List<PlaceSuggestionDto>))]
[JsonSerializable(typeof(CheckNowResponse))]
[JsonSerializable(typeof(RoutePathDto))]
[JsonSerializable(typeof(OptimalDepartureDto))]
[JsonSerializable(typeof(List<MonitoringWindowDto>))]
[JsonSerializable(typeof(List<SessionDto>))]
[JsonSerializable(typeof(PagedResult<PollRecordDto>))]
[JsonSerializable(typeof(BaselineResponse))]
[JsonSerializable(typeof(VolatilityHeatmapDto))]
[JsonSerializable(typeof(List<AlertDto>))]
[JsonSerializable(typeof(AlertDto))]
[JsonSerializable(typeof(StopSessionRequest))]
[JsonSerializable(typeof(SaveWindowRequest))]
[JsonSerializable(typeof(DashboardSnapshot))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
