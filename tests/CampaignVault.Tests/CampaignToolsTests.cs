using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using CampaignVault.Rulesets;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class CampaignToolsTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public CampaignToolsTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private CampaignTools CreateTools()
    {
        var repo = new CampaignRepository(_fixture.Store);
        var rollSvc = new DefaultRollService();
        var selector = new RulesetResolverSelector(new IRulesetResolver[] { 
            new Dnd5eRulesetResolver(rollSvc),
            new Pf2eRulesetResolver(rollSvc),
            new Fallout2d20RulesetResolver(rollSvc)
        });
        
        return new CampaignTools(
            repo,
            new DefaultBehaviorSynthesizer(),
            selector,
            new CampaignDocumentKeys(),
            new CurrentCampaignContext()
        );
    }

    [Fact]
    public async Task Commit_RejectsBatchOverLimit()
    {
        var tools = CreateTools();
        var changes = new WorldChange[51];
        
        // Fill with dummy changes
        for (int i = 0; i < changes.Length; i++)
        {
            changes[i] = new HpChange { CharacterId = "dummy", Delta = -1 };
        }

        var result = await tools.Commit(changes, "Massive batch");

        Assert.False(result.Success);
        Assert.Equal("RateLimitExceeded", result.Error);
        Assert.Contains("Maximum allowed is 50", result.Summary);
    }

    [Fact]
    public async Task Commit_RejectsWhenRateLimitExceeded()
    {
        var tools = CreateTools();
        var change = new WorldChange[] { new HpChange { CharacterId = "dummy", Delta = -1 } };
        
        int successCount = 0;
        int rejectCount = 0;

        // The limit is 20 tokens. If we slam it with 30 concurrent or rapid requests, some should fail.
        for (int i = 0; i < 30; i++)
        {
            var res = await tools.Commit(change, "Spamming the system");
            if (res.Success) successCount++;
            else if (res.Error == "RateLimitExceeded" && res.Summary!.Contains("rate limit exceeded")) rejectCount++;
        }

        Assert.True(successCount <= 20, $"Should have successfully processed at most 20 requests, but got {successCount}.");
        Assert.True(rejectCount > 0, $"Should have rejected some requests due to rate limiting, but got {rejectCount}.");
    }
}
