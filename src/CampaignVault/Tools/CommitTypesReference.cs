namespace CampaignVault.Tools;

/// <summary>
/// Canonical list of supported <c>commit</c> $type discriminators — single source for tool descriptions and get_help.
/// </summary>
internal static class CommitTypesReference
{
    internal const string SupportedTypesList =
        "hp, item, item_update, item_equip, item_unequip, item_use, status, statusremove, event, rumor, relationship, engagement_relation, spatial_position, need, attribute, mood, activity, ruleset_action, level_up, location_update, character_update, system_stats, knowledge_update, schedule_change, travel, rest, scene_interrupt_check, faction_reputation, faction_state, quest_progress, plot_thread_progress, plot_thread_clue, resource, archive_entity";

    internal const string SupportedTypesBullet =
        $"Supported `$type`s: {SupportedTypesList}.";
}