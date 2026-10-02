using System.Text.Json;
using System.Text.Json.Serialization;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Creation;

/// <summary>
/// One choice a class's progression asks for at a level up to the draft's: a wizard's arcane tradition at 2, an ability
/// score improvement at 4, a fighter's fighting style at 1. A <see cref="CreationStepKinds.LevelChoices"/> step's choice
/// is an object of slot id → pick (a string, or for an ability score improvement a list).
/// </summary>
public sealed record LevelChoiceSlot
{
    /// <summary><c>&lt;level&gt;.&lt;choice key&gt;</c>: <c>2.subclass</c>, <c>4.asiOrFeat</c>. Its options carry it as their group.</summary>
    public string Id { get; init; } = null!;

    public int Level { get; init; }

    /// <summary>The progression's choice key, recorded on the character (<c>levelUpChoices</c>) like level_up's.</summary>
    public string Key { get; init; } = null!;

    /// <summary>"Level 2 · Arcane Tradition".</summary>
    public string Title { get; init; } = null!;

    /// <summary>The progression's <see cref="ChoiceType"/>: Enum, AsiOrFeat, FeatSelection, FreeText, SkillIncrease or AttributeBoosts.</summary>
    public string Type { get; init; } = null!;

    public bool Required { get; init; }

    /// <summary>
    /// How many picks: the choice's count (two metamagic options), or for an ability score improvement 2 at most (two
    /// abilities at +1).
    /// </summary>
    public int Picks { get; init; } = 1;

    /// <summary>
    /// AsiOrFeat and AttributeBoosts: the option ids that are abilities. For an improvement one of them is +2, two are +1
    /// each, and any other option of the slot is a feat, taken instead.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Abilities { get; init; }

    /// <summary>What the slot offers (group: its id). Sent with the step's options, not with the slot.</summary>
    [JsonIgnore]
    public IReadOnlyList<CreationOption> Options { get; init; } = [];

    /// <summary>The progression's own options by id, with what they give (a racket's skills and key attribute).</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ChoiceOption> Data { get; init; } = new Dictionary<string, ChoiceOption>();

    /// <summary>The levelChoices step whose choice holds this slot's picks.</summary>
    [JsonIgnore]
    public string Step { get; init; } = null!;

    public bool IsAsi => Type == nameof(ChoiceType.AsiOrFeat);

    /// <summary>
    /// A 5e half-feat's ability pick: each pick (one of <see cref="Abilities"/>) rises by this much. 0 for any other slot.
    /// </summary>
    public int IncreaseAmount { get; init; }

    /// <summary>Whether a pick here raises an ability score: an improvement or a half-feat's ability.</summary>
    public bool GivesIncrease => IsAsi || IncreaseAmount > 0;

    /// <summary>PF2e attribute boosts: <see cref="Picks"/> different attributes, each +1.</summary>
    public bool IsBoosts => Type == nameof(ChoiceType.AttributeBoosts);

    /// <summary>PF2e skill increase: one skill a rank up.</summary>
    public bool IsSkillIncrease => Type == nameof(ChoiceType.SkillIncrease);

    /// <summary>5e: skills the character isn't proficient in yet, <see cref="Picks"/> of them (a Lore bard's three).</summary>
    public bool IsSkillProficiency => Type == nameof(ChoiceType.SkillProficiency);

    /// <summary>A pick names an option once per character (a subclass, an invocation); boosts and increases repeat across levels.</summary>
    public bool Unique => !IsAsi && !IsBoosts && !IsSkillIncrease;

