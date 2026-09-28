using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>Applies an <see cref="ApplyEffectChange"/> as a status effect: clamped, expiring, one per key.</summary>
public sealed class ApplyEffectChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ApplyEffectChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var req = (ApplyEffectChange)change;

        if (!context.Characters.TryGetValue(req.CharacterId ?? "", out var character))
        {
            character = string.IsNullOrWhiteSpace(req.CharacterId) ? null : await ctx.Session.LoadAsync<Character>(req.CharacterId, ct);
            if (character is null)
            {
                var hint = await context.SuggestCharacterMatchAsync(req.CharacterId ?? "");
                return ChangeHandlerResult.Failure($"Character {req.CharacterId} not found." + (hint != null ? $" Did you mean: {hint}?" : ""));
            }

            context.RegisterNewCharacter(character);
        }

        if (!EffectTiers.TryClamp(req, out var tier, out var modifiers, out var hours, out var notes, out var error))
            return ChangeHandlerResult.Failure($"apply_effect '{req.Key}': {error}");

        var time = await context.GetCurrentTimeAsync();
        var nowDays = time.TotalDaysElapsed + time.Hour / 24.0;
        float? expires = hours is { } h ? (float)(nowDays + h / 24.0) : null;

        character.SystemStats ??= new SystemExtension();
        var effects = character.SystemStats.StatusEffects ??= [];
        var key = req.Key.Trim();
        var buff = req.Valence.Trim().Equals("buff", StringComparison.OrdinalIgnoreCase);

        var existing = effects.FirstOrDefault(e => string.Equals(e.EffectKey, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // Reapplying refreshes to the stronger value; it never adds a second copy.
            foreach (var (stat, value) in modifiers)
            {
                if (!existing.StatModifiers.TryGetValue(stat, out var old) || Math.Sign(old) != Math.Sign(value) || Math.Abs(value) > Math.Abs(old))
                    existing.StatModifiers[stat] = value;
            }

            existing.Name = req.Name.Trim();
            if (RankOf(tier.Name) >= RankOf(existing.EffectTier))
                existing.EffectTier = tier.Name;
            existing.ExpiresAtDay = tier.Name == EffectTiers.Persistent
                ? null
                : Math.Max(expires!.Value, existing.ExpiresAtDay ?? expires.Value);
            existing.RecoveryHint = req.RecoveryHint ?? existing.RecoveryHint;
            Report(context, character, existing, notes, "refreshed");
            return ChangeHandlerResult.Ok;
        }

        if (tier.Name != EffectTiers.Persistent)
        {
            var sameSide = effects.Count(e =>
                e.EffectTier is { } t && t != EffectTiers.Persistent && EffectTiers.IsBuff(e) == buff);
            if (sameSide >= EffectTiers.MaxEventEffectsPerSide)
            {
                context.RecordMessage(
                    $"apply_effect '{req.Key}' NOT applied: {character.Name} already carries {sameSide} {(buff ? "buffs" : "debuffs")} " +
                    "from events. Let one run out (or refresh it with its own key) first.");
                return ChangeHandlerResult.Ok;
            }
        }

        var effect = new StatusEffect
        {
            Name = req.Name.Trim(),
            Category = string.IsNullOrWhiteSpace(req.Category)
                ? tier.Name == EffectTiers.Persistent ? (buff ? "Buff" : "Curse") : (buff ? "Buff" : "Injury")
                : req.Category.Trim(),
            StatModifiers = modifiers,
            ExpiresAtDay = expires,
            RecoveryHint = req.RecoveryHint ?? req.Removal,
            AppliedBy = string.IsNullOrWhiteSpace(req.ImposedBy) ? "apply_effect" : req.ImposedBy.Trim(),
            EffectKey = key,
            EffectTier = tier.Name,
        };
        effects.Add(effect);
        Report(context, character, effect, notes, "applied");
        return ChangeHandlerResult.Ok;
    }

    private static int RankOf(string? tier) => EffectTiers.All.ToList().FindIndex(t => t.Name == tier);

    private static void Report(IChangeContext context, Character character, StatusEffect effect, List<string> notes, string verb)
    {
        var mods = string.Join(", ", effect.StatModifiers.Select(kv => $"{kv.Key} {kv.Value:+0.##;-0.##}"));
        var span = effect.ExpiresAtDay is null ? "until removed" : "expires by the clock";
        var clamp = notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "";
        context.RecordMessage($"{character.Name}: '{effect.Name}' {verb} [{effect.EffectTier}] {mods}, {span}.{clamp}");
        context.RecordPhysicalStateNudge($"{character.Name} is now affected by '{effect.Name}'.");
    }
}
