using System;
using System.IO;
using System.Linq;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// The PF2e data the character builder reads (scripts/generate_pf2e_origins.py, generate_pf2e_classes.py,
/// generate_pf2e_feats.py): ancestry boosts, HP and heritages, background boosts and skills, class skills, feat categories.
/// </summary>
public class Pf2eCreationDataTests
{
    private static readonly System.Reflection.Assembly Asm = typeof(RaceDefinitionProvider).Assembly;

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "cv_pf2e_data_test_" + Guid.NewGuid());

    [Fact]
    public void Ancestry_HasItsHp_FixedBoostsAndFlaw_FreeBoosts_AndHeritages()
    {
        var races = new RaceDefinitionProvider(TempRoot(), Asm);

        Assert.True(races.TryGet(RulesetSystem.Pathfinder2e, "dwarf", out var dwarf));
        Assert.Equal(10, dwarf!.Hp);
        Assert.Equal(1, dwarf.AbilityBonuses["Constitution"]);
        Assert.Equal(1, dwarf.AbilityBonuses["Wisdom"]);
        Assert.Equal(-1, dwarf.AbilityBonuses["Charisma"]);
        Assert.Equal(1, dwarf.FreeBoosts);
        Assert.Contains("Clan Dagger", dwarf.Traits);
        Assert.Contains(dwarf.Heritages, h => h.Name == "rock_dwarf" && h.Label == "Rock Dwarf" && h.Description!.Contains("Shove"));

        Assert.True(races.TryGet(RulesetSystem.Pathfinder2e, "human", out var human));
        Assert.Empty(human!.AbilityBonuses);
        Assert.Equal(2, human.FreeBoosts);
        Assert.Equal(["skilled_human", "versatile_human"], human.Heritages.Select(h => h.Name).Take(2));
        // The versatile heritages are any ancestry's, so the dwarf has them as well.
        string[] versatile = ["aiuvarin", "changeling", "dhampir", "dragonblood", "dromaar", "duskwalker", "nephilim"];
        Assert.Equal(versatile, human.Heritages.Skip(2).Select(h => h.Name));
        Assert.Equal(versatile, dwarf.Heritages.Select(h => h.Name).Intersect(versatile));
        Assert.StartsWith("Versatile heritage.", human.Heritages.Last().Description);
    }

    [Fact]
    public void Background_HasItsBoostPair_ItsSkillOrAChoiceOfTwo_ItsLore_AndItsSkillFeat()
    {
        var backgrounds = new BackgroundDefinitionProvider(TempRoot(), Asm);

        Assert.True(backgrounds.TryGet(RulesetSystem.Pathfinder2e, "acolyte", out var acolyte));
        Assert.Equal(["Intelligence", "Wisdom"], acolyte!.Boosts);
        Assert.Equal(["Religion"], acolyte.SkillProficiencies);
        Assert.Equal("Scribing Lore", acolyte.Lore);
        Assert.Equal("student_of_the_canon", acolyte.SkillFeat);

        Assert.True(backgrounds.TryGet(RulesetSystem.Pathfinder2e, "hermit", out var hermit));
        Assert.Empty(hermit!.SkillProficiencies);
        Assert.Equal(["Nature", "Occultism"], hermit.SkillOptions);
    }

    [Fact]
    public void Class_HasItsHp_AndTrainedSkills_FixedOneOfAndHowManyMore()
    {
        var classes = new ClassDefinitionProvider(TempRoot(), Asm);

        Assert.True(classes.TryResolveClass(RulesetSystem.Pathfinder2e, "wizard", out var wizard));
        Assert.Equal("d6", wizard!.HitDie);
        Assert.Equal(["Arcana"], wizard.SkillChoices!.Trained);
        Assert.Equal(2, wizard.SkillChoices.Count);

        Assert.True(classes.TryResolveClass(RulesetSystem.Pathfinder2e, "fighter", out var fighter));
        Assert.Equal("d10", fighter!.HitDie);
        Assert.Empty(fighter.SkillChoices!.Trained);
        Assert.Equal(["Acrobatics", "Athletics"], fighter.SkillChoices.OneOf);
        Assert.Equal(4, fighter.SkillChoices.Count); // 3 more, plus the Acrobatics-or-Athletics pick

        Assert.True(classes.TryResolveClass(RulesetSystem.Pathfinder2e, "ranger", out var ranger));
        Assert.Equal("d10", ranger!.HitDie);
    }

    [Fact]
    public void Feats_HaveACategory_AncestryFeatsTheirAncestry_AndSkillFeatsTheirSkill()
    {
        var feats = new FeatDefinitionProvider(TempRoot(), Asm).GetFeatsForSystem(RulesetSystem.Pathfinder2e);

        Assert.All(feats.Values, f => Assert.Contains(f.Category, new[] { "ancestry", "class", "skill", "general", "archetype" }));
        Assert.Equal("ancestry", feats["dwarven_lore"].Category);
        Assert.Equal(["dwarf"], feats["dwarven_lore"].Ancestries);
        Assert.Equal("skill", feats["cat_fall"].Category);
        Assert.Equal(["Acrobatics"], feats["cat_fall"].Skills);
        Assert.Equal("general", feats["toughness"].Category);
        Assert.Equal("class", feats["reach_spell"].Category);
        Assert.Contains("wizard", feats["reach_spell"].Classes);
    }
}