    /// <summary>
    /// The ability score increases a pick list gives: one ability is +2, two are +1 each (a half-feat's pick by its
    /// amount). Empty for a feat.
    /// </summary>
    public IReadOnlyList<(string Ability, int Amount)> Increases(IReadOnlyList<string> picks)
    {
        if (IncreaseAmount > 0 && Abilities is not null)
        {
            return
            [
                .. picks.Select(p => Abilities.FirstOrDefault(a => a.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    .OfType<string>()
                    .Select(a => (a, IncreaseAmount)),
            ];
        }

        if (!IsAsi || picks.Count is 0 or > 2 || Abilities is null)
            return [];

        var names = picks.Select(p => Abilities.FirstOrDefault(a => a.Equals(p, StringComparison.OrdinalIgnoreCase))).ToList();
        if (names.Any(n => n is null))
            return [];

        return names.Count == 1 ? [(names[0]!, 2)] : [.. names.Select(n => (n!, 1))];
    }
}

/// <summary>
/// What the slots of a feat taken at an improvement need (5e): the feat by id, and the spells of some classes' lists at a
/// spell level, as options.
/// </summary>
public sealed record FeatChoiceSource(
    Func<string, FeatDefinition?> Feat,
    Func<IReadOnlyList<string>, int, IReadOnlyList<CreationOption>> Spells);

/// <summary>Reads and builds the level choice slots of a draft.</summary>
public static class LevelChoiceSlots
{
    /// <summary>
    /// Every choice of the class's progression at levels 1 to <paramref name="level"/> of the given types (all when empty),
    /// in level order, for the levelChoices step <paramref name="step"/>. Spell picks are the spells step's, so
    /// SpellSelection choices are left out. A choice with no options of its own (a later invocation) offers the options of
    /// the same key at another level; a skill increase or proficiency offers the system's skills, attribute boosts the
    /// abilities. A picked option's features add their choices (a hunter's prey once the hunter is picked):
    /// <paramref name="picked"/> gives the picks by level and choice key. A feat picked at an improvement adds its own
    /// choices after it (<see cref="FeatSlots"/>) when <paramref name="featChoices"/> is given.
    /// </summary>
    public static IReadOnlyList<LevelChoiceSlot> For(
        ProgressionDefinition progression,
        int level,
        IReadOnlyList<CreationOption> feats,
        string step,
        IReadOnlyCollection<string>? types = null,
        IReadOnlyList<string>? skills = null,
        Func<int, string, IEnumerable<string>>? picked = null,
        FeatChoiceSource? featChoices = null)
    {
        picked ??= (_, _) => [];
        var slots = new List<LevelChoiceSlot>();
        foreach (var (at, choice, from) in progression.ChoicesUpTo(level, picked).Select(g => (g.Level, g.Choice, g.From)))
        {
            if (choice.Type == ChoiceType.SpellSelection)
                continue;

            if (types is { Count: > 0 } && !types.Contains(choice.Type.ToString(), StringComparer.OrdinalIgnoreCase))
                continue;

            var id = $"{at}.{choice.Key}";
            var slot = new LevelChoiceSlot
            {
                Id = id,
                Level = at,
                Key = choice.Key,
                Title = $"Level {at} · {Humanize(choice.Prompt ?? choice.Key)}",
                Type = choice.Type.ToString(),
                Required = choice.Required,
                Picks = Math.Max(1, choice.Count),
                Step = step,
            };
            switch (choice.Type)
            {
                case ChoiceType.AsiOrFeat:
                    var abilities = choice.AbilityOptions.Count > 0 ? choice.AbilityOptions : [.. CreationSources.AbilityNames];
                    slots.Add(slot with
                    {
                        Picks = 2,
                        Abilities = abilities,
                        Options =
                        [
                            .. abilities.Select(a => new CreationOption(a, a, "One ability +2, or two abilities +1 each.", id)),
                            .. feats.Select(f => f with { Group = id }),
                        ],
                    });
                    if (featChoices is not null)
                    {
                        foreach (var feat in picked(at, choice.Key).Select(featChoices.Feat).OfType<FeatDefinition>())
                            slots.AddRange(FeatSlots(feat, at, step, featChoices));
                    }

                    break;
                case ChoiceType.AttributeBoosts:
                    slots.Add(slot with
                    {
                        Abilities = CreationSources.AbilityNames,
                        Options = [.. CreationSources.AbilityNames.Select(a => new CreationOption(a, a, null, id))],
                    });
                    break;
                case ChoiceType.SkillIncrease:
                case ChoiceType.SkillProficiency:
                    slots.Add(slot with { Options = [.. (skills ?? []).Select(k => new CreationOption(k, k, null, id))] });
                    break;
                default:
                    var options = progression.OptionsFor(choice, from);
                    slots.Add(slot with
                    {
                        Options = [.. options.Select(o => new CreationOption(o.Id, o.Label == o.Id ? Humanize(o.Id) : o.Label, o.Description, id) { Homebrew = o.Homebrew })],
                        Data = options.GroupBy(o => o.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                    });
                    break;
            }
        }

        return slots;
    }

    /// <summary>
    /// The choices a feat taken at level <paramref name="at"/> asks, each a slot <c>&lt;level&gt;.&lt;feat&gt;.&lt;what&gt;</c>:
    /// the ability a half-feat raises (key <c>&lt;feat&gt;.ability</c>), its skills (key <c>skills</c>, like a class's skill
    /// picks) and its spells (key <c>&lt;feat&gt;.spells</c>). A half-feat with one ability needs no pick.
    /// </summary>
    public static IEnumerable<LevelChoiceSlot> FeatSlots(FeatDefinition feat, int at, string step, FeatChoiceSource source)
    {
        var title = $"Level {at} · {CreationSources.Label(feat.Name)}";
        if (feat.AbilityIncrease is { Choose.Count: not 1 } increase)
        {
            var abilities = increase.Choose.Count > 0 ? increase.Choose : [.. CreationSources.AbilityNames];
            var id = $"{at}.{feat.Name}.ability";
            yield return new LevelChoiceSlot
            {
                Id = id, Level = at, Key = $"{feat.Name}.ability", Title = $"{title}: ability +{increase.Amount}",
                Type = nameof(ChoiceType.Enum), Required = true, Step = step,
                Abilities = abilities, IncreaseAmount = increase.Amount,
                Options = [.. abilities.Select(a => new CreationOption(a, a, null, id))],
            };
        }

        if (feat.SkillChoices > 0)
        {
            var id = $"{at}.{feat.Name}.skills";
            yield return new LevelChoiceSlot
            {
                Id = id, Level = at, Key = Bootstrap.Dnd5eDeriveProficiencyStep.SkillsChoiceKey, Title = $"{title}: skills",
                Type = nameof(ChoiceType.SkillProficiency), Required = true, Picks = feat.SkillChoices, Step = step,
                Options = [.. CreationSources.SkillNames(RulesetSystem.Dnd5e).Select(k => new CreationOption(k, k, null, id))],
            };
        }

        for (var i = 0; i < feat.SpellChoices.Count; i++)
        {
            var choice = feat.SpellChoices[i];
            var id = $"{at}.{feat.Name}.spells{(i == 0 ? "" : i + 1)}";
            yield return new LevelChoiceSlot
            {
                Id = id, Level = at, Key = $"{feat.Name}.spells",
                Title = $"{title}: {(choice.Level == 0 ? "cantrips" : $"level {choice.Level} spells")}",
                Type = nameof(ChoiceType.Enum), Required = true, Picks = Math.Max(1, choice.Count), Step = step,
                Options = [.. source.Spells(choice.Lists, choice.Level).Select(o => o with { Group = id })],
            };
        }
    }

    /// <summary>The draft's picks per slot id: each value as a list (a string counts as one). Empty when the step has no object.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Picks(CharacterDraft draft, string stepKey)
    {
        var picks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (!draft.Choices.TryGetValue(stepKey, out var value) || value.ValueKind != JsonValueKind.Object)
            return picks;

        foreach (var slot in value.EnumerateObject())
        {
            IReadOnlyList<string> list = slot.Value.ValueKind switch
            {
                JsonValueKind.String => [slot.Value.GetString()!],
                JsonValueKind.Array => [.. slot.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)],
                _ => [],
            };
            list = [.. list.Select(p => p.Trim()).Where(p => p.Length > 0)];
            if (list.Count > 0)
                picks[slot.Name] = list;
        }

        return picks;
    }

    /// <summary>"agonizingBlast" → "Agonizing Blast", "Arcane Tradition" stays.</summary>
    public static string Humanize(string text)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(text, "(?<=[a-z])(?=[A-Z])", " ");
        return CreationSources.Label(spaced);
    }
}
