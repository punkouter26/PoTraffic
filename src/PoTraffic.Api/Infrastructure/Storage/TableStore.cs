using Azure;
using Azure.Data.Tables;

namespace PoTraffic.Api.Infrastructure.Storage;

public enum TableOpKind { Upsert, Delete }

/// <summary>
/// A single durable write: one row in one table. <paramref name="ETag"/> is the
/// last-known ETag for an <see cref="TableOpKind.Upsert"/> — non-null means "update this
/// existing row only if it hasn't changed" (optimistic concurrency, §5.5); null means the
/// row is new (insert). Ignored for deletes.
/// </summary>
public sealed record TableOp(
    TableOpKind Kind, string Table, string PartitionKey, string RowKey, string? Json, string? ETag = null);

/// <summary>Result of a successful upsert — the row's refreshed ETag, so the caller can issue
/// a correctly-guarded conditional write next time.</summary>
public sealed record TableWriteResult(string Table, string PartitionKey, string RowKey, string ETag);

/// <summary>Durable row store behind <see cref="TableStorageContext"/> (Azurite locally, Azure Table Storage in the cloud).</summary>
public interface ITableStore
{
    Task EnsureTablesAsync(IEnumerable<string> tables, CancellationToken ct = default);
    Task<IReadOnlyList<(string PartitionKey, string RowKey, string Json, string ETag)>> ReadAllAsync(string table, CancellationToken ct = default);

    /// <summary>Applies the batch and returns the refreshed ETag for every successful upsert.</summary>
    Task<IReadOnlyList<TableWriteResult>> ApplyAsync(IReadOnlyList<TableOp> ops, CancellationToken ct = default);

    /// <summary>Cheap round-trip that proves the store is reachable right now (for health checks).
    /// Throws if the backing store is unavailable.</summary>
    Task PingAsync(CancellationToken ct = default);
}

/// <summary>
/// Azure Table Storage implementation. Each entity is one row: PartitionKey/RowKey
/// from the entity registry and the JSON payload in a single <c>Data</c> column,
/// so the schema evolves with the C# types without table migrations.
/// </summary>
public sealed class AzureTableStore(TableServiceClient tableService) : ITableStore
{
    private readonly Dictionary<string, TableClient> _clients = [];
    private readonly Lock _gate = new();

    private TableClient Client(string table)
    {
        lock (_gate)
        {
            if (!_clients.TryGetValue(table, out TableClient? client))
            {
                client = tableService.GetTableClient(table);
                _clients[table] = client;
            }
            return client;
        }
    }

    public async Task EnsureTablesAsync(IEnumerable<string> tables, CancellationToken ct = default)
    {
        foreach (string table in tables)
            await tableService.CreateTableIfNotExistsAsync(table, ct);
    }

    public async Task<IReadOnlyList<(string, string, string, string)>> ReadAllAsync(string table, CancellationToken ct = default)
    {
        List<(string, string, string, string)> rows = [];
        await foreach (TableEntity entity in Client(table).QueryAsync<TableEntity>(cancellationToken: ct))
        {
            if (entity.TryGetValue("Data", out object? data) && data is string json)
                rows.Add((entity.PartitionKey, entity.RowKey, json, entity.ETag.ToString()));
        }
        return rows;
    }

    public async Task PingAsync(CancellationToken ct = default)
    {
        // Read at most one row from the always-seeded config table. A connectivity/auth
        // failure surfaces as RequestFailedException, which the health check reports.
        TableClient client = Client("SystemConfigurations");
        await foreach (Page<TableEntity> page in client
            .QueryAsync<TableEntity>(maxPerPage: 1, cancellationToken: ct)
            .AsPages())
        {
            break; // one page fetched → store reachable
        }
    }

    public async Task<IReadOnlyList<TableWriteResult>> ApplyAsync(IReadOnlyList<TableOp> ops, CancellationToken ct = default)
    {
        List<TableWriteResult> results = [];
        foreach (TableOp op in ops)
        {
            TableClient client = Client(op.Table);
            if (op.Kind == TableOpKind.Upsert)
            {
                TableEntity entity = new(op.PartitionKey, op.RowKey) { ["Data"] = op.Json };
                string etag = await WriteAsync(client, entity, op.ETag, ct);
                results.Add(new TableWriteResult(op.Table, op.PartitionKey, op.RowKey, etag));
            }
            else
            {
                try
                {
                    await client.DeleteEntityAsync(op.PartitionKey, op.RowKey, ETag.All, ct);
                }
                catch (RequestFailedException ex) when (ex.Status is 404 or 409 or 412)
                {
                    // Already gone / concurrent delete — delete is idempotent (§5.5: 409 == success).
                }
            }
        }
        return results;
    }

    /// <summary>
    /// ETag-guarded write (§5.5): update the existing row only if it hasn't changed; insert
    /// when it's new. On any concurrency conflict (412 stale ETag, 409 already-exists, 404
    /// vanished) the in-memory working set is authoritative, so we rewrite unconditionally
    /// and adopt the fresh ETag — treating the conflict as success rather than failing the flush.
    /// </summary>
    private static async Task<string> WriteAsync(TableClient client, TableEntity entity, string? knownETag, CancellationToken ct)
    {
        try
        {
            Response response = string.IsNullOrEmpty(knownETag)
                ? await client.AddEntityAsync(entity, ct)
                : await client.UpdateEntityAsync(entity, new ETag(knownETag), TableUpdateMode.Replace, ct);
            return ETagOf(response, knownETag);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412 or 404)
        {
            Response response = await client.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
            return ETagOf(response, knownETag);
        }
    }

    private static string ETagOf(Response response, string? fallback)
        => response.Headers.ETag?.ToString() ?? fallback ?? ETag.All.ToString();
}
