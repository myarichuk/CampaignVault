using System.Diagnostics;

namespace CampaignVault.IntegrationTests;

public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly bool IsDockerInstalled = CheckIfDockerInstalled();

    public DockerFactAttribute()
    {
        if (!IsDockerInstalled)
        {
            Skip = "Docker is not installed or not running on this machine.";
        }
    }

    private static bool CheckIfDockerInstalled()
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
}