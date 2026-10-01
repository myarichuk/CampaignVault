using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Plugin rules content: list edits (<c>list+:</c>/<c>list-:</c>) in inheritance, <c>patches:</c>, collision warnings,
/// layer order, and <c>requires:</c> on any template. Plugin roots are passed to <see cref="RulesetContentLayers{T}"/>
/// directly so no test touches <see cref="PluginDataRoots.Additional"/>.
/// </summary>
[Collection("PluginStatics")]
public class PluginRulesContentTests : IDisposable
{
    private static readonly System.Reflection.Assembly Asm = typeof(RaceDefinitionProvider).Assembly;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv-plugin-content-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Write(string root, string system, string folder, string file, string yaml)
    {
        var dir = Path.Combine(_root, root, system, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), yaml);
        return Path.Combine(_root, root);
    }

    private const string Elf = """
        name: elf
        system: testsys
        traits: [Darkvision, Fey Ancestry, Trance]
        extraLanguages: [Elvish]
        """;

    [Fact]
    public void Race_ListEdits_AppendRemoveAndReplace_OnTopOfTheParent()
    {
        var host = Write("host", "testsys", "races", "elf.yaml", Elf);
        Write("host", "testsys", "races", "moon_elf.yaml", """
            name: moon_elf
            inherits: [elf]
            traits+: [Moonlit Step, Darkvision]
            traits-: [Trance]
            extraLanguages: [Sylvan]
            """);

        var races = new RaceDefinitionProvider(host, Asm).GetRacesForSystem("testsys");

        // Append keeps order and skips a duplicate; remove drops; a plain list still replaces.
        Assert.Equal(["Darkvision", "Fey Ancestry", "Moonlit Step"], races["moon_elf"].Traits);
        Assert.Equal(["Sylvan"], races["moon_elf"].ExtraLanguages);
        // The parent is untouched.
        Assert.Equal(["Darkvision", "Fey Ancestry", "Trance"], races["elf"].Traits);
    }

    [Fact]
    public void Background_ListEdits_WorkWithoutInheritanceToo()
    {
        var host = Write("host", "testsys", "backgrounds", "sage.yaml", """
            name: sage
            system: testsys
            skillProficiencies: [Arcana, History]
            languages: [Draconic]
            """);
        Write("host", "testsys", "backgrounds", "hedge_sage.yaml", """
            name: hedge_sage
            inherits: [sage]
            skillProficiencies-: [arcana]
            skillProficiencies+: [Nature]
            languages: [Sylvan]
            """);
        Write("host", "testsys", "backgrounds", "loner.yaml", """
            name: loner
            system: testsys
            skillProficiencies: [Survival]
            skillProficiencies+: [Stealth]
            """);

        var backgrounds = new BackgroundDefinitionProvider(host, Asm).GetBackgroundsForSystem("testsys");

        // Removal matches names case-insensitively.
        Assert.Equal(["History", "Nature"], backgrounds["hedge_sage"].SkillProficiencies);
        Assert.Equal(["Sylvan"], backgrounds["hedge_sage"].Languages);
        Assert.Equal(["Survival", "Stealth"], backgrounds["loner"].SkillProficiencies);
    }

    [Fact]
    public void Patch_FromAPluginRoot_MergesIntoTheCoreRace_AndReachesItsChildren()
    {
        var host = Write("host", "testsys", "races", "elf.yaml", Elf);
        Write("host", "testsys", "races", "wood_elf.yaml", """
            name: wood_elf
            inherits: [elf]
            traits+: [Mask of the Wild]
            """);
        var plugin = Write("plugin", "testsys", "races", "elf_patch.yaml", """
            patches: elf
            description: Patched by the plugin.
            traits+: [Starlight Sense]
            extraLanguages+: [Sylvan]
            """);

        var races = Layers<RaceDefinition>(host, RaceDefinition.Merge, ["races"], plugin).Resolve("testsys");

        Assert.Equal("elf", races["elf"].Name);
        Assert.Equal("Patched by the plugin.", races["elf"].Description);
        Assert.Equal(["Darkvision", "Fey Ancestry", "Trance", "Starlight Sense"], races["elf"].Traits);
        Assert.Equal(["Elvish", "Sylvan"], races["elf"].ExtraLanguages);
        Assert.Equal(["Darkvision", "Fey Ancestry", "Trance", "Starlight Sense", "Mask of the Wild"], races["wood_elf"].Traits);
        Assert.Equal(2, races.Count);
    }

    [Fact]
    public void Patch_OnARealCoreRace_KeepsItsShippedFields()
    {
        var host = Path.Combine(_root, "host");
        Directory.CreateDirectory(host);
        var plugin = Write("plugin", "dnd5e", "races", "elf_patch.yaml", """
            patches: elf
            traits+: [Starlight Sense]
            """);

        var elf = Layers<RaceDefinition>(host, RaceDefinition.Merge, ["races", "ancestries"], plugin).Resolve("dnd5e")["elf"];

        Assert.Equal(["Darkvision", "Fey Ancestry", "Trance", "Starlight Sense"], elf.Traits);
        Assert.Equal(2, elf.AbilityBonuses["Dexterity"]);
        Assert.Equal("dnd5e", elf.System);
    }

    [Fact]
    public void Patch_WithAMissingTarget_IsSkippedWithAWarning()
    {
        var host = Write("host", "testsys", "races", "elf.yaml", Elf);
        var plugin = Write("plugin", "testsys", "races", "ghost_patch.yaml", """
            patches: gnome
            traits+: [Ghostly]
            """);
        var logger = new CollectingLogger();

        var races = Layers<RaceDefinition>(host, RaceDefinition.Merge, ["races"], plugin, logger).Resolve("testsys");

        Assert.Equal(["elf"], races.Keys);
        Assert.Contains(logger.Messages, m => m.Contains("'gnome'") && m.Contains("skipped"));
    }

    [Fact]
    public void Collision_BetweenCoreAndAPlugin_LogsBothSources_AndThePluginWins()
    {
        var host = Write("host", "testsys", "backgrounds", "sage.yaml", "name: sage\nsystem: testsys\nskillProficiencies: [Arcana]\n");
        var plugin = Write("plugin", "testsys", "backgrounds", "sage.yaml", "name: Sage\nsystem: testsys\nskillProficiencies: [History]\n");
        var logger = new CollectingLogger();

        var backgrounds = Layers<BackgroundDefinition>(host, BackgroundDefinition.Merge, ["backgrounds"], plugin, logger).Resolve("testsys");

        Assert.Equal(["History"], backgrounds["sage"].SkillProficiencies);
        Assert.Contains(logger.Messages, m => m.Contains("'Sage'") && m.Contains("core") && m.Contains(plugin) && m.Contains("patches: Sage"));
    }

    [Fact]
    public void Layers_ApplyInTheGivenPluginOrder_NotFolderOrder()
    {
        var host = Write("host", "testsys", "backgrounds", "sage.yaml", "name: sage\nsystem: testsys\nskillProficiencies: [Arcana]\n");
        // "zzz" sorts after "aaa" on disk, but it is listed first, so "aaa" layers over it.
        var first = Write("zzz", "testsys", "backgrounds", "p.yaml", "patches: sage\nskillProficiencies+: [First]\n");
        var second = Write("aaa", "testsys", "backgrounds", "p.yaml", "patches: sage\nskillProficiencies+: [Second]\n");

        var layers = new RulesetContentLayers<BackgroundDefinition>(host, Asm, ["backgrounds"], BackgroundDefinition.Merge, null, [first, second]);

        Assert.Equal(["Arcana", "First", "Second"], layers.Resolve("testsys")["sage"].SkillProficiencies);
    }

    [Fact]
    public void Patch_WithAPlainList_ReplacesIt_AndDropsTheTargetsPendingEditsToIt()
    {
        var host = Write("host", "testsys", "races", "elf.yaml", Elf);
        Write("host", "testsys", "races", "moon_elf.yaml", "name: moon_elf\ninherits: [elf]\ntraits+: [Moonlit Step]\n");
        var plugin = Write("plugin", "testsys", "races", "p.yaml", "patches: moon_elf\ntraits: [Only This]\n");

        var races = Layers<RaceDefinition>(host, RaceDefinition.Merge, ["races"], plugin).Resolve("testsys");

        Assert.Equal(["Only This"], races["moon_elf"].Traits);
    }

    [Fact]
    public void ListEdit_OnAFieldThatIsNotAList_IsIgnoredWithAWarning()
    {
        var host = Write("host", "testsys", "races", "elf.yaml", Elf + "\nsize+: [Huge]\n");
        var logger = new CollectingLogger();

        var races = Layers<RaceDefinition>(host, RaceDefinition.Merge, ["races"], null, logger).Resolve("testsys");

        Assert.Equal(["Darkvision", "Fey Ancestry", "Trance"], races["elf"].Traits);
        Assert.Contains(logger.Messages, m => m.Contains("'size+:'") && m.Contains("ignored"));
    }

    [Fact]
    public void Requires_IsInheritedByChildren()
    {
        var host = Write("host", "testsys", "races", "elf.yaml", Elf + "\nrequires: { plugin: elf-pack }\n");
        Write("host", "testsys", "races", "moon_elf.yaml", "name: moon_elf\ninherits: [elf]\n");

        var races = new RaceDefinitionProvider(host, Asm).GetRacesForSystem("testsys");

        Assert.Equal("elf-pack", races["moon_elf"].Requires?.Plugin);
    }

    [Fact]
    public void Background_RequiringAPluginThatIsNotLoaded_IsHiddenFromTheHandbook_ButStillLoaded()
    {
        var host = Write("host", "dnd5e", "backgrounds", "spy.yaml", """
            name: spy
            system: dnd5e
            skillProficiencies: [Deception]
            requires: { plugin: shadow-pack }
            """);
        var backgrounds = new BackgroundDefinitionProvider(host, Asm);
        var previous = PluginDataRoots.LoadedPluginIds;
        try
        {
            PluginDataRoots.LoadedPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hidden = Handbook(host, backgrounds);
            PluginDataRoots.LoadedPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shadow-pack" };
            var shown = Handbook(host, backgrounds);

            Assert.DoesNotContain("spy", hidden.Backgrounds);
            Assert.Contains("acolyte", hidden.Backgrounds);
            Assert.Contains("spy", shown.Backgrounds);
            Assert.True(backgrounds.TryGet("dnd5e", "spy", out _));
        }
        finally
        {
            PluginDataRoots.LoadedPluginIds = previous;
        }
    }

    private static SystemHandbookResponse Handbook(string host, BackgroundDefinitionProvider backgrounds) =>
        SystemHandbookBuilder.Build(
            "dnd5e",
            new ClassDefinitionProvider(host, Asm),
            new RaceDefinitionProvider(host, Asm),
            backgrounds,
            new FeatDefinitionProvider(host, Asm),
            new ConditionDefinitionProvider(host, Asm));

    private static RulesetContentLayers<T> Layers<T>(
        string host, Func<T, T, T> merge, string[] folders, string? plugin, CollectingLogger? logger = null)
        where T : RulesetTemplate =>
        new(host, Asm, folders, merge, logger, plugin is null ? [] : [plugin]);
}
