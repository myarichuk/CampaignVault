using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// A pluggable handler responsible for applying one (or a family of) WorldChange types.
/// 
/// The dispatcher asks handlers in registration order via ShouldHandle until one claims the change.
/// Handlers must have mutually exclusive ShouldHandle predicates (duplicate claims are treated as a bug).
/// 
/// Handlers are registered via DI as IEnumerable&lt;IWorldChangeHandler&gt; and are expected to be stateless
/// and safe to use across multiple commits.
/// </summary>
public interface IWorldChangeHandler
{
    /// <summary>
    /// Returns true if this handler wants to process the given change.
    /// Should be cheap and have no side effects.
    /// </summary>
    bool ShouldHandle(WorldChange change);

    /// <summary>
    /// Applies the change. The handler is responsible for mutating entities (from the pre-loaded context),
    /// recording summary messages, and indicating success/failure via the returned result.
    /// </summary>
    Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default);
}