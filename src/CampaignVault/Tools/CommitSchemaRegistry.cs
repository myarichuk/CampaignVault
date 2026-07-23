namespace CampaignVault.Tools;

public record CommitTypeSchema(
    string Type,
    string Category,
    string Description,
    string[] RequiredFields,
    string[] OptionalFields,
    bool HasSideEffects,
    string[] SideEffects,
    string[] CoCommitHints,
    string? Example = null
);

internal static class CommitSchemaRegistry
{
    private static readonly IReadOnlyList<CommitTypeSchema> All = BuildAll();

    public static IReadOnlyList<CommitTypeSchema> GetAll(string? category = null)
    {
        if (string.IsNullOrWhiteSpace(category)) return All;
        return All.Where(s => s.Category.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static IReadOnlyList<CommitTypeSchema> BuildAll() =>
    [
        // ── Combat ──────────────────────────────────────────────────────────────────
        new("hp", "Combat",
            "Adjust a character's HP by a delta. Positive heals, negative damages.",
            ["characterId", "delta"],
            [],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: ["event"],
            Example: """{"$type":"hp","characterId":"chars/grog","delta":-8}"""),

        new("ruleset_action", "Combat",
            "THE ENGINE'S ONLY DICE ROLLER — used for combat AND every ordinary out-of-combat check (Perception on " +
            "arrival, Investigation, Stealth, a persuasion attempt, a passive save, etc.). Never invent a die result " +
            "yourself (mentally, or via an external script/tool) — always resolve uncertainty through this $type so the " +
            "roll is recorded and reproducible. actionType covers all of it: Attack, Spell, SkillCheck (ordinary skill " +
            "vs. a DC — this is the one for ambient/exploration checks), SavingThrow, ContestedCheck, OpposedCheck, " +
            "UseItem, Recovery. ENGINE SIDE EFFECTS: rolls dice and auto-applies HpChange, StatusChange, " +
            "EngagementRelationChange for Attack/Spell — do NOT add separate hp/status commits for the same action, " +
            "double-application will occur. actionCategory defaults to Melee if omitted — set it explicitly " +
            "(Spell/Ranged/Social/Survival/Maneuver) especially for non-combat checks.",
            ["characterId", "actionName", "actionType"],
            ["targetIds", "parameters", "advantageState", "damageType", "actionCategory"],
            HasSideEffects: true,
            SideEffects: ["hp", "status", "engagement_relation"],
            CoCommitHints: ["event"],
            Example: """Attack: {"$type":"ruleset_action","characterId":"chars/asha","targetIds":["chars/bandit"],"actionName":"longsword","actionType":"Attack","actionCategory":"Melee"} | Ambient skill check (no targets): {"$type":"ruleset_action","characterId":"chars/lyra","actionName":"Perception","actionType":"SkillCheck","actionCategory":"Survival","parameters":{"dc":"12"}}"""),

        new("status", "Combat",
            "Add a structured status effect to a character (preferred: use 'effect' field for full control).",
            ["characterId"],
            ["effect", "status"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: ["event"],
            Example: """{"$type":"status","characterId":"chars/grog","effect":{"name":"Poisoned","category":"Poison","expiresAtRound":3}}"""),

        new("statusremove", "Combat",
            "Remove a named status condition from a character (case-insensitive, removes all matching).",
            ["characterId", "status"],
            [],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: [],
            Example: """{"$type":"statusremove","characterId":"chars/grog","status":"Poisoned"}"""),

        new("level_up", "Combat",
            "Increase a character's level and apply ruleset-specific HP gains.",
            ["characterId"],
            ["levelsGained", "hpMode", "healToMatch", "classGained"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: ["event"]),

        new("rest", "Combat",
            "ENGINE SIDE EFFECTS: Calculates danger of the rest location and may emit encounter events. " +
            "Applies need recovery (tiredness settles toward baseline) and resource pool recovery " +
            "(spell slots/focus points, per the rest type) immediately — no separate advance_world call needed.",
            ["characterId", "locationId", "intendedHours"],
            ["securityModifier", "restType", "narrativeNote"],
            HasSideEffects: true,
            SideEffects: ["need", "event"],
            CoCommitHints: ["event"],
            Example: """{"$type":"rest","characterId":"chars/valen","locationId":"locations/inn_room","intendedHours":8,"securityModifier":10}"""),

        new("scene_interrupt_check", "Narrative",
            "ENGINE MACRO: Rolls crowd reaction and may emit an ActivityChange promoting a transient from the ambient crowd. " +
            "Cooldown: once per location per day. Call after tense beats in crowded locations, or any long non-combat scene " +
            "(interrogation, negotiation, a late-night stakeout/talk) where an ambient interruption is dramatically plausible — not combat-only.",
            ["locationId", "characterId"],
            ["riskModifier", "notes"],
            HasSideEffects: true,
            SideEffects: ["activity"],
            CoCommitHints: [],
            Example: """{"$type":"scene_interrupt_check","locationId":"locations/market","characterId":"chars/valen","riskModifier":25}"""),

        // ── Narrative ────────────────────────────────────────────────────────────────
        new("event", "Narrative",
            "Record a noteworthy occurrence. Required after combat rounds, conversations, and discoveries to populate recall_history and get_npc_context. Set recordingMode to 'Deliberate' when the player performs an explicit, in-fiction act of recording (marking a map, writing a name down, deliberately memorizing a landmark). Leave as 'Passive' (or omit) for ambient narrative elements. Deliberate events lock in high importance and skip heuristic inference; Passive events infer tone/importance from context and decay naturally. Do NOT put a location ID inside 'involved' — use 'locationId'/'relatedLocationIds' so recall_history/location-scoped queries can find it.",
            ["summary"],
            ["category", "involved", "emotionalBeat", "relatedEntityId", "locationId", "relatedLocationIds", "eventId", "importance", "recordingMode"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: [],
            Example: """{"$type":"event","summary":"Grog defeated the bandit captain","category":"Combat","involved":["chars/grog","chars/bandit-captain"]}"""),

        new("relationship", "Narrative",
            "Apply a numeric delta to one character's opinion of another. Include 'reason' for behavioral synthesis.",
            ["characterId", "targetId", "delta", "reason"],
            [],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: ["event"]),

        new("mood", "Narrative",
            "Override a character's current mood string. Use for strong narrative moments.",
            ["characterId", "newMood"],
            [],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        new("activity", "Narrative",
            "Update what an NPC is doing and/or where they are — a direct position write with NO encounter/interruption " +
            "check of its own. Use for local repositioning already established as safe (moving within a scene, settling " +
            "into a spot the party already occupies). For a PC/NPC actually TRAVELING somewhere (crossing distance, " +
            "especially anywhere risk is plausible — alone, at night, unescorted, hostile territory), use 'travel' " +
            "instead — it rolls an encounter check via encounterRiskModifier; this $type silently assumes nothing " +
            "happens en route. When the destination is a specific, notable spot inside a broader existing location " +
            "(a hidden camp, a stash) that stakes will make matter later, set poiName/poiDetails in the SAME change " +
            "instead of a separate location_update — bundles the move and its detail into one commit so the detail " +
            "can't be forgotten.",
            ["characterId"],
            ["newActivity", "newLocationId", "updateLocation", "reason", "poiName", "poiDetails"],
            HasSideEffects: true,
            SideEffects: ["location_update"],
            CoCommitHints: [],
            Example: """{"$type":"activity","characterId":"chars/lyra","newActivity":"settling by the hearth to read","newLocationId":"locations/the-tavern","updateLocation":true,"poiName":"Dog-eared journal by the hearth","poiDetails":"Left open to a half-finished entry."}"""),

        new("need", "Narrative",
            "Adjust an NPC's named need (hunger, thirst, tiredness, etc.). Negative satisfies, positive increases.",
            ["characterId", "need", "delta"],
            [],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        new("attribute", "Narrative",
            "Set or delta an arbitrary narrative attribute (willpower, morale, corruption, etc.). " +
            "'exhaustion_level' is the mechanical D&D 5e exhaustion track (1-6 scale) — distinct from " +
            "narrative tiredness, which is set via 'need' commits instead.",
            ["characterId", "attribute", "value"],
            ["isDelta"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        new("knowledge_update", "Narrative",
            "Store a memory in an NPC's psychology (topic, details, importance). Set recordingMode to 'Deliberate' when the character performs an explicit, in-fiction act of recording — marking a map, writing a name in a journal, deliberately memorizing a face/route/detail. Leave as 'Passive' (or omit) for ambient absorption. Deliberate memories lock in high salience/importance and skip heuristic inference; Passive memories infer emotional tone and importance from details and decay naturally.",
            ["characterId", "topic", "details"],
            ["importance", "createMemory", "source", "valence", "salience", "urgency", "relatedEntityIds", "sourceEventIds", "recordingMode"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        new("rumor", "Narrative",
            "Advance or retire an existing rumor's lifecycle state.",
            ["rumorId", "newState"],
            ["newText"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        // ── World ────────────────────────────────────────────────────────────────────
        new("character_update", "World",
            "Patch visual appearance, tags, features, or party flags on an existing character.",
            ["characterId"],
            ["appearanceOverride", "tagsToAdd", "tagsToRemove", "featuresToAdd", "featuresToRemove",
             "keepAlive", "isPc", "isPartyCompanion", "systemStats"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        new("system_stats", "World",
            "Patch or bootstrap a character's ruleset-specific stats (partial merge).",
            ["characterId", "systemStats"],
            [],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        new("location_update", "World",
            "Granular updates to an existing location (exits, PoIs, tags, state).",
            ["locationId"],
            ["addExit", "removeExitTarget", "addPointOfInterest", "removePointOfInterest",
             "materializePointOfInterest", "poiDetails", "pointOfInterestDetails", "ambientCrowd", "name", "description",
             "newState", "tagsToAdd", "tagsToRemove", "featuresToAdd", "featuresToRemove", "dangerModifier"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        new("item", "World",
            "Move an item to a new holder (character, location, or container item). Also how to model " +
            "worn/carried gear that should visibly hang off another equipped item — a sword in a back " +
            "sheath, throwing knives on a bandolier, a pouch on a belt: set toHolderId to the sheath/" +
            "bandolier/belt item's id (not the character's), while that container item is itself equipped " +
            "on the character via item_equip. The contained item's holder chain resolves through it.",
            ["itemId", "toHolderId"],
            [],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        new("item_update", "World",
            "Update an item's state, category, tags, or properties. Also handles persistent granular " +
            "ItemDetails (scratches, stains, secret compartments) via upsertItemDetail/retireItemDetailId — " +
            "durable, examine-able state distinct from temporary tags or narrative flavor. To seed initial " +
            "ItemDetails on a brand-new item, pass itemDetails on upsert_item/world_build instead — this " +
            "$type is for incremental changes to an item that already exists.",
            ["itemId"],
            ["newState", "coreCategory", "tagsToAdd", "tagsToRemove", "featuresToAdd",
             "featuresToRemove", "propertiesToUpsert", "propertiesToRemove",
             "ambientPersistenceNote", "ambientExpiresAtDay",
             "upsertItemDetail", "retireItemDetailId"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: [],
            Example: """{"$type":"item_update","itemId":"items/oak-table","upsertItemDetail":{"name":"secret glyph","description":"three-line elven rune, faintly glowing","origin":{"type":"Actor","id":"chars/rogue"},"participants":[{"id":"chars/rogue","role":"Caused"}]}}"""),

        new("item_equip", "World",
            "Equip a carried item into its EquipZones. HARD-FAILS listing conflicts (same zone+layer, or off-hand for a two-handed weapon) unless replaceConflicts:true. " +
            "Different EquipLayers on the same zone coexist (e.g. an enchanted robe worn over chainmail). " +
            "ENGINE SIDE EFFECTS: recomputes ArmorClass, WarmthRating, and MovementModifier from all equipped items.",
            ["characterId", "itemId"],
            ["replaceConflicts"],
            HasSideEffects: true,
            SideEffects: ["system_stats"],
            CoCommitHints: [],
            Example: """{"$type":"item_equip","characterId":"chars/grog","itemId":"items/chain-shirt","replaceConflicts":false}"""),

        new("item_unequip", "World",
            "Unequip a currently equipped item. The item stays carried. ENGINE SIDE EFFECTS: recomputes ArmorClass, WarmthRating, and MovementModifier.",
            ["characterId", "itemId"],
            [],
            HasSideEffects: true,
            SideEffects: ["system_stats"],
            CoCommitHints: [],
            Example: """{"$type":"item_unequip","characterId":"chars/grog","itemId":"items/chain-shirt"}"""),

        new("item_use", "World",
            "Spend or restore charges/doses on a limited-use item (water gourd, healing ointment, reagent vial). " +
            "Lazy-initializes CurrentCharges = MaxCharges on first use. HARD-FAILS on insufficient charges (no silent clamping, same precedent as 'resource').",
            ["itemId"],
            ["delta", "reason"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: ["event"],
            Example: """{"$type":"item_use","itemId":"items/healing-ointment","delta":-1,"reason":"Applied a dose to the wound"}"""),

        new("travel", "World",
            "ENGINE SIDE EFFECTS: Auto-applies tiredness NeedChange, time advance, and optional random encounter.",
            ["characterId", "destinationLocationId"],
            ["narrative", "travelCostHoursOverride", "terrainOverride", "encounterRiskModifier"],
            HasSideEffects: true,
            SideEffects: ["need", "activity", "event"],
            CoCommitHints: ["event"],
            Example: """{"$type":"travel","characterId":"chars/valen","destinationLocationId":"locations/highpass","encounterRiskModifier":-10}"""),

        new("engagement_relation", "World",
            "Establish pairwise engagement state between two entities (grapple, embrace, watch). Bidirectional by default.",
            ["characterId", "targetId"],
            ["category", "verb", "restrictionLevel", "bidirectional"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        new("spatial_position", "World",
            "Set relative zone/distance positioning for a character.",
            ["characterId", "targetId"],
            ["distanceBand", "bearing", "zone"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        new("schedule_change", "World",
            "Change or remove a character's schedule (promotes transient → persistent or vice versa).",
            ["characterId"],
            ["schedule"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: []),

        new("faction_reputation", "World",
            "Adjust a character's reputation with a faction.",
            ["characterId", "factionId", "delta"],
            ["reason"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: ["event"]),

        new("faction_state", "World",
            "Update a faction's stance toward another faction or its influence level. targetFactionId is conditionally required: setting newStance without it fails the commit.",
            ["factionId"],
            ["newStance", "targetFactionId", "influenceDelta", "narrative"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: ["event"]),

        new("quest_progress", "World",
            "Advance or fail a quest objective. When the quest reaches Complete/Failed, 'involvedIds' becomes the 'involved' list on the auto-generated completion event — omit it and that event will have no 'involved' entries.",
            ["questId", "newState"],
            ["objectiveIndex", "objectiveName", "narrativeNote", "involvedIds"],
            HasSideEffects: false,
            SideEffects: ["event"],
            CoCommitHints: ["event"]),

        // ── PlotThread ───────────────────────────────────────────────────────────────
        new("plot_thread_progress", "PlotThread",
            "Update state, tension, notes, resolution condition, or involved entities of a plot thread.",
            ["plotThreadId"],
            ["newState", "tensionDelta", "resolutionCondition", "addForeshadowingHook",
             "addInvolvedEntityId", "removeInvolvedEntityId", "addClue", "narrativeNote"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: ["event"],
            Example: """{"$type":"plot_thread_progress","plotThreadId":"plot-threads/guild-infiltration","tensionDelta":-20,"newState":"Resolved","narrativeNote":"Guild leader arrested."}"""),

        new("plot_thread_clue", "PlotThread",
            "Mark a clue as discovered. AUTO-EMITS an EventOccurred (Discovery) if narrativeNote is set. " +
            "Resets the staleness pressure timer.",
            ["plotThreadId", "clueId"],
            ["discoveredByCharacterIds", "narrativeNote"],
            HasSideEffects: true,
            SideEffects: ["event"],
            CoCommitHints: [],
            Example: """{"$type":"plot_thread_clue","plotThreadId":"plot-threads/guild-infiltration","clueId":"clue-1","discoveredByCharacterIds":["chars/valen"],"narrativeNote":"Found the captain's ledger."}"""),

        // ── Resources (Spells, Focus Points, Action Points) ────────────────────────────
        new("resource", "World",
            "Spend or recover a resource pool (spell slots, sorcerer points, focus points, action points, " +
            "party currency). Negative delta = spend (cast spell, use ability, pay for goods), positive = " +
            "recover/award. Currency pools: 'gold' (dnd5e/pf2e), 'caps' (fallout2d20) — never recover, capped " +
            "at a large finite max (not literally unlimited). Grants clamp to max; spends that would go " +
            "below 0 HARD-FAIL with an 'Insufficient <pool>' error instead of clamping. " +
            "Set spellName when spending spell_slots_* for slot-level validation (call get_spells for names).",
            ["characterId", "poolName", "delta"],
            ["reason", "spellName"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: ["event", "status"],
            Example: """{"$type":"resource","characterId":"chars/wizard-1","poolName":"spell_slots_3","delta":-1,"spellName":"fireball","reason":"Cast Fireball"}"""),

        // ── Lifecycle ────────────────────────────────────────────────────────────────
        new("archive_entity", "World",
            "Soft-delete (or restore) an entity created via world_build — hides it from default search/scene/list " +
            "results without deleting the document. Recoverable: commit again with archived:false to restore. " +
            "Does NOT support Character (the Character model has no archive field) — for a mistakenly-created NPC, " +
            "set keepAlive:false and clear its schedule via character_update instead, so transient GC can clean it up.",
            ["entityType", "entityId"],
            ["archived"],
            HasSideEffects: false,
            SideEffects: [],
            CoCommitHints: [],
            Example: """{"$type":"archive_entity","entityType":"Quest","entityId":"quests/stop-nightshade","archived":true}"""),
    ];
}
