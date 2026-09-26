using CampaignVault.Plugins;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles campaign_update — campaign-level meta edits reachable from take_turn.
/// Supports replacing narrative focus, EnabledModeIds, and merging SystemOptions keys
/// (plugin campaign options). Keys a plugin marks playerOnly, and switching a playerOnlyModeIds mode, need
/// playerRequest and a batch of their own.
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

        var playerOwned = cu.SystemOptions?.Keys
            .Where(k => !string.IsNullOrWhiteSpace(k) && IsPlayerOnly(k.Trim()))
            .Select(k => $"systemOptions.{k.Trim()}")
            .ToList() ?? [];
        if (cu.EnabledModeIds is not null && PluginDataRoots.PlayerOnlyModeIds.Count > 0)
        {
            var before = (await EnsureConfigAsync(ctx, ct)).EnabledModeIds ?? [];
            playerOwned.AddRange(PluginDataRoots.PlayerOnlyModeIds
                .Where(m => before.Contains(m, StringComparer.OrdinalIgnoreCase) !=
                            cu.EnabledModeIds.Contains(m, StringComparer.OrdinalIgnoreCase))
                .Select(m => $"mode {m}"));
        }

        if (playerOwned.Count > 0)
        {
            var owned = string.Join(", ", playerOwned);
            if (string.IsNullOrWhiteSpace(cu.PlayerRequest))
            {
                return ChangeHandlerResult.Failure(
                    $"{owned} belong to the player. Change them only when the player asks, with playerRequest set to their words.");
            }

            if (ctx.Batch is { Count: > 1 })
            {
                return ChangeHandlerResult.Failure(
                    $"{owned} belong to the player. Send that campaign_update as its own commit, not alongside story changes.");
            }

            ctx.RecordMessage($"Player-owned settings {owned} changed at the player's request: \"{cu.PlayerRequest.Trim()}\".");
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
                    "systemOptions was provided but empty — pass at least one key to merge.");
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

    private static bool IsPlayerOnly(string key) =>
        PluginDataRoots.DeclaredCampaignOptions.Any(o =>
            o.PlayerOnly && string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));

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
