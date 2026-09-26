using CampaignVault.Models;
using CampaignVault.Plugins;

namespace CampaignVault.Data.Migrations;

/// <summary>
/// Runs every <see cref="IPluginCampaignOptionsUpgrader"/> over each campaign's SystemOptions — both the meta
/// document's copy (what handlers read at runtime) and the <see cref="CampaignConfig"/> copy — and saves the
/// documents an upgrader changed. Idempotent as long as the upgraders are.
/// </summary>
public sealed class UpgradePluginCampaignOptions(
    IDocumentStore documentStore,
    IEnumerable<IPluginCampaignOptionsUpgrader>? upgraders,
    ILogger? logger = null)
{
    private readonly IReadOnlyList<IPluginCampaignOptionsUpgrader> _upgraders = upgraders?.ToList() ?? [];

    /// <summary>Returns how many documents were changed.</summary>
    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        if (_upgraders.Count == 0)
        {
            return 0;
        }

        using var session = documentStore.OpenAsyncSession();
        var changed = 0;

        foreach (var campaign in await session.Query<Campaign>().ToListAsync(ct))
        {
            campaign.SystemOptions ??= [];
            if (Apply(campaign.SystemOptions, _upgraders, logger, campaign.Id))
            {
                changed++;
            }
        }

        foreach (var config in await session.Query<CampaignConfig>().ToListAsync(ct))
        {
            config.SystemOptions ??= [];
            if (Apply(config.SystemOptions, _upgraders, logger, config.Id))
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            await session.SaveChangesAsync(ct);
        }

        return changed;
    }

    /// <summary>Applies every upgrader to one SystemOptions dictionary. An upgrader that throws is logged and skipped.</summary>
    public static bool Apply(
        IDictionary<string, string> systemOptions,
        IEnumerable<IPluginCampaignOptionsUpgrader> upgraders,
        ILogger? logger = null,
        string? documentId = null)
    {
        var changed = false;
        foreach (var upgrader in upgraders)
        {
            try
            {
                if (upgrader.TryUpgrade(systemOptions))
                {
                    changed = true;
                    logger?.LogInformation(
                        "Plugin '{PluginId}' upgraded campaign options on '{DocumentId}'.", upgrader.PluginId, documentId);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Campaign options upgrader '{PluginId}' threw on '{DocumentId}'; changes it made before throwing were kept.",
                    upgrader.PluginId, documentId);
            }
        }

        return changed;
    }
}
