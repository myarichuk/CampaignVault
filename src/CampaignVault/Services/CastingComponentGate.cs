using CampaignVault.Data;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Services;

/// <summary>
/// Shared logic for gating a Spell RulesetAction on the caster's condition state: standard
/// incapacitation, tagged component blocks/waivers on active StatusEffects, and passive
/// feat-granted waivers. Consulted by RulesetActionHandler — the single choke point every
/// RulesetAction passes through regardless of ruleset system.
///
/// The engine can't enumerate every homebrew condition (mind control, magical silence zones,
/// etc.) in advance. Instead of trying, it recognizes a small set of well-known StatModifiers
/// keys the LLM DM sets when applying *any* condition (standard or homebrew) that should affect
/// spellcasting — see BlocksVerbalComponents/BlocksSomaticComponents/BlocksMaterialComponents/
/// BlocksAllActions and their Waives* counterparts. An untagged homebrew condition falls back to
/// a soft RecordMessage warning rather than a silent pass or an over-eager hard block.
///
/// DELIBERATELY queries CustomSpell/CustomFeat directly via RavenDB indexes rather than going
/// through CampaignRepository: CampaignRepository depends on WorldChangeDispatcher, which depends
/// on every IWorldChangeHandler (including RulesetActionHandler) — injecting it here would create
/// an Autofac circular dependency.
/// </summary>
public static class CastingComponentGate
{
    /// <summary>Standard conditions that prevent taking any action at all, in either system.</summary>
    public static readonly IReadOnlySet<string> HardBlockConditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "incapacitated", "paralyzed", "petrified", "stunned", "unconscious",
    };

    public const string BlocksVerbal = "BlocksVerbalComponents";
    public const string BlocksSomatic = "BlocksSomaticComponents";
    public const string BlocksMaterial = "BlocksMaterialComponents";
    public const string BlocksAllActions = "BlocksAllActions";
    public const string WaivesVerbal = "WaivesVerbalComponents";
    public const string WaivesSomatic = "WaivesSomaticComponents";
    public const string WaivesMaterial = "WaivesMaterialComponents";

    /// <summary>Passive FeatDefinition/CustomFeat CastingWaivers value for feats like War Caster.</summary>
    public const string SomaticHandsFullWaiver = "SomaticHandsFull";

    public sealed record SpellComponents(bool Verbal, bool Somatic, bool Material);

    /// <summary>
    /// Resolves a spell's component requirements, checking the SRD/YAML SpellDefinitionProvider
    /// first, then falling back to campaign-scoped homebrew CustomSpell — so a DM-authored spell's
    /// declared components are actually checked, not silently skipped. Returns null if the spell
    /// name doesn't match either source (nothing to check against).
    /// </summary>
    public static async Task<SpellComponents?> ResolveSpellComponentsAsync(
        IAsyncDocumentSession session,
        SpellDefinitionProvider spellProvider,
        string system,
        string spellName,
        string? campaignName)
    {
        // RulesetAction.ActionName is documented/used as a free-text title (e.g. "Fire Bolt"), but
        // SpellDefinition/CustomSpell keys are slugs (e.g. "fire_bolt"). Normalize before matching
        // either source, or nearly every multi-word spell name would silently miss.
        var slug = NormalizeSlug(spellName);

        if (spellProvider.TryGet(system, slug, out var srd) && srd != null)
        {
            return new SpellComponents(srd.Verbal ?? false, srd.Somatic ?? false, srd.Material ?? false);
        }

        var effectiveCampaign = campaignName != null && CampaignSlug.TryCanonicalize(campaignName, out var slugged) ? slugged : campaignName;

        var customSpells = await session.Query<CustomSpell, CustomSpell_Search>()
            .Where(s => s.System == system)
            .ToListAsync();

        var custom = customSpells.FirstOrDefault(s =>
            !s.IsArchived
            && NormalizeSlug(s.Name) == slug
            && (effectiveCampaign == null || CampaignEntityVisibility.IsVisibleInCampaign(s.CampaignName, effectiveCampaign)));

        return custom == null
            ? null
            : new SpellComponents(custom.Verbal ?? false, custom.Somatic ?? false, custom.Material ?? false);
    }

    private static string NormalizeSlug(string name) =>
        name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    /// <summary>Every feat slug the character knows, regardless of which system-specific list it lives in.</summary>
    public static IEnumerable<string> GetKnownFeatNames(SystemExtension? stats) => stats switch
    {
        Dnd5eExtension d => d.Feats,
        Pf2eExtension p => p.AncestryFeats.Concat(p.ClassFeats).Concat(p.SkillFeats).Concat(p.GeneralFeats),
        _ => [],
    };

    /// <summary>
    /// True if any of the character's known feats (SRD or homebrew CustomFeat) declares the given
    /// passive CastingWaivers value.
    /// </summary>
    public static async Task<bool> HasCastingWaiverAsync(
        IAsyncDocumentSession session,
        FeatDefinitionProvider featProvider,
        string system,
        IEnumerable<string> knownFeatNames,
        string waiver,
        string? campaignName)
    {
        var names = knownFeatNames as ICollection<string> ?? knownFeatNames.ToList();
        if (names.Count == 0)
        {
            return false;
        }

        foreach (var name in names)
        {
            if (featProvider.TryGet(system, name, out var feat) && feat != null
                && feat.CastingWaivers.Contains(waiver, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var effectiveCampaign = campaignName != null && CampaignSlug.TryCanonicalize(campaignName, out var slugged) ? slugged : campaignName;

        var customFeats = await session.Query<CustomFeat, CustomFeat_Search>()
            .Where(f => f.System == system)
            .ToListAsync();

        return customFeats.Any(f =>
            !f.IsArchived
            && names.Contains(f.Name, StringComparer.OrdinalIgnoreCase)
            && f.CastingWaivers.Contains(waiver, StringComparer.OrdinalIgnoreCase)
            && (effectiveCampaign == null || CampaignEntityVisibility.IsVisibleInCampaign(f.CampaignName, effectiveCampaign)));
    }
}
