using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class ClimateCycleTests
{
    [Fact]
    public void GetTemperatureCelsius_DesertSwingsMoreThanTemperate()
    {
        var desertSwing = ClimateCycle.GetTemperatureCelsius(ClimateZone.Desert, 12)
                           - ClimateCycle.GetTemperatureCelsius(ClimateZone.Desert, 0);
        var temperateSwing = ClimateCycle.GetTemperatureCelsius(ClimateZone.Temperate, 12)
                              - ClimateCycle.GetTemperatureCelsius(ClimateZone.Temperate, 0);

        Assert.True(desertSwing > temperateSwing);
    }

    [Theory]
    [InlineData(ClimateZone.Arctic)]
    [InlineData(ClimateZone.Tundra)]
    [InlineData(ClimateZone.Temperate)]
    [InlineData(ClimateZone.Desert)]
    [InlineData(ClimateZone.Tropical)]
    [InlineData(ClimateZone.Alpine)]
    [InlineData(ClimateZone.Subterranean)]
    public void GetTemperatureCelsius_NightIsColderThanNoon(ClimateZone zone)
    {
        var noon = ClimateCycle.GetTemperatureCelsius(zone, 12);
        var night = ClimateCycle.GetTemperatureCelsius(zone, 0);

        Assert.True(night < noon);
    }

    [Fact]
    public void GetTemperatureCelsius_SubterraneanNearlyFlatAcrossDayNight()
    {
        var noon = ClimateCycle.GetTemperatureCelsius(ClimateZone.Subterranean, 12);
        var night = ClimateCycle.GetTemperatureCelsius(ClimateZone.Subterranean, 0);

        Assert.True(noon - night < 3f);
    }

    [Fact]
    public void GetTemperatureCelsius_ArcticColderThanTropicalAtSameTime()
    {
        var arctic = ClimateCycle.GetTemperatureCelsius(ClimateZone.Arctic, 12);
        var tropical = ClimateCycle.GetTemperatureCelsius(ClimateZone.Tropical, 12);

        Assert.True(arctic < tropical);
    }
}
