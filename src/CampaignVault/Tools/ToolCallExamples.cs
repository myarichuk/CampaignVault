using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CampaignVault.Tools;

/// <summary>
/// Curated per-tool argument synonyms and copy-paste retry bodies for LLM self-correction.
/// </summary>
internal static class ToolCallExamples
{
    private static readonly IReadOnlyDictionary<string, ToolCallExample> Registry = BuildRegistry();

    public static bool TryGet(string toolName, out ToolCallExample example) =>
        Registry.TryGetValue(toolName, out example!);

    /// <summary>
    /// Rewrites known wrong parameter names and upsert wrapper shapes before MCP binding.
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

        if (example.LegacyWrapperKey is { } legacyKey &&
            example.WrapperKey is { } wrapperKey &&
            arguments.TryGetPropertyValue(legacyKey, out var legacyNode) &&
            legacyNode is not null &&
            !arguments.ContainsKey(wrapperKey))
        {
            arguments[wrapperKey] = legacyNode.DeepClone();
            arguments.Remove(legacyKey);
            applied.Add($"{legacyKey}→{wrapperKey}");
            modified = true;
        }

        if (example.AllowFlattenedWrapper &&
            example.WrapperKey is { } wrapKey &&
            !arguments.ContainsKey(wrapKey) &&
            example.FlattenedFieldDetector?.Invoke(arguments) == true)
        {
            var wrapped = new JsonObject();
            var clone = JsonNode.Parse(arguments.ToJsonString()) as JsonObject;
            if (clone is not null)
            {
                wrapped[wrapKey] = clone;
                arguments.Clear();
                foreach (var prop in wrapped)
                {
                    arguments[prop.Key] = prop.Value?.DeepClone();
                }

                applied.Add($"flattened→{wrapKey}");
                modified = true;
            }
        }

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

        if (string.Equals(toolName, "commit", StringComparison.OrdinalIgnoreCase)
            && arguments.TryGetPropertyValue("changes", out var changesNode))
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
                        arguments["changes"] = parsed;
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

            if (changeType is "rumor" or "rumor_create")
            {
                if (NormalizeRumorChange(changeObj, applied))
                {
                    modified = true;
                }

                continue;
            }

            if (string.Equals(changeType, "quest_create", StringComparison.OrdinalIgnoreCase))
            {
                if (NormalizeQuestCreate(changeObj, applied))
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
            ["rangeModifier"] = ["range_modifier"],
            ["bonusDice"] = ["bonus_dice", "luckDice"],
            ["targetPart"] = ["target_part", "hitLocation"],
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

        if (changeObj.TryGetPropertyValue("newState", out var newStateNode)
            && newStateNode is JsonValue newStateValue
            && string.Equals(newStateValue.GetValue<string>(), "Active", StringComparison.OrdinalIgnoreCase))
        {
            changeObj.Remove("newState");
            applied.Add("rumor.newState(Active)→removed");
            modified = true;
        }

        var looksLikeCreate = changeObj.ContainsKey("text")
            || (changeObj.ContainsKey("newText") && !changeObj.ContainsKey("newState"));

        if (changeObj.ContainsKey("newText") && !changeObj.ContainsKey("text"))
        {
            if (looksLikeCreate)
            {
                changeObj["text"] = changeObj["newText"]!.DeepClone();
                changeObj.Remove("newText");
                applied.Add("rumor.newText→text");
                modified = true;
            }
        }

        if (looksLikeCreate
            || (changeObj.ContainsKey("subject") && changeObj.ContainsKey("text") && !changeObj.ContainsKey("newState")))
        {
            if (!string.Equals(changeObj["$type"]?.GetValue<string>(), "rumor_create", StringComparison.OrdinalIgnoreCase))
            {
                changeObj["$type"] = "rumor_create";
                applied.Add("rumor→rumor_create");
                modified = true;
            }

            if (changeObj.ContainsKey("subject") && changeObj.ContainsKey("newState"))
            {
                changeObj.Remove("newState");
                applied.Add("rumor.removed newState on create");
                modified = true;
            }
        }
        else if (changeObj.ContainsKey("subject") && changeObj.ContainsKey("newState"))
        {
            changeObj.Remove("subject");
            applied.Add("rumor.removed subject (evolve)");
            modified = true;
        }

        return modified;
    }

    private static bool NormalizeQuestCreate(JsonObject changeObj, List<string> applied)
    {
        var modified = false;

        if (changeObj.ContainsKey("deadlineDays") && !changeObj.ContainsKey("deadlineDay"))
        {
            changeObj["deadlineDay"] = JsonNode.Parse(changeObj["deadlineDays"]!.ToJsonString());
            changeObj.Remove("deadlineDays");
            applied.Add("quest_create.deadlineDays→deadlineDay");
            modified = true;
        }

        if (changeObj.TryGetPropertyValue("objectives", out var objectivesNode) && objectivesNode is JsonArray objectives)
        {
            foreach (var objective in objectives)
            {
                if (objective is not JsonObject objectiveObj)
                {
                    continue;
                }

                if (objectiveObj.Remove("state"))
                {
                    applied.Add("quest_create.objectives.removed state");
                    modified = true;
                }
            }
        }

        return modified;
    }

