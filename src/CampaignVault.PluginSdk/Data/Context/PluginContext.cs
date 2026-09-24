using CampaignVault.Models;

namespace CampaignVault.Data.Context;

/// <summary>
/// Plugin-supplied turn context: one-line facts the next prose needs, pushed only on the turn that uses
/// them (e.g. a crafting mode pushing recipe state on its own verb). Discovered by convention scanning,
/// like guidance contributors. The host namespaces each key, delivers it once per session (a changed
/// fact needs a new key, e.g. one that includes the value) and shares one character budget between core
/// and plugin lines, so keep each line short.
/// </summary>
public interface IPluginContextContributor
{
    /// <summary>Called once per committed take_turn. Return empty on quiet turns; exceptions are swallowed.</summary>
    Task<IEnumerable<PluginContextItem>> ContributeAsync(IContextTurn turn, CancellationToken ct = default);
}

/// <summary>Raven-free, read-only view of the committed turn a context contributor is evaluated for.</summary>
public interface IContextTurn
{
    string CampaignName { get; }

    /// <summary>Changes committed by this turn, including engine ambient deltas.</summary>
    IReadOnlyList<WorldChange> AppliedChanges { get; }

    /// <summary>Every entity ID the turn's changes touched.</summary>
    IReadOnlyList<string> InvolvedEntityIds { get; }

    /// <summary>PC and companion IDs.</summary>
    IReadOnlyList<string> PartyCharacterIds { get; }

    /// <summary>The PC's current location, when known.</summary>
    string? PartyLocationId { get; }
}

/// <param name="Key">Delivery key: the same key is not sent twice in a session.</param>
/// <param name="Text">One line, ideally under 150 chars.</param>
/// <param name="Priority">Higher wins when the budget is tight.</param>
public sealed record PluginContextItem(string Key, string Text, int Priority = 0);
