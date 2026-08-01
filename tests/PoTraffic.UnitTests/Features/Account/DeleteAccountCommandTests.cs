using FluentAssertions;
using PoTraffic.API.Features.Account;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.UnitTests.Helpers;



namespace PoTraffic.UnitTests.Features.Account;

/// <summary>
/// Unit tests for <see cref="DeleteAccountCommandHandler"/>.
/// FR-031: GDPR Art. 17 — hard delete of user and all associated data.
/// </summary>
public sealed class DeleteAccountCommandTests
{

    [Fact]
    public async Task DeleteAccount_RemovesUserAndCascadeRoutes()
    {
        // Arrange
        TableStorageContext db = TestDoubles.CreateDb();

        UserId userId = UserId.New();
        RouteId routeId = RouteId.New();

        db.Add(new User { Id = userId, Email = "del@test.com", Locale = "en-IE", CreatedAt = DateTimeOffset.UtcNow });
        db.Add(new EntityRoute
        {
            Id = routeId,
            UserId = userId,
            OriginAddress = "A",
            OriginCoordinates = "0,0",
            DestinationAddress = "B",
            DestinationCoordinates = "1,1"
        });
        await db.SaveChangesAsync();

        var handler = new DeleteAccountCommandHandler(db);

        // Act
        bool result = await handler.Handle(new DeleteAccountCommand(userId), CancellationToken.None);

        // Assert
        result.Should().BeTrue("user existed and was deleted");
        (db.Users.FirstOrDefault(x => x.Id == userId)).Should().BeNull("user row must be hard-deleted (FR-031)");
    }

    [Fact]
    public async Task DeleteAccount_WhenUserNotFound_ReturnsFalse()
    {
        TableStorageContext db = TestDoubles.CreateDb();

        var handler = new DeleteAccountCommandHandler(db);

        bool result = await handler.Handle(new DeleteAccountCommand(UserId.New()), CancellationToken.None);

        result.Should().BeFalse("non-existent user returns false");
    }
}
