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
    public const string Party = "party";
    // Replaced by Party. Kept so onboardings that already answered them keep their answers and wording.
    public const string PcCreation = "pc_creation";
    public const string PcRoster = "pc_roster";
    public const string StartingLevel = "starting_level";
    public const string OpeningScene = "opening_scene";

    // pc_creation options
    public const string PcCreationDescribeNow = "describe-now";
    public const string PcCreationDmPregenerates = "dm-pregenerates";
    public const string PcCreationBuildAtTable = "build-at-table";

    /// <summary>
    /// Get the full question sequence with branching logic.
    /// </summary>
    public static List<OnboardingQuestion> GetQuestionSequence()
    {
        return
        [

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
                EnumOptions = ["Dnd5e", "Pathfinder2e", "Narrative"],
                HelpText =
                    "This determines mechanics, NPC stat generation, and combat rules. It will be locked and cannot be changed later.",
                BranchingRules = new Dictionary<string, OnboardingBranchingRule>
                {
                    {
                        // Narrative play has no character levels.
                        "Narrative", new OnboardingBranchingRule
                        {
                            TriggerValue = "Narrative",
                            SkipQuestions = [StartingLevel]
                        }
                    }
                }
            },

            // Q2: Tone & Themes

            new OnboardingQuestion
            {
                Key = Tone,
                Text =
                    "What's the tone and themes of your campaign? (e.g., 'dark fantasy', 'cozy tavern mysteries', 'space opera')",
                AnswerType = OnboardingAnswerType.Text,
                HelpText = "This steers the LLM's content generation and how important events should feel."
            },

            // Q2b: Starting Era/Year

            new OnboardingQuestion
            {
                Key = StartingEra,
                Text =
                    "What year, era, or calendar date does your campaign begin in? (e.g., '1492 DR', 'the Age of Dragons, year 20', or say 'present day'/'doesn't matter' for a default fantasy start)",
                AnswerType = OnboardingAnswerType.Text,
                HelpText =
                    "Sets the campaign's starting in-world date (epoch name and year). Free text — a leading number is parsed as the starting year; the rest is kept as the epoch label."
            },

            // Q3: Solo vs party, existing vs homebrew world

            new OnboardingQuestion
            {
                Key = WorldSetting,
                Text =
                    "Are you running a campaign with: (1) a solo player, (2) a party in a published setting you already know, or (3) a party in a homebrew world?",
                AnswerType = OnboardingAnswerType.Enum,
                EnumOptions = ["solo", "party-existing", "party-homebrew"],
                HelpText = "This determines party composition questions and world-building depth.",
                BranchingRules = new Dictionary<string, OnboardingBranchingRule>
                {
                    {
                        "solo", new OnboardingBranchingRule
                        {
                            TriggerValue = "solo",
                            SkipQuestions = [PartyComposition]
                        }
                    },
                    {
                        "party-existing", new OnboardingBranchingRule
                        {
                            TriggerValue = "party-existing",
                            SkipQuestions = [SoloCompanions]
                        }
                    },
                    {
                        "party-homebrew", new OnboardingBranchingRule
                        {
                            TriggerValue = "party-homebrew",
                            SkipQuestions = [SoloCompanions]
                        }
                    }
                }
            },

            // Q4: The world itself (every path — an existing setting still needs a name and region)

            new OnboardingQuestion
            {
                Key = HomebrewWorldDetails,
                Text =
                    "Describe the world. For an existing setting, name it and the region you start in (e.g. the setting of a campaign book you own, and the coast or city where play begins); for a homebrew world, give its climate, geography and history (e.g. 'Temperate forests with mountain kingdoms, 2000 years of history'). The plot is the next question.",
                AnswerType = OnboardingAnswerType.Text,
                HelpText = "Grounds the world-building in your vision."
            },

            // Q5: Plot source, right after the world: players pour plot into the world answer otherwise,
            // and meeting "do you have a plot idea?" four questions later reads like being asked twice.

            new OnboardingQuestion
            {
                Key = PlotSource,
                Text = "Do you have a plot idea in mind, or would you like the system to generate one?",
                AnswerType = OnboardingAnswerType.Enum,
                EnumOptions = ["user-provided", "generated-surprise", "generated-with-direction"],
                HelpText = "Choose 'generated-with-direction' if you want to guide the theme (e.g., 'murder mystery').",
                BranchingRules = new Dictionary<string, OnboardingBranchingRule>
                {
                    {
                        "generated-surprise", new OnboardingBranchingRule
                        {
                            TriggerValue = "generated-surprise",
                            SkipQuestions = [PlotDirection]
                        }
                    }
                }
            },

            // Q5b: The plot idea or its direction (skipped for a surprise plot)

            new OnboardingQuestion
            {
                Key = PlotDirection,
                Text =
                    "Describe your plot idea, or the direction it should take (e.g. 'murder mystery', 'grand adventure', 'traveling scholars discovering ancient ruins').",
                AnswerType = OnboardingAnswerType.Text,
                HelpText = "The DM seeds the opening quests and plot threads from this."
            },

            // Q6: Party composition (party only)

            new OnboardingQuestion
            {
                Key = PartyComposition,
                Text = "How many player characters are in the party, and which roles should it cover? (e.g. 'four players — we need a healer', or '2 rogues, 1 cleric, 1 wizard')",
                AnswerType = OnboardingAnswerType.Text,
                HelpText = "Helps with encounter difficulty and with generating characters if you let the DM make them."
            },

            // Q7: The party: who the player characters are and how they get built (the client opens the
            // character builder from here). Companions fold into the same answer.

            new OnboardingQuestion
            {
                Key = Party,
                Text = "Who is in the party? Build the characters now, let the DM draft them for you to review, or build them at the table.",
                AnswerType = OnboardingAnswerType.Party,
                HelpText =
                    "Answer is JSON: { \"mode\": \"build-now|dm-drafts|build-at-table\", \"level\": 1, \"characterIds\": [], \"companionIds\": [] }. 'build-now': the characters were built in the character builder (ids listed). 'dm-drafts': the DM drafts them, the player reviews each in the builder. 'build-at-table': the DM walks the player through creation before the first scene. The level is the starting level (ignored for Narrative)."
            },

            // Q9: Opening scene

            new OnboardingQuestion
            {
                Key = OpeningScene,
                Text =
                    "Where and how does the first session open? (e.g. 'a rain-soaked caravan stop on the Trade Way, just after an ambush', or 'surprise me')",
                AnswerType = OnboardingAnswerType.Text,
                HelpText = "The DM seeds this place first and puts the player characters in it."
            },

            // Q10: Side Quests & NPC Stories

            new OnboardingQuestion
            {
                Key = SideQuestGeneration,
                Text =
                    "Should the system pre-generate side quests and NPC stories before session 1, or generate them on-the-fly during play?",
                AnswerType = OnboardingAnswerType.Enum,
                EnumOptions = ["pre-generate", "on-the-fly"],
                HelpText = "Pre-generate = faster start, more structure. On-the-fly = more spontaneity."
            },

            // Q11: Factions

            new OnboardingQuestion
            {
                Key = Factions,
                Text =
                    "What factions or groups should exist in this world? (e.g., 'Thieves' Guild', 'Mage Tower', 'Barbarian Tribes')",
                AnswerType = OnboardingAnswerType.List,
                HelpText = "The system will create plot threads describing what each faction wants and their conflicts."
            }
        ];
    }

    /// <summary>
    /// The three questions the party step replaced. Only an onboarding that already answered
    /// <see cref="PcCreation"/> still walks them (it finishes with the wording it started with).
    /// </summary>
    private static List<OnboardingQuestion> GetLegacyPcQuestions()
    {
        return
        [
            new OnboardingQuestion
            {
                Key = PcCreation,
                Text = "How should the player characters be made?",
                AnswerType = OnboardingAnswerType.Enum,
                EnumOptions = [PcCreationDescribeNow, PcCreationDmPregenerates, PcCreationBuildAtTable],
                HelpText =
                    "'describe-now': you describe each character next. 'dm-pregenerates': the DM invents characters that fit and shows them to you before play. 'build-at-table': the DM walks you through character creation, one choice at a time, before the first scene.",
                BranchingRules = new Dictionary<string, OnboardingBranchingRule>
                {
                    {
                        PcCreationDmPregenerates, new OnboardingBranchingRule
                        {
                            TriggerValue = PcCreationDmPregenerates,
                            SkipQuestions = [PcRoster]
                        }
                    },
                    {
                        PcCreationBuildAtTable, new OnboardingBranchingRule
                        {
                            TriggerValue = PcCreationBuildAtTable,
                            SkipQuestions = [PcRoster]
                        }
                    }
                }
            },
            new OnboardingQuestion
            {
                Key = PcRoster,
                Text =
                    "Describe each player character, one per line: Name — ancestry and class, plus a line of concept (e.g. 'Lyra — elf ranger, exiled scout hunting her brother's killer').",
                AnswerType = OnboardingAnswerType.List,
                HelpText = "The DM builds full character sheets from these. Anything you leave out, the DM fills in to fit."
            },
            new OnboardingQuestion
            {
                Key = StartingLevel,
                Text = "What level do the player characters start at? (1–20)",
                AnswerType = OnboardingAnswerType.Number,
                MinValue = 1,
                MaxValue = 20,
                HelpText = "Level 1 is a fresh start; 3 gives everyone their subclass."
            }
        ];
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
        // An onboarding that already answered the old pc_creation question finishes on the old questions.
        var legacy = state.CollectedAnswers.ContainsKey(PcCreation);

        foreach (var question in allQuestions)
        {
            if (skipSet.Contains(question.Key))
            {
                continue;
            }

            if (question.Key == Party && legacy)
            {
                questionsToAsk.AddRange(GetLegacyPcQuestions().Where(q => !skipSet.Contains(q.Key)));
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
        var question = GetQuestionSequence().Concat(GetLegacyPcQuestions()).FirstOrDefault(q => q.Key == questionKey);
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

            case OnboardingAnswerType.Party:
                if (!OnboardingPartyAnswer.TryParse(answerStr, out _, out var partyError))
                {
                    return partyError;
                }
                break;

            case OnboardingAnswerType.Number:
                if (!int.TryParse(answerStr.Trim(), out var number))
                {
                    return "Answer must be a whole number.";
                }
                if ((question.MinValue.HasValue && number < question.MinValue.Value)
                    || (question.MaxValue.HasValue && number > question.MaxValue.Value))
                {
                    return $"Answer must be between {question.MinValue} and {question.MaxValue}.";
                }
                break;
        }

        return null;
    }
}
