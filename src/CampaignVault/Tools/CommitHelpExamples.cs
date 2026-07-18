namespace CampaignVault.Tools;

/// <summary>
/// Copy-paste commit examples shared between the <c>commit</c> tool description and get_help.
/// </summary>
internal static class CommitHelpExamples
{
    internal const string ConversationBatch = """
[
  { "$type": "event", "eventId": "events/valen-lirael-caravans", "category": "Conversation", "summary": "Valen asked Lirael about missing caravans on the Gold Road.", "involved": ["chars/valen", "chars/lirael-goldvein"], "locationId": "locations/rusty-nail" },
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/lirael-goldvein", "category": "Social", "verb": "discussing the disappearances with", "bidirectional": true },
  { "$type": "activity", "characterId": "chars/valen", "newActivity": "Listening intently at the bar" },
  { "$type": "activity", "characterId": "chars/lirael-goldvein", "newActivity": "Sharing guarded information over the bar" },
  { "$type": "knowledge_update", "characterId": "chars/valen", "topic": "Caravan Disappearances on the Gold Road", "details": "Three caravans vanished without trace near Whispering Pass.", "source": "Heard", "valence": "Negative", "urgency": "High", "importance": "Important", "sourceEventIds": ["events/valen-lirael-caravans"] }
]
""";

    internal const string ConversationSection = """
**Conversation (REQUIRED: `involved` with every speaker — NOT `participants`):**
""" + ConversationBatch;

    internal const string PoiMaterializeBatch = """
[
  { "$type": "event", "category": "Discovery", "summary": "Kergil read the notice board near the door.", "involved": ["chars/kergil", "locations/drunken-kraken"] },
  { "$type": "activity", "characterId": "chars/kergil", "newActivity": "Examining the notice board" },
  { "$type": "location_update", "locationId": "locations/drunken-kraken", "materializePointOfInterest": "A notice board with wanted posters and job postings", "poiDetails": "Wanted poster for Grim 'the Hook' Tallow; caravan guard job to Neverwinter; handwritten note about two missing wagons and a Voss contact." },
  { "$type": "knowledge_update", "characterId": "chars/kergil", "topic": "Drunken Kraken notice board", "details": "Specific postings: Grim the Hook (50gp alive), caravan run, missing wagons and Lirael Voss.", "importance": "Important" }
]
""";

    internal const string PoiMaterializeSection = """
**Add / materialize / update / remove Points of Interest (LLM decides when something matters):**
Use `addPointOfInterest`, `materializePointOfInterest`+`poiDetails` (also re-use to *change* state), the map, and `removePointOfInterest`.
Examples: ripping a poster, setting the board on fire, brawl breaking furniture.
After time passes (`advance_world`), update or remove PoI details to reflect cleanup/repair.
""" + PoiMaterializeBatch;

    internal const string EquipBatch = """
[
  { "$type": "event", "category": "Interaction", "summary": "Valen donned chainmail from the armory.", "involved": ["chars/valen"], "locationId": "locations/stronghold-armory" },
  { "$type": "item_equip", "characterId": "chars/valen", "itemId": "items/valen-chainmail", "replaceConflicts": false },
  { "$type": "item_unequip", "characterId": "chars/valen", "itemId": "items/valen-padded-jerkin" },
  { "$type": "item_use", "itemId": "items/ration-waybread", "delta": -1 },
  { "$type": "event", "category": "Interaction", "summary": "Valen ate trail rations to recover strength.", "involved": ["chars/valen"] }
]
""";

    internal const string EquipSection = """
**Equip / Unequip / Use items (outfits, armor AC/warmth, consumables):**
Equip/unequip mid-combat or narrative—AC and WarmthRating recompute immediately.
`replaceConflicts: true` silently removes conflicting equipped items (e.g., swap shield for two-hander).
`item_use` decreases charge/quantity; fires nag if ambient item expires.
""" + EquipBatch;
}