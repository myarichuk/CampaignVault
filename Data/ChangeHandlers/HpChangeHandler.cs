using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles HpChange using the pre-loaded character (safe pattern).
/// </summary>
public sealed class HpChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is HpChange;

    public Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var hp = (HpChange)change;

        if (!context.Characters.TryGetValue(hp.CharacterId, out var character) || character is null)
        {
            context.RecordMessage($"WARNING: Character {hp.CharacterId} not found during HpChange.");
            context.RecordFailure();
            return Task.FromResult(ChangeHandlerResult.Failure());
        }

        if (character.MaxHp <= 0)
        {
            context.Logger.LogWarning("HpChange skipped for {CharacterId}: MaxHp is {MaxHp} (not a combatant?)", hp.CharacterId, character.MaxHp);
            context.RecordMessage($"WARNING: HpChange skipped for {hp.CharacterId} — MaxHp is {character.MaxHp}. Set MaxHp > 0 to enable HP tracking.");
            context.RecordFailure();
            return Task.FromResult(ChangeHandlerResult.Failure());
        }

        character.CurrentHp = Math.Clamp(character.CurrentHp + hp.Delta, 0, character.MaxHp);
        context.RecordMessage($"HP adjusted for {hp.CharacterId} by {hp.Delta} (now {character.CurrentHp}/{character.MaxHp})");

        return Task.FromResult(ChangeHandlerResult.Ok);
    }
}