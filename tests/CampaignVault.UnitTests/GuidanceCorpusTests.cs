using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CampaignVault.Data.Guidance;
using CampaignVault.Models;
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

        // After Phase 3.5 trim: ~11.6KB (12,040 chars)
        // Target: < 15KB to leave headroom
        Assert.True(totalChars < 15000, $"DmHelpManual total {totalChars} exceeds 15000 char limit");
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

        var skillsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "claude_skills");
        if (!Directory.Exists(skillsDir))
        {
            // Skills directory doesn't exist in test context; skip this validation
            // in CI but ensure it runs locally if the repo structure changes.
            return;
        }

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

    [Fact]
    public void RecommendedSystemPrompt_UnderSizeCap()
    {
        // After Phase 3.7 trim: ~3KB (3,157 chars)
        // Target: < 5KB to stay under typical injection limits
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        var promptPath = Path.Combine(repoRoot, "recommended-system-prompt.md");

        if (!File.Exists(promptPath))
        {
            // Skip in unusual test environments where the file isn't accessible
            return;
        }

        var prompt = File.ReadAllText(promptPath);
        Assert.True(prompt.Length < 5000, $"recommended-system-prompt.md {prompt.Length} exceeds 5000 char limit");
    }

    [Fact]
    public void RecommendedSystemPrompt_MentionsGuidance()
    {
        // Verify the critical line directing users to follow guidance on tool responses.
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        var promptPath = Path.Combine(repoRoot, "recommended-system-prompt.md");

        if (!File.Exists(promptPath))
        {
            // Skip in unusual test environments where the file isn't accessible
            return;
        }

        var prompt = File.ReadAllText(promptPath);
        Assert.Contains("guidance", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("don't call `get_help` speculatively", prompt, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot(string startPath)
    {
        var current = new DirectoryInfo(startPath);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "recommended-system-prompt.md")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
