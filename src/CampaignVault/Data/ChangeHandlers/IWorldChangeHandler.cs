using System;
using System.Collections.Generic;
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

        bool hasPrefix = val.StartsWith("chars/") || val.StartsWith("characters/") ||
                         val.StartsWith("loc") || val.StartsWith("fac") ||
                         val.StartsWith("que") || val.StartsWith("item");

        if (isIdLike || hasPrefix)
        {
            allInvolvedIds?.Add(val);
            if (val.StartsWith("chars/") || val.StartsWith("characters/")) characterIds?.Add(val);
            else if (val.StartsWith("loc")) locationIds?.Add(val);
            else if (val.StartsWith("fac")) factionIds?.Add(val);
            else if (val.StartsWith("que")) questIds?.Add(val);
            else if (val.StartsWith("item")) itemIds?.Add(val);
        }
    }
}