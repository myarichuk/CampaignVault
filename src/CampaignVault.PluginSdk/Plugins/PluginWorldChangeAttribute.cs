namespace CampaignVault.Plugins;

/// <summary>
/// Marks a plugin-authored <see cref="Models.WorldChange"/> subtype for wire/schema registration.
/// Discriminator must match the JSON <c>$type</c> string and must not collide with core or other plugins.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PluginWorldChangeAttribute : Attribute
{
    public string Discriminator { get; }

    /// <summary>
    /// Optional <c>IInteractionMode.ModeId</c> this verb belongs to. Mode-scoped verbs are left out
    /// of the get_commit_schema index (they stay resolvable via type=) so campaigns that never enable the mode don't pay for them.
    /// </summary>
    public string? ModeId { get; init; }

    public PluginWorldChangeAttribute(string discriminator)
    {
        if (string.IsNullOrWhiteSpace(discriminator))
            throw new ArgumentException("Discriminator is required.", nameof(discriminator));
        Discriminator = discriminator;
    }
}
