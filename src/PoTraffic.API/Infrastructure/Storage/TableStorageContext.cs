using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.API.Features.Admin;
using PoTraffic.API.Features.Alerts;
using PoTraffic.API.Features.Auth;
using PoTraffic.API.Features.Config;
using PoTraffic.API.Features.MonitoringWindows;

namespace PoTraffic.API.Infrastructure.Storage;

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
    /// <summary>
    /// Everything the context needs to know about an entity type: its table, its key
    /// projections, and the backing working-set list. Registering the list here (rather than
    /// in a parallel type-test chain) means adding an entity type is a single edit.
    /// </summary>
    private sealed record EntityMap(
        string Table,
        Func<object, string> Pk,
        Func<object, string> Rk,
        Func<TableStorageContext, IList> List);

    private static readonly Dictionary<Type, EntityMap> Maps = new()
    {
        [typeof(User)] = new("Users", _ => "main", e => ((User)e).Id.ToString(), c => c._users),
        [typeof(EntityRoute)] = new("Routes", _ => "main", e => ((EntityRoute)e).Id.ToString(), c => c._routes),
        [typeof(MonitoringWindow)] = new("MonitoringWindows", _ => "main", e => ((MonitoringWindow)e).Id.ToString(), c => c._windows),
        [typeof(MonitoringSession)] = new("MonitoringSessions", _ => "main", e => ((MonitoringSession)e).Id.ToString(), c => c._sessions),
        // Polls partition by route for efficient per-route reads and pruning.
        [typeof(PollRecord)] = new("PollRecords", e => ((PollRecord)e).RouteId.ToString(), e => ((PollRecord)e).Id.ToString(), c => c._polls),
        [typeof(SystemConfiguration)] = new("SystemConfigurations", _ => "main", e => ((SystemConfiguration)e).Key, c => c._configs),
        // Alerts partition by user for efficient per-user reads.
        [typeof(Alert)] = new("Alerts", e => ((Alert)e).UserId.ToString(), e => ((Alert)e).Id.ToString(), c => c._alerts),
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
    internal readonly List<Alert> _alerts = new();

    private readonly object _gate = new();
    private readonly ITableStore? _store;
    private readonly Dictionary<object, string> _snapshots = new(ReferenceEqualityComparer.Instance);
    // Last-known ETag per tracked entity → drives optimistic-concurrency conditional writes (§5.5).
    private readonly Dictionary<object, string> _etags = new(ReferenceEqualityComparer.Instance);
    private readonly List<TableOp> _pendingDeletes = [];

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

    public IQueryable<Alert> Alerts
    {
        get { lock (_gate) return _alerts.AsQueryable(); }
    }

    // ── Legacy aliases (post-refactor) ──────────────────────────────────────
    // These match the old DbSet<T> names that handlers used with EF Core.

    public IQueryable<PollRecord> PollRecords => Polls;
    public IQueryable<MonitoringSession> MonitoringSessions => Sessions;
    public IQueryable<SystemConfiguration> SystemConfigurations => Configurations;
    public IQueryable<MonitoringWindow> MonitoringWindows => Windows;

    // ── Write operations (Add / Remove) ─────────────────────────────────────

    /// <summary>
    /// Reflection over an entity's <c>Id</c> property, resolved once per entity type.
    /// Ids are strongly-typed structs wrapping a GUID (<see cref="IStronglyTypedId{TSelf}"/>),
    /// so "is this unassigned?" and "mint a new one" both go through the wrapper's
    /// <c>Value</c> and its <c>(Guid)</c> constructor rather than a bare Guid cast.
    /// </summary>
    private sealed record IdAccessor(PropertyInfo Property, PropertyInfo ValueProperty)
    {
        public bool IsUnassigned(object entity)
        {
            object? id = Property.GetValue(entity);
            return id is null || (Guid)ValueProperty.GetValue(id)! == Guid.Empty;
        }

        public void AssignNew(object entity)
            => Property.SetValue(entity, Activator.CreateInstance(Property.PropertyType, Guid.NewGuid()));
    }

    // Concurrent, not Dictionary: Add<T> runs on request threads (and on many xUnit threads
    // at once), and an unsynchronised write here corrupts the dictionary — surfacing as
    // "Operations that change non-concurrent collections must have exclusive access" from
    // whichever unrelated caller happens to be inside TryInsert at the time. GetOrAdd is
    // safe to race: building the same accessor twice is harmless, so no lock is needed.
    private static readonly ConcurrentDictionary<Type, IdAccessor?> s_idProperties = new();
    private static readonly MethodInfo GetListMethod = typeof(TableStorageContext)
        .GetMethod(nameof(GetList), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo AddMethod = typeof(TableStorageContext)
        .GetMethod(nameof(Add), BindingFlags.Public | BindingFlags.Instance)!;

    /// <summary>True for the strongly-typed id structs declared in PoTraffic.Shared.Ids.</summary>
    private static bool IsStronglyTypedId(Type type)
        => type.IsValueType
        && Array.Exists(
            type.GetInterfaces(),
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>));

    private static IdAccessor? ResolveIdAccessor(Type type)
    {
        PropertyInfo? prop = type.GetProperty("Id");
        return prop is not null && IsStronglyTypedId(prop.PropertyType)
            ? new IdAccessor(prop, prop.PropertyType.GetProperty(nameof(IStronglyTypedId<UserId>.Value))!)
            : null;
    }

    public void Add<T>(T entity) where T : class
    {
        // Auto-generate the Id when it is still default (replaces EF Core auto-gen).
        IdAccessor? idProp = s_idProperties.GetOrAdd(typeof(T), static t => ResolveIdAccessor(t));

        if (idProp is not null && idProp.IsUnassigned(entity))
            idProp.AssignNew(entity);

        lock (_gate)
        {
            GetList<T>().Add(entity);
        }

        // Cascade: add navigation collection children (replaces EF Core change tracking)
        AddNavigationChildren(entity, typeof(T));
    }

    /// <summary>
    /// A cascade-able navigation collection on a parent type, with every reflection lookup
    /// it needs already resolved: the child element type, the child's back-reference to the
    /// parent, and the closed <c>Add&lt;TChild&gt;</c>. Resolved once per parent type —
    /// <see cref="Add{T}"/> runs on the poll hot path and in bulk from the seeder, so a
    /// per-call <c>GetProperties</c> scan (and a per-child <c>MakeGenericMethod</c>) is pure waste.
    /// </summary>
    private sealed record NavigationCascade(
        PropertyInfo Collection,
        PropertyInfo? ForeignKey,
        MethodInfo AddChild,
        MethodInfo GetChildList);

    private static readonly ConcurrentDictionary<Type, NavigationCascade[]> s_navigationCascades = new();

    private static NavigationCascade[] ResolveNavigationCascades(Type type)
    {
        List<NavigationCascade> cascades = [];

        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!typeof(IEnumerable).IsAssignableFrom(prop.PropertyType) || prop.PropertyType == typeof(string))
                continue;

            Type[] generics = prop.PropertyType.GetGenericArguments();
            if (generics.Length != 1 || !IsKnownEntityType(generics[0]))
                continue;

            Type elementType = generics[0];
            cascades.Add(new NavigationCascade(
                prop,
                // The child's back-reference to the parent (e.g. MonitoringWindow.Route : Route).
                elementType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => p.PropertyType == type),
                AddMethod.MakeGenericMethod(elementType),
                GetListMethod.MakeGenericMethod(elementType)));
        }

        return [.. cascades];
    }

    private void AddNavigationChildren(object entity, Type type)
    {
        foreach (NavigationCascade cascade in s_navigationCascades.GetOrAdd(type, static t => ResolveNavigationCascades(t)))
        {
            if (cascade.Collection.GetValue(entity) is not IEnumerable collection)
                continue;

            // Backing list for this element type — used to skip children a handler already added.
            var existingList = (IList)cascade.GetChildList.Invoke(this, null)!;

            foreach (object child in collection)
            {
                // Set reverse navigation property (e.g. MonitoringWindow.Route = route)
                if (cascade.ForeignKey is not null && cascade.ForeignKey.GetValue(child) is null)
                    cascade.ForeignKey.SetValue(child, entity);

                if (existingList.Contains(child))
                    continue;

                cascade.AddChild.Invoke(this, [child]);
            }
        }
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
        _etags.Remove(entity);
        if (Maps.TryGetValue(typeof(T), out EntityMap? map))
            _pendingDeletes.Add(new TableOp(TableOpKind.Delete, map.Table, map.Pk(entity), map.Rk(entity), null));
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
                IEnumerable<object> candidates = GetListUntyped(type).Cast<object>();

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

            // The tables are independent and the store client is safe for concurrent use, so
            // read them in one wave — sequential reads made cold start (which gates
            // /health/ready) the sum of every table's latency.
            (Type Type, EntityMap Map)[] tables = [.. Maps.Select(kv => (kv.Key, kv.Value))];
            IReadOnlyList<(string, string, string Json, string ETag)>[] tableRows =
                await Task.WhenAll(tables.Select(t => _store.ReadAllAsync(t.Map.Table, ct)));

            int totalEntities = 0;
            for (int i = 0; i < tables.Length; i++)
            {
                (Type type, EntityMap map) = tables[i];
                IReadOnlyList<(string, string, string Json, string ETag)> rows = tableRows[i];
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
        ILookup<RouteId, MonitoringWindow> windowsByRoute = _windows.ToLookup(w => w.RouteId);
        ILookup<RouteId, MonitoringSession> sessionsByRoute = _sessions.ToLookup(s => s.RouteId);
        ILookup<RouteId, PollRecord> pollsByRoute = _polls.ToLookup(p => p.RouteId);
        foreach (EntityRoute route in _routes)
        {
            route.Windows = [.. windowsByRoute[route.Id]];
            route.Sessions = [.. sessionsByRoute[route.Id]];
            route.PollRecords = [.. pollsByRoute[route.Id]];
        }
    }

    private IList GetListUntyped(Type type)
        => Maps.TryGetValue(type, out EntityMap? map)
            ? map.List(this)
            : throw new InvalidOperationException($"TableStorageContext: unknown entity type {type.FullName}");

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
            Ensure(PollCostRates.GoogleMapsKey, "0.005", "Cost per poll - Google Maps", false);
            Ensure(PollCostRates.TomTomKey, "0.004", "Cost per poll - TomTom", false);
            Ensure("quota.daily.default", "10", "Default daily session quota per user", false);
        }
    }
}
