using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Cross-cutting post-commit hook tier, distinct from IWorldChangeHandler: a handler *owns* a
/// WorldChange type and can fail the commit; an observer reacts *after* a handler's ApplyAsync
/// succeeds and cannot fail it. This is the right shape for things that watch everything but own
/// nothing — a trauma-triggered "inner voice" reactor, a narrative/achievement log — so a bug in one
/// observer can't corrupt another mutation's outcome. See PLUGIN_SYSTEM_PLAN.md Track B.
///
/// Registered via DI as IEnumerable&lt;IWorldChangeObserver&gt;, same convention-scanning path as
/// IWorldChangeHandler, so plugin-supplied observers are picked up automatically.
///
/// An observer that wants to *cause* further mutations (e.g. spawn an "inner voice" WorldChange)
/// should enqueue a new WorldChange via WorldChangeDispatcher.DispatchMutationAsync rather than
/// mutating entities directly — keeps it auditable the same way every other mutation is.
/// </summary>
public interface IWorldChangeObserver
{
    /// <summary>Cheap interest check, no side effects. Runs only after the change's own handler already succeeded.</summary>
    bool IsInterestedIn(WorldChange committed, IChangeContext context);

    Task OnCommittedAsync(WorldChange committed, IChangeContext context, CancellationToken ct = default);
}
