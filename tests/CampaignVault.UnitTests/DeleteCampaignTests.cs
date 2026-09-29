using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Tools;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class DeleteCampaignTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public DeleteCampaignTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private CampaignManagementTools CreateTools() =>
        TestCampaignToolsFactory.CreateTool<CampaignManagementTools>(_fixture);

    [Fact]
    public async Task Delete_RequiresExactSlugConfirmation()
    {
        var tools = CreateTools();
        var created = await tools.CreateCampaign("delete-me-probe", "dnd5e");
        Assert.True(created.Success);

        var missing = await tools.DeleteCampaign("delete-me-probe");
        Assert.False(missing.Success);
        Assert.Equal("InvalidArgument", missing.Error);

        var wrong = await tools.DeleteCampaign("delete-me-probe", "something-else");
        Assert.False(wrong.Success);
        Assert.Equal("InvalidArgument", wrong.Error);

        var listed = await tools.ListCampaigns();
        Assert.True(listed.Success);
        Assert.Contains(listed.Data!, c => c.Name == "delete-me-probe");
    }

    [Fact]
    public async Task Delete_RemovesCampaignEntirely()
    {
        var tools = CreateTools();
        var created = await tools.CreateCampaign("delete-me-gone", "dnd5e");
        Assert.True(created.Success);

        var deleted = await tools.DeleteCampaign("delete-me-gone", "delete-me-gone");
        Assert.True(deleted.Success);
        Assert.Equal("delete-me-gone", deleted.Data!.Slug);
        Assert.True(deleted.Data.DeletedDocuments >= 1);

        // Deterministic check first: a point read is never stale.
        var probeRepo = _fixture.CreateRepository();
        using (var probeSession = probeRepo.OpenSession())
        {
            var meta = await probeSession.LoadAsync<CampaignVault.Models.Campaign>(
                "campaigns/delete-me-gone/meta");
            Assert.Null(meta);
        }

        // list_campaigns allows stale reads: poll until the deletion is visible.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var listed = await tools.ListCampaigns();
            Assert.True(listed.Success);
            if (listed.Data!.All(c => c.Name != "delete-me-gone"))
            {
                break;
            }
            Assert.True(DateTime.UtcNow < deadline, "deleted campaign still listed after 10s");
            await Task.Delay(100);
        }

        var again = await tools.DeleteCampaign("delete-me-gone", "delete-me-gone");
        Assert.False(again.Success);
        Assert.Equal("SlugNotFound", again.Error);
    }
}
