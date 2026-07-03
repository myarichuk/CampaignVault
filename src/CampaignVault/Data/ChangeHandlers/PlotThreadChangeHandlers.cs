using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class PlotThreadCreateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is PlotThreadCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var ptc = (PlotThreadCreate)change;

        if (string.IsNullOrWhiteSpace(ptc.PlotThreadId) || string.IsNullOrWhiteSpace(ptc.Title))
            return ChangeHandlerResult.Failure("PlotThreadId and Title are required.");

        var existing = await context.Session.LoadAsync<PlotThread>(ptc.PlotThreadId, ct);
        if (existing != null)
            return ChangeHandlerResult.Failure($"PlotThread '{ptc.PlotThreadId}' already exists. Use plot_thread_progress to update it.");

        var time = await context.GetCurrentTimeAsync();

        var thread = new PlotThread
        {
            Id = ptc.PlotThreadId,
            Title = ptc.Title,
            Summary = ptc.Summary,
            State = ptc.State,
            TensionLevel = Math.Clamp(ptc.TensionLevel, 0, 100),
            ResolutionCondition = ptc.ResolutionCondition,
            ForeshadowingHooks = ptc.ForeshadowingHooks ?? [],
            InvolvedEntityIds = ptc.InvolvedEntityIds ?? [],
            DmNotes = ptc.DmNotes,
            DeadlineDay = ptc.DeadlineDay,
            IsPlayerVisible = ptc.IsPlayerVisible,
            CampaignName = context.CampaignName,
            DayCreated = time.TotalDaysElapsed,
            LastUpdatedDay = time.TotalDaysElapsed,
            Clues = ptc.Clues?.Select(c => new PlotClue(c.Id, c.Description, InvolvedEntityIds: c.InvolvedEntityIds)).ToList() ?? []
        };

        await context.Session.StoreAsync(thread, ct);
        context.RecordMessage($"Created plot thread: '{ptc.Title}' (state: {ptc.State}, tension: {thread.TensionLevel}).");

        return ChangeHandlerResult.Ok;
    }

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not PlotThreadCreate ptc) return false;
        foreach (var id in ptc.InvolvedEntityIds)
        {
            allInvolvedIds?.Add(id);
            if (id.StartsWith("characters/") || id.StartsWith("chars/")) characterIds?.Add(id);
            else if (id.StartsWith("locations/")) locationIds?.Add(id);
            else if (id.StartsWith("factions/")) factionIds?.Add(id);
        }
        return true;
    }
}

