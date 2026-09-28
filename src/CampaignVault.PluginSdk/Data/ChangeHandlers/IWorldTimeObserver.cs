using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// One slice of campaign time that just passed: <see cref="Hours"/> is this step only (a 12h trip arrives as two 6h
/// steps, a long rest as 4h steps), <see cref="TotalHoursSoFar"/> the absolute campaign clock after the step.
/// </summary>
/// <param name="Source">"travel", "rest", "activity" (a long-enough ordinary beat) or "advance_world".</param>
/// <param name="Hours">Length of this step in hours (at most the source's bucket size).</param>
/// <param name="TotalHoursSoFar">Campaign hours elapsed after this step (days × 24 + hour of day).</param>
/// <param name="CharacterIds">Characters that lived through it. Empty when the engine cannot tell (e.g. advance_world without a party location).</param>
/// <param name="LocationId">Where it happened (travel: the destination when reached, else the origin).</param>
/// <param name="Terrain">Terrain of the route or location, when known (travel exit terrain, or the location's).</param>
public sealed record TimeAdvance(
    string Source,
    double Hours,
    double TotalHoursSoFar,
    IReadOnlyList<string> CharacterIds,
    string? LocationId,
    string? Terrain);

/// <summary>
/// Plugin hook for time passing (SDK 0.10.0). Called after the clock moved, once per bucket of the span (travel 6h,
/// rest 4h, everything else 6h), oldest step first. Registered by convention like <see cref="IWorldChangeObserver"/>;
/// only observers that apply to the campaign's active system run.
///
/// Like an observer it cannot fail the commit: an exception is logged and swallowed. To cause effects, dispatch new
/// <see cref="WorldChange"/>s through the context's dispatcher rather than mutating entities directly. An observer
/// that dispatches a rest or travel does not re-trigger the hook.
/// </summary>
public interface IWorldTimeObserver
{
    Task OnTimeAdvancedAsync(TimeAdvance advance, IChangeContext context, CancellationToken ct = default);
}
