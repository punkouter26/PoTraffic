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

        // Read connection string from env var first (CI / Docker dev), fall back to local dev default.
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Server=tcp:localhost,52357;Database=PoTraffic;User=sa;Password=Dev!P@ssw0rd;TrustServerCertificate=True";

        optionsBuilder.UseSqlServer(connectionString);

        return new PoTrafficDbContext(optionsBuilder.Options);
    }
}
