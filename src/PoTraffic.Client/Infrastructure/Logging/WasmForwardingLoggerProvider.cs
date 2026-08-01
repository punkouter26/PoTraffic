using System.Net.Http.Json;
using System.Threading.Channels;
using PoTraffic.Client.Infrastructure.Http;
using Microsoft.Extensions.Logging;

namespace PoTraffic.Client.Infrastructure.Logging;

/// <summary>
/// Observer pattern — buffers log entries in a Channel and batches them to POST /api/client-logs.
/// Serilog-compatible ILogger forwarding for Blazor WASM structured log shipping.
/// Amendment v1.1.0: Serilog is the sole logging backend; all log entries are forwarded via this provider.
/// </summary>
public sealed class WasmForwardingLoggerProvider : ILoggerProvider
{
    private readonly HttpClient _httpClient;
    private readonly Channel<ClientLogEntry> _channel;
    private readonly CancellationTokenSource _cts = new();

    // Stable id for this app load (browser tab). Stamped on every forwarded entry so a
    // tab's client-side logs correlate with each other on the server, instead of every
    // entry arriving with SessionId=null (previously un-joinable to anything).
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    private const int BatchSize = 20;
    private const int FlushIntervalMs = 5000;

    public WasmForwardingLoggerProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _channel = Channel.CreateBounded<ClientLogEntry>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        // Fire-and-forget: the loop runs for the app's lifetime and stops on Dispose via
        // the token. Nothing awaits it, so it is deliberately not retained.
        _ = FlushLoopAsync(_cts.Token);
    }

    public ILogger CreateLogger(string categoryName) =>
        new WasmForwardingLogger(categoryName, _channel.Writer, _sessionId);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        List<ClientLogEntry> batch = new(BatchSize);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushIntervalMs, ct);

                while (_channel.Reader.TryRead(out ClientLogEntry? entry))
                {
                    batch.Add(entry);
                    if (batch.Count >= BatchSize) break;
                }

                if (batch.Count > 0)
                {
                    await SendBatchAsync(batch, ct);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* swallow — log forwarding must never crash the app */ }
        }
    }

    private async Task SendBatchAsync(List<ClientLogEntry> entries, CancellationToken ct)
    {
        try
        {
            await _httpClient.PostAsJsonAsync("/api/client-logs", new ClientLogBatch(entries), AppJsonContext.Default.ClientLogBatch, ct);
        }
        catch { /* network failure — entries dropped gracefully */ }
    }
}

/// <summary>Per-category logger that writes to the shared Channel.</summary>
internal sealed class WasmForwardingLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ChannelWriter<ClientLogEntry> _writer;
    private readonly string _sessionId;

    public WasmForwardingLogger(string categoryName, ChannelWriter<ClientLogEntry> writer, string sessionId)
    {
        _categoryName = categoryName;
        _writer = writer;
        _sessionId = sessionId;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        string message = formatter(state, exception);
        if (exception is not null) message += $"\n{exception}";

        ClientLogEntry entry = new(
            Level: logLevel.ToString(),
            Message: message,
            SourceContext: _categoryName,
            CorrelationId: null,
            SessionId: _sessionId,
            Timestamp: DateTimeOffset.UtcNow);

        _writer.TryWrite(entry);
    }
}

/// <summary>Local DTO matching the API contract at POST /api/client-logs.</summary>
internal sealed record ClientLogEntry(
    string Level,
    string Message,
    string? SourceContext,
    string? CorrelationId,
    string? SessionId,
    DateTimeOffset Timestamp);
