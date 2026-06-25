namespace CampaignVault.Tools;

/// <summary>
/// Copy-paste commit examples shared between the <c>commit</c> tool description and get_help.
/// </summary>
internal static class CommitHelpExamples
{
    internal const string ConversationBatch = """
[
  { "$type": "event", "category": "Conversation", "summary": "Valen asked Lirael about missing caravans on the Gold Road.", "involved": ["chars/valen", "chars/lirael-goldvein"] },
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/lirael-goldvein", "category": "Social", "verb": "discussing the disappearances with", "bidirectional": true },
  { "$type": "activity", "characterId": "chars/valen", "newActivity": "Listening intently at the bar" },
  { "$type": "activity", "characterId": "chars/lirael-goldvein", "newActivity": "Sharing guarded information over the bar" },
  { "$type": "knowledge_update", "characterId": "chars/valen", "topic": "Caravan Disappearances on the Gold Road", "details": "Three caravans vanished without trace near Whispering Pass.", "source": "Heard", "valence": "Negative", "urgency": "High", "importance": "Important" }
]
""";

    internal const string ConversationSection = """
**Conversation (REQUIRED: `involved` with every speaker — NOT `participants`):**
""" + ConversationBatch;
}