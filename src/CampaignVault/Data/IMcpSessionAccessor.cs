namespace CampaignVault.Data;

/// <summary>
/// Provides the MCP session identifier for the active HTTP request, when available.
/// </summary>
public interface IMcpSessionAccessor
{
    /// <summary>
    /// The <c>Mcp-Session-Id</c> header value, or <see langword="null"/> when not in an HTTP context
    /// or when the server runs in stateless MCP mode (which disables session IDs).
    /// </summary>
    string? SessionId { get; }
}