using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
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
}
