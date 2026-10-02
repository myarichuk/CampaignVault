using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>
/// Adds the spells a character's class features give outright (a domain's, an oath's, a circle's: the feature's
/// <c>spells:</c>) to its prepared list, once the class level reaches them, and the spells its feats give (their own and
/// the ones picked for them) and its race gives by character level to its known list. A cantrip goes to the cantrips instead when <paramref name="spells"/> can
/// tell. Runs at creation and on every level gained, so a subclass picked at level 3 brings its level-3 spells; a spell
/// already listed (prepared, known or cantrip) is left alone.
/// </summary>
public sealed class Dnd5eGrantClassSpellsStep(
    ProgressionDefinitionProvider? progressions,
    FeatDefinitionProvider? feats = null,
    SpellDefinitionProvider? spells = null,
    RaceDefinitionProvider? races = null) : IBootstrapStep, ILevelGainStep
{
    public string Name => "dnd5e.grant_class_spells";

    public bool CanApply(BootstrapContext context) =>
        (progressions is not null || feats is not null || races is not null) && context.Character.SystemStats is Dnd5eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(Grant(context));

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(Grant(context));

    private BootstrapStepResult? Grant(BootstrapContext context)
    {
        var stats = (Dnd5eExtension)context.Character.SystemStats;
        var listed = stats.Spells.Cantrips.Concat(stats.Spells.Known).Concat(stats.Spells.Prepared)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fromFeatures = CharacterClassFeatures.GrantedSpells(context.Character, context.ActiveSystem, progressions)
            .Where(listed.Add)
            .ToList();
        // On a level gain the hit point step, which runs first, has already moved the level on.
        var fromFeats = CharacterFeats.Spells(context.Character, context.ActiveSystem, feats)
            .Concat(CharacterRace.Spells(context.Character, context.ActiveSystem, races, stats.Level ?? 1))
            .Where(listed.Add)
            .ToList();
        if (fromFeatures.Count == 0 && fromFeats.Count == 0)
            return null;

        foreach (var spell in fromFeatures)
            (IsCantrip(context.ActiveSystem, spell) ? stats.Spells.Cantrips : stats.Spells.Prepared).Add(spell);
        foreach (var spell in fromFeats)
            (IsCantrip(context.ActiveSystem, spell) ? stats.Spells.Cantrips : stats.Spells.Known).Add(spell);

        var added = fromFeatures.Concat(fromFeats).ToList();
        return new BootstrapStepResult
        {
            StepName = Name,
            Message = $"Added the spells {context.Character.Name}'s class features, feats and race give: {string.Join(", ", added)}.",
        };
    }

    private bool IsCantrip(string system, string spell) =>
        spells is not null && spells.TryGet(system, spell, out var definition) && definition is { Level: 0 };
}
