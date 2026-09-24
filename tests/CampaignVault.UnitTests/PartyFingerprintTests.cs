using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class PartyFingerprintTests
{
    [Fact]
    public void SameLocations_IgnoresHpDifferences()
    {
        Assert.True(PartyFingerprint.SameLocations("chars/a:10/20@locations/x,chars/b:5/5@locations/x",
            "chars/a:20/20@locations/x,chars/b:5/5@locations/x"));
    }

    [Fact]
    public void SameLocations_DetectsMovedMember()
    {
        Assert.False(PartyFingerprint.SameLocations("chars/a:10/20@locations/x", "chars/a:10/20@locations/y"));
    }
}
