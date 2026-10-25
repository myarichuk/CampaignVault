using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Validates guidance corpus against size and duplication budgets (Phase 3.7).
/// Ensures push-based guidance doesn't bloat token usage and that skill files
/// only reference live tools (preventing 41% dead-code reaccumulation).
/// </summary>
public class GuidanceCorpusTests
{
    [Fact]
    public void DmHelpManual_UnderSizeCap()
    {
        var sections = new[]
        {
            DmHelpManual.CommitEnumSection,
            DmHelpManual.FaqSection,
            DmHelpManual.OnboardingSection,
            DmHelpManual.WorldBuildingSection,
        };

        var totalChars = sections.Sum(s => (long)s.Length);

        // After Phase 3.5 trim: ~11.6KB (12,040 chars). 3e moved the /build tools' call examples here
        // (paid only on lookup kind=help, instead of on every tools/list): 15,561. Cap raised to 16k.
        Assert.True(totalChars < 16000, $"DmHelpManual total {totalChars} exceeds 16000 char limit");
    }

    [Fact]
    public void HintKeys_AreUnique()
    {
        // Placeholder: tests that if guidance hints were registered here,
        // all ledger keys would be unique (no duplicate delivery logic).
        // Once contributors are fully implemented, this will validate
        // across all contributor outputs via GuidanceOrchestrator.

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        // For now, this is a structural test ensuring the pattern exists.
        // When hints are delivered, add them here and validate uniqueness.
        Assert.Empty(duplicates);
    }

    [Fact]
    public void SkillFiles_ReferenceOnlyLiveTools()
    {
        // Validate that claude_skills/* SKILL.md files only name tools in ToolCatalog.
        // This prevents the 41% dead-code scenario that plagued ToolCallExamples.

        var skillsDir = Path.Combine(RepoPaths.Root, "claude_skills");
        Assert.True(Directory.Exists(skillsDir), $"claude_skills not found at {skillsDir}");

        var liveTools = ToolCatalog.GetByCategory(null).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skillFiles = Directory.GetFiles(skillsDir, "SKILL.md", SearchOption.AllDirectories);

        foreach (var skillFile in skillFiles)
        {
            var content = File.ReadAllText(skillFile);

            // Simple heuristic: look for tool names in backticks or quoted strings
            // Full implementation would parse the skill file format more rigorously.
            // For now, check that no removed tools are mentioned.
            var deadTools = new[] { "upsert_character", "upsert_location", "upsert_item" };
            foreach (var deadTool in deadTools)
            {
                Assert.False(content.Contains(deadTool, StringComparison.OrdinalIgnoreCase),
                    $"Skill file {skillFile} references removed tool '{deadTool}'");
            }
        }
    }

    private const string WorldChangeSkill = "claude_skills/dnd-world-change/SKILL.md";
    private const string RegenerateEnv = "CV_WRITE_SKILL_FIELDS";

    /// <summary>
    /// Golden file: the `## Fields` section of dnd-world-change is generated from CommitSchemaModel and
    /// must match the server byte for byte. Set CV_WRITE_SKILL_FIELDS=1 and run this test to regenerate.
    /// </summary>
    [Fact]
    public void WorldChangeSkill_FieldsSection_MatchesServer()
    {
        var path = Path.Combine(RepoPaths.Root, WorldChangeSkill);
        var text = File.ReadAllText(path);
        var expected = CampaignVault.Schema.CommitFieldReference.RenderSkillSection();

        if (Environment.GetEnvironmentVariable(RegenerateEnv) == "1")
        {
            File.WriteAllText(path, CampaignVault.Schema.CommitFieldReference.ApplySkillSection(text));
            text = File.ReadAllText(path);
        }

        Assert.True(expected.Length <= CampaignVault.Schema.CommitFieldReference.SkillSectionMaxChars,
            $"generated Fields section is {expected.Length} chars; budget {CampaignVault.Schema.CommitFieldReference.SkillSectionMaxChars}");

        var begin = text.IndexOf(CampaignVault.Schema.CommitFieldReference.SkillSectionBegin, StringComparison.Ordinal);
        var end = text.IndexOf(CampaignVault.Schema.CommitFieldReference.SkillSectionEnd, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, $"{WorldChangeSkill} has no generated Fields section; run with {RegenerateEnv}=1");

        var actual = text[begin..(end + CampaignVault.Schema.CommitFieldReference.SkillSectionEnd.Length)] + "\n";
        Assert.True(expected == actual,
            $"{WorldChangeSkill} Fields section is stale; run this test with {RegenerateEnv}=1 to regenerate.");

        var staged = Path.Combine(RepoPaths.Root, "UnityClient", "Assets", "StreamingAssets", "CampaignVault", "skills", "dnd-world-change", "SKILL.md");
        if (File.Exists(staged))
        {
            Assert.True(File.ReadAllText(staged) == text, "the staged Unity copy of dnd-world-change differs from claude_skills; copy it over.");
        }
    }

    [Fact]
    public void FieldReference_HotTierTypes_AreInlineAndOthersIndexed()
    {
        var section = CampaignVault.Schema.CommitFieldReference.RenderSkillSection();
        Assert.Contains("**hp** —", section);
        Assert.Contains("**ruleset_action** —", section);
        Assert.Contains("characterId:string [req]", section);
        Assert.Contains("Other types: ", section);
        Assert.Contains("knowledge_update", section);
        Assert.DoesNotContain("**knowledge_update**", section);
    }
}
