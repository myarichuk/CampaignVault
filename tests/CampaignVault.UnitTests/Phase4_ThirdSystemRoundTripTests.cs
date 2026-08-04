using System;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Data;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Phase 4 acceptance criterion: validates that campaigns on fabricated third-system IDs
/// backed only by YAML data round-trip without being coerced to Dnd5e.
///
/// These tests are RED until Phase 4.3 (runtime type resolver) and 4.5 (coercion removal) land.
/// Once both are complete, a campaign with system="swade" should survive:
/// - Creation with explicit system id
/// - Load/re-save without mutation
/// - Character bootstrap without type mutation
/// </summary>
[Collection("RavenDB")]
public class Phase4_ThirdSystemRoundTripTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public Phase4_ThirdSystemRoundTripTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ThirdSystem_Campaign_CreatesWithoutCoercion()
    {
        const string campaignName = "test-swade-campaign";
        const string swadeSystemId = "swade";
        var keys = new CampaignDocumentKeys();

        // Store in one session
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var campaign = new Campaign
            {
                Id = keys.Meta(campaignName),
                Name = campaignName,
                DisplayName = "Test SWADE Campaign",
                System = swadeSystemId,
                IsSystemLocked = true,
                CreatedAt = DateTime.UtcNow
            };

            await session.StoreAsync(campaign);
            await session.SaveChangesAsync();
        }

        // Reload in a new session (forces deserialization from DB)
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var reloaded = await session.LoadAsync<Campaign>(keys.Meta(campaignName));

            // Assert: system should remain "swade", not coerced to "dnd5e"
            Assert.NotNull(reloaded);
            Assert.Equal(swadeSystemId, reloaded.System);
        }
    }

    [Fact]
    public async Task ThirdSystem_CampaignConfig_PersistsWithoutCoercion()
    {
        const string campaignName = "test-swade-config";
        const string swadeSystemId = "swade";
        var keys = new CampaignDocumentKeys();

        // Store in one session
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var config = new CampaignConfig
            {
                Id = keys.Config(campaignName),
                ActiveSystem = swadeSystemId,
                SystemOptions = []
            };

            await session.StoreAsync(config);
            await session.SaveChangesAsync();
        }

        // Reload in a new session (forces deserialization from DB)
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var reloaded = await session.LoadAsync<CampaignConfig>(keys.Config(campaignName));

            // Assert: system should remain "swade"
            Assert.NotNull(reloaded);
            Assert.Equal(swadeSystemId, reloaded.ActiveSystem);
        }
    }

    [Fact]
    public async Task ThirdSystem_Character_SystemStatsDeserializesWithCorrectType()
    {
        const string characterId = "chars/test-swade-char";

        // Store a character with base SystemExtension in one session
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var character = new Character
            {
                Id = characterId,
                Name = "Swade Character",
                ClassLevel = "Gunslinger 5",
                SystemStats = new SystemExtension
                {
                    Willpower = 80f,
                    Morale = 70f
                }
            };

            await session.StoreAsync(character);
            await session.SaveChangesAsync();
        }

        // Reload in a new session (forces polymorphic deserialization)
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var reloaded = await session.LoadAsync<Character>(characterId);

            // Assert: SystemStats should maintain its type (SystemExtension for unknown systems)
            // and preserve the custom values — should not be coerced to Dnd5eExtension
            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded.SystemStats);
            Assert.IsType<SystemExtension>(reloaded.SystemStats);
            Assert.False(reloaded.SystemStats is Dnd5eExtension);
            Assert.False(reloaded.SystemStats is Pf2eExtension);
            Assert.Equal(80f, reloaded.SystemStats.Willpower);
            Assert.Equal(70f, reloaded.SystemStats.Morale);
        }
    }
}
