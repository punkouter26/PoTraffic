using System.Text.Json;
using System.Text.Json.Serialization;
using PoTraffic.Client.Infrastructure.Logging;
using PoTraffic.Shared.DTOs.Account;
using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.DTOs.Alerts;
using PoTraffic.Shared.DTOs.Auth;
using PoTraffic.Shared.DTOs.History;
using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.Client.Infrastructure.Http;

// ── Client-side contracts (previously anonymous objects or page-private records) ──
public sealed record ProvidersResponse(List<string> Providers, bool GuestEnabled);
public sealed record FeatureFlags(bool TripleTestEnabled, bool UseMockProviders);
public sealed record CheckNowResponse(int DurationSeconds, int DistanceMetres);
public sealed record SeedDataRequest(int RouteCount, int DaysOfHistory);
public sealed record SeedResult(int UsersCreated, int RoutesCreated, int PollsCreated);
public sealed record ClearResult(int UsersDeleted, int RoutesDeleted, int PollsDeleted);
public sealed record StopSessionRequest(Guid SessionId);
public sealed record SaveWindowRequest(string StartTime, string EndTime, byte DaysOfWeekMask);
internal sealed record ClientLogBatch(List<ClientLogEntry> Entries);

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
[JsonSerializable(typeof(TripleTestRequest))]
[JsonSerializable(typeof(TripleTestSessionDto))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ProvidersResponse))]
[JsonSerializable(typeof(FeatureFlags))]
[JsonSerializable(typeof(AuthMeResponse))]
[JsonSerializable(typeof(PagedResult<RouteDto>))]
[JsonSerializable(typeof(RouteDto))]
[JsonSerializable(typeof(CreateRouteRequest))]
[JsonSerializable(typeof(List<PlaceSuggestionDto>))]
[JsonSerializable(typeof(CheckNowResponse))]
[JsonSerializable(typeof(SeedDataRequest))]
[JsonSerializable(typeof(SeedResult))]
[JsonSerializable(typeof(ClearResult))]
[JsonSerializable(typeof(OptimalDepartureDto))]
[JsonSerializable(typeof(List<MonitoringWindowDto>))]
[JsonSerializable(typeof(List<SessionDto>))]
[JsonSerializable(typeof(PagedResult<PollRecordDto>))]
[JsonSerializable(typeof(BaselineResponse))]
[JsonSerializable(typeof(WeekdayComparisonDto))]
[JsonSerializable(typeof(List<AlertDto>))]
[JsonSerializable(typeof(AlertDto))]
[JsonSerializable(typeof(PushSubscriptionRequest))]
[JsonSerializable(typeof(VapidPublicKeyResponse))]
[JsonSerializable(typeof(StopSessionRequest))]
[JsonSerializable(typeof(SaveWindowRequest))]
[JsonSerializable(typeof(ClientLogBatch))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
