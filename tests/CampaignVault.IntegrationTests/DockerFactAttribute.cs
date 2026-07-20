using System.Diagnostics;

namespace CampaignVault.IntegrationTests;

public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly string? DockerSkipReason = GetDockerSkipReason();
    private const string CAMPAIGNVAULT_IMAGE = "campaignvault:latest";

    public DockerFactAttribute()
    {
        if (DockerSkipReason is not null)
        {
            Skip = DockerSkipReason;
        }
    }

    private static string? GetDockerSkipReason()
    {
        // Check if docker is installed
        if (!IsDockerInstalled())
        {
            return "Docker is not installed on this machine.";
        }

        // Check if docker daemon is running
        if (!IsDockerRunning())
        {
            return "Docker daemon is not running. Start Docker and try again.";
        }

        // Check if the CampaignVault image is built
        if (!IsImageBuilt(CAMPAIGNVAULT_IMAGE))
        {
            return $"Docker image '{CAMPAIGNVAULT_IMAGE}' is not built. " +
                   "Build it with: docker build -t campaignvault:latest -f Dockerfile .";
        }

        return null;
    }

    private static bool IsDockerInstalled()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var exited = process.WaitForExit(1000);
            return exited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDockerRunning()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "ps",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var exited = process.WaitForExit(2000);
            return exited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsImageBuilt(string imageName)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"image inspect {imageName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var exited = process.WaitForExit(2000);
            return exited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}