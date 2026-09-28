using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.UnitTests.Helpers;

/// <summary>
/// Fixtures every handler test needs. These were previously re-declared verbatim as private
/// statics in ~17 test classes; kept here so a change to how the context is built is one
/// edit rather than seventeen.
/// </summary>
internal static class TestDoubles
{
    /// <summary>A volatile, memory-only <see cref="TableStorageContext"/> (no backing store).</summary>
    public static TableStorageContext CreateDb() => new();
}
