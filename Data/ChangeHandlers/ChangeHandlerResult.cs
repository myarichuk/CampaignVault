using System;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Lightweight result from a single IWorldChangeHandler.ApplyAsync call.
/// Success path is intentionally small and allocation-light.
/// </summary>
public readonly record struct ChangeHandlerResult(bool Success, string? Message = null)
{
    /// <summary>
    /// Convenience for the common successful case with no additional message.
    /// </summary>
    public static readonly ChangeHandlerResult Ok = new(true);

    /// <summary>
    /// Creates a failure result with an optional message that will be recorded in the commit summary.
    /// </summary>
    public static ChangeHandlerResult Failure(string? message = null) => new(false, message);
}