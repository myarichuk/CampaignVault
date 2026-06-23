namespace CampaignVault.Data;

public sealed class HttpMcpSessionAccessor(IHttpContextAccessor httpContextAccessor) : IMcpSessionAccessor
{
    public string? SessionId
    {
        get
        {
            var headers = httpContextAccessor.HttpContext?.Request.Headers;
            if (headers is null)
            {
                return null;
            }

            if (headers.TryGetValue("Mcp-Session-Id", out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.ToString();
            }

            if (headers.TryGetValue("MCP-Session-Id", out value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.ToString();
            }

            return null;
        }
    }
}