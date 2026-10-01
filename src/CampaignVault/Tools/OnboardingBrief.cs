using System.Text;
using CampaignVault.Models;

namespace CampaignVault.Tools;

/// <summary>
/// Turns finalized onboarding answers into the setup brief the DM seeds the world from.
/// finalize_campaign_onboarding returns it and stores it on the campaign meta, and start_session
/// repeats it while the campaign still has no party, so the answers reach the DM even when the
/// client that ran onboarding isn't the one narrating.
/// </summary>
public static class OnboardingBrief
{
    public const string MetadataKey = "onboarding_brief";

    public static string Build(string slug, string displayName, string system, IReadOnlyDictionary<string, object> answers)
    {
        string Answer(string key) => answers.TryGetValue(key, out var v) ? v?.ToString()?.Trim() ?? "" : "";

        var worldSetting = Answer(OnboardingQuestionCatalog.WorldSetting);
        var solo = worldSetting == "solo";
        var pcCreation = Answer(OnboardingQuestionCatalog.PcCreation);
        var roster = SplitList(Answer(OnboardingQuestionCatalog.PcRoster));
        var level = Answer(OnboardingQuestionCatalog.StartingLevel);
        // The party step replaced the three questions above; an onboarding that answered them keeps the old wording.
        OnboardingPartyAnswer? party = null;
        if (pcCreation.Length == 0 && OnboardingPartyAnswer.TryParse(Answer(OnboardingQuestionCatalog.Party), out var parsedParty, out _))
        {
            party = parsedParty;
            pcCreation = party.Mode;
            level = party.Level.ToString();
        }

        // Narrative campaigns skip the level question: no level to state.
        var levelled = !string.Equals(system, "Narrative", StringComparison.OrdinalIgnoreCase);
        if (levelled && level.Length == 0)
        {
            level = "1";
        }

        var plotSource = Answer(OnboardingQuestionCatalog.PlotSource);
        var plotDirection = Answer(OnboardingQuestionCatalog.PlotDirection);
        var openingScene = Answer(OnboardingQuestionCatalog.OpeningScene);
        var factions = SplitList(Answer(OnboardingQuestionCatalog.Factions));

        var sb = new StringBuilder();
        sb.AppendLine($"CAMPAIGN SETUP BRIEF: \"{displayName}\" (campaignName: {slug}, system: {system}). These are the player's onboarding answers. Seed the world from them; don't ask the player to repeat them.");
        sb.AppendLine();
        sb.AppendLine("Answers:");
        Line(sb, "Tone", Answer(OnboardingQuestionCatalog.Tone));
        Line(sb, "Starting era", Answer(OnboardingQuestionCatalog.StartingEra));
        Line(sb, "Table", worldSetting switch
        {
            "solo" => "solo player",
            "party-existing" => "party, existing world",
            "party-homebrew" => "party, homebrew world",
            _ => worldSetting
        });
        Line(sb, "World", Answer(OnboardingQuestionCatalog.HomebrewWorldDetails));
        Line(sb, "Party", Answer(OnboardingQuestionCatalog.PartyComposition));
        Line(sb, "Companions for the solo PC", Answer(OnboardingQuestionCatalog.SoloCompanions));
        Line(sb, "Player characters", pcCreation switch
        {
            OnboardingPartyAnswer.ModeBuildNow => "already built by the player (below)",
            OnboardingPartyAnswer.ModeDmDrafts when party is { HasBuiltCharacters: true } => "drafted by the DM and reviewed by the player; already built (below)",
            OnboardingPartyAnswer.ModeDmDrafts => "the DM pre-generates them for the player's approval",
            OnboardingPartyAnswer.ModeBuildAtTable => "built with the player at the table, step by step",
            OnboardingQuestionCatalog.PcCreationDescribeNow => "described by the player (below)",
            OnboardingQuestionCatalog.PcCreationDmPregenerates => "the DM pre-generates them for the player's approval",
            _ => pcCreation
        });
        foreach (var pc in roster)
        {
            sb.AppendLine($"  - {pc}");
        }

        var built = party is { HasBuiltCharacters: true };
        if (built)
        {
            foreach (var id in party!.CharacterIds)
            {
                sb.AppendLine($"  - already built: {id}");
            }

            if (party.CompanionIds.Count > 0)
            {
                Line(sb, "Companions (already built)", string.Join(", ", party.CompanionIds));
            }
        }

        if (levelled)
        {
            Line(sb, "Starting level", level);
        }

        Line(sb, "Plot", plotSource switch
        {
            "user-provided" => $"the player's idea: {plotDirection}",
            "generated-with-direction" => $"DM-generated, steered toward: {plotDirection}",
            "generated-surprise" => "DM-generated, keep it a surprise from the player",
            _ => plotDirection
        });
        Line(sb, "Opening scene", openingScene);
        Line(sb, "Side quests", Answer(OnboardingQuestionCatalog.SideQuestGeneration));
        Line(sb, "Factions", string.Join("; ", factions));

        sb.AppendLine();
        sb.AppendLine("Do, in order:");
        var step = 1;
        var pcWord = solo ? "the player character" : "the player characters";
        var statLine = $"isPc=true, {(levelled ? $"level {level}, " : "")}full systemStats for the {system} ruleset, and their starting gear as items[] with holderId set (same batch)";
        switch (pcCreation)
        {
            case OnboardingPartyAnswer.ModeBuildNow:
            case OnboardingPartyAnswer.ModeDmDrafts when built:
                sb.AppendLine($"{step++}. {(solo ? "The player character is" : "The player characters are")} already built (ids above). Do NOT world_build {(solo ? "it" : "them")}; they exist with full stats and gear. Place {(solo ? "it" : "them")} at the opening location.");
                break;
            case OnboardingQuestionCatalog.PcCreationDmPregenerates:
            case OnboardingPartyAnswer.ModeDmDrafts:
                sb.AppendLine($"{step++}. Invent {pcWord} to fit the tone{(solo ? "" : " and the party answer")}. Show the player each one (name, ancestry, class, a one-line hook) and let them swap or tweak before you world_build them with {statLine}.");
                break;
            case OnboardingPartyAnswer.ModeBuildAtTable: // same value as the old build-at-table option
                sb.AppendLine($"{step++}. Before any scene, walk the player through creating {pcWord}: one decision per message (ancestry, class, background, ability scores, gear, name), each time offering a short numbered list of options from lookup kind=handbook. Then world_build them with {statLine}.");
                break;
            default:
                sb.AppendLine($"{step++}. world_build one character per player-character line above, with {statLine}. Keep the player's names and concepts; fill anything they left out to fit.");
                break;
        }

        if (solo && Answer(OnboardingQuestionCatalog.SoloCompanions) == "yes")
        {
            sb.AppendLine($"{step++}. world_build 1–2 companion NPCs with isPartyCompanion=true and full systemStats, each with a reason to travel with the player character.");
        }

        var opening = openingScene.Length == 0 || openingScene.Contains("surprise", StringComparison.OrdinalIgnoreCase)
            ? "an opening location of your choice that fits the tone"
            : "the opening location from the opening-scene answer";
        sb.AppendLine($"{step++}. world_build {opening}, the people there, {(factions.Count > 0 ? "each listed faction with a plot thread for what it wants" : "one or two factions")}, and the quests and plot threads the plot answer implies.");
        sb.AppendLine(Answer(OnboardingQuestionCatalog.SideQuestGeneration) == "pre-generate"
            ? $"{step++}. Also seed 2–3 side quests and NPC stories now."
            : $"{step++}. Keep side content light; improvise it in play.");
        sb.AppendLine($"{step++}. Set every player character's currentLocationId to the opening location.");
        sb.Append($"{step}. Call start_session, then narrate the opening scene.");
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.AppendLine($"- {label}: {value}");
        }
    }

    /// <summary>List answers arrive as "a; b; c" or one entry per line.</summary>
    private static List<string> SplitList(string answer) =>
        [.. answer.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
