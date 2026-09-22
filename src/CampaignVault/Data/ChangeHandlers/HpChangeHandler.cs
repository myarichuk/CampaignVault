using System.Linq;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles HpChange using the pre-loaded character (safe pattern).
/// </summary>
public sealed class HpChangeHandler(IRollService rollService) : IWorldChangeHandler
{
    private readonly IRollService _rollService = rollService ?? throw new ArgumentNullException(nameof(rollService));

    public bool ShouldHandle(WorldChange change) => change is HpChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var hp = (HpChange)change;

        if (!ctx.Characters.TryGetValue(hp.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(hp.CharacterId, ct);
            if (character == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(hp.CharacterId);
                var msg = $"Character {hp.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                ctx.RecordMessage($"WARNING: {msg}");
                ctx.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            ctx.RegisterNewCharacter(character);
        }

        if (character.MaxHp <= 0)
        {
            ctx.Logger.LogWarning("HpChange skipped for {CharacterId}: MaxHp is {MaxHp} (not a combatant?)", hp.CharacterId, character.MaxHp);
            ctx.RecordMessage($"WARNING: HpChange skipped for {hp.CharacterId} — MaxHp is {character.MaxHp}. Set MaxHp > 0 to enable HP tracking.");
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure();
        }

        var damageTaken = hp.Delta < 0 ? -hp.Delta : 0;
        character.CurrentHp = Math.Clamp(character.CurrentHp + hp.Delta, 0, character.MaxHp);
        ctx.RecordMessage($"HP adjusted for {hp.CharacterId} by {hp.Delta} (now {character.CurrentHp}/{character.MaxHp})");

        // Concentration break check: DC = max(10, half damage taken), save vs DC (CON for 5e, Fortitude for PF2e).
        if (damageTaken > 0 && character.SystemStats?.StatusEffects != null)
        {
            var concentration = character.SystemStats.StatusEffects.FirstOrDefault(e => e.Name.Contains("Concentration", StringComparison.OrdinalIgnoreCase));
            if (concentration != null)
            {
                var dc = Math.Max(10, (int)Math.Ceiling(damageTaken / 2.0f));
                var (saveMod, saveLabel) = GetConcentrationSaveModifier(character.SystemStats);
                var outcome = await _rollService.RollAsync(
                    new RollRequest { Tag = "concentration", Expression = "1d20", Bonus = saveMod }, ct);

                if (outcome.Result < dc)
                {
                    character.SystemStats.StatusEffects.Remove(concentration);
                    ctx.RecordMessage(
                        $"Concentration broken for {hp.CharacterId}: {damageTaken} damage (DC {dc}), {saveLabel} save {outcome.Result} failed.");
                }
                else
                {
                    ctx.RecordMessage(
                        $"Concentration held for {hp.CharacterId}: {damageTaken} damage (DC {dc}), {saveLabel} save {outcome.Result} succeeded.");
                }
            }
        }

        return ChangeHandlerResult.Ok;
    }

    private static (int Modifier, string SaveLabel) GetConcentrationSaveModifier(SystemExtension stats)
    {
        if (stats is Dnd5eExtension dnd5e)
        {
            var matchedKey = dnd5e.SavingThrowModifiers.Keys
                .FirstOrDefault(k => string.Equals(k, "Constitution", StringComparison.OrdinalIgnoreCase));
            if (matchedKey != null && dnd5e.SavingThrowModifiers.TryGetValue(matchedKey, out var saveMod))
            {
                return (saveMod, "CON");
            }

            return (dnd5e.GetAbilityModifier(dnd5e.Constitution), "CON");
        }

        if (stats is Pf2eExtension pf2e)
        {
            var matchedKey = pf2e.SavingThrowModifiers.Keys
                .FirstOrDefault(k => string.Equals(k, "Fortitude", StringComparison.OrdinalIgnoreCase));
            if (matchedKey != null && pf2e.SavingThrowModifiers.TryGetValue(matchedKey, out var saveMod))
            {
                return (saveMod, "Fortitude");
            }

            return (pf2e.ConstitutionMod, "Fortitude");
        }

        // Ruleset not yet wired for concentration saves (e.g. Narrative) — no modifier to apply.
        return (0, "CON");
    }
}