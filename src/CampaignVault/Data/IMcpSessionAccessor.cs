namespace CampaignVault.Data;

/// <summary>
/// Provides the MCP session identifier for the active HTTP request, when available.
/// </summary>
public interface IMcpSessionAccessor
{
    /// <summary>
    /// The <c>Mcp-Session-Id</c> HTTP header, the <c>MCP_SESSION_ID</c> environment variable,
    /// or <see langword="null"/> when neither is available (e.g. stateless HTTP without a configured session).
    /// </summary>
    string? SessionId { get; }
}