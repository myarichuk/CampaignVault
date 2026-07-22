using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Integration tests for Campaign.CommitsSinceTimeRecorded — the counter TimeStalenessPressureContributor
/// reads to nudge the DM-LLM once too many commits pass with no recorded time (no day-boundary crossing,
/// no MinutesElapsed on any change).
/// </summary>
[Collection("RavenDB")]
public class TimeStalenessTrackingTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private readonly CampaignDocumentKeys _keys = new();

    public TimeStalenessTrackingTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Campaign> LoadCampaignAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string campaign)
        => (await session.LoadAsync<Campaign>(_keys.Meta(campaign)))!;

    [Fact]
    public async Task StageChangesAsync_NoTimeRecorded_IncrementsCounterEachCommit()
    {
        var repo = _fixture.CreateRepository();
        const string campaign = "staleness-increments";
        await TestCampaignDefaults.EnsureExistsAsync(_fixture, campaign);

        using var session = _fixture.Store.OpenAsyncSession();
        const string charId = "chars/staleness-1";
        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest { Id = charId, Name = "Test", KeepAlive = true, MaxHp = 10 }, campaign);
        await session.SaveChangesAsync();

        await repo.StageChangesAsync(session, [new HpChange { CharacterId = charId, Delta = -1 }], campaign);
        await session.SaveChangesAsync();
        Assert.Equal(1, (await LoadCampaignAsync(session, campaign)).CommitsSinceTimeRecorded);

        await repo.StageChangesAsync(session, [new HpChange { CharacterId = charId, Delta = -1 }], campaign);
        await session.SaveChangesAsync();
        Assert.Equal(2, (await LoadCampaignAsync(session, campaign)).CommitsSinceTimeRecorded);
    }

    [Fact]
    public async Task StageChangesAsync_MinutesElapsedRecorded_ResetsCounter()
    {
        var repo = _fixture.CreateRepository();
        const string campaign = "staleness-reset-minutes";
        await TestCampaignDefaults.EnsureExistsAsync(_fixture, campaign);

        using var session = _fixture.Store.OpenAsyncSession();
        const string charId = "chars/staleness-2";
        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest { Id = charId, Name = "Test", KeepAlive = true, MaxHp = 10 }, campaign);
        await session.SaveChangesAsync();

        await repo.StageChangesAsync(session, [new HpChange { CharacterId = charId, Delta = -1 }], campaign);
        await repo.StageChangesAsync(session, [new HpChange { CharacterId = charId, Delta = -1 }], campaign);
        await session.SaveChangesAsync();
        Assert.Equal(2, (await LoadCampaignAsync(session, campaign)).CommitsSinceTimeRecorded);

        await repo.StageChangesAsync(session, [new HpChange { CharacterId = charId, Delta = -1, MinutesElapsed = 20 }], campaign);
        await session.SaveChangesAsync();
        Assert.Equal(0, (await LoadCampaignAsync(session, campaign)).CommitsSinceTimeRecorded);
    }

    [Fact]
    public async Task AdvanceWorldAsync_ResetsCounter_EvenAtZeroDays()
    {
        var repo = _fixture.CreateRepository();
        const string campaign = "staleness-reset-advance";
        await TestCampaignDefaults.EnsureExistsAsync(_fixture, campaign);

        using var session = _fixture.Store.OpenAsyncSession();
        const string charId = "chars/staleness-3";
        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest { Id = charId, Name = "Test", KeepAlive = true, MaxHp = 10 }, campaign);
        await session.SaveChangesAsync();

        await repo.StageChangesAsync(session, [new HpChange { CharacterId = charId, Delta = -1 }], campaign);
        await session.SaveChangesAsync();
        Assert.Equal(1, (await LoadCampaignAsync(session, campaign)).CommitsSinceTimeRecorded);

        // days=0 is the explicit "run the sweep now" pattern (see RestThenAdvanceWorldIntegrationTests) —
        // still counts as time recorded even though the calendar itself doesn't move.
        await repo.AdvanceWorldAsync(session, 0, TimeOfDay.Noon, campaign);
        await session.SaveChangesAsync();
        Assert.Equal(0, (await LoadCampaignAsync(session, campaign)).CommitsSinceTimeRecorded);
    }
}
