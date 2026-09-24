using System.Reflection;
using CampaignVault.Data.Events;
using CampaignVault.Data.Pressure;
using CampaignVault.Events;
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
    BackgroundDefinitionProvider? backgroundProvider = null,
    IEnumerable<IWorldChangeObserver>? observers = null,
    IRollService? rollService = null,
    IEnumerable<IDomainEventHandler>? eventHandlers = null,
    PluginEventSources? eventSources = null)
{
    /// <summary>
    /// Events at this depth are dropped instead of delivered: batch change (0) → reaction (1) → reaction (2)
    /// is plenty for any real integration; deeper chains are almost always a publish loop.
    /// </summary>
    internal const int MaxEventDepth = 3;

    private readonly IReadOnlyList<IDomainEventHandler> _eventHandlers = eventHandlers?.ToList() ?? [];
    private readonly PluginEventSources _eventSources = eventSources ?? PluginEventSources.CoreOnly;
    private readonly IReadOnlyList<IWorldChangeHandler> _handlers = handlers?.ToList() ?? [];
    private readonly IReadOnlyList<IWorldChangeObserver> _observers = observers?.ToList() ?? [];
    private readonly ILogger<WorldChangeDispatcher> _logger = logger ?? NullLogger<WorldChangeDispatcher>.Instance;
    private readonly CampaignDocumentKeys _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly EncounterResolver? _encounterResolver = encounterResolver;
    private readonly ClassDefinitionProvider? _classProvider = classProvider;
    private readonly BackgroundDefinitionProvider? _backgroundProvider = backgroundProvider;
    private readonly IRollService? _rollService = rollService;

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
    ///
    /// The dictionary built by <see cref="BuildHandlerDictionary"/> only enumerates WorldChange subtypes
    /// declared in the *core* CampaignVault assembly (typeof(WorldChange).Assembly) — a plugin-defined
    /// WorldChange subtype (e.g. a mode plugin's own verb) is never a key in it. Fall back to a linear
    /// ShouldHandle scan for any change type not already indexed, and cache the result so repeat
    /// dispatches of the same plugin-defined type don't re-scan. This is the mechanism that makes
    /// PLUGINS.md's "define your own WorldChange subtype, register a handler, it Just Works" claim true
    /// for plugin assemblies, not just the core one.
    /// </summary>
    public IWorldChangeHandler? FindHandler(WorldChange change)
    {
        var type = change.GetType();
        if (_handlersByChangeType.TryGetValue(type, out var handler))
        {
            return handler;
        }

        foreach (var candidate in _handlers)
        {
            if (candidate.ShouldHandle(change))
            {
                _handlersByChangeType[type] = candidate;
                return candidate;
            }
        }

        return null;
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
            // Background need/attribute simulation ticks (hunger, tiredness, morale drift, climate
            // readings) touch every scheduled NPC every turn — still need the character loaded so the
            // handler can apply the delta, but they shouldn't make every campaign NPC show up as
            // "involved" in a turn they had no narrative part in.
            var isBackgroundTick = change.IsEngineAuthored && change is NeedChange or AttributeChange;
            ExtractInvolvedIds(change, characterIds, locationIds, factionIds, questIds, itemIds, isBackgroundTick ? null : allInvolved);
            if (change is RulesetAction) needsCombat = true;
            if (change is RulesetAction or LevelUpChange or ModeTransitionChange or CampaignUpdateChange) needsRulesetConfig = true;
        }

        Dictionary<string, Character> characters;
        Dictionary<string, Item> items;
        Dictionary<string, Location> locations;
        Dictionary<string, Faction> factions;
        Dictionary<string, Quest> quests;
        CombatEncounter? activeCombat = null;
        ModeEncounter? activeMode = null;
        List<ModeEncounter>? activeModes = null;
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

            // Config is needed for EnabledModeIds (plugin mode verbs) as well as ruleset/level-up flags.
            if (!string.IsNullOrEmpty(effectiveCampaign))
            {
                var configId = _keys.Config(effectiveCampaign);
                var loaded = await session.LoadAsync<CampaignConfig>(configId);
                if (loaded != null)
                    config = loaded;
                else if (needsRulesetConfig)
                    config = new CampaignConfig { Id = configId };
            }

            // Preload every active ModeEncounter among EnabledModeIds (modes can overlap, e.g. crafting
            // mid-combat). activeMode stays the first for the legacy single-mode view.
            if (config?.EnabledModeIds is { Count: > 0 } && !string.IsNullOrEmpty(effectiveCampaign))
            {
                activeModes = [];
                foreach (var modeId in config.EnabledModeIds)
                {
                    var enc = await session.LoadAsync<ModeEncounter>(_keys.ModeCurrent(effectiveCampaign, modeId));
                    if (enc is { IsActive: true })
                    {
                        activeModes.Add(enc);
                    }
                }

                activeMode = activeModes.FirstOrDefault();
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

        var physicalStateNudges = new List<string>();

        ChangeContext context;
        if (session is null)
        {
            // Support pure unit tests of handler selection / duplicate detection / result aggregation
            // that use fake TestHandlers which never access Session / time / logging hooks.
            context = new ChangeContext(null, characters, items, locations, factions, quests, _logger, summary, this, activeCombat, activeMode, effectiveCampaign, config, physicalStateNudges, activeModes);
            context.Rolls = _rollService;
        }
        else
        {
            context = new ChangeContext(session, characters, items, locations, factions, quests, _logger, getCurrentTimeAsync, getSystemOptionsAsync, logEventAsync, summary, this, activeCombat, activeMode, effectiveCampaign, config, physicalStateNudges, activeModes);
            context.Rolls = _rollService;
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
                    result = await RunAsSourceAsync(context, chosen, () => chosen.ApplyAsync(change, context));
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
                    context.DiscardPendingEvents();
                    context.RecordFailure();
                    overallSuccess = false;
                }
                else
                {
                    await NotifyObserversAsync(change, context);
                    await DeliverDomainEventsAsync(context);
                }
            }
            catch (Exception ex)
            {
                context.DiscardPendingEvents();
                _logger.LogError(ex, "Error processing change of type {ChangeType}", change?.GetType().Name);
                summary.Add(ex is PluginFaultException { FixHint: { Length: > 0 } fixHint }
                    ? $"ERROR: Failed to process {change?.GetType().Name}: {ex.Message} Fix: {fixHint}"
                    : $"ERROR: Failed to process {change?.GetType().Name}: {ex.Message}");
                context.RecordFailure();
                overallSuccess = false;
            }
        }

        // A parent handler can dispatch child mutations via DispatchMutationAsync (e.g. RestChangeHandler
        // dispatching an ActivityChange) and still report its own result.Success = true even when a child
        // failed — DispatchMutationAsync records the failure onto the shared context but returns void, so
        // nothing upstream of this point re-checks it. Catch it here instead of at each of the many
        // parent-handler call sites.
        if (context.HasFailure)
        {
            overallSuccess = false;
        }

        if (overallSuccess)
        {
            await ApplyMicroTimeNudgeAsync(context, changes, getCurrentTimeAsync);
            ApplyMomentumTracking(context, changes);
            await ApplyAmbientInterruptCheckAsync(context, changes, getCurrentTimeAsync);

            // Engine steps above can dispatch child mutations (e.g. ambient damage) that publish events.
            await DeliverDomainEventsAsync(context);
            await DeliverFaultEventsAsync(context);

            // A FailCommit reaction to a post-loop or fault event can still fail the batch here.
            if (context.HasFailure)
            {
                overallSuccess = false;
            }
        }

        context.DiscardPendingEvents();

        _logger.LogInformation("WorldChangeDispatcher processed {Processed} changes (overall success: {Success})",
            changes.Length, overallSuccess);

        return new CommitResult
        {
            Success = overallSuccess,
            ChangesProcessed = changes.Length,
            Summary = summary,
            InvolvedEntities = context.InvolvedEntities.ToList(),
            EntityCollisions = context.EntityCollisions.ToList(),
            CommittedIds = context.CommittedIds.ToList(),
            PhysicalStateNudges = physicalStateNudges,
            PluginFaults = context.PluginFaults.ToList(),
            ReactionChanges = overallSuccess ? context.ReactionChanges.ToList() : []
        };
    }

    /// <summary>
    /// Delivers core events raised outside a take_turn batch (the combat lifecycle lives in CombatTools, not in a
    /// WorldChange handler). Same delivery rules as a batch: reactions dispatch as follow-ups on
    /// <paramref name="session"/>, faults are isolated unless a subscriber opts into FailCommit (then
    /// Success is false and the caller must not save). Pass the characters the caller already loaded, plus the
    /// tracked encounter, so reactions mutate the same instances the caller saves.
    /// </summary>
    public async Task<CommitResult> PublishAsync(
        IAsyncDocumentSession session,
        string campaign,
        IReadOnlyList<(string Topic, object? Data)> events,
        IEnumerable<Character> loadedCharacters,
        CombatEncounter? activeCombat,
        Func<Task<CampaignTime>> getCurrentTimeAsync,
        Func<Task<Dictionary<string, string>>> getSystemOptionsAsync,
        Func<Event, Task> logEventAsync)
    {
        var subscribed = events.Any(e => _eventHandlers.Any(h =>
            h.Topics.Contains(e.Topic, StringComparer.OrdinalIgnoreCase)));
        if (!subscribed)
        {
            return new CommitResult { Success = true };
        }

        var config = await session.LoadAsync<CampaignConfig>(_keys.Config(campaign));
        var activeModes = new List<ModeEncounter>();
        foreach (var modeId in config?.EnabledModeIds ?? [])
        {
            var enc = await session.LoadAsync<ModeEncounter>(_keys.ModeCurrent(campaign, modeId));
            if (enc is { IsActive: true })
            {
                activeModes.Add(enc);
            }
        }

        var summary = new List<string>();
        var characters = loadedCharacters
            .Where(c => c != null)
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var context = new ChangeContext(session, characters, new Dictionary<string, Item>(), new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(), new Dictionary<string, Quest>(), _logger, getCurrentTimeAsync,
            getSystemOptionsAsync, logEventAsync, summary, this, activeCombat, activeModes.FirstOrDefault(), campaign,
            config, [], activeModes)
        {
            Rolls = _rollService,
            Batch = [],
            BatchIndex = -1
        };

        foreach (var (topic, data) in events)
        {
            context.EnqueueCoreEvent(topic, data, depth: 0);
        }

        await DeliverDomainEventsAsync(context);
        if (!context.HasFailure)
        {
            await DeliverFaultEventsAsync(context);
        }

        context.DiscardPendingEvents();

        var success = !context.HasFailure;
        return new CommitResult
        {
            Success = success,
            Summary = summary,
            InvolvedEntities = context.InvolvedEntities.ToList(),
            PluginFaults = context.PluginFaults.ToList(),
            ReactionChanges = success ? context.ReactionChanges.ToList() : []
        };
    }

    /// <summary>
    /// Sums WorldChange.MinutesElapsed across the batch and, if any beat carried a duration, accounts
    /// for needs (hunger/thirst/tiredness/social_drive) drift for it — exactly once, via one of two
    /// mutually exclusive paths depending on how much time passed:
    ///
    /// - Under an hour: applies an instant nudge directly to the characters involved in *this* batch
    ///   only (not a campaign-wide sweep — see <see cref="CollectOnScreenCharacterIds"/>). The clock's
    ///   Hour never moves for a span this small (a few lines of dialogue shouldn't flip TimeOfDay), so
    ///   the day-tick simulation has no other way to ever see these minutes — this is their only
    ///   accounting.
    /// - An hour or more: advances CampaignTime via AdvanceHours instead, which accumulates into
    ///   CampaignTime.UnsimulatedHours and causes StageChangesAsync to run the full simulation tick
    ///   immediately after this batch — that tick's NeedsAccumulationRule already sweeps every
    ///   scheduled character (on-screen or not) for exactly this span, so applying the instant nudge
    ///   here TOO would double-count it for the on-screen characters.
    ///
    /// RestChange/TravelChange are excluded from the sum even if MinutesElapsed is set on them (LLM
    /// mistake or otherwise) — both already call CampaignTime.AdvanceHours themselves via their own
    /// handlers, so including them here would double-advance the clock and double-accumulate needs
    /// for the same stretch of time.
    /// </summary>
    private async Task ApplyMicroTimeNudgeAsync(
        IChangeContext context, WorldChange[] changes, Func<Task<CampaignTime>> getCurrentTimeAsync)
    {
        var minutesElapsed = changes
            .Where(c => c is not RestChange and not TravelChange)
            .Sum(c => c.MinutesElapsed ?? 0);
        if (minutesElapsed <= 0)
        {
            return;
        }

        if (minutesElapsed >= 60)
        {
            // Defer entirely to the day-tick that's about to run for this exact span (see summary
            // above) — do not also apply the instant on-screen nudge below.
            var time = await getCurrentTimeAsync();
            time.AdvanceHours(minutesElapsed / 60.0);
            return;
        }

        var onScreenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            CollectOnScreenCharacterIds(change, onScreenIds);
        }

        if (onScreenIds.Count == 0)
        {
            return;
        }

        var days = minutesElapsed / 1440.0;
        var nudgedCharacterIds = new List<string>();

        // Applies deltas directly (bypassing NeedChangeHandler/DispatchMutationAsync's per-need
        // context.RecordMessage) and emits one collapsed summary line for the whole nudge instead of
        // one line per need per character — same numeric effect, without flooding the LLM-facing
        // summary with near-zero-magnitude ambient noise on every commit that carries MinutesElapsed.
        foreach (var character in context.Characters.Values)
        {
            if (character.Needs is null || !onScreenIds.Contains(character.Id))
            {
                continue;
            }

            // Per-character rates: each on-screen character's own AccumulationRates apply
            // (core-need keys override the config-driven value for that character only).
            var perDayDeltas = NeedAccumulationMath.ComputeDeltas(context.Config, days, character.Needs.AccumulationRates);

            var updatedNeeds = new Dictionary<string, float>(character.Needs.ActiveNeeds);
            var changedAny = false;
            foreach (var (need, delta) in perDayDeltas)
            {
                var current = updatedNeeds.GetValueOrDefault(need, 0f);
                // Clamp-to-100 headroom bounds output regardless of rate magnitude —
                // no separate cap is needed for custom AccumulationRates.
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
    private static void ApplyMomentumTracking(IChangeContext context, WorldChange[] changes)
    {
        var ctx = (ChangeContext)context;
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
        IChangeContext context, WorldChange[] changes, Func<Task<CampaignTime>> getCurrentTimeAsync)
    {
        var ctx = (ChangeContext)context;
        if (_encounterResolver is null || ctx.Session is null || context.ActiveCombat != null)
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
                location = await ctx.Session.LoadAsync<Location>(locationId);
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
                    ctx.Session, context.CampaignName, locationId, currentDay))
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
    /// Runs every interested IWorldChangeObserver after a change's own handler has already committed
    /// successfully. Deliberately non-failing: an observer exception is logged and swallowed rather than
    /// recorded via context.RecordFailure(), so a broken observer (e.g. a buggy "inner voice" plugin)
    /// can never roll back or block an unrelated mutation. Not invoked for changes dispatched via
    /// DispatchMutationAsync (child mutations) — only the top-level batch loop.
    /// </summary>
    private async Task NotifyObserversAsync(WorldChange change, IChangeContext context)
    {
        foreach (var observer in _observers)
        {
            try
            {
                if (observer.IsInterestedIn(change, context))
                {
                    await RunAsSourceAsync((ChangeContext)context, observer, async () =>
                    {
                        await observer.OnCommittedAsync(change, context);
                        return true;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IWorldChangeObserver {ObserverType} threw while handling {ChangeType}",
                    observer.GetType().Name, change.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the context's event source set to <paramref name="actor"/>'s source,
    /// so anything it publishes is stamped (and prefix-checked) as coming from the actor's assembly.
    /// </summary>
    private async Task<T> RunAsSourceAsync<T>(ChangeContext context, object actor, Func<Task<T>> action)
    {
        var previous = context.EventSource;
        context.EventSource = _eventSources.SourceFor(actor.GetType());
        try
        {
            return await action();
        }
        finally
        {
            context.EventSource = previous;
        }
    }

    /// <summary>
    /// Delivers pending domain events synchronously, in publish order, until none are left. Subscriber
    /// reactions (follow-up changes) dispatch as child mutations of the current commit, one depth level deeper,
    /// so whatever they publish is delivered in a later wave. Events at <see cref="MaxEventDepth"/> are dropped.
    /// </summary>
    private async Task DeliverDomainEventsAsync(ChangeContext context)
    {
        if (_eventHandlers.Count == 0)
        {
            context.DiscardPendingEvents();
            return;
        }

        for (var wave = context.TakePendingEvents(); wave.Count > 0; wave = context.TakePendingEvents())
        {
            foreach (var domainEvent in wave)
            {
                var subscribers = _eventHandlers
                    .Where(s => s.Topics.Contains(domainEvent.Topic, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (subscribers.Count == 0)
                {
                    continue;
                }

                if (domainEvent.Depth >= MaxEventDepth)
                {
                    _logger.LogWarning(
                        "Dropped domain event {Topic} from {Source}: depth cap {MaxDepth} reached (likely a publish loop)",
                        domainEvent.Topic, domainEvent.Source, MaxEventDepth);
                    RecordFault(context, domainEvent, new PluginFault(
                        domainEvent.Source, "(dispatcher)", domainEvent.Topic, PluginFault.DepthCapped, null,
                        $"Event dropped: reaction chains stop at depth {MaxEventDepth}.",
                        "A subscriber republishes events that trigger itself again. Break the loop (publish only on an edge, not on every reaction).",
                        [], CommitKept: true));
                    continue;
                }

                foreach (var subscriber in subscribers)
                {
                    await DeliverToSubscriberAsync(context, subscriber, domainEvent);
                }
            }
        }
    }

    /// <summary>
    /// Runs one subscriber's reaction. A fault (the handler throws, or a follow-up is rejected) stops the reaction:
    /// events it published after the last good step are dropped, the fault is recorded, and under
    /// <see cref="ReactionFailurePolicy.Isolate"/> the commit's failure state is restored so the turn is kept.
    /// Follow-ups that already applied are not rolled back (handlers mutate tracked entities in place); the
    /// fault lists them so the model knows the reaction is partial.
    /// </summary>
    private async Task DeliverToSubscriberAsync(ChangeContext context, IDomainEventHandler subscriber, DomainEvent domainEvent)
    {
        var previousDepth = context.EventDepth;
        context.EventDepth = domainEvent.Depth + 1;
        var policy = subscriber.FailurePolicy;
        var pluginId = _eventSources.SourceFor(subscriber.GetType());
        var handlerName = subscriber.GetType().Name;
        var failuresBefore = context.FailureCount;
        try
        {
            var mark = context.PendingEventMark;
            IReadOnlyList<WorldChange> followUps;
            try
            {
                followUps = await RunAsSourceAsync(context, subscriber, () => subscriber.HandleAsync(domainEvent, context));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IDomainEventHandler {HandlerType} threw while handling {Topic}", handlerName, domainEvent.Topic);
                context.TruncatePendingEvents(mark);
                if (policy == ReactionFailurePolicy.FailCommit)
                {
                    context.RecordFailure();
                }

                RecordFault(context, domainEvent, new PluginFault(
                    pluginId, handlerName, domainEvent.Topic, PluginFault.HandlerThrew, null,
                    $"{ex.GetType().Name}: {ex.Message}",
                    (ex as PluginFaultException)?.FixHint ?? "Bug in the plugin's event handler; check the host log for the stack trace.",
                    [], CommitKept: policy == ReactionFailurePolicy.Isolate));
                return;
            }

            var applied = new List<string>();
            foreach (var followUp in followUps ?? [])
            {
                if (followUp is null)
                {
                    continue;
                }

                mark = context.PendingEventMark;
                var failuresBeforeStep = context.FailureCount;
                await PreloadAsync(context, followUp);
                var result = await DispatchChildAsync(context, followUp);
                if (result.Success && context.FailureCount == failuresBeforeStep)
                {
                    applied.Add(followUp.GetType().Name);
                    context.ReactionChanges.Add(followUp);
                    await NotifyObserversAsync(followUp, context);
                    continue;
                }

                context.TruncatePendingEvents(mark);
                if (policy == ReactionFailurePolicy.Isolate)
                {
                    context.ResetFailureCount(failuresBefore);
                }
                else
                {
                    context.RecordFailure();
                }

                RecordFault(context, domainEvent, new PluginFault(
                    pluginId, handlerName, domainEvent.Topic, PluginFault.FollowUpFailed, followUp.GetType().Name,
                    $"Follow-up rejected: {result.Message ?? "the change's handler reported a failure (see summary)."}",
                    result.FixHint ?? "The plugin returned a change the engine rejected; check the ids and required fields it sets.",
                    applied, CommitKept: policy == ReactionFailurePolicy.Isolate));
                return;
            }
        }
        finally
        {
            context.EventDepth = previousDepth;
        }
    }

    /// <summary>
    /// Loads the entities a reaction's follow-up references that the batch did not preload (a subscriber may
    /// touch any character, e.g. the astral self when the body is hit). Most handlers read only the preloaded
    /// dictionaries, so without this a valid follow-up would be rejected as "not found".
    /// </summary>
    private async Task PreloadAsync(ChangeContext context, WorldChange change)
    {
        if (context.Session is not { } session)
        {
            return;
        }

        var characterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var factionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var questIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WorldChangeHandlerHelpers.NormalizeIdFields(change);
        ExtractInvolvedIds(change, characterIds, locationIds, factionIds, questIds, itemIds);

        bool Visible(string? campaignName) =>
            string.IsNullOrEmpty(context.CampaignName) || CampaignEntityVisibility.IsVisibleInCampaign(campaignName, context.CampaignName);

        var missingCharacters = characterIds.Where(id => !context.Characters.ContainsKey(id)).ToList();
        if (missingCharacters.Count > 0)
        {
            var loaded = (await session.LoadAsync<Character>(missingCharacters) ?? new Dictionary<string, Character>())
                .Where(kv => kv.Value != null && Visible(kv.Value.CampaignName))
                .ToDictionary(kv => kv.Key, kv => kv.Value!);
            if (loaded.Count > 0 && !string.IsNullOrEmpty(context.CampaignName))
            {
                await SystemStatsUpgradeHelper.UpgradeCharacterSystemStatsAsync(
                    session, loaded, context.CampaignName, _keys, _classProvider, _backgroundProvider);
            }

            foreach (var character in loaded.Values)
            {
                context.RegisterNewCharacter(character);
            }
        }

        foreach (var item in await LoadMissingAsync<Item>(session, itemIds, context.Items))
        {
            if (Visible(item.CampaignName)) context.RegisterNewItem(item);
        }

        foreach (var location in await LoadMissingAsync<Location>(session, locationIds, context.Locations))
        {
            if (Visible(location.CampaignName)) context.RegisterNewLocation(location);
        }

        foreach (var faction in await LoadMissingAsync<Faction>(session, factionIds, context.Factions))
        {
            if (Visible(faction.CampaignName)) context.RegisterNewFaction(faction);
        }

        foreach (var quest in await LoadMissingAsync<Quest>(session, questIds, context.Quests))
        {
            if (Visible(quest.CampaignName)) context.RegisterNewQuest(quest);
        }
    }

    private static async Task<IEnumerable<T>> LoadMissingAsync<T>(
        IAsyncDocumentSession session, HashSet<string> ids, IReadOnlyDictionary<string, T> have)
    {
        var missing = ids.Where(id => !have.ContainsKey(id)).ToList();
        if (missing.Count == 0)
        {
            return [];
        }

        var loaded = await session.LoadAsync<T>(missing);
        return loaded?.Values.Where(v => v != null).Select(v => v!) ?? [];
    }

    /// <summary>
    /// Records a fault for the response and queues <see cref="CoreEvents.PluginFaulted"/> for the fault phase
    /// (<see cref="DeliverFaultEventsAsync"/>). Faults raised during the fault phase are reported only, so a
    /// broken fault listener can never feed on its own faults.
    /// </summary>
    private static void RecordFault(ChangeContext context, DomainEvent cause, PluginFault fault)
    {
        context.PluginFaults.Add(fault);
        context.RecordMessage(fault.Describe());

        if (context.InFaultPhase)
        {
            return;
        }

        context.PendingFaultEvents.Add(DomainEvent.Create(CoreEvents.PluginFaulted, new Dictionary<string, object?>
        {
            [CoreEvents.Fields.PluginId] = fault.PluginId,
            [CoreEvents.Fields.Handler] = fault.Handler,
            [CoreEvents.Fields.Topic] = fault.Topic,
            [CoreEvents.Fields.Stage] = fault.Stage,
            [CoreEvents.Fields.ChangeType] = fault.ChangeType,
            [CoreEvents.Fields.Message] = fault.Message,
            [CoreEvents.Fields.FixHint] = fault.FixHint,
            [CoreEvents.Fields.AppliedChangeTypes] = fault.AppliedChangeTypes,
            [CoreEvents.Fields.CommitKept] = fault.CommitKept
        }) with { Source = CoreEvents.Source, Depth = 0 });
    }

    /// <summary>
    /// Fault phase: delivers the plugin_faulted events collected during normal delivery, at depth 0 so fault
    /// listeners get the full depth budget. Faults raised by fault listeners are reported, not republished.
    /// </summary>
    private async Task DeliverFaultEventsAsync(ChangeContext context)
    {
        if (context.PendingFaultEvents.Count == 0)
        {
            return;
        }

        context.DiscardPendingEvents();
        foreach (var faultEvent in context.PendingFaultEvents)
        {
            context.EnqueueCoreEvent(faultEvent.Topic, faultEvent.Data, depth: 0);
        }

        context.PendingFaultEvents.Clear();
        context.InFaultPhase = true;
        try
        {
            await DeliverDomainEventsAsync(context);
        }
        finally
        {
            context.InFaultPhase = false;
        }
    }

    /// <summary>
    /// Dispatches a single child mutation directly within an ongoing change context.
    /// Used by handlers like RulesetActionHandler that compute secondary mutations.
    /// </summary>
    public async Task DispatchMutationAsync(IChangeContext parentContext, WorldChange mutation, CancellationToken ct = default) =>
        await DispatchChildAsync((ChangeContext)parentContext, mutation, ct);

    private readonly record struct ChildResult(bool Success, string? Message, string? FixHint);

    private async Task<ChildResult> DispatchChildAsync(ChangeContext parent, WorldChange mutation, CancellationToken ct = default)
    {
        WorldChangeHandlerHelpers.NormalizeIdFields(mutation);
        var chosen = FindHandler(mutation);

        if (chosen == null)
        {
            _logger.LogWarning("No handler found for child mutation of type {ChangeType}", mutation?.GetType().Name);
            parent.RecordFailure();
            return new ChildResult(false, $"No handler for change type {mutation?.GetType().Name}.", null);
        }

        TrackInvolvedEntities(mutation, parent);

        try
        {
            var result = await RunAsSourceAsync(parent, chosen, () => chosen.ApplyAsync(mutation, parent, ct));
            TrackInvolvedEntities(mutation, parent);
            if (result.Message is not null)
            {
                parent.RecordMessage(result.Message);
            }
            if (!result.Success)
            {
                parent.RecordFailure();
            }

            return new ChildResult(result.Success, result.Message, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing child mutation of type {ChangeType}", mutation?.GetType().Name);
            parent.RecordFailure();
            return new ChildResult(false, $"{ex.GetType().Name}: {ex.Message}", (ex as PluginFaultException)?.FixHint);
        }
    }

    private void TrackInvolvedEntities(WorldChange change, IChangeContext context)
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