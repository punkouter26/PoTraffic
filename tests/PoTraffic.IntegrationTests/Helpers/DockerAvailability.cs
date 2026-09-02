using System.Diagnostics;

namespace PoTraffic.IntegrationTests.Helpers;

/// <summary>
/// Whether a Docker daemon is reachable, probed once per test process.
///
/// <para>
/// The probe shells out to <c>docker info</c> with a 5-second timeout. xUnit constructs a
/// test's attribute during discovery, so probing inside the attribute's constructor ran that
/// command once per test method — on a machine without Docker that is a 5-second stall per
/// test, paid before a single assertion executes. <see cref="Lazy{T}"/> makes it once per run.
/// </para>
/// </summary>
internal static class DockerAvailability
{
    private static readonly Lazy<bool> Probe = new(ProbeDockerDaemon, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when <c>docker info</c> succeeds, i.e. Testcontainers can start Azurite.</summary>
    public static bool IsAvailable => Probe.Value;

    /// <summary>Reason shown on skipped tests when the daemon is unreachable.</summary>
    public const string SkipReason = "Docker daemon not reachable — start Docker Desktop and re-run.";

    private static bool ProbeDockerDaemon()
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "info",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            return process.WaitForExit(5_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
