using System;
using System.IO;
using System.Reflection;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;

namespace CampaignVault.Tests;

internal static class RulesetDataTestHelper
{
    private static readonly Assembly Assembly = typeof(ResourcePoolProvider).Assembly;

    public static (
        ResourcePoolProvider Pools,
        ClassDefinitionProvider Classes,
        ConditionDefinitionProvider Conditions,
        FeatDefinitionProvider Feats,
        ResourcePoolInitializer Initializer)
        CreateServices()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_ruleset_test_" + Guid.NewGuid());
        var pools = new ResourcePoolProvider(dir, Assembly);
        var classes = new ClassDefinitionProvider(dir, Assembly);
        var conditions = new ConditionDefinitionProvider(dir, Assembly);
        var feats = new FeatDefinitionProvider(dir, Assembly);
        var initializer = new ResourcePoolInitializer(pools, classes, feats);
        return (pools, classes, conditions, feats, initializer);
    }

    public static CharacterCreateHandler CreateCharacterCreateHandler(
        CampaignDocumentKeys? keys = null,
        CharacterBootstrapOrchestrator? bootstrap = null)
    {
        var (_, classes, _, _, initializer) = CreateServices();
        return new CharacterCreateHandler(
            keys ?? new CampaignDocumentKeys(),
            bootstrap ?? BootstrapTestHelper.CreateOrchestrator(),
            initializer,
            classes);
    }

    public static LevelUpChangeHandler CreateLevelUpHandler(
        CampaignDocumentKeys? keys = null,
        CharacterBootstrapOrchestrator? bootstrap = null)
    {
        var (_, _, _, _, initializer) = CreateServices();
        return new LevelUpChangeHandler(
            keys ?? new CampaignDocumentKeys(),
            bootstrap ?? BootstrapTestHelper.CreateOrchestrator(),
            initializer);
    }

    public static ConditionDefinitionProvider CreateConditionProvider() =>
        CreateServices().Conditions;

    public static RestChangeHandler CreateRestChangeHandler(EncounterResolver? resolver = null) =>
        new(resolver ?? new EncounterResolver(() => 1.0), CreateConditionProvider());

    public static StatusChangeHandler CreateStatusChangeHandler() =>
        new(CreateConditionProvider());
}