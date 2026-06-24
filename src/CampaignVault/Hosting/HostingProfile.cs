namespace CampaignVault.Hosting;

/// <summary>
/// Deployment profile for MCP hosting. Both profiles use session-keyed campaign selection
/// (no process-wide fallback). The profile affects documentation and default env hints only.
/// </summary>
public enum HostingProfileKind
{
    /// <summary>Local dev: stdio or single-operator HTTP. Session via header or MCP_SESSION_ID.</summary>
    Local,

    /// <summary>Remote deployment: stateless HTTP multi-client. Prefer campaignName or Mcp-Session-Id per request.</summary>
    Remote
}

public static class HostingProfile
{
    public static HostingProfileKind Resolve()
    {
        var explicitProfile = Environment.GetEnvironmentVariable("MCP_HOSTING_PROFILE");
        if (string.Equals(explicitProfile, "remote", StringComparison.OrdinalIgnoreCase))
        {
            return HostingProfileKind.Remote;
        }

        if (string.Equals(explicitProfile, "local", StringComparison.OrdinalIgnoreCase))
        {
            return HostingProfileKind.Local;
        }

        var stateless = string.Equals(
            Environment.GetEnvironmentVariable("MCP_STATELESS"),
            "1",
            StringComparison.OrdinalIgnoreCase);

        return stateless ? HostingProfileKind.Remote : HostingProfileKind.Local;
    }
}