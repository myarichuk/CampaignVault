using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Creation;

/// <summary>The class gaining a level, the level it reaches in it, and the choices that level asks for.</summary>
public sealed record LevelUpPlan(string ClassName, int ClassLevel, int CharacterLevel, IReadOnlyList<LevelChoiceSlot> Slots);

/// <summary>
/// A level-up for an existing character, using the slots the builder uses (<see cref="LevelChoiceSlots"/>): which choices
/// the next level asks for, whether a set of picks is allowed, and what it changes. The rules live in the class
/// progressions (data); this only reads them for a character instead of a draft.
/// </summary>
public sealed class LevelUpPlanner(CreationSources sources, ProgressionDefinitionProvider progressions)
{
    /// <summary>The partial boost marker: a PF2e boost to a modifier already at +4 or more counts half, in pairs.</summary>
    public const string PartialBoostKey = "partialBoost";

    private const int PartialBoostFrom = 4;

    /// <summary>
    /// The slots of the next level of the class gaining it (<paramref name="classGained"/>, else the first class), or null
    /// without a progression. <paramref name="picks"/> (by slot id) add the slots a pick asks for: a feat's own choices.
    /// </summary>
    public LevelUpPlan? Plan(Character character, string system, string? classGained = null, IReadOnlyDictionary<string, List<string>>? picks = null)
    {
        var entries = CharacterClassResolver.ResolveClassLevels(character);
        if (entries.Count == 0)
            return null;

        var entry = classGained is { Length: > 0 } && entries.FirstOrDefault(e => e.Class.Contains(classGained, StringComparison.OrdinalIgnoreCase)) is { } named
            ? named
            : entries[0];
        var progression = Progression(system, entry.Class);
        if (progression is null)
            return null;

        var classLevel = entry.Level + 1;
        var level = XpThresholdCalculator.GetCurrentLevel(character) + 1;
        var recorded = CharacterClassFeatures.Picked(character.SystemStats, entries.Count > 1);
        IEnumerable<string> Picked(int at, string key) =>
            at == classLevel && picks?.GetValueOrDefault($"{at}.{key}") is { } now ? now : recorded(at, key);
        var slots = LevelChoiceSlots.For(
                progression, classLevel, sources.AsiFeats(system), "levelUp", null, CreationSources.SkillNames(system),
                Picked, sources.FeatChoices(system))
            .Where(s => s.Level == classLevel)
            .ToList();
        return new LevelUpPlan(entry.Class, classLevel, level, slots);
    }

    /// <summary>A slot in the wire shape menus and models read.</summary>
    public static PendingLevelUpSlot Describe(LevelChoiceSlot slot) => new()
    {
        Id = slot.Id,
        Title = slot.Title,
        Type = slot.Type,
        Required = slot.Required,
        Picks = slot.Picks,
        Abilities = slot.Abilities is null ? null : [.. slot.Abilities],
        Options = [.. slot.Options.Select(o => new PendingLevelUpOption { Id = o.Id, Label = o.Label, Description = o.Description, Homebrew = o.Homebrew })],
    };

    private ProgressionDefinition? Progression(string system, string className)
    {
        if (progressions.TryGetProgression(system, className, out var progression))
            return progression;

        var first = className.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && progressions.TryGetProgression(system, first, out progression) ? progression : null;
    }

