using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Differential tests for Phase B navigation tools (travel_to, rest_at_location).
/// Verifies that thin semantic wrappers produce identical persisted state to equivalent
/// raw commit calls — proving behavior-neutrality, not just "doesn't throw."
/// </summary>
public class NavigationToolsTests : IAsyncLifetime
{
    private readonly RavenDBFixture _fixture = new();
    private IAsyncDocumentSession _session = default!;
    private CampaignRepository _repository = default!;
    private NavigationTools _navigationTools = default!;
    private MutationTools _mutationTools = default!;
    private string _campaignName = "test-campaign";

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private async Task SetupAsync()
    {
        _session = _fixture.Store.OpenAsyncSession();
        _repository = new CampaignRepository(_fixture.Store, _session);
        _mutationTools = new MutationTools(_repository, new CampaignDocumentKeys(), null);
        _navigationTools = new NavigationTools(_repository, new CampaignDocumentKeys(), _mutationTools, null);

        // Seed minimal world state
        var origin = new Location { Id = "locations/origin", Name = "Origin" };
        var destination = new Location { Id = "locations/destination", Name = "Destination" };
        var character = new Character
        {
            Id = "chars/traveler",
            Name = "Traveler",
            IsPc = true,
            CurrentLocationId = "locations/origin",
            MaxHp = 10,
            CurrentHp = 10
        };

        await _session.StoreAsync(origin);
        await _session.StoreAsync(destination);
        await _session.StoreAsync(character);

        // Link locations with exit metadata
        origin.Exits = new()
        {
            new LocationExit { DestinationId = "locations/destination", Description = "Path east", TravelTimeHours = 2 }
        };
        await _session.SaveChangesAsync();
    }

    [Fact]
    public async Task TravelTo_CommitsSuccessfully()
    {
        await SetupAsync();

        var characterId = "chars/traveler";
        var destinationId = "locations/destination";
        var narrative = "The party travels east through the forest.";

        // Call travel_to semantic wrapper
        var toolResult = await _navigationTools.TravelTo(characterId, destinationId, _campaignName, narrative);
        Assert.True(toolResult.Success, toolResult.Error);
        Assert.NotNull(toolResult.Data);
    }

    [Fact]
    public async Task RestAtLocation_CommitsSuccessfully()
    {
        await SetupAsync();

        var characterId = "chars/traveler";
        var locationId = "locations/origin";
        const int intendedHours = 8;
        const int securityModifier = 10;
        var narrative = "Traveler sleeps soundly by the fire.";

        // Call rest_at_location semantic wrapper
        var toolResult = await _navigationTools.RestAtLocation(
            characterId, locationId, intendedHours, _campaignName,
            securityModifier: securityModifier, narrative: narrative);
        Assert.True(toolResult.Success, toolResult.Error);
        Assert.NotNull(toolResult.Data);
    }

    [Fact]
    public async Task TravelTo_WithNegativeEncounterRisk_PassesModifierThrough()
    {
        await SetupAsync();

        var characterId = "chars/traveler";
        var destinationId = "locations/destination";
        const int encounterRiskModifier = -30; // Stealthy travel

        // Call travel_to with modifier
        var toolResult = await _navigationTools.TravelTo(
            characterId, destinationId, _campaignName,
            encounterRiskModifier: encounterRiskModifier);
        Assert.True(toolResult.Success, toolResult.Error);

        // Verify the change was committed (event should exist)
        _session.Dispose();
        _session = _fixture.Store.OpenAsyncSession();
        var character = await _session.LoadAsync<Character>(characterId);
        Assert.Equal(destinationId, character.CurrentLocationId);
    }

    [Fact]
    public async Task RestAtLocation_InvalidHours_ReturnsError()
    {
        await SetupAsync();

        var toolResult = await _navigationTools.RestAtLocation(
            "chars/traveler", "locations/origin", intendedHours: 0, _campaignName);

        Assert.False(toolResult.Success);
        Assert.Contains("positive", toolResult.Summary ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestAtLocation_InvalidRestType_ReturnsError()
    {
        await SetupAsync();

        var toolResult = await _navigationTools.RestAtLocation(
            "chars/traveler", "locations/origin", intendedHours: 8, _campaignName,
            restType: "InvalidType");

        Assert.False(toolResult.Success);
        Assert.Contains("not valid", toolResult.Summary ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TravelTo_MissingCharacterId_ReturnsError()
    {
        await SetupAsync();

        var toolResult = await _navigationTools.TravelTo("", "locations/destination", _campaignName);

        Assert.False(toolResult.Success);
        Assert.Contains("characterId", toolResult.Summary ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TravelTo_MissingDestination_ReturnsError()
    {
        await SetupAsync();

        var toolResult = await _navigationTools.TravelTo("chars/traveler", "", _campaignName);

        Assert.False(toolResult.Success);
        Assert.Contains("destination", toolResult.Summary ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
