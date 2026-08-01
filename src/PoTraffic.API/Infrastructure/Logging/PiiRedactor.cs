using System.Security.Cryptography;
using System.Text;

namespace PoTraffic.API.Infrastructure.Logging;

/// <summary>
/// Produces a stable, non-reversible token for a PII value (e.g. a user-entered home/work
/// address) so logs can correlate the same value across events without exposing it. The
/// system already models GDPR deletion, so raw addresses must never reach the log sinks.
/// </summary>
public static class PiiRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(empty)";

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return "addr:" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}
