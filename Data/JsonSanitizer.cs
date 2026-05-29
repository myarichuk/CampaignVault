using System.Collections;
using System.Text.Json;

namespace CampaignVault.Data;

/// <summary>
/// Universal sanitizer for the dangerous Dictionary&lt;string, object&gt; / object properties
/// that cross the System.Text.Json &lt;-&gt; Newtonsoft.Json boundary in this application.
///
/// Problems this solves:
/// - Inbound MCP / Microsoft.Extensions.AI uses STJ → complex values become JsonElement.
/// - We persist via Raven (Newtonsoft) → bad JsonElement can be stored or cause "dead document" errors later.
/// - Outbound tool responses are serialized with STJ again → "Operation is not valid due to the current state of the object".
///
/// This type provides:
/// 1. A Raven OnBeforeStore listener hook (see Program.cs).
/// 2. Reusable methods for repository and tool layers.
/// 3. Safe handling of already-dead JsonElements (common with legacy data).
/// </summary>
public static class JsonSanitizer
{
    /// <summary>
    /// Sanitizes a single entity (Location, Item, or Event) in-place.
    /// Safe to call from OnBeforeStore listeners, Upsert methods, or response guards.
    /// </summary>
    public static void Sanitize(object? entity)
    {
        switch (entity)
        {
            case Models.Location loc:
                SanitizeDictionary(loc.Metadata);
                break;
            case Models.Item item:
                SanitizeDictionary(item.Properties);
                break;
            case Models.Event ev:
                SanitizeDictionary(ev.Details);
                break;
        }
    }

    /// <summary>
    /// Recursively sanitizes any value graph, replacing System.Text.Json.JsonElement
    /// (live or dead) with plain CLR types. Extremely defensive because we may be called
    /// from OnBeforeStore on partially constructed or legacy-polluted graphs.
    /// </summary>
    public static object? SanitizeValue(object? value)
    {
        if (value is JsonElement je)
        {
            JsonValueKind kind;
            try
            {
                kind = je.ValueKind;
            }
            catch (InvalidOperationException)
            {
                return je.GetRawText(); // completely dead element
            }

            try
            {
                return kind switch
                {
                    JsonValueKind.String => je.GetString(),
                    JsonValueKind.Number => je.TryGetInt64(out var l) ? l :
                                            je.TryGetDecimal(out var dec) ? dec :
                                            je.TryGetDouble(out var d) ? d : je.GetRawText(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    JsonValueKind.Object =>
                        je.EnumerateObject()
                          .ToDictionary(p => p.Name, p => SanitizeValue(p.Value) ?? (object?)null, StringComparer.Ordinal),
                    JsonValueKind.Array =>
                        je.EnumerateArray().Select(x => SanitizeValue(x)).ToList(),
                    _ => je.GetRawText()
                };
            }
            catch (InvalidOperationException)
            {
                return je.GetRawText();
            }
        }

        if (value is IDictionary<string, object> dict)
            return SanitizeDictionary(dict);

        if (value is IList list && value is not string)
        {
            var result = new List<object?>(list.Count);
            foreach (var item in list)
                result.Add(SanitizeValue(item));
            return result;
        }

        return value;
    }

    /// <summary>
    /// Sanitizes a dictionary in place (mutates the original). This is safer for Raven change tracking
    /// than replacing the whole dictionary reference on a tracked entity.
    /// </summary>
    public static IDictionary<string, object>? SanitizeDictionary(IDictionary<string, object>? source)
    {
        if (source == null || source.Count == 0)
            return source;

        // Collect keys that need fixing first to avoid modification-during-enumeration issues
        var toFix = new List<(string key, object? badValue)>();
        foreach (var (key, value) in source)
        {
            if (value is JsonElement or IDictionary<string, object> or IList)
            {
                toFix.Add((key, value));
            }
        }

        foreach (var (key, badValue) in toFix)
        {
            try
            {
                source[key] = SanitizeValue(badValue)!;
            }
            catch
            {
                source[key] = badValue?.ToString() ?? "unsanitizable";
            }
        }

        return source;
    }

    /// <summary>
    /// Deep-sanitizes a tool response payload (or any graph) before it is returned
    /// through the MCP layer (which uses STJ for final wire serialization).
    /// </summary>
    public static void SanitizeForToolResponse(object? response)
    {
        if (response == null) return;

        switch (response)
        {
            case Models.Location loc:
                Sanitize(loc);
                break;
            case Models.Item item:
                Sanitize(item);
                break;
            case Models.Event ev:
                Sanitize(ev);
                break;
            case Models.SceneView scene:
                Sanitize(scene.Location);
                if (scene.VisibleItems != null)
                    foreach (var it in scene.VisibleItems) Sanitize(it);
                break;
            case IEnumerable<object> seq:
                foreach (var item in seq) SanitizeForToolResponse(item);
                break;
            case IEnumerable seq2:
                foreach (var item in seq2) SanitizeForToolResponse(item);
                break;
            default:
                break;
        }
    }
}
