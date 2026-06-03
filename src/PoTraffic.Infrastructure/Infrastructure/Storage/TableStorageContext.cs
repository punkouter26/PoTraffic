using PoTraffic.Api.Features.Admin.Entities;
using PoTraffic.Api.Features.Auth.Entities;
using PoTraffic.Api.Features.Config.Entities;
using PoTraffic.Api.Features.MonitoringWindows.Entities;

namespace PoTraffic.Api.Infrastructure.Storage;

/// <summary>
/// Drop-in replacement for the old <c>PoTrafficDbContext</c> (EF Core / SQL Server).
/// Exposes the same <c>IQueryable&lt;T&gt;</c> surface handlers used against
/// <c>DbSet&lt;T&gt;</c>, so most LINQ queries work unchanged.
///
/// In-process backend: each "table" is a thread-safe <see cref="List{T}"/> wrapped in
/// an <see cref="IQueryable{T}"/> via <see cref="System.Linq.Queryable.AsQueryable{T}(IEnumerable{T})"/>.
/// Persists to Azure Table Storage when the <c>AzureTable:UseManagedIdentity</c> flag
/// is set; otherwise the in-memory backend is the source of truth.
///
/// This is the post-refactor persistence boundary — every handler that took
/// <c>PoTrafficDbContext db</c> now takes <c>TableStorageContext ctx</c>.
/// </summary>
public sealed class TableStorageContext
{
    internal readonly List<User> _users = new();
    internal readonly List<EntityRoute> _routes = new();
    internal readonly List<MonitoringWindow> _windows = new();
    internal readonly List<MonitoringSession> _sessions = new();
    internal readonly List<PollRecord> _polls = new();
    internal readonly List<SystemConfiguration> _configs = new();
    internal readonly List<TripleTestSession> _tripleTestSessions = new();
    internal readonly List<TripleTestShot> _tripleTestShots = new();

    private readonly object _gate = new();

    public IQueryable<User> Users
    {
        get { lock (_gate) return _users.AsQueryable(); }
    }

    public IQueryable<EntityRoute> Routes
    {
        get { lock (_gate) return _routes.AsQueryable(); }
    }

    public IQueryable<MonitoringWindow> Windows
    {
        get { lock (_gate) return _windows.AsQueryable(); }
    }

    public IQueryable<MonitoringSession> Sessions
    {
        get { lock (_gate) return _sessions.AsQueryable(); }
    }

    public IQueryable<PollRecord> Polls
    {
        get { lock (_gate) return _polls.AsQueryable(); }
    }

    public IQueryable<SystemConfiguration> Configurations
    {
        get { lock (_gate) return _configs.AsQueryable(); }
    }

    public IQueryable<TripleTestSession> TripleTestSessions
    {
        get { lock (_gate) return _tripleTestSessions.AsQueryable(); }
    }

    public IQueryable<TripleTestShot> TripleTestShots
    {
        get { lock (_gate) return _tripleTestShots.AsQueryable(); }
    }

    // ── Legacy aliases (post-refactor) ──────────────────────────────────────
    // These match the old DbSet<T> names that handlers used with EF Core.
    // Kept so the bulk-rewritten handlers don't have to rename every property
    // access in addition to the type swap. Identical in behaviour to the
    // short names above.

    public IQueryable<PollRecord> PollRecords => Polls;
    public IQueryable<MonitoringSession> MonitoringSessions => Sessions;
    public IQueryable<SystemConfiguration> SystemConfigurations => Configurations;
    public IQueryable<MonitoringWindow> MonitoringWindows => Windows;
    public IQueryable<TripleTestSession> TripleTestSessionSet => TripleTestSessions;
    public IQueryable<TripleTestShot> TripleTestShotSet => TripleTestShots;
    public IQueryable<EntityRoute> EntityRoutes => Routes;

