using Microsoft.EntityFrameworkCore;

using PoTraffic.Api.Infrastructure.Data.Projections;

namespace PoTraffic.Api.Infrastructure.Data;

public sealed class PoTrafficDbContext : DbContext
{
    public PoTrafficDbContext(DbContextOptions<PoTrafficDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<EntityRoute> Routes => Set<EntityRoute>();
    public DbSet<MonitoringWindow> MonitoringWindows => Set<MonitoringWindow>();
    public DbSet<MonitoringSession> MonitoringSessions => Set<MonitoringSession>();
    public DbSet<PollRecord> PollRecords => Set<PollRecord>();
    public DbSet<SystemConfiguration> SystemConfigurations => Set<SystemConfiguration>();
    public DbSet<TripleTestSession> TripleTestSessions => Set<TripleTestSession>();
    public DbSet<TripleTestShot> TripleTestShots => Set<TripleTestShot>();

    // Keyless projections — mapped for raw-SQL result materialisation
    public DbSet<BaselineSlotDto> BaselineSlots => Set<BaselineSlotDto>();
    public DbSet<UserDailyUsageDto> UserDailyUsages => Set<UserDailyUsageDto>();
    public DbSet<GlobalVolatilityProjection> GlobalVolatilityProjections => Set<GlobalVolatilityProjection>();
    public DbSet<PollCostProjection> PollCostProjections => Set<PollCostProjection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Keyless projections ──────────────────────────────────────────────
        modelBuilder.Entity<BaselineSlotDto>().HasNoKey();
        modelBuilder.Entity<UserDailyUsageDto>().HasNoKey();
        modelBuilder.Entity<GlobalVolatilityProjection>().HasNoKey();
        modelBuilder.Entity<PollCostProjection>().HasNoKey();

        // ── User ─────────────────────────────────────────────────────────────
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            entity.Property(u => u.Email).HasMaxLength(320).IsRequired();
            entity.HasIndex(u => u.Email).IsUnique().HasDatabaseName("UX_Users_Email");
            entity.Property(u => u.PasswordHash).IsRequired();
            entity.Property(u => u.Locale).IsRequired();
            entity.Property(u => u.Role).IsRequired().HasMaxLength(50);
            entity.Property(u => u.IsGdprDeleteRequested).HasDefaultValue(false);
            entity.Property(u => u.IsEmailVerified).HasDefaultValue(false);
        });

        // ── Route ─────────────────────────────────────────────────────────────
        modelBuilder.Entity<EntityRoute>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            entity.Property(r => r.OriginAddress).HasMaxLength(500).IsRequired();
            entity.Property(r => r.OriginCoordinates).IsRequired();
            entity.Property(r => r.DestinationAddress).HasMaxLength(500).IsRequired();
            entity.Property(r => r.DestinationCoordinates).IsRequired();

            // Strategy: store enums as integers (conversion)
            entity.Property(r => r.Provider).HasConversion<int>();
            entity.Property(r => r.MonitoringStatus).HasConversion<int>();

            entity.HasOne(r => r.User)
                  .WithMany(u => u.Routes)
                  .HasForeignKey(r => r.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Logic: prevent multiple active routes with the same origin/dest/provider for the same user
            entity.HasIndex(r => new { r.UserId, r.OriginAddress, r.DestinationAddress, r.Provider })
                  .HasDatabaseName("UX_Routes_User_Origin_Dest_Provider")
                  .IsUnique()
                  .HasFilter("[MonitoringStatus] != 2"); // Exclude 'Deleted' routes

            entity.HasIndex(r => new { r.UserId, r.MonitoringStatus })
                  .HasDatabaseName("IX_Routes_UserId_MonitoringStatus");
        });

        // ── MonitoringWindow ──────────────────────────────────────────────────
        modelBuilder.Entity<MonitoringWindow>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            entity.Property(w => w.StartTime).HasColumnType("time(0)");
            entity.Property(w => w.EndTime).HasColumnType("time(0)");

            entity.HasOne(w => w.Route)
                  .WithMany(r => r.Windows)
                  .HasForeignKey(w => w.RouteId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ── MonitoringSession ─────────────────────────────────────────────────
        modelBuilder.Entity<MonitoringSession>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            entity.Property(s => s.SessionDate).HasColumnType("date");

            entity.HasOne(s => s.Route)
                  .WithMany(r => r.Sessions)
                  .HasForeignKey(s => s.RouteId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(s => new { s.RouteId, s.SessionDate })
                  .IsUnique()
                  .HasDatabaseName("IX_MonitoringSessions_RouteId_SessionDate");
        });

        // ── PollRecord ────────────────────────────────────────────────────────
        modelBuilder.Entity<PollRecord>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            entity.Property(p => p.RawProviderResponse).HasMaxLength(int.MaxValue);

            // Global soft-delete filter — callers that need deleted records call IgnoreQueryFilters()
            entity.HasQueryFilter(p => !p.IsDeleted);

            entity.HasOne(p => p.Route)
                  .WithMany(r => r.PollRecords)
                  .HasForeignKey(p => p.RouteId)
                  .OnDelete(DeleteBehavior.NoAction); // NoAction avoids multi-cascade-path conflict via MonitoringSessions → PollRecords

            entity.HasOne(p => p.Session)
                  .WithMany(s => s.PollRecords)
                  .HasForeignKey(p => p.SessionId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(p => new { p.RouteId, p.PolledAt })
                  .HasDatabaseName("IX_PollRecords_RouteId_PolledAt");

            entity.HasIndex(p => p.SessionId)
                  .HasDatabaseName("IX_PollRecords_SessionId");

            entity.HasIndex(p => p.PolledAt)
                  .HasDatabaseName("IX_PollRecords_PolledAt");
        });

        // ── TripleTestSession / TripleTestShot ────────────────────────────────
        modelBuilder.Entity<TripleTestSession>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            entity.Property(t => t.OriginAddress).HasMaxLength(500).IsRequired();
            entity.Property(t => t.DestinationAddress).HasMaxLength(500).IsRequired();
            entity.Property(t => t.OriginCoordinates).IsRequired();
            entity.Property(t => t.DestinationCoordinates).IsRequired();
            entity.Property(t => t.Provider).HasConversion<int>();
        });

        modelBuilder.Entity<TripleTestShot>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            entity.Property(s => s.ErrorCode).HasMaxLength(100);

            entity.HasOne(s => s.Session)
                  .WithMany(t => t.Shots)
                  .HasForeignKey(s => s.SessionId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(s => new { s.SessionId, s.ShotIndex })
                  .IsUnique()
                  .HasDatabaseName("UX_TripleTestShots_SessionId_ShotIndex");
        });

        // ── SystemConfiguration ───────────────────────────────────────────────
        modelBuilder.Entity<SystemConfiguration>(entity =>
        {
            entity.HasKey(c => c.Key);
            entity.Property(c => c.Key).HasMaxLength(100);
            entity.Property(c => c.Value).IsRequired();

            entity.HasData(
                new SystemConfiguration
                {
                    Key = "cost.perpoll.googlemaps",
                    Value = "0.005",
                    Description = "Cost per poll - Google Maps",
                    IsSensitive = false
                },
                new SystemConfiguration
                {
                    Key = "cost.perpoll.tomtom",
                    Value = "0.004",
                    Description = "Cost per poll - TomTom",
                    IsSensitive = false
                },
                new SystemConfiguration
                {
                    Key = "quota.daily.default",
                    Value = "10",
                    Description = "Default daily session quota per user",
                    IsSensitive = false
                },
                new SystemConfiguration
                {
                    Key = "quota.reset.utc",
                    Value = "00:00",
                    Description = "Quota reset time (UTC)",
                    IsSensitive = false
                }
            );
        });
    }
}
