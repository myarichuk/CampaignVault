namespace CampaignVault.Tools;

/// <summary>
/// Copy-paste rumor and quest commit examples for get_help and LLM documentation.
/// </summary>
internal static class CommitRumorHelpExamples
{
    internal const string RoutingGuide = """
**Rumors — two $types:**
- `rumor_create` — seed a new rumor: `rumorId`, `subject`, `text` (state starts Nascent).
- `rumor` — evolve existing rumor: `rumorId`, `newState` (Nascent|Spreading|Peak|Fading|Resolved|Forgotten), optional `newText`.
Do NOT use `subject` + `newState: Active` on `$type: rumor` — that fails. Put the source NPC in a separate `event.involved`.
""";

    internal const string RumorCreate = """
{ "$type": "rumor_create", "rumorId": "rumors/nightshade-gang", "subject": "Nightshade Gang",
  "text": "Nightshade pirates have raided three barges on the Ashford River this month — cargo vanishing, crews turning up dead." }
""";

    internal const string RumorEvolve = """
{ "$type": "rumor", "rumorId": "rumors/nightshade-gang", "newState": "Resolved",
  "newText": "The Nightshade pirates were smashed by adventurers at their own hideout. The river may be safe again." }
""";

    internal const string QuestCreateHook = """
{ "$type": "quest_create", "questId": "quests/stop-nightshade", "title": "Cut Out the Nightshade",
  "giverId": "chars/bram-the-barkeep", "dmNotes": "River merchants desperate; disrupt Nightshade operations on the Ashford.",
  "objectives": [
    { "description": "Locate the Nightshade hideout" },
    { "description": "Destroy or scatter the gang" },
    { "description": "Report back to the River Merchants' Guild" }
  ],
  "deadlineDay": 14 }
""";
}