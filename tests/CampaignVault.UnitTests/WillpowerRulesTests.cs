using CampaignVault.Models;
using CampaignVault.Rulesets;
using Xunit;

namespace CampaignVault.Tests;

public class WillpowerRulesTests
{
    private static SystemExtension Stats(float will = 75f) => new() { Willpower = will };

    [Fact]
    public void A_negative_delta_is_owed_and_rest_gives_it_back_a_step_at_a_time()
    {
        var s = Stats();
        WillpowerRules.Apply(s, -12, isDelta: true);
        Assert.Equal(63f, s.Willpower);
        Assert.Equal(12f, s.WillpowerDrained);

        Assert.Equal(5f, WillpowerRules.RestStep(s));
        Assert.Equal(5f, WillpowerRules.RestStep(s));
        Assert.Equal(2f, WillpowerRules.RestStep(s));
        Assert.Equal(0f, WillpowerRules.RestStep(s));
        Assert.Equal(75f, s.Willpower);
    }

    [Fact]
    public void A_value_the_dm_sets_is_a_new_baseline_and_rest_never_lifts_it()
    {
        var s = Stats();
        WillpowerRules.Apply(s, -12, isDelta: true);
        WillpowerRules.Apply(s, 40, isDelta: false);

        Assert.Equal(0f, s.WillpowerDrained);
        Assert.Equal(0f, WillpowerRules.RestStep(s));
        Assert.Equal(40f, s.Willpower);
    }

    [Fact]
    public void Raising_it_by_hand_pays_down_what_was_owed_so_rest_does_not_double_restore()
    {
        var s = Stats();
        WillpowerRules.Apply(s, -20, isDelta: true);
        WillpowerRules.Apply(s, 15, isDelta: true);

        Assert.Equal(5f, s.WillpowerDrained);
    }

    [Fact]
    public void Drain_never_goes_below_zero_and_reports_what_was_lost()
    {
        var s = Stats(8f);

        Assert.Equal(8f, WillpowerRules.Drain(s, 15));
        Assert.Equal(0f, s.Willpower);
        Assert.Equal(8f, s.WillpowerDrained);
    }

    [Fact]
    public void Restoring_never_passes_a_hundred()
    {
        var s = Stats(98f);
        s.WillpowerDrained = 50f;

        WillpowerRules.RestStep(s);

        Assert.Equal(100f, s.Willpower);
    }
}
