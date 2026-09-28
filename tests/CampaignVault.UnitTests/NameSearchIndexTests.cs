using System.Linq;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class NameSearchIndexTests
{
    private static readonly string[] Names =
    [
        "tiny_hut", "Leomund's Tiny Hut", "Tiny Servant", "Hut of Chickens", "Shutter Spell",
        "fireball", "Fire Shield", "Wall of Fire", "Firestorm", "magic_missile", "Magic Weapon",
        "Summon Fey", "Summon Undead", "Cure Wounds", "Mass Cure Wounds", "Bless", "Bane",
    ];

    private static readonly NameSearchIndex<string> Index = NameSearchIndex<string>.Build(Names, n => n);

    private static string[] Search(string query) => [.. Index.Search(query).OrderByDescending(h => h.Score).Select(h => h.Item)];

    [Fact]
    public void AllQueryWords_MustMatch_SoNoUnrelatedNamesAppear()
    {
        var hits = Search("tiny hut");

        Assert.Equal(2, hits.Length);
        Assert.Contains("tiny_hut", hits);
        Assert.Contains("Leomund's Tiny Hut", hits);
        Assert.DoesNotContain("Tiny Servant", hits);
        Assert.DoesNotContain("Hut of Chickens", hits);
        Assert.DoesNotContain("Shutter Spell", hits);
    }

    [Theory]
    [InlineData("hut tiny", "Leomund's Tiny Hut")]
    [InlineData("leomunds", "Leomund's Tiny Hut")]
    [InlineData("LEOMUND'S TINY", "Leomund's Tiny Hut")]
    [InlineData("tiny huts", "Leomund's Tiny Hut")]
    [InlineData("magic mis", "magic_missile")]
    [InlineData("fireballs", "fireball")]
    [InlineData("firebal", "fireball")]
    [InlineData("fierball", "fireball")]
    [InlineData("sumon undead", "Summon Undead")]
    public void Query_FindsTarget(string query, string expected) => Assert.Contains(expected, Search(query));

    [Fact]
    public void ExactName_RanksAboveLongerNames()
    {
        Assert.Equal("fireball", Search("fireball")[0]);
        Assert.Equal("Bless", Search("bless")[0]);
        Assert.Equal("Cure Wounds", Search("cure wounds")[0]);
    }

    [Fact]
    public void Prefix_RanksAboveTypo()
    {
        var hits = Search("fire");

        Assert.Contains("Fire Shield", hits);
        Assert.Contains("Wall of Fire", hits);
        Assert.Contains("fireball", hits);
    }

    [Fact]
    public void ShortTokens_DoNotFuzzyMatchUnrelatedWords()
    {
        // "bane" must not typo-match "bless"/"base" style words; short tokens are exact/prefix only.
        Assert.Equal(["Bane"], Search("bane"));
        Assert.Empty(Search("hat"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("'")]
    public void BlankQuery_ReturnsNothing(string query) => Assert.Empty(Index.Search(query));
}
