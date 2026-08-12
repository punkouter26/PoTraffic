using PoTraffic.API.Features.Alerts;
using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.UnitTests.Helpers;

/// <summary>Builds a no-op <see cref="AlertEvaluator"/> for unit tests that construct
/// <c>ExecutePollCommandHandler</c> directly. Web Push was removed — only the in-app
/// alert pipeline remains.</summary>
internal static class AlertTestHelper
{
    public static AlertEvaluator NoOp(TableStorageContext db) =>
        new(db);
}
