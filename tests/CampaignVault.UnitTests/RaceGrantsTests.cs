using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.UnitTests;

/// <summary>What a 5e race gives on the sheet beyond scores and traits: effects, spells by level, skills, proficiencies.</summary>
public sealed class RaceGrantsTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "cv_race_grants_" + Guid.NewGuid());
    private static readonly System.Reflection.Assembly Asm = typeof(RaceDefinitionProvider).Assembly;
    private static readonly RaceDefinitionProvider Races = new(Dir, Asm);

    private static Character Of(string race, int level = 1) => new()
    {
        Id = "chars/x", Name = "X", ClassLevel = $"Fighter {level}",
        SystemStats = new Dnd5eExtension { Race = race, Level = level, HitDie = "d10" },
    };

    [Fact]
    public void ADwarfResistsPoison_AndSavesAgainstItWithAdvantage()
    {
        var effects = CharacterRace.Effects(Of("dwarf"), RulesetSystem.Dnd5e, Races);

        Assert.Contains(effects, e => e.Effect is { Kind: "resistance", DamageType: "poison" });
        Assert.Contains(effects, e => e.Effect is { Kind: "advantage", On: "save" } && e.Effect.Assert.Contains("againstPoison"));
    }

    [Theory]
    [InlineData(1, new[] { "thaumaturgy" }, new string[0])]
    [InlineData(3, new[] { "thaumaturgy" }, new[] { "hellish_rebuke" })]
    [InlineData(5, new[] { "thaumaturgy" }, new[] { "hellish_rebuke", "darkness" })]
    public async Task ATieflingsSpells_ComeByLevel(int level, string[] cantrips, string[] known)
    {
        var tiefling = Of("tiefling", level);

        await new Dnd5eGrantClassSpellsStep(null, spells: new SpellDefinitionProvider(Dir, Asm), races: Races)
            .ApplyAsync(new BootstrapContext { Character = tiefling, ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);

        var stats = (Dnd5eExtension)tiefling.SystemStats!;
        Assert.Equal(cantrips, stats.Spells.Cantrips);
        Assert.Equal(known, stats.Spells.Known);
    }

    [Fact]
    public async Task ARacesSkillsAndWeapons_ReachTheSheet()
    {
        var elf = Of("elf");
        var dwarf = Of("dwarf");
        var step = new Dnd5eDeriveProficiencyStep(raceProvider: Races);

        await step.ApplyAsync(new BootstrapContext { Character = elf, ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);
        await step.ApplyAsync(new BootstrapContext { Character = dwarf, ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);

        Assert.True(((Dnd5eExtension)elf.SystemStats!).SkillModifiers.ContainsKey("Perception"));
        Assert.Contains("warhammer", ((Dnd5eExtension)dwarf.SystemStats!).WeaponProficiencies);
    }
}
