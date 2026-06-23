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
    /// <summary>When set (e.g. advance_world), enables world-scope ambient crowd refresh reminders.</summary>
    int? DaysAdvanced = null,
    bool DisableCooldowns = false
);

public interface IPressureContributor
{
    PressureScope Scope { get; }
    int Order { get; }
    Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default);
}

public interface IPressureOrchestrator
{
    Task<string[]> CollectAndCapAsync(PressureScope scope, PressureContext ctx, CancellationToken ct = default);
}