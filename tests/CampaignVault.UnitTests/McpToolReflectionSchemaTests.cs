using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Regression coverage for the exact failure mode that let a stale [JsonConverter(typeof(JsonStringEnumConverter))]
/// on a since-migrated-to-string property (Campaign.System) crash schema generation for every MCP tool at
/// startup, in both stdio and HTTP transport. No prior test exercised this because it requires walking every
/// [McpServerTool] method's full parameter/return type graph the way WithToolsFromAssembly() does — unit tests
/// exercising individual tool methods never touch JsonSchemaExporter at all.
/// </summary>
public class McpToolReflectionSchemaTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public static IEnumerable<object[]> AllToolMethods()
    {
        var assembly = typeof(IMcpServerTool).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                {
                    yield return [type, method];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllToolMethods))]
    public void ToolMethod_SchemaGeneration_DoesNotThrow(Type declaringType, MethodInfo method)
    {
        // Mirrors what McpServerTool.Create (invoked by WithToolsFromAssembly at startup) does when it
        // builds input/output schemas: walk every parameter type and the (unwrapped) return type through
        // AIJsonUtilities.CreateJsonSchema, the exact call in the crash's stack trace. A bad [JsonConverter]
        // anywhere in that graph throws here exactly as it did at real startup.
        var typesToCheck = method.GetParameters()
            .Select(p => p.ParameterType)
            .Append(UnwrapTaskType(method.ReturnType));

        foreach (var type in typesToCheck)
        {
            var target = Nullable.GetUnderlyingType(type) ?? type;
            if (target.IsPrimitive || target == typeof(string) || target == typeof(void) || target == typeof(CancellationToken))
            {
                continue;
            }

            var exception = Record.Exception(() =>
                AIJsonUtilities.CreateJsonSchema(target, serializerOptions: JsonOptions));
            Assert.True(exception is null,
                $"{declaringType.Name}.{method.Name}: schema generation failed for {target}: {exception}");
        }
    }

    private static Type UnwrapTaskType(Type returnType)
    {
        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return typeof(void);
        }

        if (returnType.IsGenericType)
        {
            var def = returnType.GetGenericTypeDefinition();
            if (def == typeof(Task<>) || def == typeof(ValueTask<>))
            {
                return returnType.GetGenericArguments()[0];
            }
        }

        return returnType;
    }
}
