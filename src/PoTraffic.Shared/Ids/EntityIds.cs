using System.Text.Json.Serialization;

namespace PoTraffic.Shared.Ids;

// Strongly-typed entity identifiers (Rule 1 — Domain Integrity).
//
// Every identifier below is a distinct type, so the compiler rejects passing a UserId
// where a RouteId belongs — a class of bug raw Guid parameters cannot catch. Each one:
//   • serialises as the bare GUID string it always was (storage + wire compatible),
//   • implements IParsable, so minimal-API route/query binding works with no extra code,
//   • exposes .Value as the single sanctioned crossing back to a raw Guid.
//
// Construct with New() for a fresh identifier, or From(guid) at a genuine boundary
// (deserialisation, a claim value, a legacy column). Prefer passing the typed value
// through and unwrapping as late as possible.
//
// The near-identical member lists below are NOT redundancy that a shared base could remove.
// Structs cannot inherit, and a `static virtual` default on IStronglyTypedId<TSelf> is only
// callable through a constrained type parameter — never as `RouteId.New()`. Parse/TryParse
// are likewise required on the concrete type by IParsable<T>, which is what makes minimal-API
// route binding work with no extra code. Only genuinely unused members have been trimmed:
// Empty and IsEmpty exist solely on the identifiers that actually use them.

/// <summary>Identifies a <c>User</c>.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<UserId>))]
public readonly record struct UserId(Guid Value) : IStronglyTypedId<UserId>, IParsable<UserId>
{
    public static UserId From(Guid value) => new(value);
    public static UserId New() => new(Guid.NewGuid());
    public static UserId Empty => default;
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString();

    public static UserId Parse(string s, IFormatProvider? provider = null) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out UserId result)
    {
        bool ok = Guid.TryParse(s, out Guid guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>Identifies a monitored <c>Route</c>.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<RouteId>))]
public readonly record struct RouteId(Guid Value) : IStronglyTypedId<RouteId>, IParsable<RouteId>
{
    public static RouteId From(Guid value) => new(value);
    public static RouteId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString();

    public static RouteId Parse(string s, IFormatProvider? provider = null) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out RouteId result)
    {
        bool ok = Guid.TryParse(s, out Guid guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>Identifies a <c>MonitoringWindow</c>.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<WindowId>))]
public readonly record struct WindowId(Guid Value) : IStronglyTypedId<WindowId>, IParsable<WindowId>
{
    public static WindowId From(Guid value) => new(value);
    public static WindowId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();

    public static WindowId Parse(string s, IFormatProvider? provider = null) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out WindowId result)
    {
        bool ok = Guid.TryParse(s, out Guid guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>Identifies a <c>MonitoringSession</c> — one execution of a window.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<SessionId>))]
public readonly record struct SessionId(Guid Value) : IStronglyTypedId<SessionId>, IParsable<SessionId>
{
    public static SessionId From(Guid value) => new(value);
    public static SessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();

    public static SessionId Parse(string s, IFormatProvider? provider = null) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out SessionId result)
    {
        bool ok = Guid.TryParse(s, out Guid guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>Identifies a single <c>PollRecord</c> sample.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<PollRecordId>))]
public readonly record struct PollRecordId(Guid Value) : IStronglyTypedId<PollRecordId>, IParsable<PollRecordId>
{
    public static PollRecordId From(Guid value) => new(value);
    public static PollRecordId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();

    public static PollRecordId Parse(string s, IFormatProvider? provider = null) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out PollRecordId result)
    {
        bool ok = Guid.TryParse(s, out Guid guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>Identifies an <c>Alert</c>.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<AlertId>))]
public readonly record struct AlertId(Guid Value) : IStronglyTypedId<AlertId>, IParsable<AlertId>
{
    public static AlertId From(Guid value) => new(value);
    public static AlertId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();

    public static AlertId Parse(string s, IFormatProvider? provider = null) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out AlertId result)
    {
        bool ok = Guid.TryParse(s, out Guid guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>Identifies a browser <c>UserPushSubscription</c>.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<PushSubscriptionId>))]
public readonly record struct PushSubscriptionId(Guid Value)
    : IStronglyTypedId<PushSubscriptionId>, IParsable<PushSubscriptionId>
{
    public static PushSubscriptionId From(Guid value) => new(value);
    public static PushSubscriptionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();

    public static PushSubscriptionId Parse(string s, IFormatProvider? provider = null) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out PushSubscriptionId result)
    {
        bool ok = Guid.TryParse(s, out Guid guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>Identifies a <c>TripleTestSession</c> (admin provider-comparison run).</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<TripleTestSessionId>))]
public readonly record struct TripleTestSessionId(Guid Value)
    : IStronglyTypedId<TripleTestSessionId>, IParsable<TripleTestSessionId>
{
    public static TripleTestSessionId From(Guid value) => new(value);
    public static TripleTestSessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();

    public static TripleTestSessionId Parse(string s, IFormatProvider? provider = null) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out TripleTestSessionId result)
    {
        bool ok = Guid.TryParse(s, out Guid guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>Identifies one shot within a <c>TripleTestSession</c>.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<TripleTestShotId>))]
public readonly record struct TripleTestShotId(Guid Value)
    : IStronglyTypedId<TripleTestShotId>, IParsable<TripleTestShotId>
{
    public static TripleTestShotId From(Guid value) => new(value);
    public static TripleTestShotId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();

    public static TripleTestShotId Parse(string s, IFormatProvider? provider = null) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out TripleTestShotId result)
    {
        bool ok = Guid.TryParse(s, out Guid guid);
        result = new(guid);
        return ok;
    }
}
