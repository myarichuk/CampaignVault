namespace CampaignVault.Tools;

/// <summary>
/// The last text a connector model reads before it writes the scene (NARRATION_LENGTH_PLAN.md, L4). Everything else in a
/// committed take_turn result is a clipped log entry, and a model tends to answer in the register it just read; this line sits
/// last, in the voice and at the length the scene should have. A client with its own storyteller ignores it.
/// </summary>
internal static class SceneCue
{
    public const string Text =
        "Now write the scene: 350 to 600 words of full sentences, shown through the senses, bodies and speech. The fields above are bookkeeping, not the voice.";
}
