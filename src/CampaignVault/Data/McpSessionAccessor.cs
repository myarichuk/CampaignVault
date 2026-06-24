namespace CampaignVault.Data;

/// <summary>
/// Resolves the MCP session ID from the active HTTP request header or the MCP_SESSION_ID environment variable.
/// Campaign selection is keyed by this ID; there is no process-wide fallback.
/// </summary>
public sealed class McpSessionAccessor(IHttpContextAccessor httpContextAccessor) : IMcpSessionAccessor
{
    public string? SessionId
    {
        get
        {
            var headers = httpContextAccessor.HttpContext?.Request.Headers;
            if (headers is not null)
            {
                if (headers.TryGetValue("Mcp-Session-Id", out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.ToString().Trim();
                }

                if (headers.TryGetValue("MCP-Session-Id", out value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.ToString().Trim();
                }
            }

            var configured = Environment.GetEnvironmentVariable("MCP_SESSION_ID");
            return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        }
    }
}