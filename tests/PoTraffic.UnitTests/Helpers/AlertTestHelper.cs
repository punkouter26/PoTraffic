using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.API.Features.Alerts;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.UnitTests.Helpers;

/// <summary>Builds an <see cref="AlertEvaluator"/> for unit tests that construct
/// <c>ExecutePollCommandHandler</c> directly. Push delivery is a substitute that sends nothing.</summary>
internal static class AlertTestHelper
{
    public static AlertEvaluator NoOp(TableStorageContext db) =>
        new(db, Substitute.For<IPushNotifier>(), Substitute.For<IIncidentProvider>(), NullLogger<AlertEvaluator>.Instance);
}
