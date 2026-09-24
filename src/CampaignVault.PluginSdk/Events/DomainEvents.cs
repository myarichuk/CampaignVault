using System.Text.Json;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Events;

/// <summary>
/// An in-process fact ("this happened"), published during a commit and delivered synchronously to every
/// <see cref="IDomainEventHandler"/> subscribed to its <see cref="Topic"/>. Topics are strings, never shared
/// types, so plugins integrate without referencing each other: a missing publisher is just a topic that never fires.
///
/// Topic convention: <c>{source}.{name}.v{n}</c>, e.g. <c>astral.projection_ended.v1</c>. A source may only
/// publish under its own prefix: <c>core.</c> for the host, the plugin's manifest id for a plugin. Bump the
/// version suffix on a breaking payload change (and publish both for a while).
/// </summary>
public sealed record DomainEvent(string Topic, IReadOnlyDictionary<string, JsonElement> Data)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Stamped by the host: <c>core</c> or the publishing plugin's id. Not settable by publishers.</summary>
    public string Source { get; init; } = "";

    /// <summary>0 for events published by a batch change; +1 for each event-handler reaction in between.</summary>
    public int Depth { get; init; }

    /// <summary>
    /// Builds an event from a payload object (anonymous object, record, or dictionary). The payload must
    /// serialize to a JSON object; anything else (a bare string or number) is wrapped under key <c>value</c>.
    /// </summary>
    public static DomainEvent Create(string topic, object? data = null)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (data != null)
        {
            var element = JsonSerializer.SerializeToElement(data, data.GetType(), Json);
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    fields[property.Name] = property.Value.Clone();
                }
            }
            else
            {
                fields["value"] = element.Clone();
            }
        }

        return new DomainEvent(topic, fields);
    }

    /// <summary>Reads a field; false when missing or not convertible to <typeparamref name="T"/>. Never throws.</summary>
    public bool TryGet<T>(string key, out T value)
    {
        value = default!;
        if (!Data.TryGetValue(key, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        try
        {
            var converted = element.Deserialize<T>(Json);
            if (converted is null)
            {
                return false;
            }

            value = converted;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// Subscribes to domain events by exact topic. Discovered by convention scanning (core and plugin assemblies).
/// React by returning follow-up <see cref="WorldChange"/>s: they run through the normal handlers, validation
/// and session as part of the same commit, in order. Do not mutate entities from <c>ctx</c> directly.
///
/// A broken reaction (this method throws, or a follow-up is rejected) is a <em>fault</em>: by default
/// (<see cref="ReactionFailurePolicy.Isolate"/>) the remaining follow-ups are skipped, the commit is kept,
/// and the fault is reported on the take_turn response and as <see cref="CoreEvents.PluginFaulted"/>.
/// There is no rollback: follow-ups that already applied stay applied. Return a single follow-up, or set
/// <see cref="FailurePolicy"/> to <see cref="ReactionFailurePolicy.FailCommit"/> when your steps only make
/// sense together. Throw <see cref="PluginFaultException"/> to attach a fix hint.
/// Handlers may publish further events; delivery stops at a fixed depth to break loops.
/// </summary>
public interface IDomainEventHandler
{
    IReadOnlyCollection<string> Topics { get; }

    /// <summary>What a fault in this handler's reaction does to the commit. Default: isolate it.</summary>
    ReactionFailurePolicy FailurePolicy => ReactionFailurePolicy.Isolate;

    Task<IReadOnlyList<WorldChange>> HandleAsync(DomainEvent e, IChangeContext ctx, CancellationToken ct = default);
}

public enum ReactionFailurePolicy
{
    /// <summary>Skip the rest of the reaction, keep the commit, report the fault.</summary>
    Isolate = 0,

    /// <summary>Fail the whole commit (nothing is saved), and still report the fault.</summary>
    FailCommit = 1
}

/// <summary>
/// Throw from a plugin handler to report a fault with a concrete fix, e.g.
/// <c>throw new PluginFaultException("Body chars/aang has no astral link", "Enter astral mode before projecting.")</c>.
/// The fix hint is shown to the model and in <see cref="CoreEvents.PluginFaulted"/>.
/// </summary>
public class PluginFaultException(string message, string? fixHint = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string? FixHint { get; } = fixHint;
}

/// <summary>Topics and payload keys the host publishes. Use these instead of hand-typed strings.</summary>
public static class CoreEvents
{
    public const string Source = "core";

    /// <summary>A mode encounter started. Fields: modeId, encounterId, locationId, participantIds.</summary>
    public const string ModeEntered = "core.mode_entered.v1";

    /// <summary>A mode encounter ended. Fields: modeId, encounterId, participantIds.</summary>
    public const string ModeExited = "core.mode_exited.v1";

    /// <summary>
    /// A character was dealt damage (published even at 0 HP: a downed body can still be hit). Fields:
    /// characterId, amount (requested), hpLost (actual), currentHp, maxHp, actorId (only when a top-level
    /// ruleset_action by that actor targeted this character).
    /// </summary>
    public const string CharacterDamaged = "core.character_damaged.v1";

    /// <summary>A character's HP went from above 0 to 0. Fields: characterId, maxHp, actorId (as for damage).</summary>
    public const string CharacterDowned = "core.character_downed.v1";

    /// <summary>A character arrived at a destination. Fields: characterId, fromLocationId, locationId, hours.</summary>
    public const string Traveled = "core.traveled.v1";

    /// <summary>A rest completed (not interrupted). Fields: characterId, locationId, restType, hours.</summary>
    public const string Rested = "core.rested.v1";

    /// <summary>
    /// A travel, rest, ambient, or crowd check rolled an encounter (an ambush, a stranger, a pickpocket).
    /// Fields: characterId, locationId, context (Travel, Rest, Ambient, Scene), encounterCategory, spawnedId.
    /// </summary>
    public const string EncounterInterrupted = "core.encounter_interrupted.v1";

    /// <summary>
    /// An event beat was logged (event_occurred, including engine-emitted ones). Conversations are
    /// category <c>Conversation</c>. Fields: eventId, category, summary, involved, initiatorId, locationId,
    /// emotionalBeat, relatedEntityId.
    /// </summary>
    public const string EventLogged = "core.event_logged.v1";

    /// <summary>Combat started. Fields: encounterId, locationId, combatantIds (in initiative order), round.</summary>
    public const string CombatStarted = "core.combat_started.v1";

    /// <summary>
    /// A combatant's turn began (including the first turn after combat starts). Fields: encounterId, round,
    /// characterId, newRound.
    /// </summary>
    public const string CombatTurnStarted = "core.combat_turn_started.v1";

    /// <summary>Combat ended, explicitly or because nobody was left standing. Fields: encounterId, round, reason.</summary>
    public const string CombatEnded = "core.combat_ended.v1";

    /// <summary>
    /// A plugin's reaction faulted. Fields: pluginId, handler, topic, stage (handler_threw, follow_up_failed,
    /// depth_capped), changeType, message, fixHint, appliedChangeTypes (follow-ups that landed before the
    /// fault), commitKept. Delivered after all other reactions, at depth 0, and only when the turn is saved; a
    /// fault raised by a listener of this topic is reported but not republished.
    /// </summary>
    public const string PluginFaulted = "core.plugin_faulted.v1";

    /// <summary>Every topic core publishes.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ModeEntered, ModeExited, CharacterDamaged, CharacterDowned, Traveled, Rested, EncounterInterrupted,
        EventLogged, CombatStarted, CombatTurnStarted, CombatEnded, PluginFaulted
    ];

    public static class Fields
    {
        public const string ModeId = "modeId";
        public const string EncounterId = "encounterId";
        public const string LocationId = "locationId";
        public const string ParticipantIds = "participantIds";
        public const string CharacterId = "characterId";
        public const string Amount = "amount";
        public const string HpLost = "hpLost";
        public const string CurrentHp = "currentHp";
        public const string MaxHp = "maxHp";
        public const string ActorId = "actorId";
        public const string FromLocationId = "fromLocationId";
        public const string Hours = "hours";
        public const string RestType = "restType";
        public const string Context = "context";
        public const string EncounterCategory = "encounterCategory";
        public const string SpawnedId = "spawnedId";
        public const string EventId = "eventId";
        public const string Category = "category";
        public const string Summary = "summary";
        public const string Involved = "involved";
        public const string InitiatorId = "initiatorId";
        public const string EmotionalBeat = "emotionalBeat";
        public const string RelatedEntityId = "relatedEntityId";
        public const string CombatantIds = "combatantIds";
        public const string Round = "round";
        public const string NewRound = "newRound";
        public const string Reason = "reason";
        public const string PluginId = "pluginId";
        public const string Handler = "handler";
        public const string Topic = "topic";
        public const string Stage = "stage";
        public const string ChangeType = "changeType";
        public const string Message = "message";
        public const string FixHint = "fixHint";
        public const string AppliedChangeTypes = "appliedChangeTypes";
        public const string CommitKept = "commitKept";
    }

    /// <summary>True when <paramref name="topic"/> sits under <paramref name="source"/>'s own prefix.</summary>
    public static bool IsOwnedBy(string topic, string source) =>
        !string.IsNullOrWhiteSpace(topic) && !string.IsNullOrWhiteSpace(source) &&
        topic.StartsWith(source + ".", StringComparison.OrdinalIgnoreCase) &&
        topic.Length > source.Length + 1;
}
