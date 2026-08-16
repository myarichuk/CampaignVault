using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace CampaignVault.Tests;

/// <summary>
/// Helper for test code to create ChangeContext instances with mock callbacks.
/// Automatically provides a mock session if none is supplied, allowing tests
/// that don't require real database functionality to use this helper.
/// </summary>
internal static class ChangeContextTestHelper
{
    /// <summary>
    /// Create a ChangeContext with optional real or auto-mocked session.
    /// If session is null, a NSubstitute mock is created. This is suitable for
    /// tests that don't make database queries (most unit tests of business logic).
    /// For tests that need real database queries, pass an IAsyncDocumentSession from RavenDBFixture.
    /// </summary>
    public static ChangeContext Create(
        IAsyncDocumentSession? session = null,
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
        // Create a mock session if not provided. Most tests don't actually use the session,
        // so a simple no-op mock is sufficient. Tests that need real queries should pass
        // a session from RavenDBFixture.
        session ??= Substitute.For<IAsyncDocumentSession>();

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
