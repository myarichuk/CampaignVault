using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class ClimateCycleTests
{
    [Fact]
    public void GetTemperatureCelsius_DesertSwingsMoreThanTemperate()
    {
        var desertSwing = ClimateCycle.GetTemperatureCelsius(ClimateZone.Desert, TimeOfDay.Noon)
                           - ClimateCycle.GetTemperatureCelsius(ClimateZone.Desert, TimeOfDay.Night);
        var temperateSwing = ClimateCycle.GetTemperatureCelsius(ClimateZone.Temperate, TimeOfDay.Noon)
                              - ClimateCycle.GetTemperatureCelsius(ClimateZone.Temperate, TimeOfDay.Night);

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
        var noon = ClimateCycle.GetTemperatureCelsius(zone, TimeOfDay.Noon);
        var night = ClimateCycle.GetTemperatureCelsius(zone, TimeOfDay.Night);

        Assert.True(night < noon);
    }

    [Fact]
    public void GetTemperatureCelsius_SubterraneanNearlyFlatAcrossDayNight()
    {
        var noon = ClimateCycle.GetTemperatureCelsius(ClimateZone.Subterranean, TimeOfDay.Noon);
        var night = ClimateCycle.GetTemperatureCelsius(ClimateZone.Subterranean, TimeOfDay.Night);

        Assert.True(noon - night < 3f);
    }

    [Fact]
    public void GetTemperatureCelsius_ArcticColderThanTropicalAtSameTime()
    {
        var arctic = ClimateCycle.GetTemperatureCelsius(ClimateZone.Arctic, TimeOfDay.Noon);
        var tropical = ClimateCycle.GetTemperatureCelsius(ClimateZone.Tropical, TimeOfDay.Noon);

        Assert.True(arctic < tropical);
    }
}
