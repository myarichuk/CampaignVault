namespace CampaignVault.Tools;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolCategoryAttribute(string category) : Attribute
{
    public string Category { get; } = category;
}