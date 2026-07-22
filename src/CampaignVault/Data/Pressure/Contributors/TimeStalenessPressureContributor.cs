using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

/// <summary>
/// Nudges the DM-LLM to record narrative time passage once enough commits have gone by without any
/// of them reporting it — neither crossing a day boundary (rest, travel, advance_world) nor carrying
/// MinutesElapsed on a change (dialogue, lockpicking, a shared meal, ...). Ground truth is
/// Campaign.CommitsSinceTimeRecorded, maintained by CampaignRepository.StageChangesAsync and
/// AdvanceWorldAsync — this contributor only reads it and decides whether to surface a reminder.
/// </summary>
public sealed class TimeStalenessPressureContributor : IPressureContributor
{
    public const string GroupingKey = "Time:Staleness";

    private readonly CampaignDocumentKeys _keys;

    public TimeStalenessPressureContributor(CampaignDocumentKeys keys)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    public PressureScope Scope => PressureScope.Both;
    public int Order => 5;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var campaign = await ctx.Session.LoadAsync<Campaign>(_keys.Meta(ctx.CampaignName), ct);
        if (campaign == null)
        {
            return [];
        }

        var threshold = ctx.Config.TimeStalenessNudgeThreshold;
        if (campaign.CommitsSinceTimeRecorded < threshold)
        {
            return [];
        }

        return
        [
            new WorldPressureItem(PressureSeverity.Suggestion, ctx.CampaignName,
                $"No in-game time has been recorded across the last {campaign.CommitsSinceTimeRecorded} commits. " +
                "If narrative time has passed (a conversation, downtime, a lockpicking attempt, a meal), " +
                "add minutesElapsed to a change in your next commit, or call rest/advance_world if a rest or a longer skip happened.",
                GroupingKey)
        ];
    }
}
