using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles campaign_update — campaign-level meta edits reachable from take_turn.
/// Supports replacing narrative focus, EnabledModeIds, and merging SystemOptions keys
/// (plugin campaign options such as intimacyTone).
/// </summary>
public sealed class CampaignUpdateChangeHandler(CampaignDocumentKeys keys) : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is CampaignUpdateChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var cu = (CampaignUpdateChange)change;

        if (cu.NarrativeFocus is null && cu.EnabledModeIds is null && cu.SystemOptions is null)
        {
            return ChangeHandlerResult.Failure(
                "campaign_update has nothing to apply — set narrativeFocus, enabledModeIds, and/or systemOptions.");
        }

        if (ctx.Session == null)
        {
            return ChangeHandlerResult.Failure("No session available to update campaign meta.");
        }

        if (string.IsNullOrWhiteSpace(ctx.CampaignName))
        {
            return ChangeHandlerResult.Failure("No campaign name in change ctx.");
        }

        if (cu.NarrativeFocus is not null)
        {
            var campaign = await ctx.Session.LoadAsync<Campaign>(keys.Meta(ctx.CampaignName), ct);
            if (campaign == null)
            {
                return ChangeHandlerResult.Failure(
                    $"Campaign '{ctx.CampaignName}' meta document not found. The campaign might not be initialized yet.");
            }

            campaign.NarrativeFocus = cu.NarrativeFocus;
        }

        if (cu.EnabledModeIds is not null)
        {
            var config = await EnsureConfigAsync(ctx, ct);
            config.EnabledModeIds = cu.EnabledModeIds;
            ctx.RecordMessage(
                $"Enabled interaction modes for this campaign: {(cu.EnabledModeIds.Count > 0 ? string.Join(", ", cu.EnabledModeIds) : "(none)")}.");
        }

        if (cu.SystemOptions is not null)
        {
            if (cu.SystemOptions.Count == 0)
            {
                return ChangeHandlerResult.Failure(
                    "systemOptions was provided but empty — pass at least one key to merge (e.g. intimacyTone).");
            }

            // Runtime tone/options path used by handlers (GetSystemOptionsAsync loads Campaign meta).
            var campaign = await ctx.Session.LoadAsync<Campaign>(keys.Meta(ctx.CampaignName), ct);
            if (campaign == null)
            {
                return ChangeHandlerResult.Failure(
                    $"Campaign '{ctx.CampaignName}' meta document not found. The campaign might not be initialized yet.");
            }

            campaign.SystemOptions ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var config = await EnsureConfigAsync(ctx, ct);
            config.SystemOptions ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var written = new List<string>();
            foreach (var (key, value) in cu.SystemOptions)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                var k = key.Trim();
                var v = value ?? "";
                campaign.SystemOptions[k] = v;
                config.SystemOptions[k] = v;
                written.Add($"{k}={v}");
            }

            if (written.Count == 0)
            {
                return ChangeHandlerResult.Failure("systemOptions contained no usable keys.");
            }

            ctx.RecordMessage($"Merged SystemOptions: {string.Join(", ", written)}.");
        }

        return ChangeHandlerResult.Ok;
    }

    private async Task<CampaignConfig> EnsureConfigAsync(ChangeContext ctx, CancellationToken ct)
    {
        var configId = keys.Config(ctx.CampaignName!);
        var config = ctx.Config ?? await ctx.Session.LoadAsync<CampaignConfig>(configId, ct);
        if (config is null)
        {
            config = new CampaignConfig { Id = configId };
            await ctx.Session.StoreAsync(config, ct);
        }

        return config;
    }
}
