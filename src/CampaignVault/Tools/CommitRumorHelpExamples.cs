namespace CampaignVault.Tools;

/// <summary>
/// Copy-paste rumor commit examples for get_help and LLM documentation.
/// </summary>
internal static class CommitRumorHelpExamples
{
    internal const string RoutingGuide = """
**Rumors:**
- To create a new rumor, use the `upsert_rumor` tool: `id`, `regionLocationId`, `subject`, `text` (state starts Nascent).
- `rumor` — evolve an EXISTING rumor via commit: `rumorId`, `newState` (Nascent|Spreading|Peak|Fading|Resolved|Forgotten), optional `newText`.
Do NOT use `subject` + `newState: Active` on `$type: rumor` — that fails. Put the source NPC in a separate `event.involved`.
""";

    internal const string RumorEvolve = """
{ "$type": "rumor", "rumorId": "rumors/nightshade-gang", "newState": "Resolved",
  "newText": "The Nightshade pirates were smashed by adventurers at their own hideout. The river may be safe again." }
""";
}
