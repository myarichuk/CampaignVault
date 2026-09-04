using System.Reflection;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
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
public sealed class WorldChangeDispatcher(
    IEnumerable<IWorldChangeHandler>? handlers,
    CampaignDocumentKeys keys,
    ILogger<WorldChangeDispatcher>? logger = null,
    EncounterResolver? encounterResolver = null,
    ClassDefinitionProvider? classProvider = null,
    BackgroundDefinitionProvider? backgroundProvider = null)
{
    private readonly IReadOnlyList<IWorldChangeHandler> _handlers = handlers?.ToList() ?? [];
    private readonly ILogger<WorldChangeDispatcher> _logger = logger ?? NullLogger<WorldChangeDispatcher>.Instance;
    private readonly CampaignDocumentKeys _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly EncounterResolver? _encounterResolver = encounterResolver;
    private readonly ClassDefinitionProvider? _classProvider = classProvider;
    private readonly BackgroundDefinitionProvider? _backgroundProvider = backgroundProvider;

    private readonly Dictionary<Type, IWorldChangeHandler> _handlersByChangeType = BuildHandlerDictionary(handlers ?? []);


    private static Dictionary<Type, IWorldChangeHandler> BuildHandlerDictionary(IEnumerable<IWorldChangeHandler> handlers)
    {
        var dict = new Dictionary<Type, IWorldChangeHandler>();
        var handlerList = handlers.ToList();

        var worldChangeType = typeof(WorldChange);
        var changeTypes = worldChangeType.Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && worldChangeType.IsAssignableFrom(t))
            .ToList();

        foreach (var handler in handlerList)
        {
            foreach (var changeType in changeTypes)
            {
                var testInstance = Activator.CreateInstance(changeType) as WorldChange;
                if (testInstance != null && handler.ShouldHandle(testInstance))
                {
                    if (!dict.ContainsKey(changeType))
                    {
                        dict[changeType] = handler;
                    }
                }
            }
        }

        return dict;
    }

    /// <summary>
    /// Returns the first handler that claims this change (if any).
    /// Used for hybrid dispatch during incremental migration.
    /// </summary>
    public IWorldChangeHandler? FindHandler(WorldChange change)
    {
        return _handlersByChangeType.TryGetValue(change.GetType(), out var handler) ? handler : null;
    }

    public async Task<CommitResult> DispatchAsync(
        IAsyncDocumentSession? session,
        WorldChange[]? changes,
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

        foreach (var note in EventFollowUpAdvisor.Apply(changes))
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
        var needsRulesetConfig = false;
        var allInvolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            WorldChangeHandlerHelpers.NormalizeIdFields(change);
            ExtractInvolvedIds(change, characterIds, locationIds, factionIds, questIds, itemIds, allInvolved);
            if (change is RulesetAction) needsCombat = true;
            if (change is RulesetAction or LevelUpChange) needsRulesetConfig = true;
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

            if (!string.IsNullOrEmpty(effectiveCampaign))
            {
                await SystemStatsUpgradeHelper.UpgradeCharacterSystemStatsAsync(
                    session, characters, effectiveCampaign, _keys, _classProvider, _backgroundProvider);
            }
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

            // Preload campaign config when ruleset resolvers or level_up need feature flags.
            if (needsRulesetConfig && !string.IsNullOrEmpty(effectiveCampaign))
            {
                var configId = _keys.Config(effectiveCampaign);
                config = await session.LoadAsync<CampaignConfig>(configId)
                         ?? new CampaignConfig { Id = configId };
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
        for (var changeIndex = 0; changeIndex < changes.Length; changeIndex++)
        {
            var change = changes[changeIndex];
            context.Batch = changes;
            context.BatchIndex = changeIndex;
            try
            {
                var chosen = FindHandler(change);

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

        if (overallSuccess)
        {
            await ApplyMicroTimeNudgeAsync(context, changes, getCurrentTimeAsync);
            ApplyMomentumTracking(context, changes);
            await ApplyAmbientInterruptCheckAsync(context, changes, getCurrentTimeAsync);
        }

        _logger.LogInformation("WorldChangeDispatcher processed {Processed} changes (overall success: {Success})",
            changes.Length, overallSuccess);

        return new CommitResult
        {
            Success = overallSuccess,
            ChangesProcessed = changes.Length,
            Summary = summary,
            InvolvedEntities = context.InvolvedEntities.ToList(),
            EntityCollisions = context.EntityCollisions.ToList()
        };
    }

    /// <summary>
    /// Sums WorldChange.MinutesElapsed across the batch and, if any beat carried a duration, nudges
    /// needs (hunger/thirst/tiredness/social_drive) for the characters involved in *this* batch —
    /// not a campaign-wide sweep, since only on-screen characters are narratively relevant at this
    /// granularity. Sub-hour totals leave TimeOfDay untouched (a few lines of dialogue shouldn't flip
    /// the clock); an hour or more nudges CampaignTime.AdvanceHours too, which lets StageChangesAsync's
    /// existing day-boundary check pick it up and run the full simulation tick if enough beats stack up
    /// across a day.
    ///
    /// RestChange/TravelChange are excluded from the sum even if MinutesElapsed is set on them (LLM
    /// mistake or otherwise) — both already call CampaignTime.AdvanceHours themselves via their own
    /// handlers, so including them here would double-advance the clock and double-accumulate needs
    /// for the same stretch of time.
    ///
    /// Only characters actually on stage this batch (named via one of this turn's changes' CharacterId/
    /// TargetId/TargetIds/Involved fields — see <see cref="CollectOnScreenCharacterIds"/>) get nudged,
    /// not everyone in context.Characters: that dictionary is a preload cache and also holds anyone
    /// merely *referenced* elsewhere in the payload (e.g. a knowledge_update's RelatedEntityIds pointing
    /// at a dead/absent character mentioned in the conversation) — a background character who wasn't
    /// part of this beat shouldn't accrue hunger for a scene they weren't in, and doing so used to leak
    /// their ID into InvolvedEntities and the next auto-refresh's Npcs[] as if they were narratively
    /// relevant this turn.
    ///
    /// Applies deltas directly (bypassing NeedChangeHandler/DispatchMutationAsync's per-need
    /// context.RecordMessage) and emits one collapsed summary line for the whole nudge instead of one
    /// line per need per character — same numeric effect, without flooding the LLM-facing summary with
    /// near-zero-magnitude ambient noise on every commit that carries MinutesElapsed.
    /// </summary>
    private async Task ApplyMicroTimeNudgeAsync(
        ChangeContext context, WorldChange[] changes, Func<Task<CampaignTime>> getCurrentTimeAsync)
    {
        var minutesElapsed = changes
            .Where(c => c is not RestChange and not TravelChange)
            .Sum(c => c.MinutesElapsed ?? 0);
        if (minutesElapsed <= 0)
        {
            return;
        }

        var onScreenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            CollectOnScreenCharacterIds(change, onScreenIds);
        }

        if (onScreenIds.Count > 0)
        {
            var days = minutesElapsed / 1440.0;
            var perDayDeltas = NeedAccumulationMath.ComputeDeltas(context.Config, days);
            var nudgedCharacterIds = new List<string>();

            foreach (var character in context.Characters.Values)
            {
                if (character.Needs is null || !onScreenIds.Contains(character.Id))
                {
                    continue;
                }

                var updatedNeeds = new Dictionary<string, float>(character.Needs.ActiveNeeds);
                var changedAny = false;
                foreach (var (need, delta) in perDayDeltas)
                {
                    var current = updatedNeeds.GetValueOrDefault(need, 0f);
                    var effective = Math.Min(delta, 100f - current);
                    if (effective > 0.0001f)
                    {
                        updatedNeeds[need] = current + effective;
                        changedAny = true;
                    }
                }

                if (changedAny)
                {
                    character.Needs.ActiveNeeds = updatedNeeds;
                    nudgedCharacterIds.Add(character.Id);
                }
            }

            if (nudgedCharacterIds.Count > 0)
            {
                context.RecordMessage(
                    $"Ambient needs drift ({minutesElapsed:0.##} min passing) applied to: {string.Join(", ", nudgedCharacterIds)}.");
            }
        }

        if (minutesElapsed >= 60)
        {
            var time = await getCurrentTimeAsync();
            time.AdvanceHours(minutesElapsed / 60);
        }
    }

    /// <summary>
    /// Narrow allowlist of "actual participant" fields (as opposed to merely-referenced ones like
    /// RelatedEntityIds/SourceEventIds) used by <see cref="ApplyMicroTimeNudgeAsync"/> to decide who was
    /// genuinely on stage this batch.
    /// </summary>
    private static readonly HashSet<string> OnScreenIdPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CharacterId", "TargetId", "TargetIds", "Involved"
    };

    private static void CollectOnScreenCharacterIds(WorldChange change, HashSet<string> ids)
    {
        foreach (var prop in change.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!OnScreenIdPropertyNames.Contains(prop.Name))
            {
                continue;
            }

            if (prop.PropertyType == typeof(string))
            {
                if (prop.GetValue(change) is string s && s.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(s);
                }
            }
            else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType))
            {
                if (prop.GetValue(change) is System.Collections.IEnumerable list)
                {
                    foreach (var item in list)
                    {
                        if (item is string s2 && s2.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
                        {
                            ids.Add(s2);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Tracks Character.IdleSceneBeats for every on-screen (see <see cref="CollectOnScreenCharacterIds"/>)
    /// party-companion/keepAlive NPC in this batch: reset to 0 (and IdleSceneLocationId updated) when they
    /// acted this batch — the CharacterId of an ActivityChange or RulesetAction, or the InitiatorId of an
    /// EventOccurred (see <see cref="CollectActingCharacterIds"/>) — otherwise incremented. Also reset
    /// (without counting as an idle beat) when CurrentLocationId no longer matches the last-tracked
    /// IdleSceneLocationId, since a new scene starts idleness over rather than carrying it across a travel/
    /// activity move. Feeds SceneMomentumInitiativeProvider: a companion who's gone several beats without
    /// their own verb eventually surfaces an "acts unprompted" nudge independent of need/relational/memory/
    /// disposition state, which none of those track (see NpcInitiativeContext's other providers — none of
    /// them respond to beats elapsed without a state change).
    ///
    /// Unlike ApplyMicroTimeNudgeAsync this doesn't require MinutesElapsed — plain narrated dialogue
    /// (EventOccurred with no time cost) still counts as a beat, since that's exactly the "pure banter"
    /// case this is meant to catch.
    /// </summary>
    private static void ApplyMomentumTracking(ChangeContext context, WorldChange[] changes)
    {
        var onScreenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            CollectOnScreenCharacterIds(change, onScreenIds);
        }

        if (onScreenIds.Count == 0)
        {
            return;
        }

        var actedIds = CollectActingCharacterIds(changes);

        foreach (var character in context.Characters.Values)
        {
            if (character.IsPc || !onScreenIds.Contains(character.Id))
            {
                continue;
            }

            if (!character.IsPartyCompanion && !character.KeepAlive)
            {
                continue;
            }

            if (!string.Equals(character.IdleSceneLocationId, character.CurrentLocationId, StringComparison.OrdinalIgnoreCase))
            {
                character.IdleSceneLocationId = character.CurrentLocationId;
                character.IdleSceneBeats = actedIds.Contains(character.Id) ? 0 : 1;
                continue;
            }

            character.IdleSceneBeats = actedIds.Contains(character.Id)
                ? 0
                : Math.Min(character.IdleSceneBeats + 1, 999);
        }
    }

    /// <summary>
    /// Characters who were the *actor* (not merely a participant/target) of a change in this batch —
    /// narrower than <see cref="CollectOnScreenCharacterIds"/>'s allowlist, which also counts a character
    /// named as a RulesetAction's TargetIds or an EventOccurred's Involved even when they didn't do anything
    /// themselves this beat.
    /// </summary>
    private static HashSet<string> CollectActingCharacterIds(WorldChange[] changes)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            switch (change)
            {
                case ActivityChange ac when !string.IsNullOrWhiteSpace(ac.CharacterId):
                    ids.Add(ac.CharacterId);
                    break;
                case RulesetAction ra when !string.IsNullOrWhiteSpace(ra.CharacterId):
                    ids.Add(ra.CharacterId);
                    break;
                case EventOccurred eo when !string.IsNullOrWhiteSpace(eo.InitiatorId):
                    ids.Add(eo.InitiatorId!);
                    break;
            }
        }

        return ids;
    }

    /// <summary>
    /// Ambient counterpart to the explicit 'rest'/'travel'/'scene_interrupt_check' encounter rolls: an
    /// ordinary commit batch that carries MinutesElapsed can also be interrupted, so a DM doesn't have
    /// to remember to separately commit scene_interrupt_check for every long, risky, non-combat beat
    /// (a search, an interrogation, a stakeout) to get a chance at one. Gated deliberately narrow so it
    /// stays quiet in safe/empty locations: only rolls where location.DangerModifier &gt; 0 or the
    /// location's ambientCrowd reads as dense. Skips entirely if the batch already contains an explicit
    /// rest/travel/scene_interrupt_check (those already roll for themselves) or if combat is active.
    /// One roll per commit batch, using the first eligible location among characters that received a
    /// time/needs nudge this batch (i.e. actually on-screen for this beat).
    /// </summary>
    private async Task ApplyAmbientInterruptCheckAsync(
        ChangeContext context, WorldChange[] changes, Func<Task<CampaignTime>> getCurrentTimeAsync)
    {
        if (_encounterResolver is null || context.Session is null || context.ActiveCombat != null)
        {
            return;
        }

        if (changes.Any(c => c is RestChange or TravelChange or SceneInterruptCheck))
        {
            return;
        }

        var minutesElapsed = changes.Sum(c => c.MinutesElapsed ?? 0);
        if (minutesElapsed <= 0)
        {
            return;
        }

        var candidates = context.Characters.Values
            .Where(c => !string.IsNullOrEmpty(c.CurrentLocationId))
            .GroupBy(c => c.CurrentLocationId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (LocationId: g.Key, Character: g.First()))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var time = await getCurrentTimeAsync();
        var currentDay = (int)time.TotalDaysElapsed;
        var totalHours = Math.Max(1, (int)Math.Ceiling(minutesElapsed / 60.0));

        foreach (var (locationId, character) in candidates)
        {
            if (!context.Locations.TryGetValue(locationId, out var location))
            {
                location = await context.Session.LoadAsync<Location>(locationId);
                if (location is null) continue;
                context.RegisterNewLocation(location);
            }

            var isDense = AmbientCrowdHeuristics.IsCrowdDenseEnough(location.AmbientCrowd);
            var isDangerous = location.DangerModifier > 0;
            if (!isDense && !isDangerous)
            {
                continue;
            }

            if (await PressureQueryHelper.HasSceneInterruptTodayAsync(
                    context.Session, context.CampaignName, locationId, currentDay))
            {
                continue;
            }

            bool interrupted;
            List<WorldChange> deltas;
            List<string> narratives;

            if (isDense)
            {
                (interrupted, deltas, narratives) = await _encounterResolver.EvaluateSceneInterruptAsync(
                    context, character, location, riskModifier: 0, contextModifier: 0,
                    notes: $"Ambient check — {minutesElapsed} min elapsed this beat.");
            }
            else
            {
                (interrupted, _, deltas, narratives) = await _encounterResolver.EvaluateAsync(
                    context, character, location, totalHours, bucketSizeHours: 4, userModifier: 0,
                    contextType: "Ambient");
            }

            if (!interrupted)
            {
                continue;
            }

            foreach (var delta in deltas)
            {
                await DispatchMutationAsync(context, delta);
            }

            context.RecordMessage(
                $"AMBIENT INTERRUPT at {location.Name}! {string.Join(" ", narratives)} " +
                "Resolve before continuing.");
            break;
        }
    }

    /// <summary>
    /// Dispatches a single child mutation directly within an ongoing change context.
    /// Used by handlers like RulesetActionHandler that compute secondary mutations.
    /// </summary>
    public async Task DispatchMutationAsync(ChangeContext parentContext, WorldChange mutation, CancellationToken ct = default)
    {
        WorldChangeHandlerHelpers.NormalizeIdFields(mutation);
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

    /// <summary>
    /// Public entry point for callers outside the dispatch loop (e.g. CampaignRepository merging
    /// ambient simulation deltas into a commit's InvolvedEntities) that need the same entity-ID
    /// extraction used during normal change dispatch, without the rest of the dispatch pipeline.
    /// </summary>
    public IEnumerable<string> ExtractInvolvedEntityIds(WorldChange change)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExtractInvolvedIds(change, null, null, null, null, null, ids);
        return ids;
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