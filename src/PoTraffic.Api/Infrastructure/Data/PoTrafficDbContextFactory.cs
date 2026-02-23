using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PoTraffic.Api.Infrastructure.Data;

/// <summary>
/// Factory pattern — provides a DbContext instance for EF Core design-time tooling
/// (migrations add, migrations script, etc.) without requiring the full application host.
/// </summary>
internal sealed class PoTrafficDbContextFactory : IDesignTimeDbContextFactory<PoTrafficDbContext>
{
    public PoTrafficDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<PoTrafficDbContext> optionsBuilder = new();

        // Use a placeholder connection string; migrations only require the provider + schema, not a live DB.
        optionsBuilder.UseSqlServer(
            "Server=(local);Database=PoTraffic_Dev;Trusted_Connection=True;TrustServerCertificate=True;");

        return new PoTrafficDbContext(optionsBuilder.Options);
    }
}
