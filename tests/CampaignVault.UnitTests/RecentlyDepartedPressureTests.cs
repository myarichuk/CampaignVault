using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class RecentlyDepartedPressureTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public RecentlyDepartedPressureTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RecentlyDepartedPressureContributor_EmitsSuggestedCommit()
    {
        var locId = "locations/tavern-test";
        var scene = new SceneView
        {
            IsLocationAnchored = true,
            Location = LocationDetailView.From(new Location
            {
                Id = locId,
                Name = "Rusty Nail",
                RecentlyDeparted =
                [
                    new DepartedNpcRecord("chars/mira", "Mira the Bard", 3, "transient eviction")
                ]
            })
        };

        var pressures = await new RecentlyDepartedPressureContributor().EvaluateAsync(new PressureContext(
            "test",
            new CampaignTime { TotalDaysElapsed = 5 },
            new CampaignConfig(),
            null!,
            Scene: scene));

        var pressure = Assert.Single(pressures);
        Assert.Equal(PressureSeverity.NarrativePrompt, pressure.Severity);
        Assert.Contains("Mira the Bard", pressure.Text);
        Assert.Contains("world_build", pressure.Text);
        Assert.Contains("chars/mira", pressure.Text);
        Assert.Contains("keepAlive", pressure.Text);
    }

    [Fact]
    public async Task RecentlyDeparted_SurfacesPressure_OnGetScene()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var campaign = "recently-departed-pressure-" + System.Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, campaign);

        const string locId = "locations/rusty-nail";
        const string charId = "chars/transient-bard";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Location
            {
                Id = locId,
                Name = "Rusty Nail",
                Type = LocationType.Room,
                CampaignName = campaign,
                RecentlyDeparted =
                [
                    new DepartedNpcRecord(charId, "Mira the Bard", 2, "transient eviction")
                ]
            });
            await session.SaveChangesAsync();
        }

        var result = await tools.GetScene(locId, campaignName: campaign, partyPresent: true);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);

        var pressureItems = result.Data!.WorldPressureItems ?? [];
        var departedPressure = pressureItems.FirstOrDefault(p =>
            p.GroupingKey == RecentlyDepartedPressureContributor.RecentlyDepartedGroupingKey);
        Assert.NotNull(departedPressure);
        Assert.Contains(charId, departedPressure!.Text);
    }
}