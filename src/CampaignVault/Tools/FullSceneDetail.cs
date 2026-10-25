using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Tools;

/// <summary>
/// The "room you are in" scene with everything a DM needs to narrate it: full description, the NPCs present
/// (PCs stripped, their state travels with the party), scene pressure and the plot threads that reference the
/// place. Shared by take_turn's <c>fullDetailLocationId</c> and start_session's opening scene, so the two never
/// drift and a session can open in one call.
/// </summary>
internal static class FullSceneDetail
{
    public static async Task<SceneView?> BuildAsync(
        CampaignRepository repository,
        IPressureOrchestrator pressureOrchestrator,
        IAsyncDocumentSession session,
        string campaign,
        string locationId,
        CampaignConfig? config = null)
    {
        var campaignSession = new CampaignSession(session, campaign);
        // Full detail here means full — bypass the Description/PointOfInterestDetails caps that
        // apply everywhere else, since this call is already bounded to one location.
        var scene = await repository.GetSceneAsync(campaignSession, locationId, markVisited: false, fullDescription: true);
        if (scene == null)
        {
            return null;
        }

        // Parity with get_entity's scene view, so arriving via travel + fullDetailLocationId doesn't need a
        // follow-up get_entity for location ENGINE WARNINGs or plot threads. Runs before the PC strip below:
        // scene contributors (e.g. location integrity) need PCs in the roster, same as get_entity.
        var time = await repository.GetTimeAsync(campaignSession);
        config ??= await repository.GetCampaignConfigAsync(campaignSession);
        var scenePressure = await pressureOrchestrator.CollectAndCapAsync(PressureScope.Scene, new PressureContext(
            campaign, time, config, session, Scene: scene, RequestedLocationId: locationId, PartyPresent: true));
        if (scenePressure.Count > 0)
        {
            scene.ScenePressure = [.. PressureManager.ToDisplayStrings(scenePressure)];
        }

        // PCs ride along internally (recognition hints / faction-reputation lookups need them), but they're
        // not NPCs and their state already travels via Party/PartyDelta (or start_session's party[]).
        scene.PresentNPCs = [.. scene.PresentNPCs.Where(n => !n.IsPc)];
        var threads = await repository.GetPlotThreadsReferencingEntityAsync(session, locationId, campaign);
        scene.AssociatedPlotThreads =
        [
            .. threads.Select(t => new PlotThreadMinimal(t.Id, t.Title, t.State, t.TensionLevel))
        ];
        return scene;
    }
}