    /// <summary>
    /// What is wrong with the picks, in words for whoever is choosing: a slot the level doesn't have, an option it doesn't
    /// offer, the wrong number of picks, a score past 20, a unique option taken twice, a skill that can't rise. Empty when fine.
    /// </summary>
    public IReadOnlyList<string> Validate(LevelUpPlan plan, Character character, IReadOnlyDictionary<string, List<string>> picks)
    {
        var problems = new List<string>();
        foreach (var id in picks.Keys.Where(id => !plan.Slots.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            problems.Add($"'{id}' isn't a choice at level {plan.ClassLevel}"
                         + (plan.Slots.Count == 0 ? " (there are none)." : $" (this level asks for {string.Join(", ", plan.Slots.Select(s => s.Id))})."));

        var ranks = character.SystemStats is Pf2eExtension pf ? new Dictionary<string, Pf2eProficiencyRank>(pf.SkillProficiencies, StringComparer.OrdinalIgnoreCase) : null;
        var records = character.SystemStats?.LevelUpChoices ?? [];
        foreach (var slot in plan.Slots)
        {
            var chosen = Chosen(slot, picks);
            if (chosen.Count == 0)
            {
                if (slot.Required)
                    problems.Add($"{slot.Title}: choose {(slot.IsAsi ? "an ability score improvement or a feat" : Count(slot.Picks))}.");
                continue;
            }

            var unknown = chosen.Where(c => !slot.Options.Any(o => o.Id.Equals(c, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0)
            {
                problems.Add($"{slot.Title}: {string.Join(", ", unknown)} isn't offered.");
                continue;
            }

            if (slot.IsAsi)
            {
                problems.AddRange(ValidateAsi(slot, chosen, character, plan));
                continue;
            }

            if (chosen.Distinct(StringComparer.OrdinalIgnoreCase).Count() != chosen.Count)
                problems.Add($"{slot.Title}: pick each option once.");
            if (slot.IncreaseAmount > 0 && character.SystemStats is Dnd5eExtension scores)
            {
                foreach (var (ability, amount) in slot.Increases(chosen).Where(i => Score(scores, i.Ability) + i.Amount > 20))
                    problems.Add($"{slot.Title}: {ability} would be {Score(scores, ability) + amount}; ability scores stop at 20.");
            }

            if (chosen.Count > slot.Picks || (slot.Required && chosen.Count < slot.Picks))
                problems.Add($"{slot.Title}: choose {Count(slot.Picks)} (you picked {chosen.Count}).");
            if (slot.Unique)
            {
                foreach (var pick in chosen.Where(c => records.Any(r => r.Key.Equals(slot.Key, StringComparison.OrdinalIgnoreCase) && r.Value.Equals(c, StringComparison.OrdinalIgnoreCase))))
                    problems.Add($"{slot.Title}: {pick} is already chosen.");
            }

            if (slot.IsSkillIncrease && ranks is not null)
            {
                var skill = slot.Options.First(o => o.Id.Equals(chosen[0], StringComparison.OrdinalIgnoreCase)).Id;
                if (Pf2eSkillRanks.Raise(ranks, skill, plan.CharacterLevel) is { } why)
                    problems.Add($"{slot.Title}: {why}");
            }
        }

        return problems;
    }

    private IEnumerable<string> ValidateAsi(LevelChoiceSlot slot, List<string> chosen, Character character, LevelUpPlan plan)
    {
        var abilities = chosen.Count(p => slot.Abilities!.Contains(p, StringComparer.OrdinalIgnoreCase));
        if (abilities != chosen.Count && chosen.Count != 1)
        {
            yield return $"{slot.Title}: take one feat, or raise one or two abilities, not both.";
            yield break;
        }

        if (chosen.Count > 2)
            yield return $"{slot.Title}: raise one ability by 2 or two by 1 (you picked {chosen.Count}).";
        else if (chosen.Count == 2 && chosen[0].Equals(chosen[1], StringComparison.OrdinalIgnoreCase))
            yield return $"{slot.Title}: to raise {chosen[0]} by 2, pick it once.";

        if (character.SystemStats is not Dnd5eExtension stats)
            yield break;

        // A feat taken instead of the improvement has to be one the character qualifies for, as in the builder.
        if (abilities == 0 && sources.FeatProvider.TryGet(RulesetSystem.Dnd5e, chosen[0], out var feat)
            && FeatPrerequisiteCheck.Message(feat, SheetOf(stats), RulesetSystem.Dnd5e) is { } why)
            yield return $"{slot.Title}: {why}";

        var fixedIncrease = abilities == 0 && sources.FeatProvider.TryGet(RulesetSystem.Dnd5e, chosen[0], out var halfFeat)
            ? CharacterFeats.FixedIncrease(halfFeat)
            : [];
        foreach (var (ability, amount) in slot.Increases(chosen).Concat(fixedIncrease))
        {
            var score = Score(stats, ability);
            if (score + amount > 20)
                yield return $"{slot.Title}: {ability} would be {score + amount}; ability score improvements stop at 20.";
        }
    }

    /// <summary>
    /// Records the picks on the character and applies them: the scores (5e) or modifiers (PF2e) they raise, a feat taken
    /// instead of an improvement, a PF2e skill increase's rank, a 5e bonus skill's modifier. Run after <see cref="Validate"/>
    /// and before the level's hit points are derived, so a Constitution improvement counts for them.
    /// </summary>
    public IReadOnlyList<string> Apply(LevelUpPlan plan, Character character, IReadOnlyDictionary<string, List<string>> picks)
    {
        if (character.SystemStats is not { } stats)
            return [];

        var canonical = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var increases = new List<(string Ability, int Amount)>();
        var messages = new List<string>();
        foreach (var slot in plan.Slots)
        {
            var chosen = Chosen(slot, picks);
            if (chosen.Count == 0)
                continue;

            canonical[slot.Id] = [.. chosen.Select(c => slot.Options.First(o => o.Id.Equals(c, StringComparison.OrdinalIgnoreCase)).Id)];
            if (slot.IncreaseAmount > 0)
            {
                var gained = slot.Increases(canonical[slot.Id]);
                increases.AddRange(gained);
                messages.Add(string.Join(", ", gained.Select(i => $"{i.Ability} +{i.Amount}")));
            }
            else if (slot.IsAsi)
            {
                var gained = slot.Increases(canonical[slot.Id]);
                increases.AddRange(gained);
                if (gained.Count > 0)
                {
                    messages.Add(string.Join(", ", gained.Select(i => $"{i.Ability} +{i.Amount}")));
                }
                else
                {
                    var half = sources.FeatProvider.TryGet(RulesetSystem.Dnd5e, canonical[slot.Id][0], out var feat) ? CharacterFeats.FixedIncrease(feat) : [];
                    increases.AddRange(half);
                    messages.Add($"feat {canonical[slot.Id][0]}" + string.Concat(half.Select(i => $" ({i.Ability} +{i.Amount})")));
                }
            }
            else if (slot.IsBoosts)
            {
                var boosted = Boosts(stats, canonical[slot.Id], increases);
                messages.Add($"boosted {string.Join(", ", boosted)}");
            }
            else
            {
                messages.Add($"{slot.Key} {string.Join(", ", canonical[slot.Id])}");
            }
        }

        DraftCharacterMapper.ApplyLevelChoices(stats, new LevelChoicesApplied(plan.Slots, canonical, increases));
        foreach (var slot in plan.Slots.Where(s => canonical.ContainsKey(s.Id)))
        {
            var pick = canonical[slot.Id][0];
            if (slot.IsSkillIncrease && stats is Pf2eExtension pf2e)
            {
                Pf2eSkillRanks.Raise(pf2e.SkillProficiencies, pf2e.SkillProficiencies.Keys.FirstOrDefault(k => k.Equals(pick, StringComparison.OrdinalIgnoreCase)) ?? pick, plan.CharacterLevel);
                messages.Add($"{pick} is now {pf2e.SkillProficiencies[pf2e.SkillProficiencies.Keys.First(k => k.Equals(pick, StringComparison.OrdinalIgnoreCase))].ToString().ToLowerInvariant()}");
            }
            else if (slot.IsSkillProficiency && stats is Dnd5eExtension dnd)
            {
                foreach (var skill in canonical[slot.Id].Where(Dnd5eSkillTable.GoverningAbility.ContainsKey))
                    dnd.SkillModifiers.TryAdd(skill, dnd.GetAbilityModifier(Score(dnd, Dnd5eSkillTable.GoverningAbility[skill])) + Dnd5eClassProfileResolver.ProficiencyBonus(plan.CharacterLevel));
            }
        }

        return messages;
    }

    /// <summary>
    /// PF2e boosts: +1 to the modifier, but from +4 only every second boost counts. Which boosts were the first of a pair is
    /// kept as <see cref="PartialBoostKey"/> records, so the count survives from level to level.
    /// </summary>
    private static List<string> Boosts(SystemExtension stats, IReadOnlyList<string> abilities, List<(string Ability, int Amount)> increases)
    {
        var boosted = new List<string>();
        foreach (var ability in abilities)
        {
            var now = stats is Pf2eExtension pf ? Modifier(pf, ability) : 0;
            var halves = stats.LevelUpChoices.Count(r => r.Key == PartialBoostKey && r.Value.Equals(ability, StringComparison.OrdinalIgnoreCase));
            if (now >= PartialBoostFrom && halves % 2 == 0)
            {
                stats.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 0, Key = PartialBoostKey, Value = ability });
                boosted.Add($"{ability} (half a boost)");
                continue;
            }

            if (now >= PartialBoostFrom)
                stats.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 0, Key = PartialBoostKey, Value = ability });
            increases.Add((ability, 1));
            boosted.Add(ability);
        }

        return boosted;
    }

