using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Creation;

/// <summary>
/// Turns a draft into the inputs world_build takes: an un-bootstrapped <see cref="Character"/> with the system's
/// default stats and the choices written in. Generic by convention, so a new system needs no code here:
/// <list type="bullet">
/// <item>the class pick (source <c>classes</c>) sets the class/level text, <c>classLevels</c>, <c>level</c> and <c>hitDie</c>;</item>
/// <item>ability scores go to the stats fields named after each ability (<c>strength</c>, ...), without racial bonuses;</item>
/// <item>each boost (allocate) pick adds 1 to the ability's modifier field (PF2e <c>strengthMod</c>, ...), or else to its
/// score (5e <c>strength</c>: a race's abilities of choice);</item>
/// <item>spells go to <c>spells</c>; any other step goes to the stats field named by its <c>target:</c> or key
/// (<c>race</c>, <c>background</c>), a pick joining a list field (<c>feats</c>);</item>
/// <item>level choices (<see cref="LevelChoiceSlot"/>) are recorded per slot at its level, as level_up records them; an
/// ability score improvement also raises the scores (after the ability step, whatever the order), and a feat taken
/// instead joins the stats' <c>feats</c>;</item>
/// <item>identity fields named in <see cref="PsychologyFields"/> (descriptors, drives, fears) go to the character's
/// psychology (traits, wants, fears), not its stats;</item>
/// <item>a step with no such field is recorded as a level-1 choice (<c>levelUpChoices</c>, one record per value) under its
/// <c>target:</c> or key, which the bootstrap steps read (5e skills: key or target <c>skills</c>).</item>
/// </list>
/// </summary>
internal static class DraftCharacterMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Character ToCharacter(
        CharacterDraft draft,
        string system,
        string id,
        IReadOnlyList<CreationStep> steps,
        CreationSources sources,
        IReadOnlyDictionary<string, StatBlockSchema>? statBlocks = null,
        LevelChoicesApplied? levels = null)
    {
        var level = Math.Max(1, draft.Level);
        var stats = SystemStatsMerger.CreateDefault(system);
        var character = new Character
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(draft.Name) ? "Unnamed" : draft.Name.Trim(),
            Notes = string.IsNullOrWhiteSpace(draft.Concept) ? null : draft.Concept.Trim(),
            CurrentAppearance = string.IsNullOrWhiteSpace(draft.Look) ? null : draft.Look.Trim(),
            IsPc = draft.Kind.Equals(RecipeCharacterCreation.PcKind, StringComparison.OrdinalIgnoreCase),
            IsPartyCompanion = draft.Kind.Equals(RecipeCharacterCreation.CompanionKind, StringComparison.OrdinalIgnoreCase),
            SystemStats = stats,
        };

        var notes = new List<string>();
        foreach (var step in steps)
        {
            if (!draft.Choices.TryGetValue(step.Key, out var value) || !draft.Has(step.Key))
                continue;

            switch (step.Kind)
            {
                case CreationStepKinds.PickOne when string.Equals(step.Source, CreationSources.Classes, StringComparison.OrdinalIgnoreCase):
                    ApplyClass(character, stats, system, draft.GetString(step.Key), level, sources);
                    break;
                case CreationStepKinds.AbilityScores:
                    foreach (var (ability, score) in draft.Get<AbilityScoreChoice>(step.Key)?.Scores ?? [])
                        TrySet(stats, ability, JsonSerializer.SerializeToElement(score, Json));
                    break;
                case CreationStepKinds.Spells:
                    TrySet(stats, step.Target ?? "spells", value);
                    break;
                case CreationStepKinds.LevelChoices:
                    // After the loop: the improvements add to the scores the ability step sets.
                    break;
                case CreationStepKinds.Allocate:
                    // Boosts: +1 to the modifier of each ability picked (PF2e's strengthMod, ...). Ancestry boosts and
                    // flaws aren't picks; the bootstrap applies them from the ancestry, like racial bonuses.
                    foreach (var ability in draft.GetList(step.Key))
                    {
                        if (!Boost(stats, ability))
                            Record(stats, step.Key, [ability], 1);
                    }

                    break;
                case CreationStepKinds.Identity:
                    // The stat block (companions): each field to the stats field of that name, the psychology
                    // (descriptors, drives, fears), or (attacks, traits, stance) a line of the notes. A key that is neither is flagged by statBlock.fields, never dropped quietly.
                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        var schema = step.Schema is { } name && statBlocks?.TryGetValue(name, out var s) == true ? s : null;
                        foreach (var field in value.EnumerateObject())
                        {
                            var def = schema?.Fields.FirstOrDefault(f => f.Key.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
                            if (Psychology(character, field.Name) is { } list)
                            {
                                list.Clear();
                                list.AddRange(ListEntries(field.Value));
                            }
                            else if (IsNotesField(field.Name))
                                notes.Add($"{def?.Label ?? field.Name}: {NotesText(field.Value, def, system)}");
                            else if (def is { Type: "list" })
                                TrySet(stats, field.Name, JsonSerializer.SerializeToElement(ListEntries(field.Value), Json));
                            else if (def is { Type: "modifiers" })
                                TrySet(stats, field.Name, Canonical(StatModifierText.Normalize(field.Value), CreationSources.FieldNames(system, def.Source)));
                            else
                                TrySet(stats, field.Name, field.Value);
                        }
                    }

                    break;
                default:
                    if (!TrySet(stats, step.Target ?? step.Key, value) && !Join(stats, step.Target ?? step.Key, draft.GetList(step.Key)))
                        Record(stats, step.Target ?? step.Key, draft.GetList(step.Key), 1);
                    break;
            }
        }

        if (levels is { Slots.Count: > 0 })
            ApplyLevelChoices(stats, levels);

        // The background's gold starts the purse (the gold pool keeps a current value it already has).
        if (Background(draft, system, steps, sources) is { Gold: > 0 } background)
            stats.ResourcePools["gold"] = new ResourcePool { Current = background.Gold, Max = background.Gold, Recovery = RecoveryType.Never };

        // A companion has no class, so its level is the draft's.
        if (character.IsPartyCompanion)
            TrySet(stats, "level", JsonSerializer.SerializeToElement(level, Json));

        if (notes.Count > 0)
            character.Notes = string.Join(" ", new[] { character.Notes }.Concat(notes).Where(n => !string.IsNullOrWhiteSpace(n)));

        return character;
    }

    /// <summary>Stat block fields that have no stats field and are kept as text in the character's notes.</summary>
    public static readonly string[] NotesFields = ["creatureType", "challengeRating", "attacks", "traits", "stance"];

    /// <summary>A modifiers object with each name in its source's spelling ("perception" → "Perception"): stats dictionaries are case-sensitive.</summary>
    private static JsonElement Canonical(JsonElement value, IReadOnlyList<string> names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return value;

        var canonical = new Dictionary<string, JsonElement>();
        foreach (var entry in value.EnumerateObject())
            canonical[names.FirstOrDefault(n => n.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)) ?? entry.Name] = entry.Value;
        return JsonSerializer.SerializeToElement(canonical, Json);
    }

    /// <summary>
    /// A notes field as its line shows it, ending as a sentence: rows in their text form, a choice in its list's spelling,
    /// the rest as written.
    /// </summary>
    private static string NotesText(JsonElement value, StatBlockField? def, string system)
    {
        if (def is { Type: "rows" })
            return Sentence(StatRowsText.Format(value, def.Columns));
        if (def is { Type: "choice" } && value.ValueKind == JsonValueKind.String)
            return Sentence(CreationSources.FieldNames(system, def.Source).FirstOrDefault(n => n.Equals(value.GetString()!.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.GetString()!);
        return Sentence(value.ToString().Trim());
    }

    /// <summary>Ends with a full stop, so the next notes line reads as its own sentence.</summary>
    private static string Sentence(string text) => text.Length == 0 || text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";

    public static bool IsNotesField(string key) => NotesFields.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Identity fields that are who the character is rather than stats (Narrative's character fields), and the
    /// <see cref="PsychologyProfile"/> list each one fills: descriptors are its traits, drives its wants.
    /// </summary>
    public static readonly string[] PsychologyFields = ["descriptors", "drives", "fears"];

    public static bool IsPsychologyField(string key) => PsychologyFields.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The psychology list a field fills, or null when it isn't one of <see cref="PsychologyFields"/>.</summary>
    public static List<string>? Psychology(Character character, string key) => key.ToLowerInvariant() switch
    {
        "descriptors" => character.Psychology.Traits,
        "drives" => character.Psychology.Wants,
        "fears" => character.Psychology.Fears,
        _ => null,
    };

    /// <summary>A <c>list</c> field's entries: an array of text, or one text split at commas, semicolons and line breaks.</summary>
    public static List<string> ListEntries(JsonElement value)
    {
        IEnumerable<string> raw = value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!),
            JsonValueKind.String => value.GetString()!.Split([',', ';', '\n'], StringSplitOptions.None),
            _ => [],
        };
        return [.. raw.Select(e => e.Trim().TrimEnd('.')).Where(e => e.Length > 0)];
    }

    /// <summary>True when a stats field has this JSON or property name and can be written.</summary>
    public static bool HasStatsField(SystemExtension stats, string name) => FindProperty(stats, name) is not null;

    private static PropertyInfo? FindProperty(SystemExtension stats, string name) =>
        stats.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.CanWrite && (
                string.Equals(p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name, name, StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    private static void ApplyClass(Character character, SystemExtension stats, string system, string? className, int level, CreationSources sources)
    {
        if (string.IsNullOrWhiteSpace(className))
            return;

        var cls = sources.ClassProvider.TryResolveClass(system, className, out var def) ? def : null;
        var label = CreationSources.Label(cls?.Name ?? className);
        character.ClassLevel = $"{label} {level}";
        TrySet(stats, "classLevels", JsonSerializer.SerializeToElement(new[] { new ClassLevelEntry { Class = label, Level = level } }, Json));
        TrySet(stats, "level", JsonSerializer.SerializeToElement(level, Json));
        if (cls?.HitDie is { } hitDie)
            TrySet(stats, "hitDie", JsonSerializer.SerializeToElement(hitDie, Json));
    }

    /// <summary>The background template the draft picked (a pickOne step from <c>backgrounds</c>), or null.</summary>
    internal static BackgroundDefinition? Background(CharacterDraft draft, string system, IReadOnlyList<CreationStep> steps, CreationSources sources) =>
        steps.Where(s => s.Kind == CreationStepKinds.PickOne && string.Equals(s.Source, CreationSources.Backgrounds, StringComparison.OrdinalIgnoreCase))
            .Select(s => draft.GetString(s.Key) is { } id ? sources.Template(system, s.Source, id) as BackgroundDefinition : null)
            .FirstOrDefault(b => b is not null);

    /// <summary>Adds the picks to the stats' list field of that name (<c>feats</c>); false when there is no such list.</summary>
    private static bool Join(SystemExtension stats, string name, IReadOnlyList<string> picks)
    {
        if (FindProperty(stats, name)?.GetValue(stats) is not List<string> list)
            return false;

        list.AddRange(picks.Where(p => !list.Contains(p, StringComparer.OrdinalIgnoreCase)));
        return true;
    }

    /// <summary>
    /// +1 to the stats field <c>&lt;ability&gt;Mod</c> (PF2e), else to the score <c>&lt;ability&gt;</c> (5e); false when
    /// there is no such whole-number field.
    /// </summary>
    private static bool Boost(SystemExtension stats, string ability)
    {
        var prop = FindProperty(stats, ability.Trim() + "Mod") is { PropertyType: var t } mod && t == typeof(int)
            ? mod
            : FindProperty(stats, ability.Trim());
        if (prop?.PropertyType != typeof(int))
            return false;

        prop.SetValue(stats, (int)prop.GetValue(stats)! + 1);
        return true;
    }

    /// <summary>
    /// One record per pick at its slot's level (its option id; an improvement as "Intelligence +2"; a boost or skill
    /// increase by name), a feat taken instead of an improvement added to the stats' feats, and the increases added: to
    /// the ability score (5e <c>intelligence</c>), else to its modifier (PF2e <c>intelligenceMod</c>).
    /// </summary>
    internal static void ApplyLevelChoices(SystemExtension stats, LevelChoicesApplied levels)
    {
        foreach (var (ability, amount) in levels.Increases)
        {
            var prop = FindProperty(stats, ability) is { PropertyType: var t } p && t == typeof(int) ? p : FindProperty(stats, ability + "Mod");
            if (prop?.PropertyType == typeof(int))
                prop.SetValue(stats, (int)prop.GetValue(stats)! + amount);
        }

        foreach (var slot in levels.Slots)
        {
            if (!levels.Picks.TryGetValue(slot.Id, out var chosen))
                continue;

            var increases = slot.IsAsi ? slot.Increases(chosen) : [];
            if (increases.Count > 0)
            {
                Record(stats, slot.Key, [string.Join(", ", increases.Select(i => $"{i.Ability} +{i.Amount}"))], slot.Level, levels.Class);
                continue;
            }

            var ids = chosen.Select(p => slot.Options.FirstOrDefault(o => o.Id.Equals(p, StringComparison.OrdinalIgnoreCase))?.Id ?? p).ToList();
            Record(stats, slot.Key, ids, slot.Level, levels.Class);
            if (slot.IsAsi && FindProperty(stats, "feats")?.GetValue(stats) is List<string> feats)
                feats.AddRange(ids.Where(id => !feats.Contains(id, StringComparer.OrdinalIgnoreCase)));
        }
    }

    private static void Record(SystemExtension stats, string key, IReadOnlyList<string> values, int level, string? className = null)
    {
        foreach (var value in values)
            stats.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = level, Class = className, Key = key, Value = value });
    }

    /// <summary>Writes a JSON value to the stats field with that JSON name (or property name); false when there is none or it doesn't fit.</summary>
    private static bool TrySet(SystemExtension stats, string name, JsonElement value)
    {
        var prop = FindProperty(stats, name);
        if (prop is null)
            return false;

        try
        {
            prop.SetValue(stats, value.Deserialize(prop.PropertyType, Json));
            return true;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>A draft's level choices for the mapper: every slot, the picks by slot id, and what they add to abilities.</summary>
internal sealed record LevelChoicesApplied(
    IReadOnlyList<LevelChoiceSlot> Slots,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Picks,
    IReadOnlyList<(string Ability, int Amount)> Increases,
    string? Class = null);
