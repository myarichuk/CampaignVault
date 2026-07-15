namespace CampaignVault.Tools;

/// <summary>
/// Marks an MCP tool parameter as required in the generated JSON schema even though its
/// C# signature is nullable (nullable is kept so the handler can return a friendly
/// <see cref="ToolArgumentErrors"/> message instead of an MCP binding failure).
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class SemanticallyRequiredAttribute : Attribute;
