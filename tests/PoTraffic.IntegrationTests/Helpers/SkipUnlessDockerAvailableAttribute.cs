using System.Diagnostics;
using Xunit;

namespace PoTraffic.IntegrationTests.Helpers;

/// <summary>
/// Conditional Fact — runs the test when the Docker daemon is reachable at test startup;
/// skips gracefully otherwise. Replaces the static <c>[Fact(Skip = "Requires Docker...")]</c>
/// pattern so tests execute automatically in CI once Docker Desktop (or Docker Engine) is running.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkipUnlessDockerAvailableAttribute : FactAttribute
{
    public SkipUnlessDockerAvailableAttribute()
    {
        if (!IsDockerRunning())
            Skip = "Docker daemon not reachable — start Docker Desktop and re-run.";
    }

    private static bool IsDockerRunning()
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = "docker",
                    Arguments              = "info",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                }
            };
            process.Start();
            process.WaitForExit(5_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
