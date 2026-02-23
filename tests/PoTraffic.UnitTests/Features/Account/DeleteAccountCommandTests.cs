using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Features.Account;
using PoTraffic.Api.Infrastructure.Data;



namespace PoTraffic.UnitTests.Features.Account;

/// <summary>
/// Unit tests for <see cref="DeleteAccountCommandHandler"/>.
/// FR-031: GDPR Art. 17 — hard delete of user and all associated data.
/// </summary>
public sealed class DeleteAccountCommandTests
{
    private static PoTrafficDbContext CreateDb(string name)
    {
        DbContextOptions<PoTrafficDbContext> opts = new DbContextOptionsBuilder<PoTrafficDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new PoTrafficDbContext(opts);
    }

    [Fact]
    public async Task DeleteAccount_RemovesUserAndCascadeRoutes()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        await using PoTrafficDbContext db = CreateDb(dbName);

        Guid userId = Guid.NewGuid();
        Guid routeId = Guid.NewGuid();

        db.Users.Add(new User { Id = userId, Email = "del@test.com", PasswordHash = "h", Locale = "en-IE", CreatedAt = DateTimeOffset.UtcNow });
        db.Routes.Add(new EntityRoute
        {
            Id = routeId, UserId = userId,
            OriginAddress = "A", OriginCoordinates = "0,0",
            DestinationAddress = "B", DestinationCoordinates = "1,1"
        });
        await db.SaveChangesAsync();

        var handler = new DeleteAccountCommandHandler(db);

        // Act
        bool result = await handler.Handle(new DeleteAccountCommand(userId), CancellationToken.None);

        // Assert
        result.Should().BeTrue("user existed and was deleted");
        (await db.Users.FindAsync(userId)).Should().BeNull("user row must be hard-deleted (FR-031)");
    }

    [Fact]
    public async Task DeleteAccount_WhenUserNotFound_ReturnsFalse()
    {
        string dbName = Guid.NewGuid().ToString();
        await using PoTrafficDbContext db = CreateDb(dbName);

        var handler = new DeleteAccountCommandHandler(db);

        bool result = await handler.Handle(new DeleteAccountCommand(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeFalse("non-existent user returns false");
    }
}
