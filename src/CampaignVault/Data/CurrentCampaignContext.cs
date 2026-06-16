namespace CampaignVault.Data;

/// <summary>
/// Provides the currently selected campaign for the active DI lifetime scope
/// (typically one MCP HTTP request in stateless hosting).
///
/// <see cref="select_campaign"/> sets this value for subsequent tool calls in the same scope.
/// Most campaign-aware tools fall back to this context when no explicit campaignName
/// is passed. Pass <c>campaignName</c> explicitly when clients cannot rely on scope-local state.
/// </summary>
public interface ICurrentCampaignContext
{
    /// <summary>
    /// The name of the currently selected campaign. Never null.
    /// </summary>
    string CurrentCampaignName { get; }

    /// <summary>
    /// Changes the current campaign for subsequent operations in this context.
    /// </summary>
    void SetCurrent(string campaignName);
}

/// <summary>
/// Per-scope in-memory implementation of <see cref="ICurrentCampaignContext"/>.
/// </summary>
public sealed class CurrentCampaignContext : ICurrentCampaignContext
{
    private string _current = DefaultCampaign;

    private const string DefaultCampaign = "default";

    public string CurrentCampaignName => _current;

    public void SetCurrent(string campaignName)
    {
        if (string.IsNullOrWhiteSpace(campaignName))
        {
            campaignName = DefaultCampaign;
        }

        _current = campaignName.Trim().ToLowerInvariant();
    }
}