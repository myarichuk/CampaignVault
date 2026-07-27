using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class OnboardingTools(
    CampaignRepository repository,
    CampaignDocumentKeys keys,
    ILogger<OnboardingTools>? logger = null)
    : CampaignToolBase(repository, keys, logger), IMcpServerTool
{
    [ToolCategory("Campaign onboarding")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"ONBOARDING TOOL: Start a structured campaign onboarding session. Returns the first question to ask the user.
This prevents hallucination by collecting user preferences through guided questions before auto-generating the campaign world.

Example: start_campaign_onboarding('dragon-heist')")]
    public Task<ToolResult<OnboardingStartResponse>> StartCampaignOnboarding(
        [Description(ToolParameterDescriptions.CampaignSlugRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            // Check if onboarding already in progress
            var existingState = await _repository.GetOnboardingStateAsync(session, effective);
            if (existingState != null)
            {
                return new ToolResult<OnboardingStartResponse>(
                    true,
                    new OnboardingStartResponse
                    {
                        State = existingState,
                        CurrentQuestion = existingState.NextQuestion,
                        Summary = $"Resuming onboarding for '{effective}'. Current progress: {existingState.CurrentQuestionIndex}/{OnboardingQuestionCatalog.GetQuestionSequence().Count} questions answered."
                    },
                    $"Onboarding for '{effective}' resumed.");
            }

            // Create new onboarding state
            var state = new OnboardingState
            {
                CampaignSlug = effective,
                CurrentQuestionIndex = 0,
                IsComplete = false,
                StartedAt = DateTime.UtcNow
            };

            // Get first question
            var firstQuestion = OnboardingQuestionCatalog.GetNextQuestion(state);
            if (firstQuestion == null)
            {
                return new ToolResult<OnboardingStartResponse>(
                    false,
                    Error: "NoQuestionsAvailable",
                    Summary: "No onboarding questions available.");
            }

            state.NextQuestion = firstQuestion;

            // Save state
            await _repository.UpsertOnboardingStateAsync(session, state, effective);

            return new ToolResult<OnboardingStartResponse>(
                true,
                new OnboardingStartResponse
                {
                    State = state,
                    CurrentQuestion = firstQuestion,
                    Summary = "Onboarding session started. Please answer the first question."
                },
                $"Onboarding started for '{effective}'.");
        });
    }

    [ToolCategory("Campaign onboarding")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"ONBOARDING TOOL: Submit an answer to the current onboarding question.
Returns the next question to ask, or 'ready_to_build' if onboarding is complete.

Example: submit_onboarding_answer('dragon-heist', 'Dnd5e')")]
    public Task<ToolResult<OnboardingAnswerResponse>> SubmitOnboardingAnswer(
        [Description(ToolParameterDescriptions.CampaignSlugRequired)]
        string campaignName,
        [Description("The user's answer to the current question.")]
        string answer)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            // Load current onboarding state
            var state = await _repository.GetOnboardingStateAsync(session, effective);
            if (state == null)
            {
                return new ToolResult<OnboardingAnswerResponse>(
                    false,
                    Error: "OnboardingNotStarted",
                    Summary: "No onboarding session in progress. Call start_campaign_onboarding first.");
            }

            if (state.IsComplete)
            {
                return new ToolResult<OnboardingAnswerResponse>(
                    false,
                    Error: "OnboardingAlreadyComplete",
                    Summary: "Onboarding is already complete. Call finalize_campaign_onboarding to build the world.");
            }

            // Get the current question
            if (state.NextQuestion == null)
            {
                return new ToolResult<OnboardingAnswerResponse>(
                    false,
                    Error: "NoCurrentQuestion",
                    Summary: "No current question found. State may be corrupted.");
            }

            // Validate answer
            var validationError = OnboardingQuestionCatalog.ValidateAnswer(state.NextQuestion, answer);
            if (validationError != null)
            {
                return new ToolResult<OnboardingAnswerResponse>(
                    false,
                    Error: "InvalidAnswer",
                    Summary: validationError);
            }

            // Record the answer
            state.CollectedAnswers[state.NextQuestion.Key] = answer;

            // Apply branching rules
            var questionsToSkip = OnboardingQuestionCatalog.ApplyBranchingRules(state, state.NextQuestion.Key, answer);
            if (questionsToSkip.Count > 0)
            {
                state.SkippedQuestions.AddRange(questionsToSkip);
                state.BranchingPath = DetermineBranchingPath(state);
                var flags = ExtractWorldBuildingFlags(state);
                foreach (var kvp in flags)
                {
                    state.WorldBuildingFlags[kvp.Key] = kvp.Value;
                }
            }

            // Get next question
            var nextQuestion = OnboardingQuestionCatalog.GetNextQuestion(state);
            if (nextQuestion == null)
            {
                // Onboarding complete
                state.IsComplete = true;
                state.NextQuestion = null;
            }
            else
            {
                state.NextQuestion = nextQuestion;
                state.CurrentQuestionIndex++;
            }

            // Save updated state
            await _repository.UpsertOnboardingStateAsync(session, state, effective);

            return new ToolResult<OnboardingAnswerResponse>(
                true,
                new OnboardingAnswerResponse
                {
                    State = state,
                    CurrentQuestion = nextQuestion,
                    IsReadyToBuild = state.IsComplete,
                    Summary = state.IsComplete
                        ? "Onboarding complete! Call finalize_campaign_onboarding to build the world."
                        : $"Answer recorded. {OnboardingQuestionCatalog.GetQuestionSequence().Count - state.CurrentQuestionIndex} questions remaining."
                },
                state.IsComplete ? "Onboarding complete." : "Answer recorded.");
        });
    }

    [ToolCategory("Campaign onboarding")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"ONBOARDING TOOL: Finalize onboarding and auto-generate the campaign world.
Creates the campaign with the collected settings, then calls world_build to seed all starter entities (locations, NPCs, factions, quests, etc.).
Returns the world state ready for session 1.

Example: finalize_campaign_onboarding('dragon-heist')")]
    public Task<ToolResult<OnboardingFinalizeResponse>> FinalizeCampaignOnboarding(
        [Description(ToolParameterDescriptions.CampaignSlugRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            // Load current onboarding state
            var state = await _repository.GetOnboardingStateAsync(session, effective);
            if (state == null)
            {
                return new ToolResult<OnboardingFinalizeResponse>(
                    false,
                    Error: "OnboardingNotStarted",
                    Summary: "No onboarding session in progress.");
            }

            if (!state.IsComplete)
            {
                return new ToolResult<OnboardingFinalizeResponse>(
                    false,
                    Error: "OnboardingNotComplete",
                    Summary: "Onboarding is not yet complete. Please answer all remaining questions first.");
            }

            // Extract campaign settings from collected answers
            var campaignNameFromAnswer = state.CollectedAnswers.TryGetValue(OnboardingQuestionCatalog.CampaignName, out var nameObj)
                ? nameObj?.ToString() ?? effective
                : effective;

            var systemStr = state.CollectedAnswers.TryGetValue(OnboardingQuestionCatalog.System, out var sysObj)
                ? sysObj?.ToString() ?? "Dnd5e"
                : "Dnd5e";

            var toneStr = state.CollectedAnswers.TryGetValue(OnboardingQuestionCatalog.Tone, out var toneObj)
                ? toneObj?.ToString() ?? ""
                : "";

            var worldSetting = state.CollectedAnswers.TryGetValue(OnboardingQuestionCatalog.WorldSetting, out var wsObj)
                ? wsObj?.ToString() ?? "party-homebrew"
                : "party-homebrew";

            var factions = state.CollectedAnswers.TryGetValue(OnboardingQuestionCatalog.Factions, out var factionsObj)
                ? factionsObj?.ToString() ?? ""
                : "";

            // Parse system enum
            if (!Enum.TryParse<RulesetSystem>(systemStr, true, out var system))
            {
                system = RulesetSystem.Dnd5e;
            }

            // Build narrative focus from collected answers
            var narrativeFocus = new List<string>();
            if (!string.IsNullOrWhiteSpace(toneStr))
            {
                narrativeFocus.Add(toneStr);
            }

            // Create the campaign with collected settings
            var campaign = await GetOrCreateCampaignMetaAsync(session, effective, system, campaignNameFromAnswer, forceLock: true);
            campaign.NarrativeFocus = narrativeFocus;
            campaign.Metadata["onboarding_completed"] = DateTime.UtcNow.ToString("O");
            await session.StoreAsync(campaign);

            // Also save the world building preferences to campaign metadata for world_build to reference
            foreach (var flag in state.WorldBuildingFlags)
            {
                campaign.Metadata[$"onboarding_{flag.Key}"] = flag.Value;
            }

            // Save collected answers for reference
            campaign.Metadata["onboarding_answers"] = System.Text.Json.JsonSerializer.Serialize(state.CollectedAnswers);

            // Delete the onboarding state (it's no longer needed)
            await _repository.DeleteOnboardingStateAsync(session, effective);

            return new ToolResult<OnboardingFinalizeResponse>(
                true,
                new OnboardingFinalizeResponse
                {
                    CampaignCreated = true,
                    CampaignName = campaignNameFromAnswer,
                    System = system,
                    NarrativeFocus = narrativeFocus,
                    CollectedAnswers = state.CollectedAnswers,
                    WorldBuildingFlags = state.WorldBuildingFlags,
                    NextSteps = new List<string>
                    {
                        "Campaign meta created and system locked.",
                        "Ready for world_build to seed starter entities (locations, NPCs, factions, quests, plot threads).",
                        "After world seeding, start_session can be called to begin session 1."
                    },
                    Summary = $"Onboarding finalized for '{campaignNameFromAnswer}'. Use world_build to populate the world with starter entities."
                },
                $"Onboarding finalized for '{campaignNameFromAnswer}'.");
        });
    }

    private static string DetermineBranchingPath(OnboardingState state)
    {
        // Determine branching path based on answers
        var partType = state.CollectedAnswers.ContainsKey(OnboardingQuestionCatalog.WorldSetting)
            ? state.CollectedAnswers[OnboardingQuestionCatalog.WorldSetting]?.ToString() ?? "party-homebrew"
            : "party-homebrew";

        return partType;
    }

    private static Dictionary<string, string> ExtractWorldBuildingFlags(OnboardingState state)
    {
        var flags = new Dictionary<string, string>();

        // Extract side quest generation preference
        if (state.CollectedAnswers.TryGetValue(OnboardingQuestionCatalog.SideQuestGeneration, out var sideQuestObj))
        {
            flags["preGenerateQuests"] = sideQuestObj?.ToString() == "pre-generate" ? "true" : "false";
        }

        // Extract solo companions preference
        if (state.CollectedAnswers.TryGetValue(OnboardingQuestionCatalog.SoloCompanions, out var companionsObj))
        {
            flags["generateCompanions"] = companionsObj?.ToString() == "yes" ? "true" : "false";
        }

        // Extract plot source
        if (state.CollectedAnswers.TryGetValue(OnboardingQuestionCatalog.PlotSource, out var plotObj))
        {
            flags["plotSource"] = plotObj?.ToString() ?? "user-provided";
        }

        return flags;
    }
}

/// <summary>
/// Response from start_campaign_onboarding.
/// </summary>
public class OnboardingStartResponse
{
    public OnboardingState State { get; set; } = null!;
    public OnboardingQuestion? CurrentQuestion { get; set; }
    public string Summary { get; set; } = null!;
}

/// <summary>
/// Response from submit_onboarding_answer.
/// </summary>
public class OnboardingAnswerResponse
{
    public OnboardingState State { get; set; } = null!;
    public OnboardingQuestion? CurrentQuestion { get; set; }
    public bool IsReadyToBuild { get; set; }
    public string Summary { get; set; } = null!;
}

/// <summary>
/// Response from finalize_campaign_onboarding.
/// </summary>
public class OnboardingFinalizeResponse
{
    public bool CampaignCreated { get; set; }
    public string CampaignName { get; set; } = null!;
    public RulesetSystem System { get; set; }
    public List<string> NarrativeFocus { get; set; } = [];
    public Dictionary<string, object> CollectedAnswers { get; set; } = [];
    public Dictionary<string, string> WorldBuildingFlags { get; set; } = [];
    public List<string> NextSteps { get; set; } = [];
    public string Summary { get; set; } = null!;
}
