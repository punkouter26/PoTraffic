using System.Collections;
using System.Reflection;
using System.Text.Json;
using PoTraffic.Api.Features.Admin.Entities;
using PoTraffic.Api.Features.Auth.Entities;
using PoTraffic.Api.Features.Config.Entities;
using PoTraffic.Api.Features.MonitoringWindows.Entities;

namespace PoTraffic.Api.Infrastructure.Storage;

/// <summary>
/// Table Storage-backed persistence context for PoTraffic entities.
/// Exposes the same <c>IQueryable&lt;T&gt;</c> surface handlers used against
/// <c>DbSet&lt;T&gt;</c>, so LINQ queries run unchanged against the in-memory
/// working set — while every <see cref="SaveChangesAsync"/> durably writes the
/// delta (JSON snapshot diff + queued deletes) to Azure Table Storage
/// (Azurite locally, managed identity in the cloud). The working set is
/// hydrated from the tables once at startup via <see cref="HydrateAsync"/>.
///
/// The parameterless constructor creates a volatile (memory-only) context for
/// unit tests.
/// </summary>
public sealed class TableStorageContext
{
    private sealed record EntityMap(string Table, Func<object, string> Pk, Func<object, string> Rk);

    private static readonly Dictionary<Type, EntityMap> Maps = new()
    {
        [typeof(User)] = new("Users", _ => "main", e => ((User)e).Id.ToString()),
        [typeof(EntityRoute)] = new("Routes", _ => "main", e => ((EntityRoute)e).Id.ToString()),
        [typeof(MonitoringWindow)] = new("MonitoringWindows", _ => "main", e => ((MonitoringWindow)e).Id.ToString()),
        [typeof(MonitoringSession)] = new("MonitoringSessions", _ => "main", e => ((MonitoringSession)e).Id.ToString()),
        // Polls partition by route for efficient per-route reads and pruning.
        [typeof(PollRecord)] = new("PollRecords", e => ((PollRecord)e).RouteId.ToString(), e => ((PollRecord)e).Id.ToString()),
        [typeof(SystemConfiguration)] = new("SystemConfigurations", _ => "main", e => ((SystemConfiguration)e).Key),
        [typeof(TripleTestSession)] = new("TripleTestSessions", _ => "main", e => ((TripleTestSession)e).Id.ToString()),
        [typeof(TripleTestShot)] = new("TripleTestShots", _ => "main", e => ((TripleTestShot)e).Id.ToString()),
    };

    private static readonly JsonSerializerOptions JsonOpts = new(); // nav properties carry [JsonIgnore]

    internal readonly List<User> _users = new();
    internal readonly List<EntityRoute> _routes = new();
    internal readonly List<MonitoringWindow> _windows = new();
    internal readonly List<MonitoringSession> _sessions = new();
    internal readonly List<PollRecord> _polls = new();
    internal readonly List<SystemConfiguration> _configs = new();
    internal readonly List<TripleTestSession> _tripleTestSessions = new();
    internal readonly List<TripleTestShot> _tripleTestShots = new();

    private readonly object _gate = new();
    private readonly ITableStore? _store;
    private readonly Dictionary<object, string> _snapshots = new(ReferenceEqualityComparer.Instance);
    private readonly List<TableOp> _pendingDeletes = [];
    private bool _durable;

    /// <summary>Volatile in-memory context — unit tests only.</summary>
    public TableStorageContext() { }

    public TableStorageContext(ITableStore store)
    {
        _store = store;
        _durable = true;
    }

    /// <summary>True when writes are being persisted to Table Storage.</summary>
    public bool IsDurable => _durable;

    /// <summary>
    /// Degrades to memory-only mode (Dev fallback when Azurite is not running).
    /// Production startup treats hydration failure as fatal instead.
    /// </summary>
    public void MarkVolatile() => _durable = false;

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

    public IQueryable<PollRecord> PollRecords => Polls;
    public IQueryable<MonitoringSession> MonitoringSessions => Sessions;
    public IQueryable<SystemConfiguration> SystemConfigurations => Configurations;
    public IQueryable<MonitoringWindow> MonitoringWindows => Windows;
    public IQueryable<EntityRoute> EntityRoutes => Routes;

