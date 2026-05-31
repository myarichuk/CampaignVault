using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Central dispatcher for WorldChange batches.
/// 
/// Responsibilities:
/// - Batch pre-load characters and items (minimizes round-trips)
/// - Iterate changes in the exact order supplied by the caller
/// - Ask each registered handler (in DI registration order) "ShouldHandle?"
/// - First handler that claims the change gets to execute it
/// - Detects (and logs) duplicate handler claims as a bug
/// - Aggregates summary messages and overall success/failure
/// 
/// Handlers are expected to be registered as IEnumerable&lt;IWorldChangeHandler&gt; via DI.
/// The dispatcher itself is stateless and can be singleton.
/// </summary>
public sealed class WorldChangeDispatcher
{
    private readonly IReadOnlyList<IWorldChangeHandler> _handlers;
    private readonly ILogger<WorldChangeDispatcher> _logger;

    public WorldChangeDispatcher(
        IEnumerable<IWorldChangeHandler> handlers,
        ILogger<WorldChangeDispatcher>? logger = null)
    {
        _handlers = handlers?.ToList() ?? new List<IWorldChangeHandler>();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WorldChangeDispatcher>.Instance;
    }

    /// <summary>
    /// Returns the first handler that claims this change (if any).
    /// Used for hybrid dispatch during incremental migration.
    /// </summary>
    public IWorldChangeHandler? FindHandler(WorldChange change)
    {
        foreach (var h in _handlers)
        {
            if (h.ShouldHandle(change))
                return h;
        }

        return null;
    }

