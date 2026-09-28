using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Core's own <see cref="IWorldTimeObserver"/>: each step of time on the road or in camp may produce a consequence beat
/// for the party (see <see cref="ConsequenceBeats"/>). It only signals valence and severity as a hint in the commit
/// result; the DM narrates it and resolves it, typically with <c>apply_effect</c>. It never spawns creatures.
/// </summary>
public sealed class ConsequenceBeatObserver : IWorldTimeObserver
{
    private readonly Func<double> _next;

    /// <summary>Test seam for the DI-constructed observer: when set, replaces the random rolls.</summary>
    internal static Func<double>? RollOverrideForTests { get; set; }

    public ConsequenceBeatObserver() : this(() => (RollOverrideForTests ?? Random.Shared.NextDouble)())
    {
    }

    public ConsequenceBeatObserver(Func<double> next) => _next = next;

    public async Task OnTimeAdvancedAsync(TimeAdvance advance, IChangeContext context, CancellationToken ct = default)
    {
        var settings = ConsequenceBeats.Read(await context.GetSystemOptionsAsync());
        var chance = ConsequenceBeats.Chance(settings, advance);
        if (chance <= 0)
            return;

        var terrain = ConsequenceBeats.Classify(advance.Terrain);
        foreach (var id in advance.CharacterIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!context.Characters.TryGetValue(id, out var character))
            {
                var ctx = (ChangeContext)context;
                character = ctx.Session is null ? null : await ctx.Session.LoadAsync<Character>(id, ct);
                if (character is null)
                    continue;
                ctx.RegisterNewCharacter(character);
            }

            if (!character.IsPc && !character.IsPartyCompanion)
                continue;

            if (_next() >= chance)
                continue;

            var outcome = ConsequenceBeats.Decide(settings, terrain, _next(), _next());
            if (!ConsequenceBeats.Allowed(settings, character, outcome, advance.TotalHoursSoFar))
                continue;

            ConsequenceBeats.Record(settings, character, outcome, advance.TotalHoursSoFar);
            context.RecordMessage(ConsequenceBeats.Hint(character, outcome, advance));
        }
    }
}
