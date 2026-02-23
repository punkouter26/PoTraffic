namespace PoTraffic.Api.Features.Config.Entities;

public sealed class SystemConfiguration
{
    /// <summary>Primary key — e.g. "cost.perpoll.googlemaps"</summary>
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSensitive { get; set; }
}