    public async Task<CommitResult> DispatchAsync(
        IAsyncDocumentSession session,
        WorldChange[] changes,
        string effectiveCampaign,
        Func<Task<CampaignTime>> getCurrentTimeAsync,
        Func<Event, Task> logEventAsync)
    {
        changes ??= Array.Empty<WorldChange>();
        _logger.LogDebug("Dispatching {ChangeCount} world changes via {HandlerCount} handlers", changes.Length, _handlers.Count);

        var summary = new List<string>();
        bool overallSuccess = true;

        if (changes.Length == 0)
        {
            return new CommitResult { Success = true, ChangesProcessed = 0, Summary = summary };
        }

        if (_handlers.Count == 0)
        {
            // Fast path for tests / early migration: no handlers registered
            foreach (var c in changes)
            {
                summary.Add($"WARNING: Unhandled change type: {c?.GetType().Name}");
            }

            return new CommitResult { Success = false, ChangesProcessed = changes.Length, Summary = summary };
        }

        // 1. Pre-identify and batch-load required entities (same logic as before, now centralized)
        var characterIds = new HashSet<string>();
        var itemIds = new HashSet<string>();
        bool needsCombat = false;

        foreach (var change in changes)
        {
            switch (change)
            {
                case ItemTransfer it:
                    itemIds.Add(it.ItemId);
                    break;
                case RelationshipChange rc:
                    characterIds.Add(rc.SourceId);
                    break;
                case NeedChange nc:
                    characterIds.Add(nc.CharacterId);
                    break;
                case AttributeChange ac:
                    characterIds.Add(ac.CharacterId);
                    break;
                case ActivityChange act:
                    characterIds.Add(act.CharacterId);
                    break;
                case MoodChange mc:
                    characterIds.Add(mc.CharacterId);
                    break;
                case HpChange hc:
                    characterIds.Add(hc.CharacterId);
                    break;
                case StatusChange sc:
                    characterIds.Add(sc.CharacterId);
                    break;
                case RulesetAction ra:
                    characterIds.Add(ra.ActorId);
                    foreach(var targetId in ra.TargetIds ?? Enumerable.Empty<string>()) characterIds.Add(targetId);
                    needsCombat = true; // Ruleset actions often interact with combat
                    break;
                case StatusRemove sr:
                    characterIds.Add(sr.CharacterId);
                    break;
            }
        }

        Dictionary<string, Character> characters;
        Dictionary<string, Item> items;

        if (session != null)
        {
            characters = (await session.LoadAsync<Character>(characterIds)).ToDictionary(kv => kv.Key, kv => kv.Value);
            items = (await session.LoadAsync<Item>(itemIds)).ToDictionary(kv => kv.Key, kv => kv.Value);
            
            // Preload combat encounter to ensure optimistic concurrency protection against racing StartCombat/NextTurn calls.
            // Assumption: Single combat encounter per campaign at a time.
            if (needsCombat && !string.IsNullOrEmpty(effectiveCampaign))
            {
                var keys = new CampaignDocumentKeys();
                await session.LoadAsync<CombatEncounter>(keys.CombatCurrent(effectiveCampaign));
            }
        }
        else
        {
            // Support pure unit tests of dispatcher + handler selection without a real session
            characters = new Dictionary<string, Character>();
            items = new Dictionary<string, Item>();
        }

        ChangeContext context;
        if (session is null)
        {
            // Support pure unit tests of handler selection / duplicate detection / result aggregation
            // that use fake TestHandlers which never access Session / time / logging hooks.
            context = new ChangeContext(null, characters, items, _logger, summary, this);
        }
        else
        {
            context = new ChangeContext(session, characters, items, _logger, getCurrentTimeAsync, logEventAsync, summary, this);
        }

        // 2. Process each change in caller-supplied order
        foreach (var change in changes)
        {
            try
            {
                IWorldChangeHandler? chosen = null;
                int claimCount = 0;

                foreach (var handler in _handlers)
                {
                    if (handler.ShouldHandle(change))
                    {
                        claimCount++;
                        if (chosen is null)
                            chosen = handler;
                    }
                }

                if (claimCount > 1)
                {
                    _logger.LogWarning(
                        "Multiple handlers claimed change of type {ChangeType}. This is a bug - ShouldHandle predicates must be mutually exclusive. Using first registered handler.",
                        change.GetType().Name);
                }

                if (chosen is null)
                {
                    summary.Add($"WARNING: Unhandled change type: {change?.GetType().Name}");
                    context.RecordFailure();
                    overallSuccess = false;
                    continue;
                }

                var result = await chosen.ApplyAsync(change, context);

                if (result.Message is not null)
                {
                    context.RecordMessage(result.Message);
                }

                if (!result.Success)
                {
                    context.RecordFailure();
                    overallSuccess = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing change of type {ChangeType}", change?.GetType().Name);
                summary.Add($"ERROR: Failed to process {change?.GetType().Name}: {ex.Message}");
                context.RecordFailure();
                overallSuccess = false;
            }
        }

        _logger.LogInformation("WorldChangeDispatcher processed {Processed} changes (overall success: {Success})",
            changes.Length, overallSuccess);

        return new CommitResult
        {
            Success = overallSuccess,
            ChangesProcessed = changes.Length,
            Summary = summary
        };
    }

    /// <summary>
    /// Dispatches a single child mutation directly within an ongoing change context.
    /// Used by handlers like RulesetActionHandler that compute secondary mutations.
    /// </summary>
    public async Task DispatchMutationAsync(ChangeContext parentContext, WorldChange mutation, CancellationToken ct = default)
    {
        IWorldChangeHandler? chosen = FindHandler(mutation);
        
        if (chosen == null)
        {
            _logger.LogWarning("No handler found for child mutation of type {ChangeType}", mutation?.GetType().Name);
            parentContext.RecordFailure();
            return;
        }

        try
        {
            var result = await chosen.ApplyAsync(mutation, parentContext, ct);
            if (result.Message is not null)
            {
                parentContext.RecordMessage(result.Message);
            }
            if (!result.Success)
            {
                parentContext.RecordFailure();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing child mutation of type {ChangeType}", mutation?.GetType().Name);
            parentContext.RecordFailure();
        }
    }
}