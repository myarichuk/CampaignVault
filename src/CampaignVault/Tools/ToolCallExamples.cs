using System.Text.Json;
using System.Text.Json.Nodes;

namespace CampaignVault.Tools;

/// <summary>
/// Curated per-tool argument synonyms and copy-paste retry bodies for LLM self-correction.
/// </summary>
internal static class ToolCallExamples
{
    private static readonly IReadOnlyDictionary<string, ToolCallExample> Registry = BuildRegistry();

    /// <summary>
    /// Top-level tool parameters that are siblings of take_turn's 'request' payload, not part of it —
    /// excluded when rewrapping a flattened call so they stay bound as separate MCP arguments.
    /// </summary>
    private static readonly HashSet<string> SiblingParameterKeys = new(StringComparer.Ordinal) { "campaignName" };

    /// <summary>Alias repairs applied to take_turn's payload (both top-level flattened calls and the nested 'request' object).</summary>
    private static readonly Dictionary<string, string[]> TakeTurnPayloadSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["changes"] = ["change", "commits", "worldChanges", "world_changes", "deltas"],
        ["narrative"] = ["summary", "description", "narration"],
    };

    public static bool TryGet(string toolName, out ToolCallExample example) =>
        Registry.TryGetValue(toolName, out example!);

    /// <summary>
    /// Rewrites known wrong parameter names and flattened take_turn shapes before MCP binding.
    /// </summary>
    public static bool TryNormalize(string toolName, JsonObject arguments, out IReadOnlyList<string> rewrites)
    {
        var applied = new List<string>();
        if (!Registry.TryGetValue(toolName, out var example))
        {
            rewrites = applied;
            return false;
        }

        var modified = false;

        foreach (var (canonical, aliases) in example.Synonyms)
        {
            if (arguments.ContainsKey(canonical))
            {
                continue;
            }

            foreach (var alias in aliases)
            {
                if (!arguments.ContainsKey(alias))
                {
                    continue;
                }

                arguments[canonical] = JsonNode.Parse(arguments[alias]!.ToJsonString());
                arguments.Remove(alias);
                applied.Add($"{alias}→{canonical}");
                modified = true;
                break;
            }
        }

        // take_turn nests its payload under 'request'. Repairs, in order: alias fixes at both levels,
        // rewrapping a flattened call, then normalizing the changes array itself.
        JsonNode? changesNode = null;
        JsonObject? changesOwner = null;
        if (string.Equals(toolName, "take_turn", StringComparison.OrdinalIgnoreCase))
        {
            bool ApplyPayloadAliases(JsonObject target)
            {
                var any = false;
                foreach (var (canonical, aliases) in TakeTurnPayloadSynonyms)
                {
                    if (target.ContainsKey(canonical))
                    {
                        continue;
                    }

                    foreach (var alias in aliases)
                    {
                        if (!target.ContainsKey(alias))
                        {
                            continue;
                        }

                        target[canonical] = JsonNode.Parse(target[alias]!.ToJsonString());
                        target.Remove(alias);
                        applied.Add($"{alias}→{canonical}");
                        any = true;
                        break;
                    }
                }

                return any;
            }

            modified |= ApplyPayloadAliases(arguments);

            if (!arguments.ContainsKey("request") &&
                (arguments.ContainsKey("changes") || arguments.ContainsKey("narrative")))
            {
                var wrapped = new JsonObject();
                foreach (var prop in arguments.ToList())
                {
                    if (SiblingParameterKeys.Contains(prop.Key))
                    {
                        continue;
                    }

                    wrapped[prop.Key] = prop.Value?.DeepClone();
                    arguments.Remove(prop.Key);
                }

                arguments["request"] = wrapped;
                applied.Add("flattened→request");
                modified = true;
            }

            if (arguments.TryGetPropertyValue("request", out var requestNode)
                && requestNode is JsonObject requestObj)
            {
                modified |= ApplyPayloadAliases(requestObj);

                if (requestObj.TryGetPropertyValue("changes", out changesNode))
                {
                    changesOwner = requestObj;
                }
            }
        }

        if (changesOwner is not null)
        {
            if (changesNode is JsonValue
                && changesNode.GetValueKind() == JsonValueKind.String
                && changesNode.GetValue<string>() is { } changesText
                && changesText.TrimStart().StartsWith('['))
            {
                try
                {
                    var parsed = JsonNode.Parse(changesText);
                    if (parsed is JsonArray)
                    {
                        changesOwner["changes"] = parsed;
                        applied.Add("changes(string)→changes(array)");
                        modified = true;
                        changesNode = parsed;
                    }
                }
                catch (JsonException)
                {
                    // Leave as-is; CommitChangesParser will surface a deserialization error.
                }
            }

            if (changesNode is JsonArray changesArray
                && NormalizeCommitChangesArray(changesArray, applied))
            {
                modified = true;
            }
        }

        rewrites = applied;
        return modified;
    }

    private static bool NormalizeCommitChangesArray(JsonArray changesArray, List<string> applied)
    {
        var modified = false;
        foreach (var node in changesArray)
        {
            if (node is not JsonObject changeObj)
            {
                continue;
            }

            if (!TryGetChangeType(changeObj, out var changeType))
            {
                continue;
            }

            if (string.Equals(changeType, "ruleset_action", StringComparison.OrdinalIgnoreCase))
            {
                if (NormalizeRulesetActionParameters(changeObj, applied))
                {
                    modified = true;
                }

                continue;
            }

            if (string.Equals(changeType, "rumor", StringComparison.OrdinalIgnoreCase))
            {
                if (NormalizeRumorChange(changeObj, applied))
                {
                    modified = true;
                }

                continue;
            }

            if (!string.Equals(changeType, "event", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!changeObj.ContainsKey("involved"))
            {
                foreach (var alias in new[] { "participants", "participantIds", "participant_ids" })
                {
                    if (!changeObj.ContainsKey(alias))
                    {
                        continue;
                    }

                    changeObj["involved"] = JsonNode.Parse(changeObj[alias]!.ToJsonString());
                    changeObj.Remove(alias);
                    applied.Add($"event.{alias}→involved");
                    modified = true;
                    break;
                }
            }
        }

        return modified;
    }

    private static bool NormalizeRulesetActionParameters(JsonObject changeObj, List<string> applied)
    {
        if (!changeObj.TryGetPropertyValue("parameters", out var paramsNode) || paramsNode is not JsonObject parameters)
        {
            return false;
        }

        var modified = false;
        foreach (var (canonical, aliases) in new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["resolution"] = ["spellResolution", "spell_resolution", "mode"],
            ["toHitBonus"] = ["to_hit_bonus", "attackBonus", "attack_bonus"],
            ["halfOnSave"] = ["half_on_save", "halfDamageOnSave"],
            ["healDice"] = ["heal_dice"],
            ["healBonus"] = ["heal_bonus"],
            ["healAmount"] = ["heal_amount"],
            ["spellAttackBonus"] = ["spell_attack_bonus"],
            ["saveAttribute"] = ["save_attribute"],
            ["saveSkill"] = ["save_skill"],
        })
        {
            if (parameters.ContainsKey(canonical))
            {
                continue;
            }

            foreach (var alias in aliases)
            {
                if (!parameters.ContainsKey(alias))
                {
                    continue;
                }

                parameters[canonical] = JsonNode.Parse(parameters[alias]!.ToJsonString());
                parameters.Remove(alias);
                applied.Add($"ruleset_action.{alias}→{canonical}");
                modified = true;
                break;
            }
        }

        if (parameters.ContainsKey("difficulty") && !parameters.ContainsKey("dc")
            && changeObj.TryGetPropertyValue("actionType", out var actionTypeNode)
            && actionTypeNode is JsonValue actionTypeValue
            && string.Equals(actionTypeValue.GetValue<string>(), "Spell", StringComparison.OrdinalIgnoreCase))
        {
            parameters["dc"] = JsonNode.Parse(parameters["difficulty"]!.ToJsonString());
            applied.Add("ruleset_action.difficulty→dc");
            modified = true;
        }

        return modified;
    }

    private static bool NormalizeRumorChange(JsonObject changeObj, List<string> applied)
    {
        var modified = false;

        if (changeObj.Remove("sourceCharacterId"))
        {
            applied.Add("rumor.removed sourceCharacterId");
            modified = true;
        }

        if (!changeObj.ContainsKey("rumorId")
            && changeObj.TryGetPropertyValue("subject", out var subjectNode)
            && subjectNode is JsonValue subjectValue
            && subjectValue.GetValue<string>() is { } subject
            && !string.IsNullOrWhiteSpace(subject))
        {
            changeObj["rumorId"] = SlugifyRumorId(subject);
            applied.Add("rumor.subject→rumorId");
            modified = true;
        }

        if (changeObj.Remove("subject"))
        {
            applied.Add("rumor.removed subject (evolve)");
            modified = true;
        }

        if (changeObj.TryGetPropertyValue("newState", out var newStateNode)
            && newStateNode is JsonValue newStateValue
            && string.Equals(newStateValue.GetValue<string>(), "Active", StringComparison.OrdinalIgnoreCase))
        {
            changeObj["newState"] = "Nascent";
            applied.Add("rumor.newState(Active)→Nascent");
            modified = true;
        }

        return modified;
    }

    private static string SlugifyRumorId(string subject)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(subject.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "rumors/unnamed" : $"rumors/{slug}";
    }

    private static bool TryGetChangeType(JsonObject changeObj, out string changeType)
    {
        changeType = string.Empty;
        if (changeObj.TryGetPropertyValue("$type", out var typeNode)
            && typeNode is JsonValue typeValue
            && typeValue.GetValue<string>() is { } fromDollarType
            && !string.IsNullOrWhiteSpace(fromDollarType))
        {
            changeType = fromDollarType;
            return true;
        }

        if (changeObj.TryGetPropertyValue("type", out var legacyTypeNode)
            && legacyTypeNode is JsonValue legacyValue
            && legacyValue.GetValue<string>() is { } fromType
            && !string.IsNullOrWhiteSpace(fromType))
        {
            changeType = fromType;
            return true;
        }

        return false;
    }

    public static (string Summary, JsonElement? RetryExample) BuildMissingParamResponse(
        string toolName,
        string paramName,
        string? guidance = null)
    {
        var baseMessage = guidance is null
            ? $"Missing required parameter '{paramName}'."
            : $"Missing required parameter '{paramName}'. {guidance}";

        if (!Registry.TryGetValue(toolName, out var example))
        {
            return (baseMessage, null);
        }

        var retry = example.BuildFullRequest();
        var summary =
            $"{baseMessage} Retry with this exact tools/call body (replace placeholder values): {retry.GetRawText()}";

        return (summary, retry);
    }

    public static (string Summary, JsonElement? RetryExample) BuildDeserializationErrorResponse(
        string toolName,
        string details)
    {
        if (!Registry.TryGetValue(toolName, out var example))
        {
            return ($"Invalid arguments for '{toolName}': {details}", null);
        }

        // The details already name the path, the $type and its fields (CommitBindingError); the retry
        // template travels once, in retryExample, instead of being repeated inside the summary.
        var retry = example.BuildFullRequest();
        var summary = $"Invalid arguments for '{toolName}': {details.TrimEnd('.')}. A retry template is in retryExample.";

        return (summary, retry);
    }

    internal sealed class ToolCallExample
    {
        public required string ToolName { get; init; }
        public IReadOnlyDictionary<string, string[]> Synonyms { get; init; } =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        public required JsonObject ArgumentsTemplate { get; init; }

        public JsonElement BuildFullRequest()
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = ToolName,
                    ["arguments"] = JsonNode.Parse(ArgumentsTemplate.ToJsonString()),
                },
            };

            return JsonSerializer.SerializeToElement(request);
        }
    }

    private static IReadOnlyDictionary<string, ToolCallExample> BuildRegistry()
    {
        var characterIdSynonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["characterId"] = ["npcId", "charId", "character_id", "char_id", "npc_id", "id"],
        };

        var locationIdSynonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["locationId"] = ["locId", "location_id", "loc_id", "location"],
        };

        return new Dictionary<string, ToolCallExample>(StringComparer.OrdinalIgnoreCase)
        {
            ["get_entity"] = new ToolCallExample
            {
                ToolName = "get_entity",
                Synonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["entityId"] = ["id", "characterId", "npcId", "charId", "locationId", "factionId", "questId", "itemId", "plotThreadId"],
                },
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "entityId": "chars/innkeeper"
                    }
                    """)!.AsObject(),
            },
            ["combat"] = new ToolCallExample
            {
                ToolName = "combat",
                Synonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["locationId"] = ["locId", "location_id", "loc_id", "location"],
                    ["combatantIds"] = ["combatants", "combatant_ids", "participantIds", "participants"],
                },
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "action": "start",
                      "locationId": "locations/tavern",
                      "combatantIds": ["chars/pc1", "chars/guard-captain"]
                    }
                    """)!.AsObject(),
            },
            ["take_turn"] = new ToolCallExample
            {
                ToolName = "take_turn",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "request": {
                        "changes": [
                          {
                            "$type": "event",
                            "category": "Conversation",
                            "summary": "Valen spoke with the innkeeper at the bar about harbor gossip.",
                            "involved": ["chars/valen", "chars/innkeeper"]
                          },
                          {
                            "$type": "engagement_relation",
                            "characterId": "chars/valen",
                            "targetId": "chars/innkeeper",
                            "category": "Social",
                            "verb": "talking with",
                            "bidirectional": true
                          },
                          {
                            "$type": "activity",
                            "characterId": "chars/valen",
                            "newActivity": "Leaning on the bar, listening to the innkeeper"
                          }
                        ],
                        "narrative": "Valen ordered ale and the innkeeper traded him news of the road: the toll bridge is watched now, and a wool caravan went missing at the ford."
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["world_build"] = new ToolCallExample
            {
                ToolName = "world_build",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "batch": {
                        "locations": [
                          { "id": "locations/rusty-nail", "name": "The Rusty Nail", "description": "A dim tavern near the docks.", "type": "Building" }
                        ],
                        "characters": [
                          { "id": "chars/innkeeper", "name": "Old Tam", "isPc": false, "isPartyCompanion": false, "currentLocationId": "locations/rusty-nail", "systemStats": { "$system": "dnd5e", "level": 2, "hitDie": "d8", "strength": 10, "dexterity": 12, "constitution": 14, "intelligence": 11, "wisdom": 13, "charisma": 14, "attributes": { "morale": 65, "reputation": 75 } } }
                        ]
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
        };
    }
}