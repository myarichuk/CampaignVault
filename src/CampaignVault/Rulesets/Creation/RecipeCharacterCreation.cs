using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Creation;

/// <summary>
/// The data-driven <see cref="ICharacterCreation"/>: runs a system's recipe (<c>creation/&lt;kind&gt;.yaml</c>). A system
/// without one gets a recipe with only the identity step, so the builder works for every system (Narrative included).
/// </summary>
public sealed class RecipeCharacterCreation(
    string system,
    CreationRecipeProvider recipes,
    CreationSources sources,
    IReadOnlyDictionary<string, IRecipeValidator> validators) : ICharacterCreation
{
    public const string PcKind = "pc";
    public const string CompanionKind = "companion";

    public string System => system;

    /// <summary>The recipe's steps in order, before <c>when:</c> filtering. Unknown kinds are an argument error.</summary>
    public IReadOnlyList<CreationStep> AllSteps(string kind)
    {
        if (recipes.TryGetRecipe(system, kind, out var recipe) && recipe is not null)
            return Order(recipe.Steps);

        return kind.ToLowerInvariant() switch
        {
            PcKind => [new CreationStep { Key = "identity", Kind = CreationStepKinds.Identity }],
            CompanionKind =>
            [
                new CreationStep
                {
                    Key = "identity",
                    Kind = CreationStepKinds.Identity,
                    Schema = recipes.GetStatBlocksForSystem(system).ContainsKey(CompanionKind) ? CompanionKind : null,
                },
            ],
            _ => throw new ArgumentException($"Unknown creation kind '{kind}' for {system}. Use '{PcKind}' or '{CompanionKind}'."),
        };
    }

    /// <summary>The context for one call on <paramref name="draft"/>: path references, options and counts resolve against its choices.</summary>
    public CreationContext ContextFor(string kind, CharacterDraft draft)
    {
        var steps = AllSteps(kind);
        CreationContext? ctx = null;
        ctx = new CreationContext
        {
            System = system,
            Kind = kind,
            Level = Math.Max(1, draft.Level),
            Resolve = path => ResolvePath(kind, steps, draft, path),
            Options = step => OptionsFor(step, draft, ctx!),
            Count = (step, group) => CountFor(steps, step, group, draft, ctx!),
        };
        Drafts.AddOrUpdate(ctx, draft);
        return ctx;
    }

    /// <summary>The draft each context was made for, for the steps whose showing depends on more than a path.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CreationContext, CharacterDraft> Drafts = new();

    private static CharacterDraft? DraftOf(CreationContext ctx) => Drafts.TryGetValue(ctx, out var draft) ? draft : null;

    public IReadOnlyList<CreationStep> Steps(string kind, CreationContext ctx) =>
        [.. AllSteps(kind).Where(step => IsShown(step, ctx))];

    public IReadOnlyList<CreationOption> Options(CreationStep step, CharacterDraft draft, CreationContext ctx) =>
        ctx.Options(step);

    public IReadOnlyList<CreationIssue> Validate(CharacterDraft draft, CreationContext ctx)
    {
        var issues = new List<CreationIssue>();
        if (MaxLevel(ctx.Kind) is { } max && ctx.Level > max)
            issues.Add(CreationIssue.Error(LevelIssueKey, $"The builder goes up to level {max} for this system; build at {max} and level up in play."));

        foreach (var step in Steps(ctx.Kind, ctx))
        {
            if (step.Kind == CreationStepKinds.LevelChoices)
            {
                issues.AddRange(ValidateLevelChoices(draft, step, ctx));
            }
            else if (step.Kind == CreationStepKinds.Identity)
            {
                if (string.IsNullOrWhiteSpace(draft.Name))
                    issues.Add(CreationIssue.Error(step.Key, "Give the character a name."));
            }
            else if (!draft.Has(step.Key))
            {
                if (!step.Optional)
                    issues.Add(CreationIssue.Error(step.Key, $"Choose {Title(step)}."));
                continue;
            }

            foreach (var name in ValidatorsFor(step))
            {
                // Recipes are checked at startup, so a missing name here is a step a plugin ICharacterCreation made up.
                if (!validators.TryGetValue(name, out var validator))
                {
                    issues.Add(CreationIssue.Error(step.Key, $"No validator named '{name}' is loaded."));
                    continue;
                }

                issues.AddRange(validator.Validate(draft, step, ctx));
            }
        }

        return issues;
    }

    /// <summary>
    /// The steps whose choices <paramref name="step"/> depends on, in recipe order: the first segment of its count,
    /// exclude and when paths (<c>modifier.*</c> reads every boost or ability-score step and the race or ancestry), and
    /// the picks its source reads (heritages read the ancestry, class feats the class, ...). A client clears a step's
    /// choice and refetches its options when one of these changes.
    /// </summary>
    public static IReadOnlyList<string> Reads(CreationStep step, IReadOnlyList<CreationStep> steps)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? BySource(string source) => steps.FirstOrDefault(s => s.Kind == CreationStepKinds.PickOne
            && string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase))?.Key;
        void Add(string? key)
        {
            if (!string.IsNullOrWhiteSpace(key))
                keys.Add(key);
        }

        foreach (var path in new[] { step.CountFrom, step.Exclude, WhenPath(step.When) }.Concat(Paths(step.CountPlus)))
        {
            var head = path?.Split('.', 2)[0].Trim();
            if (string.Equals(head, "modifier", StringComparison.OrdinalIgnoreCase))
            {
                Add(BySource(CreationSources.Races));
                foreach (var s in steps.Where(s => s.Kind is CreationStepKinds.Allocate or CreationStepKinds.AbilityScores))
                    Add(s.Key);
            }
            else if (!string.Equals(head, "draft", StringComparison.OrdinalIgnoreCase))
            {
                Add(steps.FirstOrDefault(s => s.Key.Equals(head ?? "", StringComparison.OrdinalIgnoreCase))?.Key);
            }
        }

        // Level choices are the class's (an ability score improvement's cap reads the scores, but doesn't clear with them),
        // narrowed by a god, patron or bloodline, so picking one refreshes the options and drops picks it no longer offers.
        if (step.Kind == CreationStepKinds.LevelChoices)
        {
            Add(BySource(CreationSources.Classes));
            foreach (var power in steps.Where(s => s.Kind == CreationStepKinds.PickOne && CreationSources.PowerType(s.Source) is not null))
                Add(power.Key);
        }

        var source = step.Kind == CreationStepKinds.Spells ? CreationSources.Spells : step.Source?.ToLowerInvariant();
        switch (source)
        {
            case "classskills" or "spells" or "keyabilities" or "classfeats" or "skillfeats" or "generalfeats" or "deities" or "patrons" or "lineages":
                Add(BySource(CreationSources.Classes));
                break;
            case "ancestryfeats":
                // Its count is the class's; its feats are the ancestry's.
                Add(BySource(CreationSources.Classes));
                Add(BySource(CreationSources.Races));
                break;
            case "heritages" or "ancestryboosts":
                Add(BySource(CreationSources.Races));
                break;
            case "backgroundskills" or "backgroundboosts":
                Add(BySource(CreationSources.Backgrounds));
                break;
            case "untrainedskills":
                Add(BySource(CreationSources.Classes));
                Add(BySource(CreationSources.Backgrounds));
                Add(BySource(CreationSources.BackgroundSkills));
                break;
        }

        // A class feature picked before (a racket) trains skills and can change the key attribute.
        if (source is "untrainedskills" or "keyabilities")
        {
            foreach (var level in steps.TakeWhile(s => s != step).Where(s => s.Kind == CreationStepKinds.LevelChoices))
                Add(level.Key);
        }

        keys.Remove(step.Key);
        return [.. steps.Where(s => keys.Contains(s.Key)).Select(s => s.Key)];
    }

    /// <summary>"modifier.intelligence + classChoices.extraSkills" → its two paths.</summary>
    private static IEnumerable<string> Paths(string? sum) =>
        string.IsNullOrWhiteSpace(sum) ? [] : sum.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>"class.casterType != None" → "class.casterType".</summary>
    private static string? WhenPath(string? when)
    {
        if (string.IsNullOrWhiteSpace(when))
            return null;

        var op = when.IndexOf("==", StringComparison.Ordinal);
        if (op < 0)
            op = when.IndexOf("!=", StringComparison.Ordinal);
        return (op < 0 ? when : when[..op]).Trim();
    }

    /// <summary>The validators a step runs: its kind's own, then the ones the recipe lists.</summary>
    public static IEnumerable<string> ValidatorsFor(CreationStep step) =>
        KindValidators(step.Kind)
            .Concat(step.Kind == CreationStepKinds.Identity && !string.IsNullOrWhiteSpace(step.Schema) ? [RecipeValidatorNames.StatBlockFields] : [])
            .Concat(step.Validators)
            .Distinct(StringComparer.Ordinal);

    public static IReadOnlyList<string> KindValidators(string kind) => kind switch
    {
        CreationStepKinds.PickOne => [RecipeValidatorNames.PickOneOption],
        CreationStepKinds.PickN => [RecipeValidatorNames.PickNOption, RecipeValidatorNames.PickNCount],
        CreationStepKinds.Feats => [RecipeValidatorNames.PickNOption, RecipeValidatorNames.PickNCount, RecipeValidatorNames.FeatPrerequisites],
        CreationStepKinds.Allocate => [RecipeValidatorNames.PickNOption, RecipeValidatorNames.PickNCount],
        CreationStepKinds.AbilityScores =>
        [
            RecipeValidatorNames.AbilityScoresMethod,
            RecipeValidatorNames.AbilityScoresStandardArray,
            RecipeValidatorNames.AbilityScoresPointBuy,
            RecipeValidatorNames.AbilityScoresRollRange,
        ],
        CreationStepKinds.Spells => [RecipeValidatorNames.SpellsCountForLevel],
        _ => [],
    };

    /// <summary>
    /// The base score the draft chose for an ability plus the chosen race's bonus, or null before abilities are chosen.
    /// Racial bonuses come from the race template only, never from the client.
    /// </summary>
    public int? AbilityScore(string kind, CharacterDraft draft, string ability)
    {
        var steps = AllSteps(kind);
        var abilityStep = steps.FirstOrDefault(s => s.Kind == CreationStepKinds.AbilityScores);
        var choice = abilityStep is null ? null : draft.Get<AbilityScoreChoice>(abilityStep.Key);
        var score = choice?.Scores.FirstOrDefault(kv => kv.Key.Equals(ability, StringComparison.OrdinalIgnoreCase));
        if (score is not { Key: not null } found)
            return null;

        var race = ChosenTemplate(steps, draft, CreationSources.Races) as RaceDefinition;
        var bonus = race?.AbilityBonuses.FirstOrDefault(kv => kv.Key.Equals(ability, StringComparison.OrdinalIgnoreCase)).Value ?? 0;
        return found.Value + bonus + Increases(kind, draft).Where(i => i.Ability.Equals(ability, StringComparison.OrdinalIgnoreCase)).Sum(i => i.Amount);
    }

    /// <summary>The issue key for the draft's level (it belongs to no step).</summary>
    public const string LevelIssueKey = "level";

    /// <summary>The recipe's highest level (<c>maxLevel:</c>), or null for no limit.</summary>
    public int? MaxLevel(string kind) =>
        recipes.TryGetRecipe(system, kind, out var recipe) && recipe?.MaxLevel is > 0 ? recipe.MaxLevel : null;

    /// <summary>
    /// The level choices the draft's class has at its level, of every levelChoices step or of the one keyed
    /// <paramref name="stepKey"/> (each step asks for its <c>choiceTypes</c>). None before a class is chosen, or without a
    /// progression.
    /// </summary>
    public IReadOnlyList<LevelChoiceSlot> LevelSlots(string kind, CharacterDraft draft, string? stepKey = null) =>
        SlotsFor(AllSteps(kind), draft, stepKey);

    private IReadOnlyList<LevelChoiceSlot> SlotsFor(IReadOnlyList<CreationStep> steps, CharacterDraft draft, string? stepKey)
    {
        var levelSteps = steps
            .Where(s => s.Kind == CreationStepKinds.LevelChoices && (stepKey is null || s.Key.Equals(stepKey, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (levelSteps.Count == 0
            || ChosenTemplate(steps, draft, CreationSources.Classes) is not ClassDefinition cls
            || !sources.ProgressionProvider.TryGetProgression(system, cls.Name, out var progression))
            return [];

        var feats = sources.AsiFeats(system);
        var skills = CreationSources.SkillNames(system);
        // Every levelChoices step's picks, since a subclass picked in one step can ask a choice another step holds.
        var all = steps.Where(s => s.Kind == CreationStepKinds.LevelChoices)
            .SelectMany(s => LevelChoiceSlots.Picks(draft, s.Key))
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Picked(int level, string key) => all.GetValueOrDefault($"{level}.{key}") ?? [];
        var powers = ChosenPowers(steps, draft);
        return
        [
            .. levelSteps.SelectMany(step => LevelChoiceSlots.For(progression, Math.Max(1, draft.Level), feats, step.Key, step.ChoiceTypes, skills, Picked))
                .Select(slot => powers.Aggregate(slot, NarrowedBy)),
        ];
    }

    /// <summary>The named powers (god, patron, bloodline) the draft's powers steps picked.</summary>
    private List<NamedPowerDefinition> ChosenPowers(IReadOnlyList<CreationStep> steps, CharacterDraft draft) =>
    [
        .. steps.Where(s => s.Kind == CreationStepKinds.PickOne && CreationSources.PowerType(s.Source) is not null)
            .Select(s => draft.GetString(s.Key) is { } id ? sources.Power(system, s.Source, id) : null)
            .OfType<NamedPowerDefinition>(),
    ];

    /// <summary>
    /// A power joins its class choice: the slot offers only the options the power names (a god's domains), unless none of
    /// them is an option there, in which case the power doesn't apply to this class and the slot stays whole.
    /// </summary>
    private static LevelChoiceSlot NarrowedBy(LevelChoiceSlot slot, NamedPowerDefinition power)
    {
        var offers = power.OffersFor(slot.Key);
        if (slot.Type != nameof(ChoiceType.Enum) || offers.Count == 0)
            return slot;

        var offered = slot.Options.Where(o => offers.Contains(o.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        return offered.Count == 0
            ? slot
            : slot with
            {
                Options = offered,
                Data = slot.Data.Where(kv => offered.Any(o => o.Id.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            };
    }

    /// <summary>Every level choice slot's picks, by slot id (slot ids are unique across a recipe's levelChoices steps).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> LevelPicks(string kind, CharacterDraft draft) =>
        PicksFor(LevelSlots(kind, draft), draft);

    private static Dictionary<string, IReadOnlyList<string>> PicksFor(IEnumerable<LevelChoiceSlot> slots, CharacterDraft draft)
    {
        var byStep = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase);
        var picks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots)
        {
            if (!byStep.TryGetValue(slot.Step, out var stepPicks))
                byStep[slot.Step] = stepPicks = LevelChoiceSlots.Picks(draft, slot.Step);
            if (stepPicks.TryGetValue(slot.Id, out var chosen))
                picks[slot.Id] = chosen;
        }

        return picks;
    }

    /// <summary>
    /// PF2e: a boost to an attribute modifier already at +4 or more is a partial boost, and two partial boosts make +1.
    /// A core rule of the AttributeBoosts choice type, not something a class's data varies, so it is code.
    /// </summary>
    private const int PartialBoostFrom = 4;

    /// <summary>
    /// What the draft's level choices add, in level order: an ability score improvement's +2 or +1s (5e scores), and each
    /// PF2e attribute boost's +1 to the modifier (half that, by pairs, from +4).
    /// </summary>
    public IReadOnlyList<(string Ability, int Amount)> Increases(string kind, CharacterDraft draft)
    {
        var slots = LevelSlots(kind, draft);
        if (slots.Count == 0)
            return [];

        var steps = AllSteps(kind);
        var picks = PicksFor(slots, draft);
        var result = new List<(string Ability, int Amount)>();
        var gained = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var partial = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots.OrderBy(s => s.Level))
        {
            if (!picks.TryGetValue(slot.Id, out var chosen))
                continue;

            if (slot.IsAsi)
            {
                result.AddRange(slot.Increases(chosen));
                continue;
            }

            if (!slot.IsBoosts)
                continue;

            foreach (var ability in chosen.Select(CanonicalAbility).OfType<string>().Distinct())
            {
                var now = BoostedModifier(steps, draft, ability) + gained.GetValueOrDefault(ability);
                if (now >= PartialBoostFrom && (partial[ability] = partial.GetValueOrDefault(ability) + 1) % 2 == 1)
                    continue;
                gained[ability] = gained.GetValueOrDefault(ability) + 1;
                result.Add((ability, 1));
            }
        }

        return result;
    }

    private static string? CanonicalAbility(string name) =>
        CreationSources.AbilityNames.FirstOrDefault(a => a.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The options the draft picked in a levelChoices step (or every one) that give something: the skills they train,
    /// more skill picks, a key attribute. A path to a levelChoices step resolves to this (<c>classChoices.extraSkills</c>).
    /// </summary>
    public sealed record GrantedChoices(IReadOnlyList<ChoiceOption> Options)
    {
        public IReadOnlyList<string> Skills => [.. Options.SelectMany(o => o.Skills).Distinct(StringComparer.OrdinalIgnoreCase)];
        public int ExtraSkills => Options.Sum(o => o.ExtraSkills);
        public IReadOnlyList<string> KeyAbilities => [.. Options.Select(o => o.KeyAbility).OfType<string>()];
    }

    private GrantedChoices Granted(IReadOnlyList<CreationStep> steps, CharacterDraft draft, string? stepKey)
    {
        var slots = SlotsFor(steps, draft, stepKey);
        var picks = PicksFor(slots, draft);
        return new GrantedChoices(
        [
            .. slots.Where(s => picks.ContainsKey(s.Id))
                .SelectMany(s => picks[s.Id].Select(p => s.Data.GetValueOrDefault(p)))
                .OfType<ChoiceOption>(),
        ]);
    }

    /// <summary>
    /// Each slot: required ones chosen, every pick one of its options, an ability score improvement one or two abilities
    /// (or one feat) leaving no score above 20, and a repeated choice (metamagic) never the same twice. A pick for a slot
    /// the class doesn't have at this level is ignored, with a warning.
    /// </summary>
    private IEnumerable<CreationIssue> ValidateLevelChoices(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        var slots = LevelSlots(ctx.Kind, draft, step.Key);
        var picks = LevelChoiceSlots.Picks(draft, step.Key);
        foreach (var unknown in picks.Keys.Where(id => !slots.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            yield return CreationIssue.Warning(step.Key, $"Ignored '{unknown}': not a choice at level {ctx.Level}.");

        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots)
        {
            if (!picks.TryGetValue(slot.Id, out var chosen))
            {
                if (slot.Required)
                    yield return CreationIssue.Error(step.Key, $"{slot.Title}: choose {CountWord(slot.Picks)}.");
                continue;
            }

            var notOptions = slot.Options.Count == 0 ? [] : chosen.Where(p => !slot.Options.Any(o => o.Id.Equals(p, StringComparison.OrdinalIgnoreCase))).ToList();
            if (notOptions.Count > 0)
            {
                yield return CreationIssue.Error(step.Key, $"{slot.Title}: {string.Join(", ", notOptions.Select(p => $"'{p}'"))} isn't one of the options.");
                continue;
            }

            if (slot.IsAsi)
            {
                foreach (var pick in chosen.Where(p => !slot.Abilities!.Contains(p, StringComparer.OrdinalIgnoreCase)))
                {
                    if (sources.FeatProvider.TryGet(system, pick, out var feat) && FeatPrerequisiteCheck.Message(feat, ctx) is { } why)
                        yield return CreationIssue.Error(step.Key, $"{slot.Title}: {why}");
                }

                var abilities = chosen.Count(p => slot.Abilities!.Contains(p, StringComparer.OrdinalIgnoreCase));
                if (abilities != chosen.Count && chosen.Count != 1)
                    yield return CreationIssue.Error(step.Key, $"{slot.Title}: take one feat, or raise one or two abilities, not both.");
                else if (chosen.Count > 2)
                    yield return CreationIssue.Error(step.Key, $"{slot.Title}: raise one ability by 2 or two by 1 (you picked {chosen.Count}).");
                else if (chosen.Count == 2 && chosen[0].Equals(chosen[1], StringComparison.OrdinalIgnoreCase))
                    yield return CreationIssue.Error(step.Key, $"{slot.Title}: to raise {chosen[0]} by 2, pick it once.");
                continue;
            }

            var distinct = chosen.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count != chosen.Count)
                yield return CreationIssue.Error(step.Key, $"{slot.Title}: pick each option once.");
            if (distinct.Count > slot.Picks || (slot.Required && distinct.Count < slot.Picks))
                yield return CreationIssue.Error(step.Key, $"{slot.Title}: choose {CountWord(slot.Picks)} (you picked {distinct.Count}).");

            foreach (var pick in slot.Unique ? distinct : [])
            {
                if (taken.TryGetValue($"{slot.Key}:{pick}", out var earlier))
                    yield return CreationIssue.Error(step.Key, $"{slot.Title}: {Label(slot, pick)} is already chosen at {earlier}.");
                else
                    taken[$"{slot.Key}:{pick}"] = $"level {slot.Level}";
            }
        }

        // PF2e skill increases, in level order, from the skills the draft is trained in.
        var ranks = TrainedRanks(AllSteps(ctx.Kind), draft, ctx.Level);
        foreach (var slot in slots.Where(s => s.IsSkillIncrease && picks.ContainsKey(s.Id)).OrderBy(s => s.Level))
        {
            var skill = slot.Options.FirstOrDefault(o => o.Id.Equals(picks[slot.Id][0], StringComparison.OrdinalIgnoreCase))?.Id;
            if (skill is not null && Pf2eSkillRanks.Raise(ranks, skill, slot.Level) is { } why)
                yield return CreationIssue.Error(step.Key, $"{slot.Title}: {why}");
        }

        // 5e bonus proficiencies: skills the draft isn't proficient in yet.
        foreach (var slot in slots.Where(s => s.IsSkillProficiency && picks.ContainsKey(s.Id)))
        {
            foreach (var pick in picks[slot.Id].Where(ranks.ContainsKey))
                yield return CreationIssue.Error(step.Key, $"{slot.Title}: you're already proficient in {pick}.");
        }

        // The improvements together can't push a score past 20 (the draft's scores include the race's bonus).
        foreach (var ability in slots.Any(s => s.IsAsi) ? CreationSources.AbilityNames : [])
        {
            if (Increases(ctx.Kind, draft).Any(i => i.Ability == ability) && AbilityScore(ctx.Kind, draft, ability) is > 20 and var score)
                yield return CreationIssue.Error(step.Key, $"{ability} would be {score}; ability score improvements stop at 20.");
        }
    }

    /// <summary>
    /// The draft's PF2e skill ranks: trained in the skills its class, background and class features give and the ones it
    /// picks (pickN steps), then each skill increase it chose, in level order (one the rules don't allow is left out).
    /// </summary>
    public IReadOnlyDictionary<string, Pf2eProficiencyRank> SkillRanks(string kind, CharacterDraft draft)
    {
        var steps = AllSteps(kind);
        var ranks = TrainedRanks(steps, draft, Math.Max(1, draft.Level));
        var slots = LevelSlots(kind, draft).Where(s => s.IsSkillIncrease).OrderBy(s => s.Level).ToList();
        var picks = PicksFor(slots, draft);
        foreach (var slot in slots.Where(s => picks.ContainsKey(s.Id)))
        {
            if (slot.Options.FirstOrDefault(o => o.Id.Equals(picks[slot.Id][0], StringComparison.OrdinalIgnoreCase)) is { } skill)
                Pf2eSkillRanks.Raise(ranks, skill.Id, slot.Level);
        }

        return ranks;
    }

    private Dictionary<string, Pf2eProficiencyRank> TrainedRanks(IReadOnlyList<CreationStep> steps, CharacterDraft draft, int level)
    {
        var names = CreationSources.SkillNames(system);
        var picked = steps
            .Where(s => s.Kind == CreationStepKinds.PickN && s.Source?.ToLowerInvariant() is "untrainedskills" or "classskills" or "skills")
            .SelectMany(s => draft.GetList(s.Key));
        var ranks = new Dictionary<string, Pf2eProficiencyRank>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in CreationSources.TrainedSkills(Picks(steps, draft, level)).Concat(picked))
            ranks[names.FirstOrDefault(n => n.Equals(skill, StringComparison.OrdinalIgnoreCase)) ?? skill] = Pf2eProficiencyRank.Trained;
        return ranks;
    }

    /// <summary>"one", "two", ... "six", then digits.</summary>
    private static string CountWord(int n) => n is >= 1 and <= 6 ? new[] { "one", "two", "three", "four", "five", "six" }[n - 1] : n.ToString();

    private static string Label(LevelChoiceSlot slot, string id) =>
        slot.Options.FirstOrDefault(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Label ?? id;

    private bool IsShown(CreationStep step, CreationContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(step.When) && !WhenHolds(step.When, ctx))
            return false;

        // A named powers step (gods, patrons, bloodlines) with none to offer this class has nothing to ask; none ship with the host.
        if (step.Kind == CreationStepKinds.PickOne && CreationSources.PowerType(step.Source) is not null)
            return ctx.Resolve("class") is not null && ctx.Options(step).Count > 0;

        // A spells step for a class with nothing to pick at this level (a level-1 paladin) has nothing to ask.
        if (step.Kind == CreationStepKinds.Spells && ctx.Resolve("class") is not null)
        {
            return (ctx.Count(step, SpellGroups.Cantrips) ?? 0) > 0
                   || (ctx.Count(step, SpellGroups.Known) ?? 0) > 0
                   || (ctx.Count(step, SpellGroups.Prepared) ?? 0) > 0;
        }

        // Nor a level choices step for a class with no choices up to this level (a level-1 wizard). Before the class is
        // chosen, it's shown above level 1, where every class has some.
        // A step that asks for some choice types only (a PF2e racket) waits for the class.
        if (step.Kind == CreationStepKinds.LevelChoices)
        {
            if (ctx.Resolve("class") is null)
                return ctx.Level > 1 && step.ChoiceTypes.Count == 0;
            return DraftOf(ctx) is not { } draft || LevelSlots(ctx.Kind, draft, step.Key).Count > 0;
        }

        // Nor does a feats step whose class gets none of those feats by this level (a level-1 wizard's class feats).
        if (step.Kind == CreationStepKinds.Feats && ctx.Resolve("class") is not null)
            return (ctx.Count(step, null) ?? 1) > 0;

        return true;
    }

    /// <summary><c>path</c>, <c>path == value</c> or <c>path != value</c>; text comparison ignoring case. An unresolved path is empty.</summary>
    internal static bool WhenHolds(string condition, CreationContext ctx)
    {
        foreach (var (op, negate) in new[] { ("!=", true), ("==", false) })
        {
            var at = condition.IndexOf(op, StringComparison.Ordinal);
            if (at < 0)
                continue;

            var left = Text(ctx.Resolve(condition[..at].Trim()));
            var right = condition[(at + op.Length)..].Trim().Trim('"', '\'');
            var equal = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            return negate ? !equal : equal;
        }

        var value = ctx.Resolve(condition.Trim());
        return value switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            string s => !string.IsNullOrWhiteSpace(s) && !s.Equals("None", StringComparison.OrdinalIgnoreCase),
            IList list => list.Count > 0,
            Enum e => !e.ToString().Equals("None", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        IList list => string.Join(",", list.Cast<object?>()),
        _ => value.ToString() ?? string.Empty,
    };

    private IReadOnlyList<CreationOption> OptionsFor(CreationStep step, CharacterDraft draft, CreationContext ctx)
    {
        if (step.Kind == CreationStepKinds.LevelChoices)
            return [.. LevelSlots(ctx.Kind, draft, step.Key).SelectMany(s => s.Options)];

        if (string.IsNullOrWhiteSpace(step.Source))
            return [];

        var options = sources.Options(system, step.Source, Picks(AllSteps(ctx.Kind), draft, ctx.Level));

        if (!string.IsNullOrWhiteSpace(step.Exclude) && ctx.Resolve(step.Exclude) is IEnumerable excluded and not string)
        {
            var drop = excluded.Cast<object?>().Select(o => o?.ToString()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            options = [.. options.Where(o => !drop.Contains(o.Id))];
        }

        return options;
    }

    /// <summary>What the draft has chosen so far that a source's options depend on.</summary>
    public CreationPicks Picks(IReadOnlyList<CreationStep> steps, CharacterDraft draft, int level)
    {
        var cls = ChosenTemplate(steps, draft, CreationSources.Classes) as ClassDefinition;
        var background = ChosenTemplate(steps, draft, CreationSources.Backgrounds) as BackgroundDefinition;
        var skillStep = steps.FirstOrDefault(s => s.Kind == CreationStepKinds.PickOne
            && string.Equals(s.Source, CreationSources.BackgroundSkills, StringComparison.OrdinalIgnoreCase));
        return new CreationPicks(
            cls,
            ChosenTemplate(steps, draft, CreationSources.Races) as RaceDefinition,
            background,
            skillStep is null ? null : CreationSources.BackgroundSkill(background, draft.GetString(skillStep.Key)),
            level,
            MaxSpellLevel(cls, level))
        {
            Granted = Granted(steps, draft, null).Options,
        };
    }

    private int? CountFor(IReadOnlyList<CreationStep> steps, CreationStep step, string? group, CharacterDraft draft, CreationContext ctx)
    {
        if (step.Kind == CreationStepKinds.Spells && group is not null)
            return SpellCount(steps, group, draft, ctx);

        var count = Number(step.CountFrom, ctx) ?? step.Count ?? FeatCount(steps, step, draft, ctx);
        if (count is null)
            return null;

        return Math.Max(0, count.Value + Paths(step.CountPlus).Sum(path => Number(path, ctx) ?? 0));
    }

    private static int? Number(string? path, CreationContext ctx)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var value = ctx.Resolve(path);
        if (value is int n)
            return n;
        return value is not null && int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    /// <summary>
    /// A PF2e feats step with no count of its own: every feat of its category the class's progression gives up to the
    /// draft's level (a level-3 fighter has class feats from 1 and 2). Null before the class is chosen.
    /// </summary>
    private int? FeatCount(IReadOnlyList<CreationStep> steps, CreationStep step, CharacterDraft draft, CreationContext ctx)
    {
        Func<LevelDefinition, int?>? pick = CreationSources.FeatCategory(step.Source) switch
        {
            "ancestry" => l => l.AncestryFeats,
            "class" => l => l.ClassFeats,
            "skill" => l => l.SkillFeats,
            "general" => l => l.GeneralFeats,
            _ => null,
        };
        if (step.Kind != CreationStepKinds.Feats || pick is null
            || ChosenTemplate(steps, draft, CreationSources.Classes) is not ClassDefinition cls
            || !sources.ProgressionProvider.TryGetProgression(system, cls.Name, out var progression))
            return null;

        return progression.Levels.Where(kv => kv.Key <= ctx.Level).Sum(kv => pick(kv.Value) ?? 0);
    }

    /// <summary>
    /// An ability's modifier from the draft's choices so far: from the ability scores step (5e), or else the chosen race's
    /// bonus plus one for each boost (allocate) step pick naming it and the level choices' boosts (PF2e). Null for an
    /// unknown ability.
    /// </summary>
    public int? AbilityModifier(string kind, CharacterDraft draft, string ability)
    {
        var name = CreationSources.AbilityNames.FirstOrDefault(a => a.Equals(ability, StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return null;

        var steps = AllSteps(kind);
        if (steps.Any(s => s.Kind == CreationStepKinds.AbilityScores))
            return AbilityScore(kind, draft, name) is { } score ? Modifier(score) : 0;

        return BoostedModifier(steps, draft, name)
               + Increases(kind, draft).Where(i => i.Ability.Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(i => i.Amount);
    }

    /// <summary>A PF2e modifier at level 1: the ancestry's boost or flaw plus each boost (allocate) pick naming it.</summary>
    private int BoostedModifier(IReadOnlyList<CreationStep> steps, CharacterDraft draft, string name)
    {
        var race = ChosenTemplate(steps, draft, CreationSources.Races) as RaceDefinition;
        var bonus = race?.AbilityBonuses.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? 0;
        return bonus + steps
            .Where(s => s.Kind == CreationStepKinds.Allocate)
            .Sum(s => draft.GetList(s.Key).Count(p => p.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    private int? SpellCount(IReadOnlyList<CreationStep> steps, string group, CharacterDraft draft, CreationContext ctx)
    {
        if (ChosenTemplate(steps, draft, CreationSources.Classes) is not ClassDefinition cls
            || !sources.ProgressionProvider.TryGetProgression(system, cls.Name, out var progression))
            return null;

        return group switch
        {
            SpellGroups.Cantrips => progression.CountAtLevel(ctx.Level, l => l.CantripsKnown),
            SpellGroups.Known => progression.CountAtLevel(ctx.Level, l => l.SpellsKnown),
            SpellGroups.Prepared when progression.PreparedSpells is { } prepared =>
                prepared.CountFor(ctx.Level, Modifier(AbilityScore(ctx.Kind, draft, prepared.Ability) ?? 10)),
            _ => 0,
        };
    }

    private static int Modifier(int score) => (int)Math.Floor((score - 10) / 2.0);

    /// <summary>The highest spell level a class can cast at a character level, by caster type (SRD slot tables).</summary>
    internal static int MaxSpellLevel(ClassDefinition? cls, int level) => cls?.CasterType switch
    {
        CasterType.Full => Math.Min(9, (level + 1) / 2),
        CasterType.Warlock => Math.Min(5, (level + 1) / 2),
        CasterType.Half => level < 2 ? 0 : Math.Min(5, (level + 3) / 4),
        CasterType.HalfRoundUp => Math.Min(5, (level + 3) / 4),
        CasterType.Third => level < 3 ? 0 : Math.Min(4, (level - 1) / 6 + 1),
        _ => 0,
    };

    private RulesetTemplate? ChosenTemplate(IReadOnlyList<CreationStep> steps, CharacterDraft draft, string source)
    {
        var step = steps.FirstOrDefault(s => s.Kind == CreationStepKinds.PickOne && string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase));
        var id = step is null ? null : draft.GetString(step.Key);
        return id is null ? null : sources.Template(system, source, id);
    }

    /// <summary>What feat prerequisites ask about (<see cref="CreationSheet"/>).</summary>
    private CreationSheet Sheet(string kind, IReadOnlyList<CreationStep> steps, CharacterDraft draft)
    {
        var abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scores = steps.Any(s => s.Kind == CreationStepKinds.AbilityScores);
        foreach (var ability in CreationSources.AbilityNames)
        {
            if ((scores ? AbilityScore(kind, draft, ability) : AbilityModifier(kind, draft, ability)) is { } value)
                abilities[ability] = value;
        }

        var slots = LevelSlots(kind, draft);
        var picks = PicksFor(slots, draft);
        var picked = slots.Where(s => picks.ContainsKey(s.Id)).ToList();
        var background = ChosenTemplate(steps, draft, CreationSources.Backgrounds) as BackgroundDefinition;
        var feats = steps.Where(s => s.Kind == CreationStepKinds.Feats).SelectMany(s => draft.GetList(s.Key))
            .Concat(picked.Where(s => s.IsAsi).SelectMany(s => picks[s.Id].Where(p => !s.Abilities!.Contains(p, StringComparer.OrdinalIgnoreCase))))
            .Concat(background?.SkillFeat is { } skillFeat ? [skillFeat, FeatId(skillFeat)] : [])
            .ToList();
        return new CreationSheet(
            abilities,
            SkillRanks(kind, draft),
            feats,
            [.. picked.Where(s => s.Unique).SelectMany(s => picks[s.Id])]);
    }

    /// <summary>"Lie to Me" → "lie_to_me", a feat's file name.</summary>
    private static string FeatId(string name) =>
        Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    /// <summary>
    /// <c>draft.&lt;field&gt;</c>, <c>sheet.&lt;field&gt;</c> (<see cref="CreationSheet"/>), or <c>&lt;stepKey&gt;[.field...]</c>:
    /// a pickOne with a template source resolves to the chosen template, a levelChoices step to what its picks give
    /// (<see cref="GrantedChoices"/>), any other step to its raw choice (string or list).
    /// </summary>
    private object? ResolvePath(string kind, IReadOnlyList<CreationStep> steps, CharacterDraft draft, string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return null;

        object? current;
        if (segments[0].Equals("modifier", StringComparison.OrdinalIgnoreCase))
        {
            return segments.Length == 2 ? AbilityModifier(kind, draft, segments[1]) : null;
        }

        if (segments[0].Equals("draft", StringComparison.OrdinalIgnoreCase))
        {
            current = draft;
        }
        else if (segments[0].Equals("sheet", StringComparison.OrdinalIgnoreCase))
        {
            current = Sheet(kind, steps, draft);
        }
        else
        {
            var step = steps.FirstOrDefault(s => s.Key.Equals(segments[0], StringComparison.OrdinalIgnoreCase));
            if (step is null || !draft.Has(step.Key))
                return null;

            var id = draft.GetString(step.Key);
            current = step.Kind == CreationStepKinds.LevelChoices
                ? Granted(steps, draft, step.Key)
                : step.Kind == CreationStepKinds.PickOne && id is not null
                    ? (object?)sources.Template(system, step.Source, id) ?? id
                    : draft.GetList(step.Key);
        }

        foreach (var segment in segments.Skip(1))
        {
            current = Member(current, segment);
            if (current is null)
                return null;
        }

        return current;
    }

    private static object? Member(object? target, string name)
    {
        switch (target)
        {
            case null:
                return null;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (string.Equals(entry.Key.ToString(), name, StringComparison.OrdinalIgnoreCase))
                        return entry.Value;
                }

                return null;
            default:
                return target.GetType()
                    .GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                    ?.GetValue(target);
        }
    }

    /// <summary>File order, except a step with <c>after:</c> moves right behind that key (its first match; unknown keys stay put).</summary>
    private static IReadOnlyList<CreationStep> Order(IReadOnlyList<CreationStep> steps)
    {
        var ordered = steps.Where(s => string.IsNullOrWhiteSpace(s.After)).ToList();
        foreach (var step in steps.Where(s => !string.IsNullOrWhiteSpace(s.After)))
        {
            var anchor = ordered.FindLastIndex(s => s.Key.Equals(step.After, StringComparison.OrdinalIgnoreCase));
            if (anchor < 0)
                ordered.Add(step);
            else
                ordered.Insert(anchor + 1, step);
        }

        return ordered;
    }

    private static string Title(CreationStep step) => step.Prompt ?? CreationSources.Label(step.Key).ToLowerInvariant();
}
