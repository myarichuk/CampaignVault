namespace CampaignVault.Models;

/// <summary>
/// Centralized catalog of all onboarding questions with branching rules.
/// </summary>
public static class OnboardingQuestionCatalog
{
    // Question keys
    public const string CampaignName = "campaign_name";
    public const string System = "system";
    public const string Tone = "tone";
    public const string StartingEra = "starting_era";
    public const string WorldSetting = "world_setting";
    public const string PartyComposition = "party_composition";
    public const string SoloCompanions = "solo_companions";
    public const string PlotSource = "plot_source";
    public const string PlotDirection = "plot_direction";
    public const string SideQuestGeneration = "side_quest_generation";
    public const string Factions = "factions";
    public const string HomebrewWorldDetails = "homebrew_world_details";

    /// <summary>
    /// Get the full question sequence with branching logic.
    /// </summary>
    public static List<OnboardingQuestion> GetQuestionSequence()
    {
        return new List<OnboardingQuestion>
        {
            // Q0: Campaign Name
            new OnboardingQuestion
            {
                Key = CampaignName,
                Text = "What's the name of your campaign?",
                AnswerType = OnboardingAnswerType.Text,
                HelpText = "This will become the campaign slug (e.g., 'Dragon Heist' → 'dragon-heist')."
            },

            // Q1: System
            new OnboardingQuestion
            {
                Key = System,
                Text = "Which game system are you using?",
                AnswerType = OnboardingAnswerType.Enum,
                EnumOptions = new List<string> { "Dnd5e", "Pathfinder2e", "Narrative" },
                HelpText = "This determines mechanics, NPC stat generation, and combat rules. It will be locked and cannot be changed later."
            },

            // Q2: Tone & Themes
            new OnboardingQuestion
            {
                Key = Tone,
                Text = "What's the tone and themes of your campaign? (e.g., 'dark fantasy', 'cozy tavern mysteries', 'space opera')",
                AnswerType = OnboardingAnswerType.Text,
                HelpText = "This steers the LLM's content generation and how important events should feel."
            },

            // Q2b: Starting Era/Year
            new OnboardingQuestion
            {
                Key = StartingEra,
                Text = "What year, era, or calendar date does your campaign begin in? (e.g., '1492 DR', 'the Age of Dragons, year 20', or say 'present day'/'doesn't matter' for a default fantasy start)",
                AnswerType = OnboardingAnswerType.Text,
                HelpText = "Sets the campaign's starting in-world date (epoch name and year). Free text — a leading number is parsed as the starting year; the rest is kept as the epoch label."
            },

            // Q3: World Setting (Solo vs Party, Existing vs Homebrew)
            new OnboardingQuestion
            {
                Key = WorldSetting,
                Text = "Are you running a campaign with: (1) a solo player, (2) a party in an existing world (like Forgotten Realms), or (3) a party in a homebrew world?",
                AnswerType = OnboardingAnswerType.Enum,
                EnumOptions = new List<string> { "solo", "party-existing", "party-homebrew" },
                HelpText = "This determines party composition questions and world-building depth.",
                BranchingRules = new Dictionary<string, OnboardingBranchingRule>
                {
                    {
                        "solo", new OnboardingBranchingRule
                        {
                            TriggerValue = "solo",
                            SkipQuestions = new List<string> { PartyComposition, Factions },
                            JumpToQuestion = SoloCompanions
                        }
                    },
                    {
                        "party-existing", new OnboardingBranchingRule
                        {
                            TriggerValue = "party-existing",
                            SkipQuestions = new List<string> { HomebrewWorldDetails },
                            JumpToQuestion = PartyComposition
                        }
                    },
                    {
                        "party-homebrew", new OnboardingBranchingRule
                        {
                            TriggerValue = "party-homebrew",
                            JumpToQuestion = HomebrewWorldDetails
                        }
                    }
                }
            },

            // Q4: Party Composition (skipped for solo)
            new OnboardingQuestion
            {
                Key = PartyComposition,
                Text = "Describe your party composition (e.g., '2 rogues, 1 cleric, 1 wizard level 5').",
                AnswerType = OnboardingAnswerType.Text,
                HelpText = "Helps with encounter difficulty and NPC interaction design."
            },

            // Q4b: Solo - Offer to generate companions
            new OnboardingQuestion
            {
                Key = SoloCompanions,
                Text = "Would you like the system to generate companion NPCs for the solo player? (yes/no)",
                AnswerType = OnboardingAnswerType.Enum,
                EnumOptions = new List<string> { "yes", "no" },
                HelpText = "If yes, you can choose to see them first (with spoilers) or be surprised."
            },

            // Q5: Plot Source
            new OnboardingQuestion
            {
                Key = PlotSource,
                Text = "Do you have a plot idea in mind, or would you like the system to generate one?",
                AnswerType = OnboardingAnswerType.Enum,
                EnumOptions = new List<string> { "user-provided", "generated-surprise", "generated-with-direction" },
                HelpText = "Choose 'generated-with-direction' if you want to guide the theme (e.g., 'murder mystery').",
                BranchingRules = new Dictionary<string, OnboardingBranchingRule>
                {
                    {
                        "generated-with-direction", new OnboardingBranchingRule
                        {
                            TriggerValue = "generated-with-direction",
                            JumpToQuestion = PlotDirection
                        }
                    }
                }
            },

            // Q5b: Plot Direction (if user wants generated plot with direction)
            new OnboardingQuestion
            {
                Key = PlotDirection,
                Text = "What direction should the plot take? (e.g., 'murder mystery', 'grand adventure', 'traveling scholars discovering ancient ruins')",
                AnswerType = OnboardingAnswerType.Text,
                HelpText = "The system will use this to seed a plot that surprises you."
            },

            // Q6: Side Quests & NPC Stories
            new OnboardingQuestion
            {
                Key = SideQuestGeneration,
                Text = "Should the system pre-generate side quests and NPC stories before session 1, or generate them on-the-fly during play?",
                AnswerType = OnboardingAnswerType.Enum,
                EnumOptions = new List<string> { "pre-generate", "on-the-fly" },
                HelpText = "Pre-generate = faster start, more structure. On-the-fly = more spontaneity."
            },

            // Q7: Homebrew World Details (for party-homebrew only)
            new OnboardingQuestion
            {
                Key = HomebrewWorldDetails,
                Text = "Describe your world's climate, geography, and history. (e.g., 'Temperate forests with mountain kingdoms, established world with 2000-year history, central conflict is a tyranny rising')",
                AnswerType = OnboardingAnswerType.Text,
                HelpText = "Helps ground the world-building in your vision."
            },

            // Q8: Factions (minimal for existing world, extensive for homebrew)
            new OnboardingQuestion
            {
                Key = Factions,
                Text = "What factions or groups should exist in this world? (e.g., 'Thieves' Guild', 'Mage Tower', 'Barbarian Tribes')",
                AnswerType = OnboardingAnswerType.List,
                HelpText = "The system will create plot threads describing what each faction wants and their conflicts."
            }
        };
    }