    private static string SlugifyRumorId(string subject)
    {
        var slug = Regex.Replace(subject.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
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

        var retry = example.BuildFullRequest();
        var extra = example.DeserializationHint is { } hint ? $" {hint}" : "";
        var summary =
            $"Invalid arguments for '{toolName}': {details}.{extra} Retry with this exact tools/call body (replace placeholder values): {retry.GetRawText()}";

        return (summary, retry);
    }

    internal sealed class ToolCallExample
    {
        public required string ToolName { get; init; }
        public IReadOnlyDictionary<string, string[]> Synonyms { get; init; } =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        public string? WrapperKey { get; init; }
        public string? LegacyWrapperKey { get; init; }
        public bool AllowFlattenedWrapper { get; init; }
        public Func<JsonObject, bool>? FlattenedFieldDetector { get; init; }
        public string? DeserializationHint { get; init; }
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
            ["get_npc_context"] = new ToolCallExample
            {
                ToolName = "get_npc_context",
                Synonyms = characterIdSynonyms,
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "characterId": "chars/innkeeper"
                    }
                    """)!.AsObject(),
            },
            ["get_npc_needs"] = new ToolCallExample
            {
                ToolName = "get_npc_needs",
                Synonyms = characterIdSynonyms,
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "characterId": "chars/innkeeper"
                    }
                    """)!.AsObject(),
            },
            ["get_scene"] = new ToolCallExample
            {
                ToolName = "get_scene",
                Synonyms = locationIdSynonyms,
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "locationId": "locations/tavern",
                      "partyPresent": true
                    }
                    """)!.AsObject(),
            },
            ["start_combat"] = new ToolCallExample
            {
                ToolName = "start_combat",
                Synonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["locationId"] = ["locId", "location_id", "loc_id", "location"],
                    ["combatantIds"] = ["combatants", "combatant_ids", "participantIds", "participants"],
                },
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "locationId": "locations/tavern",
                      "combatantIds": ["chars/pc1", "chars/guard-captain"]
                    }
                    """)!.AsObject(),
            },
            ["commit"] = new ToolCallExample
            {
                ToolName = "commit",
                Synonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["changes"] = ["change", "commits", "worldChanges", "world_changes", "deltas"],
                    ["narrative"] = ["summary", "description", "narration"],
                },
                DeserializationHint =
                    "Conversation events MUST include 'involved' with every participant's character ID (NOT 'participants'). "
                    + "Crowd interrupt: $type scene_interrupt_check with locationId, characterId, optional riskModifier (-50..+50). "
                    + "SPELLS: use actionType Spell + parameters.resolution (attack|save|check|utility|heal). "
                    + "AoE saves: ONE ruleset_action with all targetIds — NOT per-target SavingThrow. "
                    + "Fireball pattern: resolution save, dc, save, damageDice, halfOnSave true. "
                    + "Detect Magic: resolution check, dc, skill — no targetIds. "
                    + "5e casters: bootstrap spellcastingAbility on systemStats; omit dc/bonus if spellSaveDc/spellAttackBonus derived. "
                    + "Fallout: dc aliases difficulty; Stimpak = UseItem + healAmount. "
                    + "RUMORS: seed with $type rumor_create (rumorId, subject, text); evolve with $type rumor (rumorId, newState). "
                    + "Engine auto-applies hp from ruleset_action — no duplicate hp commits. "
                    + "See get_help → Ruleset Actions for copy-paste JSON.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
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
                        },
                        {
                          "$type": "activity",
                          "characterId": "chars/innkeeper",
                          "newActivity": "Tending bar and sharing gossip with Valen"
                        }
                      ],
                      "narrative": "Valen ordered ale and exchanged news with the innkeeper."
                    }
                    """)!.AsObject(),
            },
            ["upsert_character"] = new ToolCallExample
            {
                ToolName = "upsert_character",
                WrapperKey = "character",
                LegacyWrapperKey = "c",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = obj =>
                    obj.ContainsKey("id") || obj.ContainsKey("name") || obj.ContainsKey("systemStats")
                    || obj.ContainsKey("psychology") || obj.ContainsKey("maxHp"),
                DeserializationHint =
                    "systemStats.attributes accepts numbers only — no strings or booleans (e.g. attributes.hitDie: \"d12\" fails). "
                    + "Put hitDie on the dnd5e extension root (systemStats.hitDie), not in attributes. "
                    + "Omit maxHp to auto-derive HP from hitDie/level/constitution; explicit maxHp always wins. "
                    + "Multiclass: systemStats.classLevels array. Casters: spellcastingAbility (derives spellSaveDc/spellAttackBonus). "
                    + "During play prefer commit with character_create.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "character": {
                        "id": "chars/kergil",
                        "name": "Kergil",
                        "keepAlive": true,
                        "classLevel": "Human Barbarian 10",
                        "notes": "Unarmored Defense active. Rage +3 damage.",
                        "systemStats": {
                          "$system": "dnd5e",
                          "hitDie": "d12",
                          "level": 10,
                          "constitution": 16,
                          "hpMode": "average",
                          "dexterity": 14,
                          "strength": 18,
                          "skillModifiers": { "Athletics": 9, "Perception": 5 },
                          "attributes": { "rageDamage": 3 }
                        }
                      }
                    }
                    """)!.AsObject(),
            },
            ["upsert_location"] = new ToolCallExample
            {
                ToolName = "upsert_location",
                WrapperKey = "location",
                LegacyWrapperKey = "l",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = obj =>
                    obj.ContainsKey("id") || obj.ContainsKey("name") || obj.ContainsKey("type"),
                DeserializationHint =
                    "Location.type must be one of: Region, Settlement, District, Building, Room, Wilderness (not Tavern). During play prefer commit with location_update instead.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "location": {
                        "id": "locations/tavern",
                        "name": "The Rusty Nail",
                        "description": "A lively dockside tavern.",
                        "type": "Building"
                      }
                    }
                    """)!.AsObject(),
            },
        };
    }
}