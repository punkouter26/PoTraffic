using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoTraffic.Shared.Ids;

/// <summary>
/// Contract every strongly-typed entity identifier implements. The static abstract
/// <see cref="From"/> lets <see cref="StronglyTypedIdJsonConverter{T}"/> round-trip any
/// identifier without reflection, which keeps the trim analyzer quiet in this
/// <c>IsTrimmable</c> assembly.
/// </summary>
public interface IStronglyTypedId<TSelf> where TSelf : struct, IStronglyTypedId<TSelf>
{
    /// <summary>The underlying GUID. Reach for this only at a real boundary (storage keys, logs).</summary>
    Guid Value { get; }

    /// <summary>Wraps a raw GUID. The single sanctioned primitive-to-identifier crossing.</summary>
    static abstract TSelf From(Guid value);
}

/// <summary>
/// Serialises a strongly-typed identifier as the bare GUID string its underlying
/// <see cref="Guid"/> would have produced — so the JSON persisted in Table Storage and
/// the JSON on the wire are byte-for-byte what they were before these types existed.
/// Existing rows and existing clients keep working.
/// </summary>
public sealed class StronglyTypedIdJsonConverter<T> : JsonConverter<T>
    where T : struct, IStronglyTypedId<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => T.From(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);

    /// <summary>Identifiers are legal dictionary keys — emit the same bare GUID string.</summary>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value.ToString());

    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => T.From(Guid.Parse(reader.GetString()!));
}
