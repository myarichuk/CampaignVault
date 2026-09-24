using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Rulesets.Modes;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles mode_transition — entering/exiting a plugin-defined interaction mode. See
/// PLUGIN_SYSTEM_PLAN.md Track B / INTERACTION_MODES_PLAN.md.
/// </summary>
public sealed class ModeTransitionChangeHandler(
    IInteractionModeSelector modeSelector,
    CampaignVault.Data.CampaignDocumentKeys keys) : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ModeTransitionChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var mt = (ModeTransitionChange)change;

        if (string.IsNullOrWhiteSpace(mt.ModeId))
        {
            return ChangeHandlerResult.Failure("modeId is required.");
        }

        var mode = modeSelector.TryGetMode(mt.ModeId);
        if (mode is null)
        {
            return ChangeHandlerResult.Failure(
                $"Unknown interaction mode '{mt.ModeId}'. Registered modes: " +
                (modeSelector.RegisteredModeIds.Count > 0 ? string.Join(", ", modeSelector.RegisteredModeIds) : "(none)") +
                ". Modes are loaded as plugins — see PLUGINS.md.");
        }

        if (string.IsNullOrEmpty(ctx.CampaignName))
        {
            return ChangeHandlerResult.Failure("mode_transition requires an active campaign.");
        }

        var enabledModeIds = ctx.Config?.EnabledModeIds ?? [];
        if (!enabledModeIds.Contains(mt.ModeId, StringComparer.OrdinalIgnoreCase))
        {
            return ChangeHandlerResult.Failure(
                $"Interaction mode '{mt.ModeId}' is not enabled for this campaign. " +
                "Enable it via CampaignConfig.EnabledModeIds first.");
        }

        var activeSystem = ctx.Config?.ActiveSystem;
        if (mode.CompatibleSystems.Count > 0 &&
            (activeSystem is null || !mode.CompatibleSystems.Contains(activeSystem, StringComparer.OrdinalIgnoreCase)))
        {
            return ChangeHandlerResult.Failure(
                $"Interaction mode '{mt.ModeId}' is not compatible with this campaign's active system " +
                $"('{activeSystem}'). Compatible systems: {string.Join(", ", mode.CompatibleSystems)}.");
        }

        var encounterId = keys.ModeCurrent(ctx.CampaignName, mt.ModeId);
        var action = (mt.Action ?? "enter").Trim().ToLowerInvariant();

        switch (action)
        {
            case "enter":
                return await EnterAsync(mode, mt, ctx, encounterId, ct);
            case "exit":
                return await ExitAsync(ctx, encounterId, ct);
            default:
                return ChangeHandlerResult.Failure($"Unknown mode_transition action '{mt.Action}'. Expected 'enter' or 'exit'.");
        }
    }

    private async Task<ChangeHandlerResult> EnterAsync(
        IInteractionMode mode, ModeTransitionChange mt, IChangeContext context, string encounterId, CancellationToken ct)
    {
        var ctx = (ChangeContext)context;
        if (string.IsNullOrWhiteSpace(mt.LocationId))
        {
            return ChangeHandlerResult.Failure("locationId is required to enter a mode.");
        }

        if (mt.ParticipantIds.Count == 0)
        {
            return ChangeHandlerResult.Failure("participantIds must include at least one character to enter a mode.");
        }

        var existing = await ctx.Session.LoadAsync<ModeEncounter>(encounterId, ct);
        if (existing is { IsActive: true })
        {
            return ChangeHandlerResult.Failure(
                $"Mode '{mt.ModeId}' already has an active encounter for this campaign. Exit it first.");
        }

        var claimConflict = FindExclusiveClaimConflict(mode, mt, ctx);
        if (claimConflict != null)
        {
            return ChangeHandlerResult.Failure(claimConflict);
        }

        var encounter = mode.StateMachine.CreateEncounter(mt.LocationId, mt.ParticipantIds);
        encounter.Id = encounterId;
        encounter.ModeId = mt.ModeId;
        encounter.IsActive = true;

        await ctx.Session.StoreAsync(encounter, ct);
        ctx.EnterMode(encounter);
        context.RecordMessage($"Entered interaction mode '{mode.DisplayName}' at {mt.LocationId}.");
        context.Publish(CoreEvents.ModeEntered, new Dictionary<string, object?>
        {
            [CoreEvents.Fields.ModeId] = encounter.ModeId,
            [CoreEvents.Fields.EncounterId] = encounter.Id,
            [CoreEvents.Fields.LocationId] = encounter.LocationId,
            [CoreEvents.Fields.ParticipantIds] = encounter.Participants.Select(p => p.CharacterId).ToList()
        });
        return ChangeHandlerResult.Ok;
    }

    /// <summary>
    /// A participant may sit in several modes at once unless one of them claims it exclusively (e.g. astral
    /// projection: the mind acts only there). Returns the failure message, or null when entry is allowed.
    /// </summary>
    private string? FindExclusiveClaimConflict(IInteractionMode mode, ModeTransitionChange mt, ChangeContext ctx)
    {
        foreach (var other in ctx.ActiveModes.Values)
        {
            if (!other.IsActive || string.Equals(other.ModeId, mt.ModeId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var otherClaim = modeSelector.TryGetMode(other.ModeId)?.ParticipantClaim ?? ModeParticipantClaim.Independent;
            if (mode.ParticipantClaim != ModeParticipantClaim.Exclusive && otherClaim != ModeParticipantClaim.Exclusive)
            {
                continue;
            }

            var shared = mt.ParticipantIds
                .Where(id => other.Participants.Any(p => string.Equals(p.CharacterId, id, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (shared.Count > 0)
            {
                var exclusiveMode = mode.ParticipantClaim == ModeParticipantClaim.Exclusive ? mt.ModeId : other.ModeId;
                return $"{string.Join(", ", shared)} already in mode '{other.ModeId}'; '{exclusiveMode}' claims its " +
                       "participants exclusively. Exit that mode first.";
            }
        }

        return null;
    }

    private static async Task<ChangeHandlerResult> ExitAsync(IChangeContext context, string encounterId, CancellationToken ct)
    {
        var ctx = (ChangeContext)context;
        var existing = await ctx.Session.LoadAsync<ModeEncounter>(encounterId, ct);
        if (existing is null || !existing.IsActive)
        {
            return ChangeHandlerResult.Failure("No active encounter for this mode to exit.");
        }

        existing.IsActive = false;
        existing.ActiveTurnId = null;
        ctx.ExitMode(existing.ModeId);
        context.RecordMessage($"Exited interaction mode '{existing.ModeId}'.");
        context.Publish(CoreEvents.ModeExited, new Dictionary<string, object?>
        {
            [CoreEvents.Fields.ModeId] = existing.ModeId,
            [CoreEvents.Fields.EncounterId] = existing.Id,
            [CoreEvents.Fields.ParticipantIds] = existing.Participants.Select(p => p.CharacterId).ToList()
        });
        return ChangeHandlerResult.Ok;
    }

    // ExtractInvolvedEntities: default interface implementation is sufficient — LocationId/ParticipantIds
    // already match the reflection-based Id/Ids naming convention in WorldChangeHandlerHelpers.
}
