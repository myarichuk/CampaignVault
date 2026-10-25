using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>start_campaign_onboarding back=true: the previous question again, its answer withdrawn and returned.</summary>
[Collection("RavenDB")]
public class OnboardingBackTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    [Fact]
    public async Task Back_ReopensThePreviousQuestion_WithItsAnswer_AndStopsAtTheCampaignName()
    {
        var repo = fixture.CreateRepository();
        var onboarding = TestCampaignToolsFactory.CreateTool<OnboardingTools>(fixture, repo);
        var slug = "onboard-back-" + Guid.NewGuid().ToString("N")[..8];

        var start = await onboarding.StartCampaignOnboarding(slug);
        Assert.True(start.Success, start.Summary);
        var first = start.Data!.CurrentQuestion!;
        Assert.Equal(OnboardingQuestionCatalog.CampaignName, first.Key);

        var tooEarly = await onboarding.StartCampaignOnboarding(slug, back: true);
        Assert.False(tooEarly.Success);
        Assert.Equal("OnboardingNoPrevious", tooEarly.Error);

        var named = await onboarding.SubmitOnboardingAnswer(slug, "The Mill");
        Assert.True(named.Success, named.Summary);
        var second = named.Data!.CurrentQuestion!;
        string secondAnswer = second.AnswerType == OnboardingAnswerType.Enum ? second.EnumOptions![0] : "Dnd5e";
        var answered = await onboarding.SubmitOnboardingAnswer(slug, secondAnswer);
        Assert.True(answered.Success, answered.Summary);
        var third = answered.Data!.CurrentQuestion!;
        Assert.NotEqual(second.Key, third.Key);
        int indexAfterTwo = answered.Data.State.CurrentQuestionIndex;

        var back = await onboarding.StartCampaignOnboarding(slug, back: true);
        Assert.True(back.Success, back.Summary);
        Assert.Equal(second.Key, back.Data!.CurrentQuestion!.Key);
        Assert.Equal(secondAnswer, back.Data.PriorAnswer);
        Assert.False(back.Data.State.CollectedAnswers.ContainsKey(second.Key));
        Assert.True(back.Data.State.CollectedAnswers.ContainsKey(OnboardingQuestionCatalog.CampaignName), "the campaign name stays");
        Assert.Equal(indexAfterTwo - 1, back.Data.State.CurrentQuestionIndex);

        var resumed = await onboarding.StartCampaignOnboarding(slug);
        Assert.Equal(second.Key, resumed.Data!.CurrentQuestion!.Key);

        var again = await onboarding.SubmitOnboardingAnswer(slug, secondAnswer);
        Assert.True(again.Success, again.Summary);
        Assert.Equal(third.Key, again.Data!.CurrentQuestion!.Key);

        var stop = await onboarding.StartCampaignOnboarding(slug, back: true);
        Assert.True(stop.Success);
        var atName = await onboarding.StartCampaignOnboarding(slug, back: true);
        Assert.False(atName.Success, "the campaign name is never revisited");
        Assert.Equal("OnboardingNoPrevious", atName.Error);
    }
}
