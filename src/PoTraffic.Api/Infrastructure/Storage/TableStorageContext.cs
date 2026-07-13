using System.Collections;
using System.Reflection;
using System.Text.Json;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// Optional logger factory — wired in DI registration. Source-generated logs
    /// mean zero allocations per hydration event.
    /// </summary>
    internal ILoggerFactory? LoggerFactory { get; set; }

    private ILogger CreateHydrationLogger()
        => (LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("PoTraffic.Storage.Hydration");

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
    // Last-known ETag per tracked entity → drives optimistic-concurrency conditional writes (§5.5).
    private readonly Dictionary<object, string> _etags = new(ReferenceEqualityComparer.Instance);
    private readonly List<TableOp> _pendingDeletes = [];

    // PollRecords are the unbounded, dominant table and carry a large RawProviderResponse
    // blob. Re-serialising every one on every SaveChanges (to diff it) is the poll hot
    // path's biggest cost. They are append-only after insert — the ONLY mutation of an
    // existing PollRecord is the prune job — so we track changed ones explicitly and skip
    // re-serialising the clean majority. Every other (small) table keeps the full diff.
    private readonly HashSet<object> _dirtyPolls = new(ReferenceEqualityComparer.Instance);

    private bool _durable;
    private volatile bool _hydrated;

    /// <summary>Volatile in-memory context — unit tests only.</summary>
    public TableStorageContext() { }

    public TableStorageContext(ITableStore store)
    {
        _store = store;
        _durable = true;
    }

    /// <summary>True when writes are being persisted to Table Storage.</summary>
    public bool IsDurable => _durable;

    /// <summary>True once <see cref="HydrateAsync"/> has finished (success or fatal).</summary>
    public bool IsHydrated => _hydrated;

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

        lock (_gate)
        {
            GetList<T>().Add(entity);
            if (entity is PollRecord)
                _dirtyPolls.Add(entity);
        }

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

    /// <summary>
    /// Announces a mutation to an <em>existing</em> <see cref="PollRecord"/> so the next
    /// <see cref="SaveChangesAsync"/> persists it. Required because PollRecords use explicit
    /// change tracking rather than the full-scan diff (see <c>_dirtyPolls</c>). Newly added
    /// records are tracked automatically by <see cref="Add{T}"/>.
    /// </summary>
    public void MarkChanged(PollRecord record)
    {
        lock (_gate) _dirtyPolls.Add(record);
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
        _etags.Remove(entity);
        _dirtyPolls.Remove(entity);
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

        List<TableOp> upserts = [];
        List<(object Entity, string Json)> written = [];
        Dictionary<(string, string, string), object> upsertKeyToEntity = [];
        List<TableOp> deletes;

        lock (_gate)
        {
            foreach ((Type type, EntityMap map) in Maps)
            {
                // PollRecord uses explicit change tracking — only serialise the ones that were
                // added or announced via MarkChanged, not the entire (unbounded) table.
                IEnumerable<object> candidates = type == typeof(PollRecord)
                    ? _dirtyPolls
                    : GetListUntyped(type).Cast<object>();

                foreach (object entity in candidates)
                {
                    string json = JsonSerializer.Serialize(entity, type, JsonOpts);
                    if (_snapshots.TryGetValue(entity, out string? prev) && prev == json)
                        continue;
                    string pk = map.Pk(entity), rk = map.Rk(entity);
                    _etags.TryGetValue(entity, out string? etag);
                    upserts.Add(new TableOp(TableOpKind.Upsert, map.Table, pk, rk, json, etag));
                    written.Add((entity, json));
                    upsertKeyToEntity[(map.Table, pk, rk)] = entity;
                }
            }

            // Rewrite-then-delete (§5.5): new/updated rows are persisted BEFORE old rows are
            // removed, so a crash mid-flush can never drop a row before its replacement is
            // durable. A queued delete that collides with an upsert of the same key is dropped —
            // the upsert (final state) wins.
            deletes = _pendingDeletes
                .Where(d => !upsertKeyToEntity.ContainsKey((d.Table, d.PartitionKey, d.RowKey)))
                .ToList();
            _pendingDeletes.Clear();
        }

        List<TableOp> ops = [.. upserts, .. deletes];
        if (ops.Count == 0)
            return 0;

        IReadOnlyList<TableWriteResult> results;
        try
        {
            results = await _store.ApplyAsync(ops, ct);
        }
        catch
        {
            // Re-queue deletes so the next save retries them; dirty entities are
            // re-detected automatically because their snapshots were not updated.
            lock (_gate) _pendingDeletes.AddRange(deletes);
            throw;
        }

        lock (_gate)
        {
            foreach ((object entity, string json) in written)
            {
                _snapshots[entity] = json;
                // Successfully persisted — clear its dirty flag so it isn't re-serialised
                // next save. On failure we skip this block, so the flag survives and retries.
                if (entity is PollRecord)
                    _dirtyPolls.Remove(entity);
            }
            // Adopt refreshed ETags so the NEXT save issues a correctly-guarded conditional write.
            foreach (TableWriteResult r in results)
            {
                if (upsertKeyToEntity.TryGetValue((r.Table, r.PartitionKey, r.RowKey), out object? entity))
                    _etags[entity] = r.ETag;
            }
        }
        return ops.Count;
    }

    /// <summary>
    /// Live reachability probe for the health check: a cheap round-trip to the backing
    /// store. Throws if the store is unreachable. Returns without doing anything in
    /// memory-only mode (the health check reports that separately via <see cref="IsDurable"/>).
    /// </summary>
    public async Task ProbeStoreAsync(CancellationToken ct = default)
    {
        if (_store is null || !_durable)
            return;
        await _store.PingAsync(ct);
    }

    /// <summary>
    /// Loads the full working set from Table Storage (creating tables on first
    /// run) and relinks the navigation collections handlers read from.
    /// Idempotent — safe to run on every cold-start AND from a background
    /// retry loop after a failed hydration. <see cref="IsHydrated"/> flips to
    /// true on completion (success or fatal) so the readiness probe can report
    /// "hydrating" until then.
    /// </summary>
    public async Task HydrateAsync(CancellationToken ct = default)
    {
        ILogger logger = CreateHydrationLogger();

        try
        {
            if (_store is null)
            {
                HydrationLog.NoStore(logger);
                _hydrated = true;
                return;
            }

            HydrationLog.EnsuringTables(logger, _store.GetType().Name, Maps.Count);

            await _store.EnsureTablesAsync(
                Maps.Values.Select(m => m.Table).Append(Scheduling.TableStorageJobScheduler.TableName),
                ct);

            int totalEntities = 0;
            foreach ((Type type, EntityMap map) in Maps)
            {
                IReadOnlyList<(string, string, string Json, string ETag)> rows = await _store.ReadAllAsync(map.Table, ct);
                lock (_gate)
                {
                    IList list = GetListUntyped(type);
                    list.Clear();
                    foreach ((_, _, string json, string etag) in rows)
                    {
                        object? entity = JsonSerializer.Deserialize(json, type, JsonOpts);
                        if (entity is null)
                            continue;
                        list.Add(entity);
                        // Snapshot the canonical re-serialised form so the first save
                        // after startup only writes genuine changes.
                        _snapshots[entity] = JsonSerializer.Serialize(entity, type, JsonOpts);
                        // Track the row ETag so post-startup writes are concurrency-guarded (§5.5).
                        _etags[entity] = etag;
                    }
                }
                HydrationLog.TableLoaded(logger, map.Table, rows.Count);
                totalEntities += rows.Count;
            }

            lock (_gate) RelinkNavigations();
            HydrationLog.HydrationComplete(logger, totalEntities);
            _hydrated = true;  // success path only — readiness probes report true ONLY when hydration actually finished
        }
        catch (RequestFailedException rfe) when (rfe.Status is 401 or 403)
        {
            HydrationLog.StorageAuthzDenied(
                logger,
                rfe.Status,
                rfe.ErrorCode ?? "AuthorizationPermissionMismatch",
                _store?.GetType().Name ?? "<none>");
            // _hydrated stays false → /health/ready returns 503 until RBAC is granted
            throw;
        }
        catch (Exception ex)
        {
            HydrationLog.HydrationFailed(logger, ex.GetType().Name, ex.Message);
            // _hydrated stays false → /health/ready returns 503 until hydration succeeds
            throw;
        }
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
