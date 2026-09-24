using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data.Context;

/// <summary>
/// T6 context contributors: the commit's change types say what the beat is about, so they say what the
/// next prose needs. Each contributor watches the applied changes and offers small keyed lines (a
/// topic-matched memory, a relationship tier crossing, the party's gold on a trade); the orchestrator
/// drops keys already delivered this session (TurnCursor.DeliveredContextKeys), ranks by priority and
/// fits a character budget. Pull (get_entity, recall_history) stays available for everything else.
/// </summary>
public interface IContextContributor
{
    Task<IEnumerable<ContextItem>> ContributeAsync(ContextTurn turn, CancellationToken ct = default);
}

public sealed record ContextItem(string Key, string Text, int Priority = 0);

/// <summary>Everything a core contributor may read about the committed turn.</summary>
public sealed class ContextTurn : IContextTurn
{
    public required IAsyncDocumentSession Session { get; init; }
    public required string CampaignName { get; init; }
    public required CampaignConfig Config { get; init; }
    public required IReadOnlyList<WorldChange> AppliedChanges { get; init; }
    public required IReadOnlyList<string> InvolvedEntityIds { get; init; }
    public required IReadOnlyList<Character> Party { get; init; }
    public IReadOnlyList<string> PartyCharacterIds => Party.Select(p => p.Id).ToList();
    public string? PartyLocationId { get; init; }

    /// <summary>NPC IDs present in the scenes this response carries.</summary>
    public IReadOnlyList<string> PresentNpcIds { get; init; } = [];

    /// <summary>Embedding of the turn's narrative (the logged SceneCommit event), when one was computed.</summary>
    public float[]? NarrativeVector { get; init; }

    /// <summary>(characterId, targetId) → relationship value before this commit.</summary>
    public IReadOnlyDictionary<(string CharacterId, string TargetId), int> RelationshipBaselines { get; init; } =
        new Dictionary<(string, string), int>();

    /// <summary>Memory lines already carried by a card in this response, so recall doesn't repeat them.</summary>
    public IReadOnlySet<string> MemoryLinesInCards { get; init; } = new HashSet<string>();
}

public interface IContextOrchestrator
{
    /// <summary>Returns the lines to send and records their keys in <paramref name="delivered"/>.</summary>
    Task<IReadOnlyList<string>> CollectAsync(ContextTurn turn, ICollection<string> delivered, CancellationToken ct = default);
}

internal sealed class ContextOrchestrator(
    IEnumerable<IContextContributor> contributors,
    IEnumerable<IPluginContextContributor> pluginContributors,
    ILogger<ContextOrchestrator>? logger = null) : IContextOrchestrator
{
    /// <summary>Soft cap on context chars per response; the rest is left for pull.</summary>
    internal const int CharBudget = 800;

    public async Task<IReadOnlyList<string>> CollectAsync(ContextTurn turn, ICollection<string> delivered, CancellationToken ct = default)
    {
        var items = new List<ContextItem>();
        foreach (var contributor in contributors)
        {
            try
            {
                items.AddRange(await contributor.ContributeAsync(turn, ct));
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Context contributor {Contributor} failed", contributor.GetType().Name);
            }
        }

        foreach (var plugin in pluginContributors)
        {
            var source = plugin.GetType().Assembly.GetName().Name ?? plugin.GetType().Name;
            try
            {
                items.AddRange((await plugin.ContributeAsync(turn, ct))
                    .Where(i => !string.IsNullOrWhiteSpace(i.Key) && !string.IsNullOrWhiteSpace(i.Text))
                    .Select(i => new ContextItem($"plugin:{source}:{i.Key}", i.Text, i.Priority)));
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Plugin context contributor {Source} failed", source);
            }
        }

        var deliveredSet = new HashSet<string>(delivered, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        var used = 0;
        foreach (var item in items
                     .Where(i => !deliveredSet.Contains(i.Key))
                     .GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                     .OrderByDescending(i => i.Priority))
        {
            if (used + item.Text.Length > CharBudget && lines.Count > 0)
            {
                // Not recorded as delivered: it can ride a later turn if it is still relevant.
                continue;
            }

            lines.Add(item.Text);
            used += item.Text.Length;
            delivered.Add(item.Key);
        }

        return lines;
    }
}
