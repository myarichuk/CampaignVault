using Autofac;
using CampaignVault.AutofacModules;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class RulesetDataDiTests
{
    [Fact]
    public void Container_Resolves_CharacterCreateHandler_WithYamlBackedServices()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CampaignVaultModule>();

        using var container = builder.Build();
        using var scope = container.BeginLifetimeScope();

        var handler = scope.Resolve<CharacterCreateHandler>();
        var initializer = scope.Resolve<ResourcePoolInitializer>();
        var pools = scope.Resolve<ResourcePoolProvider>();
        var classes = scope.Resolve<ClassDefinitionProvider>();

        Assert.NotNull(handler);
        Assert.NotNull(initializer);

        var yamlPools = pools.GetPoolsForSystem(Models.RulesetSystem.Dnd5e);
        Assert.True(yamlPools.ContainsKey("spell_slots_1"));
        Assert.Equal(4, yamlPools["spell_slots_1"].DefaultMax);
        Assert.True(classes.GetClassesForSystem(Models.RulesetSystem.Dnd5e).ContainsKey("fighter"));
    }

    [Fact]
    public void Container_Scans_AllRulesetYamlProviders()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CampaignVaultModule>();

        using var container = builder.Build();

        Assert.NotNull(container.Resolve<ResourcePoolProvider>());
        Assert.NotNull(container.Resolve<ClassDefinitionProvider>());
    }

    [Fact]
    public void Container_Scans_DefaultImplementations()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CampaignVaultModule>();

        using var container = builder.Build();

        Assert.NotNull(container.Resolve<CampaignVault.Data.IRollService>());
        Assert.NotNull(container.Resolve<CampaignVault.Data.INpcBehaviorSynthesizer>());
        Assert.NotNull(container.Resolve<CampaignVault.Data.Initiative.IRelevantMemorySelector>());
    }
}