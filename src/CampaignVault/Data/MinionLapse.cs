using CampaignVault.Data.Templates;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// Shared control-lapse mechanics for summoned minions: concentration broken,
/// caster downed, duration elapsed, cap released, dismissed. Lapsing unlinks the
/// minion and drops it from its caster's list; the body stays as an ordinary NPC
/// played at the binding's recorded disposition.
/// </summary>
internal static class MinionLapse
{
    /// <summary>
    /// Pure narrative for a lapse. Used by delta-emitting paths (simulation rules)
    /// that cannot mutate the minion before the change applies.
    /// </summary>
    public static string LapseNarrative(Character minion, string causePhrase)
    {
        var disposition = minion.MinionBinding?.Disposition ?? SummonDisposition.Loyal;
        return disposition switch
        {
            SummonDisposition.Hostile =>
                $"{minion.Name} {causePhrase} It breaks free and turns hostile!",
            SummonDisposition.Neutral =>
                $"{minion.Name} {causePhrase} It is no longer bound and acts on its own (neutral).",
            _ =>
                $"{minion.Name} {causePhrase} It holds, still loyal but awaiting new orders.",
        };
    }

    /// <summary>
    /// Lapses control in place (session-tracked docs persist on save). Returns the narrative.
    /// </summary>
    public static string LapseMinion(Character minion, string causePhrase)
    {
        var narrative = LapseNarrative(minion, causePhrase);
        minion.ControlledById = null;
        if (minion.MinionBinding != null)
        {
            minion.MinionBinding = minion.MinionBinding with { ControlLapsed = true };
        }

        return narrative;
    }

    /// <summary>
    /// Lapses a caster's minions in place: every live binding when the caster goes
    /// down, only concentration-bound ones on a broken save. The caller appends
    /// <paramref name="messages"/> to its own record/narrative channel.
    /// </summary>
    public static async Task LapseCasterMinionsAsync(
        IAsyncDocumentSession session,
        IReadOnlyDictionary<string, Character> loaded,
        Character caster,
        bool concentrationOnly,
        List<string> messages,
        CancellationToken ct = default)
    {
        if (caster.ControlsMinionIds.Count == 0)
        {
            return;
        }

        var minions = await LoadControlledAsync(session, loaded, caster, true, messages, ct);
        var cause = concentrationOnly
            ? $"loses {caster.Name}'s concentration."
            : $"loses its binder {caster.Name}, who goes down.";
        foreach (var minion in minions)
        {
            if (minion.MinionBinding is not { ControlLapsed: false })
            {
                continue;
            }

            if (concentrationOnly && !minion.MinionBinding.ConcentrationBound)
            {
                continue;
            }

            caster.ControlsMinionIds.Remove(minion.Id);
            messages.Add(LapseMinion(minion, cause));
        }
    }

    /// <summary>
    /// Lapses one minion and drops it from its controller's list, loading the
    /// controller through the session when it is not pre-loaded. Used by expiry
    /// sweeps that iterate minions rather than casters. When the controller doc
    /// itself is gone the minion still lapses; the stale list entry is reported.
    /// </summary>
    public static async Task LapseAndUnlinkAsync(
        IAsyncDocumentSession session,
        IReadOnlyDictionary<string, Character> loaded,
        Character minion,
        string causePhrase,
        List<string> messages,
        CancellationToken ct = default)
    {
        var controllerId = minion.ControlledById ?? minion.MinionBinding?.ControllerId;
        messages.Add(LapseMinion(minion, causePhrase));
        if (string.IsNullOrEmpty(controllerId))
        {
            return;
        }

        if (loaded.TryGetValue(controllerId, out var controller) && controller != null)
        {
            controller.ControlsMinionIds.Remove(minion.Id);
            return;
        }

        var doc = await session.LoadAsync<Character>(controllerId, ct);
        if (doc != null)
        {
            doc.ControlsMinionIds.Remove(minion.Id);
        }
        else
        {
            messages.Add($"Controller '{controllerId}' of {minion.Name} is gone; its retinue list could not be cleaned.");
        }
    }

    /// <summary>
    /// Loads every minion on a caster's list: pre-loaded map first, session fallback
    /// after. Missing docs are pruned from the list when <paramref name="pruneMissing"/>
    /// is set (the caller owns a session-tracked caster); otherwise they are skipped
    /// and reported, never auto-released sight-unseen.
    /// </summary>
    public static async Task<IReadOnlyList<Character>> LoadControlledAsync(
        IAsyncDocumentSession session,
        IReadOnlyDictionary<string, Character> loaded,
        Character caster,
        bool pruneMissing,
        List<string> notes,
        CancellationToken ct = default)
    {
        var result = new List<Character>();
        foreach (var id in caster.ControlsMinionIds.ToList())
        {
            if (loaded.TryGetValue(id, out var known) && known != null)
            {
                result.Add(known);
                continue;
            }

            var doc = await session.LoadAsync<Character>(id, ct);
            if (doc != null)
            {
                result.Add(doc);
                continue;
            }

            if (pruneMissing)
            {
                caster.ControlsMinionIds.Remove(id);
                notes.Add($"Pruned missing minion '{id}' from {caster.Name}'s retinue.");
            }
            else
            {
                notes.Add($"Minion '{id}' of {caster.Name} is not loaded; left untouched.");
            }
        }

        return result;
    }
}
