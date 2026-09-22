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

    private static async Task<ChangeHandlerResult> EnterAsync(
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

        var encounter = mode.StateMachine.CreateEncounter(mt.LocationId, mt.ParticipantIds);
        encounter.Id = encounterId;
        encounter.ModeId = mt.ModeId;
        encounter.IsActive = true;

        await ctx.Session.StoreAsync(encounter, ct);
        ctx.ActiveMode = encounter;
        context.RecordMessage($"Entered interaction mode '{mode.DisplayName}' at {mt.LocationId}.");
        return ChangeHandlerResult.Ok;
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
        if (ctx.ActiveMode != null &&
            string.Equals(ctx.ActiveMode.Id, existing.Id, StringComparison.OrdinalIgnoreCase))
        {
            ctx.ActiveMode = null;
        }
        context.RecordMessage($"Exited interaction mode '{existing.ModeId}'.");
        return ChangeHandlerResult.Ok;
    }

    // ExtractInvolvedEntities: default interface implementation is sufficient — LocationId/ParticipantIds
    // already match the reflection-based Id/Ids naming convention in WorldChangeHandlerHelpers.
}
