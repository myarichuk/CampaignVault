using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using CampaignVault.AutofacModules;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class RulesetDataDiTests
{
    [Fact]
    public void Container_Resolves_CharacterCreateHandler_WithYamlBackedServices()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CampaignVaultModule>();
        builder.RegisterInstance(new TestFakeEmbeddingService()).As<ILocalEmbeddingService>();

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
        builder.RegisterInstance(new TestFakeEmbeddingService()).As<ILocalEmbeddingService>();

        using var container = builder.Build();

        Assert.NotNull(container.Resolve<ResourcePoolProvider>());
        Assert.NotNull(container.Resolve<ClassDefinitionProvider>());
        Assert.NotNull(container.Resolve<ConditionDefinitionProvider>());
        Assert.NotNull(container.Resolve<RaceDefinitionProvider>());
        Assert.NotNull(container.Resolve<BackgroundDefinitionProvider>());
        Assert.NotNull(container.Resolve<FeatDefinitionProvider>());
        Assert.NotNull(container.Resolve<SpellDefinitionProvider>());
    }

    [Fact]
    public void Container_Resolves_ConditionProvider_ForStatusExpiryRule()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CampaignVaultModule>();
        builder.RegisterInstance(new TestFakeEmbeddingService()).As<ILocalEmbeddingService>();

        using var container = builder.Build();

        var provider = container.Resolve<ConditionDefinitionProvider>();
        var rule = new StatusExpiryRule(provider);

        Assert.Equal("Status Expiry Rule", rule.Name);
        Assert.True(provider.GetConditionsForSystem(Models.RulesetSystem.Dnd5e).ContainsKey("frightened"));
    }

    [Fact]
    public async Task Container_Resolves_ResourceChangeHandler_WithSpellProvider()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CampaignVaultModule>();
        builder.RegisterInstance(new TestFakeEmbeddingService()).As<ILocalEmbeddingService>();

        using var container = builder.Build();
        using var scope = container.BeginLifetimeScope();

        var handler = scope.Resolve<ResourceChangeHandler>();
        Assert.NotNull(handler);

        var character = new Character
        {
            Id = "chars/di_wizard",
            Name = "DI Wizard",
            SystemStats = new Dnd5eExtension
            {
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["spell_slots_2"] = new() { Current = 2, Max = 2 }
                }
            }
        };

        var context = new ChangeContext(
            null!,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            new WorldChangeDispatcher(
                [],
                new CampaignDocumentKeys(),
                NullLogger<WorldChangeDispatcher>.Instance));

        var result = await handler.ApplyAsync(
            new ResourceChange
            {
                CharacterId = character.Id,
                PoolName = "spell_slots_2",
                Delta = -1,
                SpellName = "fireball"
            },
            context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("fireball", result.Message!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Container_Resolves_StatusChangeHandler_WithConditionProvider()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CampaignVaultModule>();
        builder.RegisterInstance(new TestFakeEmbeddingService()).As<ILocalEmbeddingService>();

        using var container = builder.Build();

        Assert.NotNull(container.Resolve<StatusChangeHandler>());
        Assert.NotNull(container.Resolve<RestChangeHandler>());
    }

    [Fact]
    public void Container_Scans_DefaultImplementations()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CampaignVaultModule>();
        builder.RegisterInstance(new TestFakeEmbeddingService()).As<ILocalEmbeddingService>();

        using var container = builder.Build();

        Assert.NotNull(container.Resolve<CampaignVault.Data.IRollService>());
        Assert.NotNull(container.Resolve<CampaignVault.Data.INpcBehaviorSynthesizer>());
        Assert.NotNull(container.Resolve<CampaignVault.Data.Initiative.IRelevantMemorySelector>());
    }
}