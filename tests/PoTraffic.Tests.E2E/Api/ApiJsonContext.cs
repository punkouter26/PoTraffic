// filepath: tests/PoTraffic.Tests.E2E/Api/ApiJsonContext.cs
using System.Text.Json.Serialization;
using PoTraffic.Shared.DTOs.History;
using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.Tests.E2E.Api;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the API scenarios —
/// avoids reflection at runtime, keeps the AOT trim warnings off the E2E project.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PagedResult<RouteDto>))]
[JsonSerializable(typeof(MonitoringWindowDto))]
[JsonSerializable(typeof(RouteDto))]
[JsonSerializable(typeof(SessionDto))]
[JsonSerializable(typeof(List<MonitoringWindowDto>))]
[JsonSerializable(typeof(List<SessionDto>))]
[JsonSerializable(typeof(ApiSessionFactory.GuestLoginResponse))]
public sealed partial class ApiJsonContext : JsonSerializerContext;