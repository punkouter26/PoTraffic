using System.Diagnostics;
using Xunit;

namespace PoTraffic.Tests.Helpers;

/// <summary>
/// Conditional Fact — runs the test when Docker is reachable. Azurite itself is
/// created and cleaned up by Testcontainers inside the integration test host.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkipUnlessAzuriteAvailableAttribute : FactAttribute
{
    public SkipUnlessAzuriteAvailableAttribute()
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
                    FileName = "docker",
                    Arguments = "info",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
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
