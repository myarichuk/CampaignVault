using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles campaign_update — campaign-level meta edits reachable from take_turn.
/// Currently supports replacing the narrative focus tag list (the former set_narrative_focus tool).
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

        if (cu.NarrativeFocus is null)
        {
            return ChangeHandlerResult.Failure(
                "campaign_update has nothing to apply — set narrativeFocus (full replacement tag list).");
        }

        if (context.Session == null)
        {
            return ChangeHandlerResult.Failure("No session available to update campaign meta.");
        }

        if (string.IsNullOrWhiteSpace(context.CampaignName))
        {
            return ChangeHandlerResult.Failure("No campaign name in change context.");
        }

        var campaign = await context.Session.LoadAsync<Campaign>(keys.Meta(context.CampaignName), ct);
        if (campaign == null)
        {
            return ChangeHandlerResult.Failure(
                $"Campaign '{context.CampaignName}' meta document not found. The campaign might not be initialized yet.");
        }

        campaign.NarrativeFocus = cu.NarrativeFocus;
        context.RecordMessage(
            $"Narrative focus set to: {(campaign.NarrativeFocus.Count > 0 ? string.Join(", ", campaign.NarrativeFocus) : "(cleared)")}.");

        return ChangeHandlerResult.Ok;
    }
}
