using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Builds the copy-paste "suggested commit" JSON snippets surfaced alongside world/scene state.
/// Shared by BuildWorldStateAsync and GetScene so the two views never drift in what they suggest.
/// </summary>
internal static class SuggestedCommitExampleBuilder
{
    public static List<string> Build(
        IReadOnlyList<WorldPressureItem> pressureItems,
        string? firstActiveQuestId,
        string? stuckCharacterId)
    {
        var suggestedExamples = new List<string>();

        var questPressureTriggered = pressureItems.Any(p =>
            p.Text.Contains("Quest", StringComparison.OrdinalIgnoreCase) ||
            p.Text.Contains("deadline", StringComparison.OrdinalIgnoreCase));
        if (questPressureTriggered && !string.IsNullOrEmpty(firstActiveQuestId))
        {
            suggestedExamples.Add(
                $"[ {{ \"$type\": \"quest_progress\", \"questId\": \"{firstActiveQuestId}\", \"objectiveIndex\": 0, \"newState\": \"Complete\", \"narrativeNote\": \"We completed the objective.\" }} ]");
        }

        var travelPressureTriggered = pressureItems.Any(p =>
            p.Text.Contains("Travel", StringComparison.OrdinalIgnoreCase) ||
            p.Text.Contains("stuck", StringComparison.OrdinalIgnoreCase) ||
            p.Text.Contains("interrupted", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(stuckCharacterId) && travelPressureTriggered)
        {
            suggestedExamples.Add(
                $"[ {{ \"$type\": \"activity\", \"characterId\": \"{stuckCharacterId}\", \"newActivity\": \"Resolved the ambush and continued\", \"updateLocation\": false }}, {{ \"$type\": \"travel\", \"characterId\": \"{stuckCharacterId}\", \"destinationLocationId\": \"locations/actual-dest\", \"encounterRiskModifier\": -30 }} ]");
        }

        suggestedExamples.AddRange(
            pressureItems
                .Where(p => !string.IsNullOrWhiteSpace(p.SuggestedCommitJson))
                .Select(p => p.SuggestedCommitJson!)
                .Distinct());

        return suggestedExamples;
    }
}
