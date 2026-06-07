using System.Threading;

namespace CampaignVault.Data;

/// <summary>
/// Provides the currently selected campaign for the lifetime of the server process
/// (or logical "session" in MCP terms).
///
/// <see cref="select_campaign"/> tool sets this value.
/// Most campaign-aware tools fall back to this context when no explicit campaignName
/// is passed.
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
/// Simple in-memory implementation of <see cref="ICurrentCampaignContext"/>.
/// Uses a normal backing field so it persists across MCP requests (which are separate HTTP requests).
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