    // ── Write operations (Add / Remove) ─────────────────────────────────────

    private static readonly Dictionary<Type, PropertyInfo?> s_idProperties = new();
    private static readonly MethodInfo GetListMethod = typeof(TableStorageContext)
        .GetMethod(nameof(GetList), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public void Add<T>(T entity) where T : class
    {
        // Auto-generate Guid for Id property if it's Guid.Empty (replaces EF Core auto-gen)
        Type type = typeof(T);
        if (!s_idProperties.TryGetValue(type, out PropertyInfo? idProp))
        {
            idProp = type.GetProperty("Id");
            s_idProperties[type] = idProp;
        }

        if (idProp?.PropertyType == typeof(Guid))
        {
            Guid currentId = (Guid)(idProp.GetValue(entity) ?? Guid.Empty);
            if (currentId == Guid.Empty)
                idProp.SetValue(entity, Guid.NewGuid());
        }

        lock (_gate) GetList<T>().Add(entity);

        // Cascade: add navigation collection children (replaces EF Core change tracking)
        AddNavigationChildren(entity, type);
    }

    private void AddNavigationChildren(object entity, Type type)
    {
        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!typeof(IEnumerable).IsAssignableFrom(prop.PropertyType) || prop.PropertyType == typeof(string))
                continue;

            if (prop.PropertyType.GetGenericArguments().Length != 1)
                continue;

            Type elementType = prop.PropertyType.GetGenericArguments()[0];

            // Only cascade for known entity types
            if (!IsKnownEntityType(elementType))
                continue;

            if (prop.GetValue(entity) is IEnumerable collection)
            {
                // Get the backing list for this element type to check for duplicates
                var existingList = (IList)GetListMethod.MakeGenericMethod(elementType)
                    .Invoke(this, null)!;

                // Try to find the FK navigation property on the child type that points back to the parent
                PropertyInfo? fkProp = FindForeignKeyProperty(elementType, type);

                foreach (object child in collection)
                {
                    // Set reverse navigation property (e.g. MonitoringWindow.Route = route)
                    if (fkProp is not null && fkProp.GetValue(child) is null)
                        fkProp.SetValue(child, entity);

                    // Skip if already added (prevents double-add when handler explicitly adds children)
                    if (existingList.Contains(child))
                        continue;

                    MethodInfo addMethod = typeof(TableStorageContext)
                        .GetMethod(nameof(Add), BindingFlags.Public | BindingFlags.Instance)!
                        .MakeGenericMethod(elementType);
                    addMethod.Invoke(this, [child]);
                }
            }
        }
    }

    private static PropertyInfo? FindForeignKeyProperty(Type childType, Type parentType)
    {
        // Look for a property whose type matches the parent type (e.g. MonitoringWindow.Route : Route)
        return childType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.PropertyType == parentType);
    }

    private static bool IsKnownEntityType(Type type)
    {
        // Only cascade for types that are always added via navigation collections
        // (not explicitly added by handlers). TripleTestShot is always explicitly
        // added by StartTripleTestCommandHandler, so we exclude it to prevent double-add.
        return type == typeof(MonitoringWindow)
            || type == typeof(MonitoringSession)
            || type == typeof(PollRecord);
    }

    public void AddRange<T>(IEnumerable<T> entities) where T : class
    {
        foreach (T entity in entities) Add(entity);
    }

    public void Remove<T>(T entity) where T : class
    {
        lock (_gate) RemoveCore(entity);
    }

    public void RemoveRange<T>(IEnumerable<T> entities) where T : class
    {
        lock (_gate)
        {
            foreach (T entity in entities) RemoveCore(entity);
        }
    }

    private void RemoveCore<T>(T entity) where T : class
    {
        if (!GetList<T>().Remove(entity))
            return;

        _snapshots.Remove(entity);
        if (Maps.TryGetValue(typeof(T), out EntityMap? map))
            _pendingDeletes.Add(new TableOp(TableOpKind.Delete, map.Table, map.Pk(entity), map.Rk(entity), null));
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

    /// <summary>
    /// Durably writes the delta since the last save: queued deletes plus every
    /// entity whose JSON differs from its last-persisted snapshot (covers both
    /// adds and in-place mutations — no explicit Update() calls required).
    /// </summary>
    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        if (_store is null || !_durable)
            return 0;

        List<TableOp> ops;
        List<(object Entity, string Json)> written = [];
        lock (_gate)
        {
            ops = [.. _pendingDeletes];
            _pendingDeletes.Clear();

            foreach ((Type type, EntityMap map) in Maps)
            {
                foreach (object entity in GetListUntyped(type))
                {
                    string json = JsonSerializer.Serialize(entity, type, JsonOpts);
                    if (_snapshots.TryGetValue(entity, out string? prev) && prev == json)
                        continue;
                    ops.Add(new TableOp(TableOpKind.Upsert, map.Table, map.Pk(entity), map.Rk(entity), json));
                    written.Add((entity, json));
                }
            }
        }

        if (ops.Count == 0)
            return 0;

        try
        {
            await _store.ApplyAsync(ops, ct);
        }
        catch
        {
            // Re-queue deletes so the next save retries them; dirty entities are
            // re-detected automatically because their snapshots were not updated.
            lock (_gate) _pendingDeletes.AddRange(ops.Where(o => o.Kind == TableOpKind.Delete));
            throw;
        }

        lock (_gate)
        {
            foreach ((object entity, string json) in written)
                _snapshots[entity] = json;
        }
        return ops.Count;
    }

    /// <summary>
    /// Loads the full working set from Table Storage (creating tables on first
    /// run) and relinks the navigation collections handlers read from.
    /// Call once at startup, before the app serves traffic.
    /// </summary>
    public async Task HydrateAsync(CancellationToken ct = default)
    {
        if (_store is null)
            return;

        await _store.EnsureTablesAsync(Maps.Values.Select(m => m.Table), ct);

        foreach ((Type type, EntityMap map) in Maps)
        {
            IReadOnlyList<(string, string, string Json)> rows = await _store.ReadAllAsync(map.Table, ct);
            lock (_gate)
            {
                IList list = GetListUntyped(type);
                list.Clear();
                foreach ((_, _, string json) in rows)
                {
                    object? entity = JsonSerializer.Deserialize(json, type, JsonOpts);
                    if (entity is null)
                        continue;
                    list.Add(entity);
                    // Snapshot the canonical re-serialised form so the first save
                    // after startup only writes genuine changes.
                    _snapshots[entity] = JsonSerializer.Serialize(entity, type, JsonOpts);
                }
            }
        }

        lock (_gate) RelinkNavigations();
    }

    private void RelinkNavigations()
    {
        ILookup<Guid, MonitoringWindow> windowsByRoute = _windows.ToLookup(w => w.RouteId);
        ILookup<Guid, MonitoringSession> sessionsByRoute = _sessions.ToLookup(s => s.RouteId);
        ILookup<Guid, PollRecord> pollsByRoute = _polls.ToLookup(p => p.RouteId);
        foreach (EntityRoute route in _routes)
        {
            route.Windows = [.. windowsByRoute[route.Id]];
            route.Sessions = [.. sessionsByRoute[route.Id]];
            route.PollRecords = [.. pollsByRoute[route.Id]];
        }

        ILookup<Guid, TripleTestShot> shotsBySession = _tripleTestShots.ToLookup(s => s.SessionId);
        foreach (TripleTestSession session in _tripleTestSessions)
            session.Shots = [.. shotsBySession[session.Id]];
    }

    private IList GetListUntyped(Type type)
    {
        if (type == typeof(User)) return _users;
        if (type == typeof(EntityRoute)) return _routes;
        if (type == typeof(MonitoringWindow)) return _windows;
        if (type == typeof(MonitoringSession)) return _sessions;
        if (type == typeof(PollRecord)) return _polls;
        if (type == typeof(SystemConfiguration)) return _configs;
        if (type == typeof(TripleTestSession)) return _tripleTestSessions;
        if (type == typeof(TripleTestShot)) return _tripleTestShots;
        throw new InvalidOperationException($"TableStorageContext: unknown entity type {type.FullName}");
    }

    private List<T> GetList<T>() where T : class => (List<T>)GetListUntyped(typeof(T));

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
