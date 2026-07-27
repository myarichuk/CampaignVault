using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// Tracks the state of an in-progress campaign onboarding session.
/// Persisted to campaigns/{slug}/state/onboarding and deleted after finalization.
/// </summary>
public class OnboardingState
{
    /// <summary>
    /// RavenDB document ID (e.g., "campaigns/dragon-heist/state/onboarding").
    /// </summary>
    public string Id { get; set; } = null!;

    /// <summary>
    /// Campaign slug this onboarding is for.
    /// </summary>
    public string CampaignSlug { get; set; } = null!;

    /// <summary>
    /// Current question index (0 = campaign name, 1 = system, 2 = tone, etc.).
    /// Advances as answers are submitted.
    /// </summary>
    public int CurrentQuestionIndex { get; set; }

    /// <summary>
    /// Mapping of question keys to user answers.
    /// Examples: { "campaignName": "Dragon Heist", "system": "Dnd5e", "tone": "urban fantasy" }
    /// </summary>
    public Dictionary<string, object> CollectedAnswers { get; set; } = [];

    /// <summary>
    /// Branching path taken (e.g., "solo-homebrew", "party-existing", "party-homebrew").
    /// Used to determine which follow-up questions to ask.
    /// </summary>
    public string? BranchingPath { get; set; }

    /// <summary>
    /// List of question keys that were skipped due to branching logic.
    /// Useful for debugging and understanding the flow.
    /// </summary>
    public List<string> SkippedQuestions { get; set; } = [];

    /// <summary>
    /// The next question to ask (formatted question text).
    /// None if onboarding is complete.
    /// </summary>
    public OnboardingQuestion? NextQuestion { get; set; }

    /// <summary>
    /// True when all branching paths have been answered.
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Timestamp when onboarding was started.
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when onboarding was last updated.
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional flags for world-building preferences.
    /// Examples: { "preGenerateQuests": true, "companionStyle": "with-spoilers" }
    /// </summary>
    public Dictionary<string, string> WorldBuildingFlags { get; set; } = [];
}

/// <summary>
/// A single question in the onboarding questionnaire.
/// </summary>
public class OnboardingQuestion
{
    /// <summary>
    /// Unique question identifier (e.g., "campaign_name", "world_setting", "party_composition").
    /// Used as the key in CollectedAnswers.
    /// </summary>
    public string Key { get; set; } = null!;

    /// <summary>
    /// Question text to display to the user.
    /// </summary>
    public string Text { get; set; } = null!;

    /// <summary>
    /// Type of answer expected (text, enum, boolean, etc.).
    /// </summary>
    public OnboardingAnswerType AnswerType { get; set; }

    /// <summary>
    /// If AnswerType is Enum, list of valid options.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? EnumOptions { get; set; }

    /// <summary>
    /// Optional help text or context for the question.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HelpText { get; set; }

    /// <summary>
    /// If true, this question should be skipped based on prior answers.
    /// Determined by branching rules.
    /// </summary>
    public bool ShouldSkip { get; set; }

    /// <summary>
    /// Optional branching rules: "if answer to question X is Y, then skip questions [Z1, Z2]".
    /// Stored as JSON for flexibility.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, OnboardingBranchingRule>? BranchingRules { get; set; }
}

public enum OnboardingAnswerType
{
    Text,
    Enum,
    Boolean,
    List
}

/// <summary>
/// Branching rule for conditional question skipping/routing.
/// </summary>
public class OnboardingBranchingRule
{
    /// <summary>
    /// Answer value that triggers this rule.
    /// </summary>
    public string TriggerValue { get; set; } = null!;

    /// <summary>
    /// List of question keys to skip if this rule is triggered.
    /// </summary>
    public List<string> SkipQuestions { get; set; } = [];

    /// <summary>
    /// Optional next question key to jump to (if null, continue sequentially).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JumpToQuestion { get; set; }
}