public class PlotThreadProgressHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is PlotThreadProgress;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var ptp = (PlotThreadProgress)change;

        if (string.IsNullOrWhiteSpace(ptp.PlotThreadId))
            return ChangeHandlerResult.Failure("PlotThreadId is required.");

        var thread = await context.Session.LoadAsync<PlotThread>(ptp.PlotThreadId, ct);
        if (thread == null)
            return ChangeHandlerResult.Failure($"PlotThread '{ptp.PlotThreadId}' not found.");

        var prevState = thread.State;

        if (ptp.NewState.HasValue)
            thread.State = ptp.NewState.Value;

        if (ptp.TensionDelta.HasValue)
            thread.TensionLevel = Math.Clamp(thread.TensionLevel + ptp.TensionDelta.Value, 0, 100);

        if (ptp.ResolutionCondition != null)
            thread.ResolutionCondition = ptp.ResolutionCondition;

        if (!string.IsNullOrWhiteSpace(ptp.AddForeshadowingHook))
            thread.ForeshadowingHooks.Add(ptp.AddForeshadowingHook);

        if (!string.IsNullOrWhiteSpace(ptp.AddInvolvedEntityId) && !thread.InvolvedEntityIds.Contains(ptp.AddInvolvedEntityId))
            thread.InvolvedEntityIds.Add(ptp.AddInvolvedEntityId);

        if (!string.IsNullOrWhiteSpace(ptp.RemoveInvolvedEntityId))
            thread.InvolvedEntityIds.Remove(ptp.RemoveInvolvedEntityId);

        if (ptp.AddClue != null)
        {
            if (!thread.Clues.Any(c => c.Id == ptp.AddClue.Id))
                thread.Clues.Add(new PlotClue(ptp.AddClue.Id, ptp.AddClue.Description, InvolvedEntityIds: ptp.AddClue.InvolvedEntityIds));
        }

        if (!string.IsNullOrWhiteSpace(ptp.NarrativeNote))
            thread.DmNotes = string.IsNullOrWhiteSpace(thread.DmNotes)
                ? ptp.NarrativeNote
                : thread.DmNotes + "\n" + ptp.NarrativeNote;

        // Stamp ClimaxEnteredDay when transitioning to Climax (only once)
        if (ptp.NewState == PlotThreadState.Climax && prevState != PlotThreadState.Climax && !thread.ClimaxEnteredDay.HasValue)
        {
            var time = await context.GetCurrentTimeAsync();
            thread.ClimaxEnteredDay = time.TotalDaysElapsed;
        }

        // Only update LastUpdatedDay for non-engine-authored changes (7-iii: staleness metric reflects agent engagement, not engine auto-progress)
        if (!ptp.IsEngineAuthored)
        {
            var time = await context.GetCurrentTimeAsync();
            thread.LastUpdatedDay = time.TotalDaysElapsed;
        }

        context.RecordMessage($"Updated plot thread '{thread.Title}': state={thread.State}, tension={thread.TensionLevel}.");
        return ChangeHandlerResult.Ok;
    }

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not PlotThreadProgress ptp) return false;
        if (!string.IsNullOrEmpty(ptp.PlotThreadId)) allInvolvedIds?.Add(ptp.PlotThreadId);
        return true;
    }
}

public class PlotThreadClueDiscoveredHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is PlotThreadClueDiscovered;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var ptcd = (PlotThreadClueDiscovered)change;

        if (string.IsNullOrWhiteSpace(ptcd.PlotThreadId) || string.IsNullOrWhiteSpace(ptcd.ClueId))
            return ChangeHandlerResult.Failure("PlotThreadId and ClueId are required.");

        var thread = await context.Session.LoadAsync<PlotThread>(ptcd.PlotThreadId, ct);
        if (thread == null)
            return ChangeHandlerResult.Failure($"PlotThread '{ptcd.PlotThreadId}' not found.");

        var clueIndex = thread.Clues.FindIndex(c => string.Equals(c.Id, ptcd.ClueId, StringComparison.OrdinalIgnoreCase));
        if (clueIndex < 0)
            return ChangeHandlerResult.Failure($"Clue '{ptcd.ClueId}' not found in plot thread '{ptcd.PlotThreadId}'.");

        var time = await context.GetCurrentTimeAsync();
        var clue = thread.Clues[clueIndex];
        thread.Clues[clueIndex] = clue with { IsDiscovered = true, DiscoveredOnDay = time.TotalDaysElapsed };
        thread.LastUpdatedDay = time.TotalDaysElapsed;

        var discoveredCount = thread.Clues.Count(c => c.IsDiscovered);
        context.RecordMessage($"Clue '{ptcd.ClueId}' discovered in plot thread '{thread.Title}' ({discoveredCount}/{thread.Clues.Count} clues found). {ptcd.NarrativeNote}");

        // Auto-emit event via mutation dispatch so the party history reflects the discovery
        if (!string.IsNullOrWhiteSpace(ptcd.NarrativeNote))
        {
            await context.Dispatcher.DispatchMutationAsync(context, new EventOccurred
            {
                Category = EventCategory.Discovery,
                Summary = $"Clue discovered for '{thread.Title}': {ptcd.NarrativeNote}",
                Involved = ptcd.DiscoveredByCharacterIds,
                RelatedEntityId = ptcd.PlotThreadId
            }, ct);
        }

        return ChangeHandlerResult.Ok;
    }

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not PlotThreadClueDiscovered ptcd) return false;
        allInvolvedIds?.Add(ptcd.PlotThreadId);
        foreach (var id in ptcd.DiscoveredByCharacterIds)
        {
            characterIds?.Add(id);
            allInvolvedIds?.Add(id);
        }
        return true;
    }
}
