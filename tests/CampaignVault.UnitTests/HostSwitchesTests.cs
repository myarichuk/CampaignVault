using CampaignVault.Hosting;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>N5: the startup switches the Unity client's embedded server relies on.</summary>
public class HostSwitchesTests
{
    [Theory]
    [InlineData(null, true, true)]
    [InlineData("", true, true)]
    [InlineData("  ", false, false)]
    [InlineData("1", false, true)]
    [InlineData("true", false, true)]
    [InlineData("0", true, false)]
    [InlineData(" FALSE ", true, false)]
    [InlineData("maybe", true, true)]
    public void Resolve_ExplicitValueWins_ElseFallback(string? value, bool fallback, bool expected)
    {
        Assert.Equal(expected, HostSwitches.Resolve(value, fallback));
    }

    [Fact]
    public void RavenLicensing_NoneSet_IsNull()
    {
        Assert.Null(HostSwitches.RavenLicensing(null, " "));
        Assert.StartsWith("none", HostSwitches.DescribeLicensing(null));
    }

    [Fact]
    public void RavenLicensing_InlineWinsOverPath_AndAcceptsEula()
    {
        var licensing = HostSwitches.RavenLicensing(" {\"Id\":\"x\"} ", "/tmp/license.json");
        Assert.NotNull(licensing);
        Assert.Equal("{\"Id\":\"x\"}", licensing!.License);
        Assert.Null(licensing.LicensePath);
        Assert.True(licensing.EulaAccepted);
        Assert.DoesNotContain("Id", HostSwitches.DescribeLicensing(licensing));
    }

    [Fact]
    public void RavenLicensing_PathOnly()
    {
        var licensing = HostSwitches.RavenLicensing(null, "/data/raven-license.json");
        Assert.Equal("/data/raven-license.json", licensing!.LicensePath);
        Assert.Null(licensing.License);
        Assert.Contains("/data/raven-license.json", HostSwitches.DescribeLicensing(licensing));
    }
}
