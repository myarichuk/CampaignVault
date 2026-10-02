using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Phase 5: the Necromancer's Grimoire content pack loads through the standard
/// providers and its summon spell resolves end to end.
/// </summary>
public class NecromancersGrimoireTests
{
    private static string PluginSourceDir()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(repo, "plugins", "NecromancersGrimoire", "RulesetData");
    }

    private static readonly Lazy<string> DataDir = new(() =>
    {
        // A copy, never the plugin's own folder: a provider's root is the host layer, which extracts every shipped
        // default into it, and that once filled the plugin with hundreds of copies of the base spells, items and creatures.
        var dir = Path.Combine(Path.GetTempPath(), "cv_grimoire_" + Guid.NewGuid());
        var source = PluginSourceDir();
        foreach (var file in Directory.EnumerateFiles(source, "*.yaml", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return dir;
    });

    private static string PluginDataDir() => DataDir.Value;

    private static SpellDefinitionProvider SpellProvider() =>
        new(PluginDataDir(), typeof(SpellDefinitionProvider).Assembly);

    private static CreatureDefinitionProvider CreatureProvider() =>
        new(PluginDataDir(), typeof(CreatureDefinitionProvider).Assembly);

    [Fact]
    public void Plugin_HoldsOnlyItsOwnContent()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(PluginSourceDir()))!;
        Assert.Empty(Directory.EnumerateFiles(root, ".extracted-manifest.json", SearchOption.AllDirectories));
        // No file shares its name with a default the host ships (those are extracted copies, not the plugin's content).
        var shipped = typeof(CreatureDefinitionProvider).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("CampaignVault.RulesetData.dnd5e.", StringComparison.Ordinal))
            .Select(n => n["CampaignVault.RulesetData.dnd5e.".Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copies = Directory.EnumerateFiles(PluginSourceDir(), "*.yaml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(PluginSourceDir(), "dnd5e"), f).Replace(Path.DirectorySeparatorChar, '.'))
            .Where(shipped.Contains)
            .ToList();
        Assert.Empty(copies);
    }

    [Fact]
    public void Grimoire_Spells_LoadWithMechanicalShapes()
    {
        var spells = SpellProvider().GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("ng_grave_chill", out var chill));
        Assert.NotNull(chill.DamageAtCharacterLevel);
        Assert.Equal("1d8", chill.DamageAtCharacterLevel[1]);
        Assert.True(chill.RequiresAttackRoll);

        Assert.True(spells.TryGetValue("ng_sepulchral_echo", out var echo));
        Assert.NotNull(echo.DelayedTick);
        Assert.Equal("3d6", echo.DelayedTick.DiceExpressionAtSlotLevel![5]);

        Assert.True(spells.TryGetValue("ng_marrow_burst", out var burst));
        Assert.Equal("sphere", burst.AreaOfEffectType);
        Assert.Equal(20, burst.AreaOfEffectSize);

        Assert.True(spells.TryGetValue("ng_red_harvest", out var harvest));
        Assert.Equal("4d8", harvest.DamageAtSlotLevel![3]);
        Assert.Equal("constitution", harvest.SaveType);

        Assert.True(spells.TryGetValue("ng_pallid_chain", out var chain));
        Assert.Equal("4d8", chain.DamageAtSlotLevel![4]);
        Assert.Equal("wisdom", chain.SaveType);

        Assert.True(spells.TryGetValue("ng_ebon_tithe", out var tithe));
        Assert.Equal("2d6", tithe.DamageAtSlotLevel![2]);
        Assert.Equal("constitution", tithe.SaveType);
    }

    [Fact]
    public void Grimoire_BindShade_HasEnforcedCap()
    {
        var spells = SpellProvider().GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("ng_bind_shade", out var bind));
        var summon = Assert.IsType<SummonEffect>(bind.Summon);
        Assert.Equal(["Umbral Stalker"], summon.Creatures);
        Assert.Equal(1, summon.CountAtSlotLevel![2]);
        Assert.Equal(1, summon.CountAtSlotLevel[4]);
        Assert.Equal(2, summon.CountAtSlotLevel[6]);
        Assert.Equal(2, summon.ControlCap!.MaxCreatures);
        Assert.True(bind.Concentration);
        Assert.True(bind.MaterialConsumed);
    }

    [Fact]
    public void Grimoire_CreaturesAndFocus_Load()
    {
        var creatures = CreatureProvider().GetCreaturesForSystem(RulesetSystem.Dnd5e);

        Assert.True(creatures.TryGetValue("Umbral Stalker", out var stalker));
        Assert.Equal(22, stalker.Hp);
        Assert.True(creatures.ContainsKey("Skeletal Archer"));
        Assert.True(creatures.ContainsKey("Zombie Brute"));
        Assert.True(creatures.ContainsKey("Ghast Hound"));
        Assert.True(creatures.ContainsKey("Cinder Wisp"));
        Assert.True(creatures.ContainsKey("Impish Trickster"));
        Assert.True(creatures.ContainsKey("Wight Blade"));

        var items = new ItemDefinitionProvider(PluginDataDir(), typeof(ItemDefinitionProvider).Assembly).GetItemsForSystem(RulesetSystem.Dnd5e);
        Assert.True(items.TryGetValue("ng_onyx_focus", out var focus));
        Assert.Equal("Consumable", focus.Category);
    }

    [Fact]
    public async Task Grimoire_BindShade_ResolvesEndToEnd()
    {
        var resolver = new Dnd5eRulesetResolver(
            Substitute.For<IRollService>(),
            spellDefinitionProvider: SpellProvider(),
            creatureDefinitionProvider: CreatureProvider());
        var caster = new Character
        {
            Id = "chars/necromancer",
            Name = "Necra",
            CampaignName = "grimoire-test",
            SystemStats = new Dnd5eExtension { Level = 3 },
        };
        var context = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { [caster.Id] = caster },
            campaignName: "grimoire-test");

        var output = await resolver.ResolveAsync(context, new RulesetAction
        {
            CharacterId = caster.Id,
            ActionType = RulesetActionType.Spell,
            ActionName = "ng_bind_shade",
            Parameters = new Dictionary<string, string>(),
        }, TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        var create = Assert.IsType<CharacterCreate>(Assert.Single(output.Mutations.OfType<CharacterCreate>()));
        Assert.Equal("Umbral Stalker", create.Name);
        Assert.Equal(22, create.MaxHp);
        Assert.Equal(14, ((Dnd5eExtension)create.SystemStats!).ArmorClass);
    }
}
