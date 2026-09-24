namespace CampaignVault.Data.Events;

/// <summary>
/// A domain-event reaction that broke. Stage is <c>handler_threw</c>, <c>follow_up_failed</c> or
/// <c>depth_capped</c>. <see cref="AppliedChangeTypes"/> lists the follow-ups that landed before the fault:
/// nothing is rolled back, so a non-empty list means the reaction is partially applied.
/// </summary>
public sealed record PluginFault(
    string PluginId,
    string Handler,
    string Topic,
    string Stage,
    string? ChangeType,
    string Message,
    string? FixHint,
    IReadOnlyList<string> AppliedChangeTypes,
    bool CommitKept)
{
    public const string HandlerThrew = "handler_threw";
    public const string FollowUpFailed = "follow_up_failed";
    public const string DepthCapped = "depth_capped";

    /// <summary>One line for the model: what broke, what already landed, and how to fix it.</summary>
    public string Describe()
    {
        var line = $"PLUGIN FAULT ({PluginId}, {Handler} on {Topic}, {Stage}";
        line += ChangeType is null ? "): " : $", {ChangeType}): ";
        line += Message;
        if (AppliedChangeTypes.Count > 0)
        {
            line += $" Partially applied: {string.Join(", ", AppliedChangeTypes)} landed before the fault.";
        }

        line += CommitKept ? " The rest of the turn was saved." : " The turn was not saved.";
        if (!string.IsNullOrWhiteSpace(FixHint))
        {
            line += $" Fix: {FixHint}";
        }

        return line;
    }
}
