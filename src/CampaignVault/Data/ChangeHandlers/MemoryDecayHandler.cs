using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class MemoryDecayHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is MemoryDecay;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var decay = (MemoryDecay)change;

        if (string.IsNullOrWhiteSpace(decay.CharacterId))
            return ChangeHandlerResult.Failure("characterId is required.");

        if (!context.Characters.TryGetValue(decay.CharacterId, out var character))
        {
            character = context.Session != null
                ? await context.Session.LoadAsync<Character>(decay.CharacterId, ct)
                : null;

            if (character == null)
                return ChangeHandlerResult.Failure($"Character '{decay.CharacterId}' not found.");

            context.RegisterNewCharacter(character);
        }

        if (character.Psychology?.Memories == null)
            return ChangeHandlerResult.Ok;

        foreach (var (entryKey, (newSalience, newUrgency, evict)) in decay.EntryChanges)
        {
            if (!character.Psychology.Memories.TryGetValue(entryKey, out var memory))
                continue;

            if (evict)
            {
                character.Psychology.Memories.Remove(entryKey);
            }
            else
            {
                if (newSalience.HasValue)
                    memory.Saliency = newSalience.Value;
                if (newUrgency.HasValue)
                    memory.Urgency = newUrgency.Value;
            }
        }

        return ChangeHandlerResult.Ok;
    }
}
