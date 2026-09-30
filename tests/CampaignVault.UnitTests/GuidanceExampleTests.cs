using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CampaignVault.Models.Converters;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Every <c>$type</c> change example in the skills and recommended prompts must parse as a real
/// commit, with unknown fields rejected. Models copy examples verbatim, so a stale field name in a
/// skill becomes a silently dropped value at the table (NARRATION_AND_CLIENT_PLAN.md, N8).
/// </summary>
public class GuidanceExampleTests
{
    private static readonly Regex JsonFence = new(@"```json\s*\n(.*?)```", RegexOptions.Singleline);
    private static readonly Regex InlineChange = new(@"`(\{[^`]*""\$type""[^`]*\})`");

    public static TheoryData<string> GuidanceFiles()
    {
        var root = FindRepoRoot();
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "claude_skills"), "SKILL.md", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(root, "recommended-system-prompt*.md"))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(root, f));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GuidanceFiles))]
    public void ChangeExamples_ParseAsRealCommits(string file)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), file));
        var problems = new List<string>();

        var snippets = JsonFence.Matches(text).Select(m => m.Groups[1].Value)
            .Concat(InlineChange.Matches(text).Select(m => m.Groups[1].Value));
        foreach (var snippet in snippets)
        {
            List<JsonNode> roots;
            try
            {
                roots = ReadAll(snippet);
            }
            catch (JsonException ex)
            {
                problems.Add($"not valid JSON ({ex.Message}):\n{Clip(snippet)}");
                continue;
            }

            foreach (var batch in roots.OfType<JsonObject>().Where(IsWorldBuildBatch))
            {
                try
                {
                    JsonSerializer.Deserialize<CampaignVault.Models.WorldBuildBatch>(batch.ToJsonString(), StrictBatchOptions.Value);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    problems.Add($"world_build: {ex.Message}\n  in {Clip(batch.ToJsonString())}");
                }
            }

            foreach (var change in roots.SelectMany(ChangesIn))
            {
                var array = new JsonArray(change.DeepClone());
                try
                {
                    var parsed = JsonSerializer.Deserialize<CampaignVault.Models.WorldChange[]>(
                        WorldChangeNormalizer.NormalizeChangesArray(array.ToJsonString()), StrictOptions.Value);
                    if (parsed is not { Length: 1 })
                    {
                        problems.Add($"parsed to nothing: {Clip(change.ToJsonString())}");
                    }
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    problems.Add($"{ex.Message}\n  in {Clip(change.ToJsonString())}");
                }
            }
        }

        Assert.True(problems.Count == 0, $"{file}: stale change examples:\n" + string.Join("\n", problems));
    }

    // A help topic that doesn't exist silently returns the generic "don't call speculatively" text.
    [Theory]
    [MemberData(nameof(GuidanceFiles))]
    public void HelpTopicReferences_AreRealTopics(string file)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), file));
        var unknown = Regex.Matches(text, @"topic[=:]\s*""?([A-Za-z-]+)")
            .Select(m => m.Groups[1].Value)
            .Where(topic => !Enum.TryParse<HelpTopic>(topic.Replace("-", "", StringComparison.Ordinal), ignoreCase: true, out var parsed)
                            || parsed == HelpTopic.None)
            .Distinct()
            .ToList();

        Assert.True(unknown.Count == 0, $"{file}: unknown lookup help topics: {string.Join(", ", unknown)}");
    }

    // The prompt variants share one core; only the ruleset and plugin sections may differ.
    [Theory]
    [InlineData("NARRATION")]
    [InlineData("SESSIONS")]
    [InlineData("TOOL HYGIENE")]
    public void PromptVariants_ShareTheCoreSections(string heading)
    {
        var root = FindRepoRoot();
        var variants = Directory.EnumerateFiles(root, "recommended-system-prompt*.md").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(3, variants.Count);

        var expected = PromptSection(File.ReadAllText(Path.Combine(root, "recommended-system-prompt.md")), heading);
        Assert.False(string.IsNullOrEmpty(expected), $"recommended-system-prompt.md has no {heading} section");
        foreach (var variant in variants)
        {
            Assert.Equal(expected, PromptSection(File.ReadAllText(variant), heading));
        }
    }

    // A section is its heading line through the next blank line, inside the ```text fence.
    private static string? PromptSection(string markdown, string heading)
    {
        var start = markdown.IndexOf("```text", StringComparison.Ordinal);
        var end = markdown.IndexOf("\n```", start + 7, StringComparison.Ordinal);
        var body = markdown[(start + 7)..end].Replace("\r\n", "\n", StringComparison.Ordinal);
        return body.Split("\n\n").FirstOrDefault(b => b.Trim('\n').StartsWith(heading, StringComparison.Ordinal))?.Trim('\n');
    }

    private static readonly Lazy<JsonSerializerOptions> StrictOptions = new(() =>
        new JsonSerializerOptions(CommitChangesParser.CreateOptions())
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        });

    private static readonly string[] BatchKinds =
    [
        "locations", "factions", "creatures", "spells", "feats", "characters", "items",
        "quests", "plotThreads", "worldEvents", "lore", "rumors", "needDescriptors",
    ];

    private static bool IsWorldBuildBatch(JsonObject obj) =>
        obj.Count > 0 && obj.All(p => BatchKinds.Contains(p.Key, StringComparer.Ordinal));

    private static readonly Lazy<JsonSerializerOptions> StrictBatchOptions = new(() =>
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter() },
        });

    // A fence may hold one value, an array, or several objects one per line.
    private static List<JsonNode> ReadAll(string snippet)
    {
        var result = new List<JsonNode>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(snippet), new JsonReaderOptions
        {
            AllowMultipleValues = true,
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        while (reader.Read())
        {
            var node = JsonNode.Parse(ref reader);
            if (node != null)
            {
                result.Add(node);
            }
        }

        return result;
    }

    // Every object carrying "$type", wherever it sits (a bare change, a changes[] array, a take_turn request).
    private static IEnumerable<JsonNode> ChangesIn(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.ContainsKey("$type"))
                {
                    yield return obj;
                    yield break;
                }

                foreach (var (_, child) in obj)
                {
                    if (child == null) continue;
                    foreach (var c in ChangesIn(child)) yield return c;
                }

                break;
            case JsonArray arr:
                foreach (var child in arr)
                {
                    if (child == null) continue;
                    foreach (var c in ChangesIn(child)) yield return c;
                }

                break;
        }
    }

    private static string Clip(string s) => s.Length <= 240 ? s : s[..240] + "…";

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "CampaignVault.sln"))
                || File.Exists(Path.Combine(dir, "src", "CampaignVault", "CampaignVault.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
