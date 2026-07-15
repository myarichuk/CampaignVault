using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using CampaignVault.Models;
using CampaignVault.Tools;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace CampaignVault.Tests;

public class McpToolMetadataTests
{
    private readonly ITestOutputHelper _output;

    public McpToolMetadataTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void AllMcpTools_HaveCorrectMetadata()
    {
        var assembly = typeof(CampaignToolBase).Assembly;
        
        var toolTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .ToList();

        Assert.NotEmpty(toolTypes);

        bool allPassed = true;

        foreach (var type in toolTypes)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .ToList();

            foreach (var method in methods)
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                
                // 1. Check UseStructuredContent
                if (toolAttr == null || !toolAttr.UseStructuredContent)
                {
                    _output.WriteLine($"[Error] Method {type.Name}.{method.Name} is missing UseStructuredContent = true on [McpServerTool].");
                    allPassed = false;
                }

                // 2. Check Description on Method
                var methodDesc = method.GetCustomAttribute<DescriptionAttribute>();
                if (methodDesc == null || string.IsNullOrWhiteSpace(methodDesc.Description))
                {
                    _output.WriteLine($"[Warning] Method {type.Name}.{method.Name} has no [Description].");
                }

                // 3. Check Parameters
                foreach (var param in method.GetParameters())
                {
                    var paramDesc = param.GetCustomAttribute<DescriptionAttribute>();
                    if (paramDesc == null || string.IsNullOrWhiteSpace(paramDesc.Description))
                    {
                        _output.WriteLine($"[Error] Parameter '{param.Name}' on {type.Name}.{method.Name} is missing a [Description] attribute.");
                        allPassed = false;
                    }
                }
            }
        }

        Assert.True(allPassed, "One or more MCP tools have invalid metadata. See test output for details.");
    }

    [Fact]
    public void WorldChanges_UseCanonicalIdNames_NoLegacyActorOrRelationType()
    {
        var assembly = typeof(WorldChange).Assembly;
        var worldChangeTypes = assembly.GetTypes()
            .Where(t => typeof(WorldChange).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        var forbidden = new[] { "ActorId", "SourceId", "actorId", "sourceId", "relationType", "RelationType" };
        var badTypes = new List<string>();

        foreach (var t in worldChangeTypes)
        {
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var p in props)
            {
                if (forbidden.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    badTypes.Add($"{t.Name}.{p.Name}");
                }
                var jpn = p.GetCustomAttribute<JsonPropertyNameAttribute>();
                if (jpn != null && forbidden.Contains(jpn.Name, StringComparer.OrdinalIgnoreCase))
                {
                    badTypes.Add($"{t.Name}.{p.Name} (json:{jpn.Name})");
                }
            }
        }

        Assert.Empty(badTypes);
    }

    [Fact]
    public void McpCampaignTools_RequireExplicitCampaignName_NoOptionals()
    {
        var assembly = typeof(CampaignToolBase).Assembly;
        var toolTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .ToList();

        var violations = new List<string>();

        foreach (var type in toolTypes)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .ToList();

            foreach (var method in methods)
            {
                foreach (var param in method.GetParameters())
                {
                    if (string.Equals(param.Name, "campaignName", StringComparison.OrdinalIgnoreCase))
                    {
                        if (param.HasDefaultValue)
                        {
                            violations.Add($"{type.Name}.{method.Name} has optional/defaulted campaignName");
                        }
                        // Nullable ref annotation does not change the runtime ParameterType (still string), so HasDefaultValue + name is the practical guard
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void GetCommitSchemaAndListTools_CategoryDescriptions_AreNotSelfReferentialOrInverted()
    {
        var getCommitSchema = typeof(MetaTools).GetMethod(nameof(MetaTools.GetCommitSchema))!;
        var listTools = typeof(MetaTools).GetMethod(nameof(MetaTools.ListTools))!;

        var commitSchemaCategoryDesc = getCommitSchema.GetParameters()
            .Single(p => p.Name == "category").GetCustomAttribute<DescriptionAttribute>()!.Description;
        var listToolsCategoryDesc = listTools.GetParameters()
            .Single(p => p.Name == "category").GetCustomAttribute<DescriptionAttribute>()!.Description;

        // Regression guard for the previously-inverted doc string: get_commit_schema's description
        // must not claim that list_tools groups commit $types (it groups tools).
        Assert.DoesNotContain("list_tools' category parameter, which groups commit $types", commitSchemaCategoryDesc);
        Assert.DoesNotContain("get_commit_schema's category parameter, which groups commit $types", listToolsCategoryDesc);

        // Each description should self-identify its own taxonomy.
        Assert.Contains("commit $type", commitSchemaCategoryDesc);
        Assert.Contains("MCP tool", listToolsCategoryDesc);
    }
}
