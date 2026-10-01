using System.Collections;
using System.Reflection;

namespace CampaignVault.Data.Templates;

/// <summary>
/// The template edits every kind shares, so no per-type <c>Merge</c> has to know about them:
/// <list type="bullet">
/// <item><c>&lt;list&gt;+:</c> appends and <c>&lt;list&gt;-:</c> removes, on top of whatever the plain list resolved to
/// (its own, or the parent's when it has none). An entry with the same name (a string, or an object's
/// <c>Name</c>/<c>Id</c>/<c>Key</c>) as one already there replaces it in place.</item>
/// <item><c>patches: &lt;name&gt;</c> merges a file into an existing template by the same rules as inheritance.</item>
/// <item><c>requires:</c> is inherited when the child doesn't set its own.</item>
/// </list>
/// Templates are records with init-only properties, so edits work on a reflection clone.
/// </summary>
internal static class TemplateEdits
{
    /// <summary>Applies the template's pending list edits once and clears them; returns it unchanged when it has none.</summary>
    public static T ApplyListOps<T>(T template) where T : RulesetTemplate
    {
        if (template.ListOps.Count == 0)
            return template;

        var copy = Clone(template);
        foreach (var op in template.ListOps)
        {
            var prop = copy.GetType().GetProperty(op.Property)!;
            var list = NewListFor(prop);
            if (prop.GetValue(copy) is IList current)
            {
                foreach (var item in current)
                    list.Add(item);
            }

            foreach (var item in op.Items)
            {
                var at = IndexOf(list, item);
                if (op.Remove)
                {
                    while (at >= 0)
                    {
                        list.RemoveAt(at);
                        at = IndexOf(list, item);
                    }
                }
                else if (at >= 0)
                {
                    list[at] = item;
                }
                else
                {
                    list.Add(item);
                }
            }

            prop.SetValue(copy, list);
        }

        copy.ListOps = [];
        return copy;
    }

    /// <summary>Carries the parent's <c>requires:</c> gate into a merged child that set none.</summary>
    public static T InheritBase<T>(T merged, T parent) where T : RulesetTemplate
    {
        if (merged.Requires is not null || parent.Requires is null)
            return merged;

        var copy = Clone(merged);
        Set(copy, nameof(RulesetTemplate.Requires), parent.Requires);
        return copy;
    }

    /// <summary>
    /// Merges a <c>patches:</c> file into the template it targets, by inheritance rules with the patch as the child.
    /// The target keeps its name, parents and gate unless the patch sets its own. A plain list in the patch replaces
    /// the target's list and drops the target's pending edits to it; the patch's own edits queue after the target's.
    /// </summary>
    public static T ApplyPatch<T>(T target, T patch, Func<T, T, T> merge) where T : RulesetTemplate
    {
        var merged = Clone(merge(patch, target));
        Set(merged, nameof(RulesetTemplate.Name), target.Name);
        if (patch.Inherits.Count == 0)
            Set(merged, nameof(RulesetTemplate.Inherits), target.Inherits);
        if (patch.Requires is null)
            Set(merged, nameof(RulesetTemplate.Requires), target.Requires);

        merged.ListOps =
        [
            .. target.ListOps.Where(op => !HasPlainList(patch, op.Property)),
            .. patch.ListOps,
        ];
        merged.PatchTarget = null;
        merged.Source = target.Source;
        return merged;
    }

    /// <summary>The list property a YAML key names (its <c>YamlMember</c> alias, else the camelCase property name), or null.</summary>
    public static PropertyInfo? ListProperty(Type templateType, string yamlKey)
    {
        var props = templateType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var prop = props.FirstOrDefault(p =>
                       p.GetCustomAttribute<YamlDotNet.Serialization.YamlMemberAttribute>()?.Alias == yamlKey)
                   ?? props.FirstOrDefault(p => p.Name.Equals(yamlKey, StringComparison.OrdinalIgnoreCase));
        return prop is not null && prop.CanWrite && typeof(IList).IsAssignableFrom(prop.PropertyType) && ElementType(prop) is not null
            ? prop
            : null;
    }

    private static bool HasPlainList<T>(T template, string property) where T : RulesetTemplate =>
        template.GetType().GetProperty(property)?.GetValue(template) is IList { Count: > 0 };

    private static IList NewListFor(PropertyInfo prop) =>
        (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ElementType(prop)!))!;

    private static Type? ElementType(PropertyInfo prop) =>
        prop.PropertyType.IsGenericType ? prop.PropertyType.GetGenericArguments()[0] : null;

    private static int IndexOf(IList list, object? item)
    {
        var key = KeyOf(item);
        for (var i = 0; i < list.Count; i++)
        {
            var other = KeyOf(list[i]);
            if (key is not null && other is not null
                    ? string.Equals(key, other, StringComparison.OrdinalIgnoreCase)
                    : Equals(list[i], item))
                return i;
        }

        return -1;
    }

    private static string? KeyOf(object? item) => item switch
    {
        null => null,
        string s => s,
        _ => item.GetType().GetProperty("Name")?.GetValue(item) as string
             ?? item.GetType().GetProperty("Id")?.GetValue(item) as string
             ?? item.GetType().GetProperty("Key")?.GetValue(item) as string,
    };

    private static T Clone<T>(T template) where T : RulesetTemplate =>
        (T)template.GetType().GetMethod("<Clone>$")!.Invoke(template, null)!;

    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property)!.SetValue(target, value);
}