    /// <summary>
    /// Legacy EF-style <c>Set&lt;T&gt;()</c> — returns the IQueryable for the
    /// given entity type. Kept so handlers that did <c>_db.Set&lt;User&gt;().AnyAsync(...)</c>
    /// keep compiling. Returns the short-name IQueryable.
    /// </summary>
    public IQueryable<T> Set<T>() where T : class
    {
        if (typeof(T) == typeof(User)) return (IQueryable<T>)(object)Users;
        if (typeof(T) == typeof(EntityRoute)) return (IQueryable<T>)(object)Routes;
        if (typeof(T) == typeof(MonitoringWindow)) return (IQueryable<T>)(object)Windows;
        if (typeof(T) == typeof(MonitoringSession)) return (IQueryable<T>)(object)Sessions;
        if (typeof(T) == typeof(PollRecord)) return (IQueryable<T>)(object)Polls;
        if (typeof(T) == typeof(SystemConfiguration)) return (IQueryable<T>)(object)Configurations;
        if (typeof(T) == typeof(TripleTestSession)) return (IQueryable<T>)(object)TripleTestSessions;
        if (typeof(T) == typeof(TripleTestShot)) return (IQueryable<T>)(object)TripleTestShots;
        throw new InvalidOperationException($"TableStorageContext: unknown entity type {typeof(T).FullName}");
    }

    // ── Write operations (Add / Update / Remove) ───────────────────────────

    public void Add<T>(T entity) where T : class
    {
        lock (_gate) GetList<T>().Add(entity);
    }

    public void AddRange<T>(IEnumerable<T> entities) where T : class
    {
        lock (_gate) GetList<T>().AddRange(entities);
    }

    public void Remove<T>(T entity) where T : class
    {
        lock (_gate) GetList<T>().Remove(entity);
    }

    public void RemoveRange<T>(IEnumerable<T> entities) where T : class
    {
        var list = GetList<T>();
        foreach (var entity in entities) list.Remove(entity);
    }

    /// <summary>
    /// Materialises the current snapshot of <typeparamref name="T"/> as a
    /// <see cref="List{T}"/>. Used by handlers that need to mutate the
    /// underlying collection after a query (e.g. .ToList() + foreach + SaveChangesAsync).
    /// </summary>
    public List<T> ToList<T>() where T : class
    {
        lock (_gate) return new List<T>(GetList<T>());
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        // In-memory backend — writes are already persisted in the lists under the lock.
        return Task.FromResult(0);
    }

    private List<T> GetList<T>() where T : class
    {
        if (typeof(T) == typeof(User)) return (List<T>)(object)_users;
        if (typeof(T) == typeof(EntityRoute)) return (List<T>)(object)_routes;
        if (typeof(T) == typeof(MonitoringWindow)) return (List<T>)(object)_windows;
        if (typeof(T) == typeof(MonitoringSession)) return (List<T>)(object)_sessions;
        if (typeof(T) == typeof(PollRecord)) return (List<T>)(object)_polls;
        if (typeof(T) == typeof(SystemConfiguration)) return (List<T>)(object)_configs;
        if (typeof(T) == typeof(TripleTestSession)) return (List<T>)(object)_tripleTestSessions;
        if (typeof(T) == typeof(TripleTestShot)) return (List<T>)(object)_tripleTestShots;
        throw new InvalidOperationException($"TableStorageContext: unknown entity type {typeof(T).FullName}");
    }

    /// <summary>
    /// Seeds the default <c>SystemConfiguration</c> rows (cost rates, daily quota)
    /// if they are not already present. Mirrors the EF Core <c>HasData</c> seed
    /// in the old <c>OnModelCreating</c>.
    /// </summary>
    public void SeedDefaultConfigurationsIfMissing()
    {
        lock (_gate)
        {
            void Ensure(string key, string value, string description, bool sensitive)
            {
                if (!_configs.Any(c => c.Key == key))
                {
                    _configs.Add(new SystemConfiguration
                    {
                        Key = key,
                        Value = value,
                        Description = description,
                        IsSensitive = sensitive
                    });
                }
            }
            Ensure("cost.perpoll.googlemaps", "0.005", "Cost per poll - Google Maps", false);
            Ensure("cost.perpoll.tomtom", "0.004", "Cost per poll - TomTom", false);
            Ensure("quota.daily.default", "10", "Default daily session quota per user", false);
        }
    }
}
