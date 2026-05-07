using Microsoft.EntityFrameworkCore;

namespace PoTraffic.Api.Infrastructure.Data;

public static class DataExtensions
{
    /// <summary>
    /// Registers EF Core (SQL Server with retry) and health checks for DB + external traffic APIs.
    /// </summary>
    public static IServiceCollection AddDataServices(this IServiceCollection services, IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Default connection string is missing.");

        services.AddDbContext<PoTrafficDbContext>(options =>
            options.UseSqlServer(
                connectionString,
                sql => sql.EnableRetryOnFailure(maxRetryCount: 5)));

        // Health checks — real connection pings (DB + external APIs)
        services.AddHealthChecks()
            .AddDbContextCheck<PoTrafficDbContext>(name: "sql-server", tags: ["ready"])
            .AddUrlGroup(new Uri("https://maps.googleapis.com/maps/api/js"), name: "google-maps",
                tags: ["ready"], timeout: TimeSpan.FromSeconds(5))
            .AddUrlGroup(new Uri("https://api.tomtom.com"), name: "tomtom",
                tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

        return services;
    }
}
