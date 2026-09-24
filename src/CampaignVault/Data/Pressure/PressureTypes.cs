using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data.Pressure;

[Flags]
public enum PressureScope
{
    World = 1,
    Scene = 2,
    Both = World | Scene
}

public sealed record QuestDeadlineInfo(string Id, string Title, int? DeadlineDay);

public sealed record PressureContext(
    string CampaignName,
    CampaignTime Time,
    CampaignConfig Config,
    IAsyncDocumentSession Session,
    IReadOnlyList<Rumor>? ActiveRumors = null,
    IReadOnlyList<Event>? RecentEvents = null,
    IReadOnlyList<QuestDeadlineInfo>? QuestDeadlines = null,
    SceneView? Scene = null,
    string? RequestedLocationId = null,
    bool PartyPresent = false,
    /// <summary>
    /// Campaign's PC + companion character IDs, when known. Lets contributors that would otherwise
    /// scan every KeepAlive character in the campaign (e.g. CharacterDistressPressureContributor's
    /// ambient-needs check) restrict "who is relevant right now" without a full scene assembly. Null
    /// means the caller has no scene context (e.g. advance_world) — contributors should fall back to
    /// their prior campaign-wide behavior.
    /// </summary>
    IReadOnlyList<string>? PartyCharacterIds = null,
    /// <summary>When set (e.g. advance_world), enables world-scope ambient crowd refresh reminders.</summary>
    int? DaysAdvanced = null,
    bool DisableCooldowns = false,
    /// <summary>Changes committed by this turn (take_turn only). Edge-trigger input for guidance contributors.</summary>
    IReadOnlyList<WorldChange>? AppliedChanges = null
);

public interface IPressureContributor
{
    PressureScope Scope { get; }
    int Order { get; }
    Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default);
}

public interface IPressureOrchestrator
{
    Task<List<WorldPressureItem>> CollectAndCapAsync(PressureScope scope, PressureContext ctx, CancellationToken ct = default);
}