using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>
/// Adds the spells a character's class features give outright (a domain's, an oath's, a circle's: the feature's
/// <c>spells:</c>) to its prepared list, once the class level reaches them. Runs at creation and on every level gained, so
/// a subclass picked at level 3 brings its level-3 spells; a spell already listed (prepared, known or cantrip) is left alone.
/// </summary>
public sealed class Dnd5eGrantClassSpellsStep(ProgressionDefinitionProvider? progressions) : IBootstrapStep, ILevelGainStep
{
    public string Name => "dnd5e.grant_class_spells";

    public bool CanApply(BootstrapContext context) =>
        progressions is not null && context.Character.SystemStats is Dnd5eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(Grant(context));

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(Grant(context));

    private BootstrapStepResult? Grant(BootstrapContext context)
    {
        var stats = (Dnd5eExtension)context.Character.SystemStats;
        var listed = stats.Spells.Cantrips.Concat(stats.Spells.Known).Concat(stats.Spells.Prepared)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = CharacterClassFeatures.GrantedSpells(context.Character, context.ActiveSystem, progressions)
            .Where(listed.Add)
            .ToList();
        if (added.Count == 0)
            return null;

        stats.Spells.Prepared.AddRange(added);
        return new BootstrapStepResult
        {
            StepName = Name,
            Message = $"Added class feature spells to {context.Character.Name}'s prepared list: {string.Join(", ", added)}.",
        };
    }
}
