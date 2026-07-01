using CampaignVault.Models;
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
    private readonly CampaignDocumentKeys _keys;

    public WorldChangeDispatcher(
        IEnumerable<IWorldChangeHandler> handlers,
        CampaignDocumentKeys keys,
        ILogger<WorldChangeDispatcher>? logger = null)
    {
        _handlers = handlers?.ToList() ?? [];
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
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
            {
                return h;
            }
        }

        return null;
    }

    public async Task<CommitResult> DispatchAsync(
        IAsyncDocumentSession session,
        WorldChange[] changes,
        string? effectiveCampaign,
        Func<Task<CampaignTime>> getCurrentTimeAsync,
        Func<Task<Dictionary<string, string>>> getSystemOptionsAsync,
        Func<Event, Task> logEventAsync)
    {
        changes ??= [];
        _logger.LogDebug("Dispatching {ChangeCount} world changes via {HandlerCount} handlers", changes.Length, _handlers.Count);

        var summary = new List<string>();
        foreach (var note in ConversationInvolvedResolver.Apply(changes))
        {
            summary.Add(note);
        }

        var overallSuccess = true;

        if (changes.Length == 0)
        {
            return new CommitResult { Success = true, ChangesProcessed = 0, Summary = summary };
        }

        if (_handlers.Count == 0)
        {
            _logger.LogError("WorldChangeDispatcher invoked with 0 registered handlers. Changes will be dropped.");
            foreach (var c in changes)
            {
                var msg = $"ERROR: Unhandled change type: {c?.GetType().Name}";
                _logger.LogError(msg);
                summary.Add(msg);
            }

            return new CommitResult { Success = false, ChangesProcessed = changes.Length, Summary = summary };
        }

        // 1. Pre-identify and batch-load required entities (same logic as before, now centralized)
        var characterIds = new HashSet<string>();
        var itemIds = new HashSet<string>();
        var locationIds = new HashSet<string>();
        var factionIds = new HashSet<string>();
        var questIds = new HashSet<string>();
        var needsCombat = false;
        var allInvolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            ExtractInvolvedIds(change, characterIds, locationIds, factionIds, questIds, itemIds, allInvolved);
            if (change is RulesetAction) needsCombat = true;
        }

        Dictionary<string, Character> characters;
        Dictionary<string, Item> items;
        Dictionary<string, Location> locations;
        Dictionary<string, Faction> factions;
        Dictionary<string, Quest> quests;
        CombatEncounter? activeCombat = null;
        CampaignConfig? config = null;

        if (session != null)
        {
            characters = (await session.LoadAsync<Character>(characterIds))
                .Where(kv => kv.Value != null)
                .Where(kv => string.IsNullOrEmpty(effectiveCampaign)
                             || CampaignEntityVisibility.IsVisibleInCampaign(kv.Value!.CampaignName, effectiveCampaign))
                .ToDictionary(kv => kv.Key, kv => kv.Value!);
            items = (await session.LoadAsync<Item>(itemIds))
                .Where(kv => kv.Value != null)
                .Where(kv => string.IsNullOrEmpty(effectiveCampaign)
                             || CampaignEntityVisibility.IsVisibleInCampaign(kv.Value!.CampaignName, effectiveCampaign))
                .ToDictionary(kv => kv.Key, kv => kv.Value!);

            // Phase 7.3 / Travel: preload the traveler's *origin* CurrentLocationId (in addition to the explicit Destination).
            // This allows TravelChangeHandler to resolve LocationExit metadata (TravelCostHours, Terrain) via the
            // preloaded context.Locations dictionary in the normal case, avoiding a mid-handler Session.LoadAsync fallback.
            // Note: Cannot be done in ExtractInvolvedEntities because the Character is not yet loaded.
            foreach (var change in changes.OfType<TravelChange>())
            {
                if (characters.TryGetValue(change.CharacterId, out var traveler) &&
                    !string.IsNullOrEmpty(traveler.CurrentLocationId))
                {
                    locationIds.Add(traveler.CurrentLocationId);
                }
            }

            locations = (await session.LoadAsync<Location>(locationIds))
                .Where(kv => kv.Value != null)
                .Where(kv => string.IsNullOrEmpty(effectiveCampaign)
                             || CampaignEntityVisibility.IsVisibleInCampaign(kv.Value!.CampaignName, effectiveCampaign))
                .ToDictionary(kv => kv.Key, kv => kv.Value!);
            factions = factionIds.Count > 0
                ? (await session.LoadAsync<Faction>(factionIds))
                    .Where(kv => kv.Value != null)
                    .Where(kv => string.IsNullOrEmpty(effectiveCampaign)
                                 || CampaignEntityVisibility.IsVisibleInCampaign(kv.Value!.CampaignName, effectiveCampaign))
                    .ToDictionary(kv => kv.Key, kv => kv.Value!)
                : new Dictionary<string, Faction>();
            quests = questIds.Count > 0
                ? (await session.LoadAsync<Quest>(questIds))
                    .Where(kv => kv.Value != null)
                    .Where(kv => string.IsNullOrEmpty(effectiveCampaign)
                                 || CampaignEntityVisibility.IsVisibleInCampaign(kv.Value!.CampaignName, effectiveCampaign))
                    .ToDictionary(kv => kv.Key, kv => kv.Value!)
                : new Dictionary<string, Quest>();

            // Preload combat encounter to ensure optimistic concurrency protection against racing StartCombat/NextTurn calls.
            // Assumption: Single combat encounter per campaign at a time.
            if (needsCombat && !string.IsNullOrEmpty(effectiveCampaign))
            {
                activeCombat = await session.LoadAsync<CombatEncounter>(_keys.CombatCurrent(effectiveCampaign));
            }

            // Preload campaign config for resolver and handler access (ruleset selection, feature flags, etc).
            if (!string.IsNullOrEmpty(effectiveCampaign))
            {
                config = await session.LoadAsync<CampaignConfig>(_keys.Config(effectiveCampaign));
            }
        }
        else
        {
            // Support pure unit tests of dispatcher + handler selection without a real session
            characters = new Dictionary<string, Character>();
            items = new Dictionary<string, Item>();
            locations = new Dictionary<string, Location>();
            factions = new Dictionary<string, Faction>();
            quests = new Dictionary<string, Quest>();
        }

        ChangeContext context;
        if (session is null)
        {
            // Support pure unit tests of handler selection / duplicate detection / result aggregation
            // that use fake TestHandlers which never access Session / time / logging hooks.
            context = new ChangeContext(null, characters, items, locations, factions, quests, _logger, summary, this, activeCombat, effectiveCampaign, config);
        }
        else
        {
            context = new ChangeContext(session, characters, items, locations, factions, quests, _logger, getCurrentTimeAsync, getSystemOptionsAsync, logEventAsync, summary, this, activeCombat, effectiveCampaign, config);
        }

        foreach (var id in allInvolved)
        {
            context.InvolvedEntities.Add(id);
        }

        // 2. Process each change in caller-supplied order
        foreach (var change in changes)
        {
            try
            {
                IWorldChangeHandler? chosen = null;
                var claimCount = 0;

                foreach (var handler in _handlers)
                {
                    if (handler.ShouldHandle(change))
                    {
                        claimCount++;
                        if (chosen is null)
                        {
                            chosen = handler;
                        }
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

                ChangeHandlerResult result;
                try
                {
                    result = await chosen.ApplyAsync(change, context);
                }
                catch (ArgumentNullException ex)
                {
                    _logger.LogWarning(ex, "ArgumentNullException during handler application");
                    result = ChangeHandlerResult.Failure($"A required property is missing on {change.GetType().Name}.");
                }

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
            Summary = summary,
            InvolvedEntities = context.InvolvedEntities.ToList()
        };
    }

    /// <summary>
    /// Dispatches a single child mutation directly within an ongoing change context.
    /// Used by handlers like RulesetActionHandler that compute secondary mutations.
    /// </summary>
    public async Task DispatchMutationAsync(ChangeContext parentContext, WorldChange mutation, CancellationToken ct = default)
    {
        var chosen = FindHandler(mutation);
        
        if (chosen == null)
        {
            _logger.LogWarning("No handler found for child mutation of type {ChangeType}", mutation?.GetType().Name);
            parentContext.RecordFailure();
            return;
        }

        TrackInvolvedEntities(mutation, parentContext);

        try
        {
            var result = await chosen.ApplyAsync(mutation, parentContext, ct);
            TrackInvolvedEntities(mutation, parentContext);
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

    private void TrackInvolvedEntities(WorldChange change, ChangeContext context)
    {
        ExtractInvolvedIds(change, null, null, null, null, null, context.InvolvedEntities);
    }

    private void ExtractInvolvedIds(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        var chosen = FindHandler(change);
        if (chosen != null)
        {
            chosen.ExtractInvolvedEntities(change, characterIds, locationIds, factionIds, questIds, itemIds, allInvolvedIds);
        }
    }
}