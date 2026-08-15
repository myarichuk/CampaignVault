using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;

namespace CampaignVault.Tests;

/// <summary>
/// Helper for test code to create ChangeContext instances with mock callbacks.
/// </summary>
internal static class ChangeContextTestHelper
{
    public static ChangeContext Create(
        IAsyncDocumentSession session,
        Dictionary<string, Character>? characters = null,
        Dictionary<string, Item>? items = null,
        Dictionary<string, Location>? locations = null,
        Dictionary<string, Faction>? factions = null,
        Dictionary<string, Quest>? quests = null,
        ILogger? logger = null,
        List<string>? summary = null,
        WorldChangeDispatcher? dispatcher = null,
        CombatEncounter? activeCombat = null,
        string? campaignName = null,
        CampaignConfig? config = null)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        dispatcher ??= new WorldChangeDispatcher([], new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);

        return new ChangeContext(
            session,
            characters ?? new Dictionary<string, Character>(),
            items ?? new Dictionary<string, Item>(),
            locations ?? new Dictionary<string, Location>(),
            factions ?? new Dictionary<string, Faction>(),
            quests ?? new Dictionary<string, Quest>(),
            logger ?? NullLogger.Instance,
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask,
            summary ?? new List<string>(),
            dispatcher,
            activeCombat,
            campaignName,
            config);
    }
}
