using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.API.Features.Alerts;
using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.UnitTests.Helpers;

/// <summary>Builds a no-op <see cref="AlertEvaluator"/> (substituted push notifier) for
/// unit tests that construct <c>ExecutePollCommandHandler</c> directly.</summary>
internal static class AlertTestHelper
{
    public static AlertEvaluator NoOp(TableStorageContext db) =>
        new(db, Substitute.For<IPushNotifier>(), NullLogger<AlertEvaluator>.Instance);
}
