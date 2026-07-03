using Azure;
using Azure.Data.Tables;

namespace PoTraffic.Api.Infrastructure.Storage;

public enum TableOpKind { Upsert, Delete }

/// <summary>A single durable write: one row in one table.</summary>
public sealed record TableOp(TableOpKind Kind, string Table, string PartitionKey, string RowKey, string? Json);

/// <summary>Durable row store behind <see cref="TableStorageContext"/> (Azurite locally, Azure Table Storage in the cloud).</summary>
public interface ITableStore
{
    Task EnsureTablesAsync(IEnumerable<string> tables, CancellationToken ct = default);
    Task<IReadOnlyList<(string PartitionKey, string RowKey, string Json)>> ReadAllAsync(string table, CancellationToken ct = default);
    Task ApplyAsync(IReadOnlyList<TableOp> ops, CancellationToken ct = default);

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

    public async Task<IReadOnlyList<(string, string, string)>> ReadAllAsync(string table, CancellationToken ct = default)
    {
        List<(string, string, string)> rows = [];
        await foreach (TableEntity entity in Client(table).QueryAsync<TableEntity>(cancellationToken: ct))
        {
            if (entity.TryGetValue("Data", out object? data) && data is string json)
                rows.Add((entity.PartitionKey, entity.RowKey, json));
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

    public async Task ApplyAsync(IReadOnlyList<TableOp> ops, CancellationToken ct = default)
    {
        foreach (TableOp op in ops)
        {
            TableClient client = Client(op.Table);
            if (op.Kind == TableOpKind.Upsert)
            {
                TableEntity entity = new(op.PartitionKey, op.RowKey) { ["Data"] = op.Json };
                await client.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
            }
            else
            {
                try
                {
                    await client.DeleteEntityAsync(op.PartitionKey, op.RowKey, ETag.All, ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Row already gone — delete is idempotent.
                }
            }
        }
    }
}
