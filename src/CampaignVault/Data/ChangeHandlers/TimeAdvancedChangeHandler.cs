using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>Hands a span of elapsed time to the plugins' time observers. See <see cref="TimeAdvancedChange"/>.</summary>
public class TimeAdvancedChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is TimeAdvancedChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var span = (TimeAdvancedChange)change;
        await SweepExpiredEffectsAsync(span, ctx, ct);
        await ctx.Dispatcher.NotifyTimeObserversAsync(span, ctx, ct);
        return ChangeHandlerResult.Ok;
    }

    /// <summary>
    /// Drops <c>apply_effect</c> effects whose hour-scale expiry has passed for the characters that lived the span.
    /// The day-level <c>StatusExpiryRule</c> only compares whole days, so an 8-hour sprain would otherwise linger to
    /// the next midnight. Effects tied to a condition template keep that template's own expiry rules.
    /// </summary>
    private static async Task SweepExpiredEffectsAsync(TimeAdvancedChange span, ChangeContext ctx, CancellationToken ct)
    {
        var now = span.TotalHoursAfter / 24.0;
        foreach (var id in span.CharacterIds)
        {
            if (!ctx.Characters.TryGetValue(id, out var character))
            {
                character = ctx.Session is null ? null : await ctx.Session.LoadAsync<Character>(id, ct);
                if (character is null)
                    continue;
                ctx.RegisterNewCharacter(character);
            }

            if (character.SystemStats is { } stats && span.Source == "rest" &&
                CampaignVault.Rulesets.WillpowerRules.RestStep(stats) is > 0 and var restored)
                ctx.RecordMessage($"{character.Name} recovers {restored:0.#} willpower ({stats.Willpower:0.#}).");

            var effects = character.SystemStats?.StatusEffects;
            if (effects is null)
                continue;

            var expired = effects
                .Where(e => e.EffectKey is not null && e.ExpiresAtDay is { } day && day <= now && e.ConditionName is null)
                .ToList();
            foreach (var effect in expired)
            {
                effects.Remove(effect);
                ctx.RecordMessage($"{character.Name}: '{effect.Name}' has worn off.");
            }
        }
    }
}
