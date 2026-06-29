using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// Built-in resource pool definitions for each supported TTRPG system.
/// Used when CampaignConfig.ResourcePoolSchemas is empty (default case).
/// </summary>
public static class ResourcePoolDefaults
{
    /// <summary>D&D 5e spell slots (levels 1-9), sorcerer points, class-specific resources (maneuvers, channel divinity, etc.).</summary>
    public static Dictionary<string, ResourcePoolTemplate> Dnd5e => new()
    {
        // ── SPELL SLOTS ──
        // Spell slots by level: based on class and character level
        // Maps: "1" -> 4 slots at level 1, "2" -> 3 slots at level 3, etc.
        {
            "spell_slots_1",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_1",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 4,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "1", 2 }, { "2", 3 }, { "3", 4 }, { "4", 4 }, { "5", 4 },
                    { "6", 4 }, { "7", 4 }, { "8", 4 }, { "9", 4 }, { "20", 4 }
                },
                Description = "1st-level spell slots (recovers on long rest)"
            }
        },
        {
            "spell_slots_2",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_2",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 2,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "3", 2 }, { "4", 3 }, { "5", 3 }, { "6", 3 }, { "7", 3 },
                    { "8", 3 }, { "9", 3 }, { "20", 3 }
                },
                Description = "2nd-level spell slots (recovers on long rest)"
            }
        },
        {
            "spell_slots_3",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_3",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "5", 2 }, { "6", 2 }, { "7", 2 }, { "8", 2 }, { "9", 2 }, { "20", 2 }
                },
                Description = "3rd-level spell slots (recovers on long rest)"
            }
        },
        {
            "spell_slots_4",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_4",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "7", 1 }, { "8", 2 }, { "9", 2 }, { "20", 2 }
                },
                Description = "4th-level spell slots (recovers on long rest)"
            }
        },
        {
            "spell_slots_5",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_5",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "9", 1 }, { "10", 2 }, { "20", 2 }
                },
                Description = "5th-level spell slots (recovers on long rest)"
            }
        },
        {
            "spell_slots_6",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_6",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "11", 1 }, { "20", 1 }
                },
                Description = "6th-level spell slots (recovers on long rest)"
            }
        },
        {
            "spell_slots_7",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_7",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "13", 1 }, { "20", 1 }
                },
                Description = "7th-level spell slots (recovers on long rest)"
            }
        },
        {
            "spell_slots_8",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_8",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "15", 1 }, { "20", 1 }
                },
                Description = "8th-level spell slots (recovers on long rest)"
            }
        },
        {
            "spell_slots_9",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_9",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "17", 1 }, { "20", 1 }
                },
                Description = "9th-level spell slots (recovers on long rest)"
            }
        },
        {
            "sorcerer_points",
            new ResourcePoolTemplate
            {
                PoolName = "sorcerer_points",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 2,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "1", 2 }, { "2", 3 }, { "3", 3 }, { "4", 4 }, { "5", 5 },
                    { "6", 5 }, { "7", 6 }, { "8", 6 }, { "9", 7 }, { "10", 7 },
                    { "11", 8 }, { "12", 8 }, { "13", 9 }, { "14", 9 }, { "15", 10 },
                    { "16", 10 }, { "17", 11 }, { "18", 11 }, { "19", 12 }, { "20", 12 }
                },
                Description = "Sorcerer Points (recovers on long rest)"
            }
        },
        {
            "warlock_invocations",
            new ResourcePoolTemplate
            {
                PoolName = "warlock_invocations",
                Recovery = RecoveryType.ShortRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "1", 1 }, { "2", 2 }, { "5", 3 }, { "7", 4 }, { "9", 5 },
                    { "12", 6 }, { "15", 7 }, { "18", 8 }, { "20", 8 }
                },
                Description = "Warlock Spell Slot (short-rest recovery)"
            }
        },
        {
            "ki_points",
            new ResourcePoolTemplate
            {
                PoolName = "ki_points",
                Recovery = RecoveryType.ShortRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "1", 0 }, { "3", 3 }, { "4", 4 }, { "5", 5 }, { "6", 6 },
                    { "7", 7 }, { "8", 8 }, { "9", 9 }, { "10", 10 }, { "11", 11 },
                    { "12", 12 }, { "13", 13 }, { "14", 14 }, { "15", 15 }, { "16", 16 },
                    { "17", 17 }, { "18", 18 }, { "19", 19 }, { "20", 20 }
                },
                Description = "Monk Ki Points (recovers on short rest)"
            }
        },

        // ── CLASS-SPECIFIC RESOURCES ──
        {
            "superiority_dice",
            new ResourcePoolTemplate
            {
                PoolName = "superiority_dice",
                Recovery = RecoveryType.ShortRest,
                DefaultMax = 4,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "3", 4 }, { "7", 5 }, { "11", 6 }, { "15", 7 }, { "18", 8 }, { "20", 8 }
                },
                Description = "Battle Master Superiority Dice for maneuvers (recovers on short rest)"
            }
        },
        {
            "channel_divinity",
            new ResourcePoolTemplate
            {
                PoolName = "channel_divinity",
                Recovery = RecoveryType.ShortRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "1", 1 }, { "6", 2 }, { "18", 3 }, { "20", 3 }
                },
                Description = "Cleric/Paladin Channel Divinity uses (recovers on short rest)"
            }
        },
        {
            "bardic_inspiration",
            new ResourcePoolTemplate
            {
                PoolName = "bardic_inspiration",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 3,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "1", 3 }, { "5", 4 }, { "9", 5 }, { "13", 6 }, { "17", 7 }, { "20", 8 }
                },
                Description = "Bard Bardic Inspiration uses (recovers on long rest)"
            }
        },
        {
            "wildshape_uses",
            new ResourcePoolTemplate
            {
                PoolName = "wildshape_uses",
                Recovery = RecoveryType.ShortRest,
                DefaultMax = 2,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "2", 2 }, { "4", 3 }, { "8", 4 }, { "12", 5 }, { "16", 6 }, { "20", 6 }
                },
                Description = "Druid Wild Shape uses (recovers on short rest)"
            }
        },
        {
            "action_surge",
            new ResourcePoolTemplate
            {
                PoolName = "action_surge",
                Recovery = RecoveryType.ShortRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "1", 1 }, { "17", 2 }, { "20", 2 }
                },
                Description = "Fighter Action Surge uses (recovers on short rest)"
            }
        },
        {
            "font_of_magic",
            new ResourcePoolTemplate
            {
                PoolName = "font_of_magic",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["dnd5e"],
                LevelToMaxMap = new()
                {
                    { "1", 0 }, { "2", 2 }, { "3", 3 }, { "4", 4 }, { "5", 5 },
                    { "6", 6 }, { "7", 7 }, { "8", 8 }, { "9", 9 }, { "20", 9 }
                },
                Description = "Sorcerer Font of Magic points (flexible metamagic, recovers on long rest)"
            }
        }
    };

    /// <summary>Pathfinder 2e spell levels (1-4), Focus Points, and class-specific resources.</summary>
    public static Dictionary<string, ResourcePoolTemplate> Pf2e => new()
    {
        // ── SPELL SLOTS ──
        // PF2e spell slots: levels 1-4 for most spellcasters
        {
            "spell_slots_1",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_1",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["pf2e"],
                Description = "1st-level spell slots (recovers on long rest)"
            }
        },
        {
            "spell_slots_2",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_2",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["pf2e"],
                Description = "2nd-level spell slots (recovers on long rest)"
            }
        },
        {
            "spell_slots_3",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_3",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["pf2e"],
                Description = "3rd-level spell slots (recovers on long rest)"
            }
        },
        {
            "spell_slots_4",
            new ResourcePoolTemplate
            {
                PoolName = "spell_slots_4",
                Recovery = RecoveryType.LongRest,
                DefaultMax = 1,
                ApplicableSystems = ["pf2e"],
                Description = "4th-level spell slots (recovers on long rest)"
            }
        },
        {
            "focus_points",
            new ResourcePoolTemplate
            {
                PoolName = "focus_points",
                Recovery = RecoveryType.ShortRest,
                DefaultMax = 1,
                ApplicableSystems = ["pf2e"],
                LevelToMaxMap = new()
                {
                    { "1", 0 }, { "4", 1 }, { "10", 2 }, { "16", 3 }, { "20", 3 }
                },
                Description = "Focus Points (recovers on short rest)"
            }
        },

        // ── CLASS-SPECIFIC RESOURCES ──
        {
            "bon_mot",
            new ResourcePoolTemplate
            {
                PoolName = "bon_mot",
                Recovery = RecoveryType.ShortRest,
                DefaultMax = 1,
                ApplicableSystems = ["pf2e"],
                Description = "Bard Bon Mot uses (recovers on short rest)"
            }
        },
        {
            "recall_knowledge",
            new ResourcePoolTemplate
            {
                PoolName = "recall_knowledge",
                Recovery = RecoveryType.ShortRest,
                DefaultMax = 1,
                ApplicableSystems = ["pf2e"],
                Description = "Investigator Recall Knowledge uses (recovers on short rest)"
            }
        }
    };

    /// <summary>Fallout 2d20 Action Points (per-turn resource).</summary>
    public static Dictionary<string, ResourcePoolTemplate> Fallout2d20 => new()
    {
        {
            "action_points",
            new ResourcePoolTemplate
            {
                PoolName = "action_points",
                Recovery = RecoveryType.PerTurn,
                DefaultMax = 10,
                ApplicableSystems = ["fallout2d20"],
                LevelToMaxMap = new()
                {
                    { "1", 10 }, { "5", 11 }, { "10", 12 }, { "15", 13 }, { "20", 14 }
                },
                Description = "Action Points (resets at start of your turn in combat — LLM manually resets via resource commits)"
            }
        }
    };

    /// <summary>Narrative/homebrew system (no default pools).</summary>
    public static Dictionary<string, ResourcePoolTemplate> Narrative => [];

    /// <summary>Get the defaults for a given ruleset system.</summary>
    public static Dictionary<string, ResourcePoolTemplate> GetDefaults(RulesetSystem system) => system switch
    {
        RulesetSystem.Dnd5e => Dnd5e,
        RulesetSystem.Pathfinder2e => Pf2e,
        RulesetSystem.Fallout2d20 => Fallout2d20,
        RulesetSystem.Narrative => Narrative,
        _ => []
    };
}