    /// <summary>
    /// Get the next question based on current state.
    /// Returns null if onboarding is complete.
    /// </summary>
    public static OnboardingQuestion? GetNextQuestion(OnboardingState state)
    {
        var allQuestions = GetQuestionSequence();
        var questionsToAsk = GetQuestionsForPath(state);

        // Find the next unanswered question
        foreach (var question in questionsToAsk)
        {
            if (!state.CollectedAnswers.ContainsKey(question.Key))
            {
                return question;
            }
        }

        // All questions answered
        return null;
    }

    /// <summary>
    /// Determine which questions to ask based on branching path.
    /// </summary>
    public static List<OnboardingQuestion> GetQuestionsForPath(OnboardingState state)
    {
        var allQuestions = GetQuestionSequence();
        var questionsToAsk = new List<OnboardingQuestion>();
        var skipSet = new HashSet<string>(state.SkippedQuestions);

        foreach (var question in allQuestions)
        {
            if (skipSet.Contains(question.Key))
            {
                continue;
            }

            questionsToAsk.Add(question);
        }

        return questionsToAsk;
    }

    /// <summary>
    /// Apply branching rules based on the answer to a question.
    /// Returns a list of question keys to skip.
    /// </summary>
    public static List<string> ApplyBranchingRules(OnboardingState state, string questionKey, object answer)
    {
        var allQuestions = GetQuestionSequence();
        var question = allQuestions.FirstOrDefault(q => q.Key == questionKey);
        if (question?.BranchingRules == null)
        {
            return [];
        }

        var answerStr = answer?.ToString() ?? "";
        if (question.BranchingRules.TryGetValue(answerStr, out var rule))
        {
            return rule.SkipQuestions;
        }

        return [];
    }

    /// <summary>
    /// Validate an answer for a given question.
    /// Returns null if valid, otherwise returns an error message.
    /// </summary>
    public static string? ValidateAnswer(OnboardingQuestion question, object answer)
    {
        var answerStr = answer?.ToString() ?? "";

        if (string.IsNullOrWhiteSpace(answerStr))
        {
            return "Answer cannot be empty.";
        }

        switch (question.AnswerType)
        {
            case OnboardingAnswerType.Enum:
                if (question.EnumOptions != null && !question.EnumOptions.Contains(answerStr))
                {
                    return $"Invalid option. Choose from: {string.Join(", ", question.EnumOptions)}";
                }
                break;

            case OnboardingAnswerType.Text:
                if (answerStr.Length < 3)
                {
                    return "Answer must be at least 3 characters.";
                }
                break;

            case OnboardingAnswerType.List:
                // Basic validation: should not be empty
                if (string.IsNullOrWhiteSpace(answerStr))
                {
                    return "Please provide at least one item.";
                }
                break;
        }

        return null;
    }
}