    /// <summary>What a feat's prerequisites ask about, read from the character as it is before this level.</summary>
    private static CreationSheet SheetOf(Dnd5eExtension stats) => new(
        CreationSources.AbilityNames.ToDictionary(a => a, a => Score(stats, a), StringComparer.OrdinalIgnoreCase),
        stats.SkillModifiers.Keys.ToDictionary(k => k, _ => Pf2eProficiencyRank.Trained, StringComparer.OrdinalIgnoreCase),
        stats.Feats,
        [.. stats.LevelUpChoices.Select(r => r.Value)]);

    private static int Modifier(Pf2eExtension stats, string ability) => ability.ToLowerInvariant() switch
    {
        "strength" => stats.StrengthMod,
        "dexterity" => stats.DexterityMod,
        "constitution" => stats.ConstitutionMod,
        "intelligence" => stats.IntelligenceMod,
        "wisdom" => stats.WisdomMod,
        "charisma" => stats.CharismaMod,
        _ => 0,
    };

    private static int Score(Dnd5eExtension stats, string ability) => ability.ToLowerInvariant() switch
    {
        "strength" => stats.Strength,
        "dexterity" => stats.Dexterity,
        "constitution" => stats.Constitution,
        "intelligence" => stats.Intelligence,
        "wisdom" => stats.Wisdom,
        "charisma" => stats.Charisma,
        _ => 10,
    };

    private static List<string> Chosen(LevelChoiceSlot slot, IReadOnlyDictionary<string, List<string>> picks)
    {
        var match = picks.FirstOrDefault(kv => kv.Key.Equals(slot.Id, StringComparison.OrdinalIgnoreCase));
        return match.Value is null ? [] : [.. match.Value.Select(p => p.Trim()).Where(p => p.Length > 0)];
    }

    private static string Count(int n) => n is >= 1 and <= 6 ? new[] { "one", "two", "three", "four", "five", "six" }[n - 1] : n.ToString();
}
