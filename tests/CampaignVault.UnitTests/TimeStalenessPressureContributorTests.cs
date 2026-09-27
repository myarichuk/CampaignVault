using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class TimeStalenessPressureContributorTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private readonly CampaignDocumentKeys _keys = new();

    public TimeStalenessPressureContributorTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EvaluateAsync_BelowThreshold_NoPressure()
    {
        const string campaign = "time-staleness-below";
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new Campaign
        {
            Id = _keys.Meta(campaign),
            Name = campaign,
            DisplayName = campaign,
            CommitsSinceTimeRecorded = 5
        }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var contributor = new TimeStalenessPressureContributor(_keys);
        var ctx = new PressureContext(campaign, new CampaignTime(), new CampaignConfig(), session);

        var pressures = (await contributor.EvaluateAsync(ctx, TestContext.Current.CancellationToken)).ToList();

        Assert.Empty(pressures);
    }

    [Fact]
    public async Task EvaluateAsync_AtOrPastThreshold_SurfacesNudge()
    {
        const string campaign = "time-staleness-past";
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new Campaign
        {
            Id = _keys.Meta(campaign),
            Name = campaign,
            DisplayName = campaign,
            CommitsSinceTimeRecorded = 15
        }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var contributor = new TimeStalenessPressureContributor(_keys);
        var ctx = new PressureContext(campaign, new CampaignTime(), new CampaignConfig(), session);

        var pressures = (await contributor.EvaluateAsync(ctx, TestContext.Current.CancellationToken)).ToList();

        var pressure = Assert.Single(pressures);
        Assert.Equal(TimeStalenessPressureContributor.GroupingKey, pressure.GroupingKey);
        Assert.Contains("15 commits", pressure.Text);
    }

    [Fact]
    public async Task EvaluateAsync_RespectsConfiguredThreshold()
    {
        const string campaign = "time-staleness-custom-threshold";
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new Campaign
        {
            Id = _keys.Meta(campaign),
            Name = campaign,
            DisplayName = campaign,
            CommitsSinceTimeRecorded = 3
        }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var contributor = new TimeStalenessPressureContributor(_keys);
        var ctx = new PressureContext(campaign, new CampaignTime(), new CampaignConfig { TimeStalenessNudgeThreshold = 3 }, session);

        var pressures = (await contributor.EvaluateAsync(ctx, TestContext.Current.CancellationToken)).ToList();

        Assert.Single(pressures);
    }
}
