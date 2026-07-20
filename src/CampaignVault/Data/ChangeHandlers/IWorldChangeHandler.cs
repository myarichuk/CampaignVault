using System.Reflection;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// A pluggable handler responsible for applying one (or a family of) WorldChange types.
/// 
/// The dispatcher asks handlers in registration order via ShouldHandle until one claims the change.
/// Handlers must have mutually exclusive ShouldHandle predicates (duplicate claims are treated as a bug).
/// 
/// Handlers are registered via DI as IEnumerable&lt;IWorldChangeHandler&gt; and are expected to be stateless
/// and safe to use across multiple commits.
/// </summary>
public interface IWorldChangeHandler
{
    /// <summary>
    /// Returns true if this handler wants to process the given change.
    /// Should be cheap and have no side effects.
    /// </summary>
    bool ShouldHandle(WorldChange change);

    /// <summary>
    /// Applies the change. The handler is responsible for mutating entities (from the pre-loaded context),
    /// recording summary messages, and indicating success/failure via the returned result.
    /// </summary>
    Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Extracts all entity IDs involved in the change so they can be pre-loaded by the dispatcher.
    /// Returns true if the change type is supported by this handler, false otherwise.
    /// </summary>
    bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (!ShouldHandle(change)) return false;

        foreach (var prop in change.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType == typeof(string))
            {
                var val = prop.GetValue(change) as string;
                WorldChangeHandlerHelpers.ProcessExtractedString(val, prop.Name, characterIds, locationIds, factionIds, questIds, itemIds, allInvolvedIds);
            }
            else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType) && prop.PropertyType != typeof(string))
            {
                var vals = prop.GetValue(change) as System.Collections.IEnumerable;
                if (vals != null)
                {
                    foreach (var item in vals)
                    {
                        WorldChangeHandlerHelpers.ProcessExtractedString(item as string, prop.Name, characterIds, locationIds, factionIds, questIds, itemIds, allInvolvedIds);
                    }
                }
            }
        }
        return true;
    }
}

internal static class WorldChangeHandlerHelpers
{
    /// <summary>
    /// Rewrites known ID-alias prefixes (e.g. "characters/" → "chars/") in place across every
    /// ID-like property of a WorldChange, before the dispatcher extracts/preloads referenced
    /// entities. This is the single write-boundary choke point for handler-consumed reference
    /// fields (ItemTransfer.ToHolderId, characterId, targetIds, involved, holderId,
    /// parentLocationId, ...) — without it, an aliased ID would preload/compare against the wrong
    /// document key deeper in the pipeline (see CanonicalId).
    /// </summary>
    public static void NormalizeIdFields(WorldChange change)
    {
        foreach (var prop in change.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
            {
                continue;
            }

            if (prop.PropertyType == typeof(string))
            {
                var val = (string?)prop.GetValue(change);
                if (string.IsNullOrEmpty(val))
                {
                    continue;
                }

                var normalized = CanonicalId.NormalizeAlias(val);
                if (normalized != val)
                {
                    prop.SetValue(change, normalized);
                }
            }
            else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType) && prop.PropertyType != typeof(string))
            {
                if (prop.GetValue(change) is List<string> list)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(list[i]))
                        {
                            list[i] = CanonicalId.NormalizeAlias(list[i]);
                        }
                    }
                }
            }
        }
    }

    public static void ProcessExtractedString(
        string? val, string propName,
        HashSet<string>? characterIds, HashSet<string>? locationIds,
        HashSet<string>? factionIds, HashSet<string>? questIds,
        HashSet<string>? itemIds, HashSet<string>? allInvolvedIds)
    {
        if (string.IsNullOrWhiteSpace(val)) return;

        bool isIdLike = propName.EndsWith("Id", StringComparison.OrdinalIgnoreCase) ||
                        propName.EndsWith("Ids", StringComparison.OrdinalIgnoreCase) ||
                        propName.Equals("Involved", StringComparison.OrdinalIgnoreCase);

        // Values reaching this point have already been rewritten to canonical prefixes by
        // NormalizeIdFields (called before ExtractInvolvedEntities on every dispatch path), so a
        // single "chars/" check is sufficient here.
        bool hasPrefix = val.StartsWith("chars/") ||
                         val.StartsWith("loc") || val.StartsWith("fac") ||
                         val.StartsWith("que") || val.StartsWith("item");

        if (isIdLike || hasPrefix)
        {
            if (val.StartsWith("chars/")) characterIds?.Add(val);
            else if (val.StartsWith("loc")) locationIds?.Add(val);
            else if (val.StartsWith("fac")) factionIds?.Add(val);
            else if (val.StartsWith("que")) questIds?.Add(val);
            else if (val.StartsWith("item")) itemIds?.Add(val);

            allInvolvedIds?.Add(val);
        }
    }
}