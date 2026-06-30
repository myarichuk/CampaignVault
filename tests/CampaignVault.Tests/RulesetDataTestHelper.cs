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

    public static (ResourcePoolProvider Pools, ClassDefinitionProvider Classes, ResourcePoolInitializer Initializer)
        CreateServices()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_ruleset_test_" + Guid.NewGuid());
        var pools = new ResourcePoolProvider(dir, Assembly);
        var classes = new ClassDefinitionProvider(dir, Assembly);
        var initializer = new ResourcePoolInitializer(pools, classes);
        return (pools, classes, initializer);
    }

    public static CharacterCreateHandler CreateCharacterCreateHandler(
        CampaignDocumentKeys? keys = null,
        CharacterBootstrapOrchestrator? bootstrap = null)
    {
        var (_, classes, initializer) = CreateServices();
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
        var (_, _, initializer) = CreateServices();
        return new LevelUpChangeHandler(
            keys ?? new CampaignDocumentKeys(),
            bootstrap ?? BootstrapTestHelper.CreateOrchestrator(),
            initializer);
    }
}