using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles HpChange using the pre-loaded character (safe pattern).
/// </summary>
public sealed class HpChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is HpChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var hp = (HpChange)change;

        if (!context.Characters.TryGetValue(hp.CharacterId, out var character))
        {
            character = context.Session != null ? await context.Session.LoadAsync<Character>(hp.CharacterId, ct) : null;
            if (character == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(hp.CharacterId);
                var msg = $"Character {hp.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewCharacter(character);
        }

        if (character.MaxHp <= 0)
        {
            context.Logger.LogWarning("HpChange skipped for {CharacterId}: MaxHp is {MaxHp} (not a combatant?)", hp.CharacterId, character.MaxHp);
            context.RecordMessage($"WARNING: HpChange skipped for {hp.CharacterId} — MaxHp is {character.MaxHp}. Set MaxHp > 0 to enable HP tracking.");
            context.RecordFailure();
            return ChangeHandlerResult.Failure();
        }

        var damageTaken = hp.Delta < 0 ? -hp.Delta : 0;
        character.CurrentHp = Math.Clamp(character.CurrentHp + hp.Delta, 0, character.MaxHp);
        context.RecordMessage($"HP adjusted for {hp.CharacterId} by {hp.Delta} (now {character.CurrentHp}/{character.MaxHp})");

        // Check concentration break on damage (DC 10 or half damage, whichever is higher)
        if (damageTaken > 0 && character.SystemStats?.StatusEffects != null)
        {
            var concentration = character.SystemStats.StatusEffects.FirstOrDefault(e => e.Name.Contains("Concentration", StringComparison.OrdinalIgnoreCase));
            if (concentration != null)
            {
                var concentrationDc = Math.Max(10, (int)Math.Ceiling(damageTaken / 2.0f));
                // For now, concentration breaks automatically if damage >= DC (no actual save roll).
                // Full implementation would roll CON save and only break on failure.
                if (damageTaken >= concentrationDc)
                {
                    character.SystemStats.StatusEffects.Remove(concentration);
                    context.RecordMessage($"Concentration broken for {hp.CharacterId} due to {damageTaken} damage (DC {concentrationDc})");
                }
            }
        }

        return ChangeHandlerResult.Ok;
    }
}