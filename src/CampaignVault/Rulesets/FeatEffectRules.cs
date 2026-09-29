using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets;

/// <summary>What feat effects did to one roll: the added bonus and the reasons to print.</summary>
internal sealed record FeatEffectFold(int Bonus, IReadOnlyList<string> Notes)
{
    public static readonly FeatEffectFold None = new(0, []);
}

/// <summary>
/// Declarative feat effects: validation, plugin/mode gating, resolving which effects are live for a character, and folding
/// them into a roll. The vocabulary is closed and every magnitude is data: a model can only choose a toggle or assert a
/// condition, never supply a number.
/// </summary>
internal static class FeatEffectRules
{
    private static readonly string[] WeaponConditions = ["ranged", "melee", "finesse", "twohanded", "heavy"];

    /// <summary>Empty when the effects are well-formed; otherwise one message per problem.</summary>
    public static IReadOnlyList<string> Validate(IEnumerable<FeatEffect>? effects)
    {
        var errors = new List<string>();
        var index = 0;
        foreach (var e in effects ?? [])
        {
            index++;
            var at = $"effects[{index}]";
            if (!FeatEffectKinds.All.Contains(e.Kind, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{at}: unknown kind '{e.Kind}' (allowed: {string.Join(", ", FeatEffectKinds.All)}). For anything else set adjudicated=true and describe it in mechanicalSummary.");
            if (e.Value == 0)
                errors.Add($"{at}: value must be non-zero.");
            if (e.BonusType is not null && !FeatBonusTypes.All.Contains(e.BonusType, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{at}: unknown bonusType '{e.BonusType}' (allowed: {string.Join(", ", FeatBonusTypes.All)}).");
            foreach (var w in e.Weapon.Where(w => !WeaponConditions.Contains(CombatFeatureRules.Norm(w))))
                errors.Add($"{at}: unknown weapon condition '{w}' (allowed: ranged, melee, finesse, twoHanded, heavy).");
            if (e.Assert.Any(string.IsNullOrWhiteSpace))
                errors.Add($"{at}: assert flags must be non-empty names.");
            if (e.NeedsAssertion && string.IsNullOrWhiteSpace(e.When))
                errors.Add($"{at}: an effect with assert flags needs a 'when' sentence so the DM knows what to judge.");
            if (e.Subject is not null && !e.Kind.Equals(FeatEffectKinds.SkillBonus, StringComparison.OrdinalIgnoreCase)
                && !e.Kind.Equals(FeatEffectKinds.SaveBonus, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{at}: subject only applies to skillBonus and saveBonus.");
        }

        return errors;
    }

    // ---- plugin / mode gating -------------------------------------------------------------------------------------

    /// <summary>The plugin half only: is the feat's plugin loaded? Scene-independent, so lookups and handbooks can filter on it.</summary>
    public static bool PluginAvailable(FeatRequirement? requires) =>
        requires is null
        || string.IsNullOrWhiteSpace(requires.Plugin)
        || PluginDataRoots.LoadedPluginIds.Contains(requires.Plugin, StringComparer.OrdinalIgnoreCase);

    /// <summary>Full gate: plugin loaded and, when a mode is named, that mode running (and owned by the named plugin).</summary>
    public static bool IsAvailable(FeatRequirement? requires, IReadOnlyCollection<string> activeModeIds)
    {
        if (requires is null)
            return true;
        if (!PluginAvailable(requires))
            return false;
        if (string.IsNullOrWhiteSpace(requires.Mode))
            return true;
        if (!activeModeIds.Contains(requires.Mode, StringComparer.OrdinalIgnoreCase))
            return false;
        return string.IsNullOrWhiteSpace(requires.Plugin)
            || !PluginDataRoots.ModeOwners.TryGetValue(requires.Mode, out var owner)
            || owner.Equals(requires.Plugin, StringComparison.OrdinalIgnoreCase);
    }

    // ---- resolving what is live -----------------------------------------------------------------------------------

    /// <summary>Every feat name the sheet records: the feat lists plus recorded level-up choices (a choice value can be a feat).</summary>
    public static IReadOnlyCollection<string> KnownFeatNames(SystemExtension? stats)
    {
        if (stats is null)
            return [];
        return [.. CastingComponentGate.GetKnownFeatNames(stats).Concat(stats.LevelUpChoices.Select(c => c.Value))
            .Where(n => !string.IsNullOrWhiteSpace(n))];
    }

    /// <summary>
    /// The effects live for each character now: SRD/YAML feats overlaid by homebrew CustomFeat of the same name, filtered by
    /// plugin/mode. Homebrew is read straight from the index (like the casting gate) to avoid a repository dependency cycle.
    /// </summary>
    public static async Task<Dictionary<string, List<ActiveFeatEffect>>> ResolveAsync(
        IAsyncDocumentSession session,
        FeatDefinitionProvider featProvider,
        string system,
        IEnumerable<Character> characters,
        string? campaignName,
        IReadOnlyCollection<string> activeModeIds)
    {
        var result = new Dictionary<string, List<ActiveFeatEffect>>(StringComparer.OrdinalIgnoreCase);
        var involved = characters.Where(c => KnownFeatNames(c.SystemStats).Count > 0).ToList();
        if (involved.Count == 0)
            return result;

        var catalog = await CatalogAsync(session, featProvider, system, campaignName);
        foreach (var character in involved)
        {
            var live = new List<ActiveFeatEffect>();
            foreach (var name in KnownFeatNames(character.SystemStats).Select(CombatFeatureRules.Norm).Distinct())
            {
                if (!catalog.TryGetValue(name, out var feat) || !IsAvailable(feat.Requires, activeModeIds))
                    continue;
                live.AddRange(feat.Effects
                    .Where(e => IsAvailable(e.Requires, activeModeIds))
                    .Select(e => new ActiveFeatEffect(feat.Name, e)));
            }

            if (live.Count > 0)
                result[character.Id] = live;
        }

        return result;
    }

    /// <summary>The feat's name, effects and gate, by normalized name; homebrew wins over SRD.</summary>
    internal sealed record CatalogFeat(string Name, List<FeatEffect> Effects, FeatRequirement? Requires, bool Adjudicated, bool HasOtherMechanics, bool Homebrew);

    public static async Task<Dictionary<string, CatalogFeat>> CatalogAsync(
        IAsyncDocumentSession session, FeatDefinitionProvider featProvider, string system, string? campaignName)
    {
        var catalog = new Dictionary<string, CatalogFeat>();
        foreach (var (key, def) in featProvider.GetFeatsForSystem(system))
        {
            var entry = new CatalogFeat(def.Name ?? key, def.Effects, def.Requires, def.Adjudicated,
                def.CastingWaivers.Count > 0 || def.ExtraPools.Count > 0, Homebrew: false);
            catalog[CombatFeatureRules.Norm(key)] = entry;
            if (!string.IsNullOrWhiteSpace(def.Name))
                catalog[CombatFeatureRules.Norm(def.Name)] = entry;
        }

        var effective = campaignName != null && CampaignSlug.TryCanonicalize(campaignName, out var slugged) ? slugged : campaignName;
        var custom = await session.Query<CustomFeat, CustomFeat_Search>().Where(f => f.System == system).ToListAsync();
        foreach (var f in custom.Where(f => !f.IsArchived
                     && (effective == null || CampaignEntityVisibility.IsVisibleInCampaign(f.CampaignName, effective))))
        {
            catalog[CombatFeatureRules.Norm(f.Name)] = new CatalogFeat(f.Name, f.Effects, f.Requires, f.Adjudicated,
                f.CastingWaivers.Count > 0, Homebrew: true);
        }

        return catalog;
    }

    // ---- folding into a roll --------------------------------------------------------------------------------------

    private static string? RollKindOf(string kind) => kind.ToLowerInvariant() switch
    {
        "attackbonus" => RollKinds.Attack,
        "damagebonus" => RollKinds.Damage,
        "skillbonus" => RollKinds.Check,
        "savebonus" => RollKinds.Save,
        "armorclassbonus" => RollKinds.ArmorClass,
        _ => null,
    };

    public static HashSet<string> Asserted(RulesetAction action) =>
        action.Parameters.TryGetValue("assert", out var raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(CombatFeatureRules.Norm).ToHashSet()
            : [];

    private static bool IsTrue(RulesetAction action, string key) =>
        action.Parameters.TryGetValue(key, out var v) && bool.TryParse(v, out var b) && b;

    private static bool WeaponMatches(FeatEffect e, WeaponProfile weapon) => e.Weapon.All(w => CombatFeatureRules.Norm(w) switch
    {
        "ranged" => weapon.Ranged,
        "melee" => weapon.Known && !weapon.Ranged,
        "finesse" => weapon.Finesse,
        "twohanded" => weapon.TwoHanded,
        "heavy" => weapon.Heavy,
        _ => false,
    });

    /// <summary>
    /// The bonus feat effects add to this roll and the reasons. An effect applies when its kind and subject match, its weapon
    /// conditions hold, its toggle is set and every asserted flag was passed; an effect blocked only by a missing assertion is
    /// reported so the DM sees what it could have claimed. In PF2e the highest bonus and worst penalty of each typed kind win.
    /// </summary>
    public static FeatEffectFold Fold(
        string rollKind, string? subject, RulesetAction action, IReadOnlyList<ActiveFeatEffect> effects, string? system, bool isActor = true)
    {
        var weapon = WeaponProfile.Read(action);
        var asserted = Asserted(action);
        var applied = new List<ActiveFeatEffect>();
        var notes = new List<string>();
        var toggleUsed = false;

        foreach (var live in effects)
        {
            var e = live.Effect;
            if (RollKindOf(e.Kind) != rollKind)
                continue;
            if (e.Subject is not null && StatusEffectModifierProvider.Normalize(e.Subject) != StatusEffectModifierProvider.Normalize(subject))
                continue;
            if (!WeaponMatches(e, weapon))
                continue;
            if (e.Toggle is not null && !IsTrue(action, e.Toggle))
                continue;

            var missing = e.Assert.Where(a => !asserted.Contains(CombatFeatureRules.Norm(a))).ToList();
            if (missing.Count > 0)
            {
                notes.Add($"{live.FeatName} {Signed(e.Value)} not applied: needs assert={string.Join(",", missing)}"
                    + (string.IsNullOrWhiteSpace(e.When) ? "" : $" ({e.When})"));
                continue;
            }

            toggleUsed |= e.Toggle is not null;
            applied.Add(live);
        }

        if (isActor && rollKind == RollKinds.Attack && IsTrue(action, "powerAttack") && !toggleUsed)
            notes.Add("powerAttack ignored: no recorded feat has an effect with toggle 'powerAttack' that fits this weapon");

        var total = 0;
        var typed = string.Equals(system, RulesetSystem.Pathfinder2e, StringComparison.OrdinalIgnoreCase);
        foreach (var group in applied.GroupBy(a => typed ? (a.Effect.BonusType ?? FeatBonusTypes.Untyped).ToLowerInvariant() : FeatBonusTypes.Untyped))
        {
            var winners = group.Key == FeatBonusTypes.Untyped
                ? group.ToList()
                : new[] { group.Where(a => a.Effect.Value > 0).MaxBy(a => a.Effect.Value), group.Where(a => a.Effect.Value < 0).MinBy(a => a.Effect.Value) }
                    .Where(a => a is not null).Select(a => a!).ToList();
            foreach (var w in winners)
            {
                total += w.Effect.Value;
                notes.Add($"{w.FeatName} {Signed(w.Effect.Value)}" + (group.Key == FeatBonusTypes.Untyped ? "" : $" ({group.Key})"));
            }
        }

        return notes.Count == 0 && total == 0 ? FeatEffectFold.None : new FeatEffectFold(total, notes);
    }

    // ---- surfacing ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The DM-judged effects a character could claim, one line each: "Long Shot (Hank): assert=highGround when the archer shoots from higher ground → +3 damageBonus".
    /// Toggles are listed too, since the player opts in. Used by the combat-start and turn briefs.
    /// </summary>
    public static IEnumerable<string> ChecklistLines(string characterName, IEnumerable<ActiveFeatEffect> effects) =>
        effects.Where(a => a.Effect.NeedsAssertion || a.Effect.Toggle is not null)
            .GroupBy(a => a.FeatName)
            .Select(g =>
            {
                var parts = g.Select(a => a.Effect.NeedsAssertion
                    ? $"assert={string.Join(",", a.Effect.Assert)} when {a.Effect.When} → {Signed(a.Effect.Value)} {a.Effect.Kind}"
                    : $"parameter {a.Effect.Toggle}=true → {Signed(a.Effect.Value)} {a.Effect.Kind}");
                return $"{g.Key} ({characterName}): {string.Join("; ", parts)}";
            });

    private static string Signed(int v) => v >= 0 ? $"+{v}" : v.ToString();
}
