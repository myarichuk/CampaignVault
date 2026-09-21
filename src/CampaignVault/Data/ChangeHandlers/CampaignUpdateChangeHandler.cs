using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles campaign_update — campaign-level meta edits reachable from take_turn.
/// Supports replacing the narrative focus tag list (the former set_narrative_focus tool) and, since
/// PLUGIN_SYSTEM_PLAN.md Track A, the CampaignConfig.EnabledModeIds opt-in list for interaction modes
/// (see ModeTransitionChangeHandler) — this is the only write path for that field, so a mode can never
/// be enabled without an explicit campaign_update.
/// </summary>
public sealed class CampaignUpdateChangeHandler(CampaignDocumentKeys keys) : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is CampaignUpdateChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var cu = (CampaignUpdateChange)change;

        if (cu.NarrativeFocus is null && cu.EnabledModeIds is null)
        {
            return ChangeHandlerResult.Failure(
                "campaign_update has nothing to apply — set narrativeFocus and/or enabledModeIds (each a full replacement list).");
        }

        if (context.Session == null)
        {
            return ChangeHandlerResult.Failure("No session available to update campaign meta.");
        }

        if (string.IsNullOrWhiteSpace(context.CampaignName))
        {
            return ChangeHandlerResult.Failure("No campaign name in change context.");
        }

        if (cu.NarrativeFocus is not null)
        {
            var campaign = await context.Session.LoadAsync<Campaign>(keys.Meta(context.CampaignName), ct);
            if (campaign == null)
            {
                return ChangeHandlerResult.Failure(
                    $"Campaign '{context.CampaignName}' meta document not found. The campaign might not be initialized yet.");
            }

            campaign.NarrativeFocus = cu.NarrativeFocus;
        }

        if (cu.EnabledModeIds is not null)
        {
            var configId = keys.Config(context.CampaignName);
            var config = context.Config ?? await context.Session.LoadAsync<CampaignConfig>(configId, ct);
            if (config is null)
            {
                config = new CampaignConfig { Id = configId };
                await context.Session.StoreAsync(config, ct);
            }

            config.EnabledModeIds = cu.EnabledModeIds;
            context.RecordMessage($"Enabled interaction modes for this campaign: {(cu.EnabledModeIds.Count > 0 ? string.Join(", ", cu.EnabledModeIds) : "(none)")}.");
        }

        return ChangeHandlerResult.Ok;
    }
}
