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
    /// Top-level tool parameters that are siblings of the wrapped entity payload, not part of it —
    /// excluded when repairing a flattened upsert_* call so they stay bound as separate MCP arguments.
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
            // Only the entity's own fields get wrapped — sibling tool parameters (campaignName)
            // stay at the top level, since they're bound as separate MCP arguments, not part of
            // the entity payload.
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

            arguments[wrapKey] = wrapped;
            applied.Add($"flattened→{wrapKey}");
            modified = true;
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
                DeserializationHint =
                    "🚨 *** CRITICAL CONSTRAINT: MUST HAVE EITHER CHANGES OR A REFRESH PARAM *** 🚨 You must pass EITHER (1) Changes with Narrative summary, OR (2) at least one refresh parameter like includeWorldState, includeParty, extraCharacterIds, extraLocationIds, fullDetailCharacterId, or fullDetailLocationId. Passing neither (empty no-op call) will be rejected. "
                    + "🚨 *** REQUIRED: '$type' IS NOT OPTIONAL *** 🚨 EVERY SINGLE change object MUST include a '$type' field. If even ONE change lacks '$type', the entire batch will be rejected with a deserialization error. See WorldChange's own description for the full list of valid values. See examples for correct structure. "
                    + "**Conversation events MUST include 'involved' field.** Other categories optional. Never put location IDs in 'involved'—use 'locationId' instead. "
                    + "ITEM PICKUP/DROP/TRANSFER: $type item with itemId + toHolderId moves an EXISTING item to a new holder (character, location, or container item) — e.g. a PC picking up a coin off a location's visibleItems: {\"$type\": \"item\", \"itemId\": \"items/gold-coin\", \"toHolderId\": \"chars/lyra\"}. Do NOT just narrate a pickup in prose — always pair it with this change or the item stays owned by its old holder. "
                    + "Crowd interrupt: $type scene_interrupt_check with locationId, characterId, optional riskModifier (-50..+50). "
                    + "SPELLS: use actionType Spell + parameters.resolution (attack|save|check|utility|heal). "
                    + "AoE saves: ONE ruleset_action with all targetIds — NOT per-target SavingThrow. "
                    + "Fireball pattern: resolution save, dc, save, damageDice, halfOnSave true. "
                    + "Detect Magic: resolution check, dc, skill — no targetIds. "
                    + "5e casters: bootstrap spellcastingAbility on systemStats; omit dc/bonus if spellSaveDc/spellAttackBonus derived. "
                    + "RESOURCES: $type resource with poolName/delta/spellName spends spell slots/ki/focus points/gold; validates spell level, and spending below 0 HARD-FAILS the commit (\"Insufficient <pool> for <name>: has X, needs Y.\") — grants above max still clamp silently. Recovery on NEXT advance_world after rest, not at rest time. "
                    + "RUMORS: create with world_build (rumors[]: id, regionLocationId, subject, text); evolve an existing one with $type rumor (rumorId, newState). "
                    + "Engine auto-applies hp from ruleset_action — no duplicate hp commits. "
                    + "See get_help → Ruleset Actions for copy-paste JSON.",
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
                        "narrative": "Valen ordered ale and exchanged news with the innkeeper."
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["world_build"] = new ToolCallExample
            {
                ToolName = "world_build",
                DeserializationHint =
                    "Each field is an optional array using the same shape as the matching upsert_* tool's entity payload "
                    + "(locations[] entries mirror location_update's fields, characters[] mirror character_update's, etc.). "
                    + "Dispatched in a fixed order: locations, factions, creatures/spells/feats, characters, items, quests, "
                    + "plotThreads, lore, rumors, then needDescriptors — all in ONE call. Capped at 100 total entries; split "
                    + "larger seeds into multiple calls. A validation failure on any entry rolls back the entire batch and "
                    + "reports '{kind}[{index}]' — fix that entry and resend the full batch. Forward references to an "
                    + "entity created later in the same batch are allowed (non-blocking warning only). "
                    + "IMPORTANT: Combat NPCs MUST have systemStats.$system='dnd5e'/'pf2e' with abilities (strength, dexterity, etc.). "
                    + "Add Attributes (morale, willpower, corruption, etc.) to enrich NPCs narratively and mechanically. "
                    + "See get_help topic=world-building for a full copy-paste example.",
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
            ["upsert_character"] = new ToolCallExample
            {
                ToolName = "upsert_character",
                WrapperKey = "character",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = args =>
                    args.ContainsKey("id") && args.ContainsKey("name") && !args.ContainsKey("character"),
                DeserializationHint =
                    "Required: id, name. This is the only tool that CREATES a character — for changes to an existing "
                    + "one (HP, activity, location, level-up, status) use take_turn changes instead, not another create call. "
                    + "Omit maxHp for PCs — the engine derives it from systemStats (hitDie/level/constitution etc.) at "
                    + "bootstrap; set maxHp directly only for creature stat blocks (or use systemStats.statBlockHp). "
                    + "systemStats: Dnd5e/Pf2e require $system (lowercase), level, hitDie, and core abilities (strength, dexterity, wisdom, etc.). "
                    + "ALWAYS add Attributes dictionary with custom narrative/mechanical properties: morale (loyalty/confidence 0-100), willpower (mental fortitude 0-100), temperature (physical comfort -50 to 100), plus campaign-specific ones (corruption, reputation, fear, honor, debt). "
                    + "Omit systemStats entirely for narrative-only NPCs (no combat/skills). "
                    + "Omitting psychology/social/needs/systemStats on an existing character preserves the stored value; providing one replaces it wholesale.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "character": {
                        "id": "chars/valen",
                        "name": "Valen the Brave",
                        "isPc": true,
                        "isPartyCompanion": false,
                        "systemStats": { "$system": "dnd5e", "level": 3, "hitDie": "d10", "strength": 14, "dexterity": 12, "constitution": 14, "intelligence": 10, "wisdom": 10, "charisma": 12, "attributes": { "morale": 75, "willpower": 60 } }
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["upsert_location"] = new ToolCallExample
            {
                ToolName = "upsert_location",
                WrapperKey = "location",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = args =>
                    args.ContainsKey("name") && args.ContainsKey("description") && !args.ContainsKey("location"),
                DeserializationHint =
                    "Required: id, name, description, type. type must be one of Region, Settlement, District, Building, "
                    + "Room, Wilderness (City/Town → Settlement; Tavern/Inn/Shop → Building — there is no 'City' or "
                    + "'Tavern' value). climateZone (Arctic, Tundra, Temperate, Desert, Tropical, Alpine, Subterranean) is "
                    + "optional — omit to inherit from the nearest parentLocationId ancestor. This is the only tool that "
                    + "CREATES a location — for incremental changes to an existing one use commit's location_update instead.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "location": {
                        "id": "locations/rusty-nail",
                        "name": "The Rusty Nail",
                        "description": "A dim tavern near the docks, smelling of pipe smoke and spilled ale.",
                        "type": "Building"
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["upsert_item"] = new ToolCallExample
            {
                ToolName = "upsert_item",
                WrapperKey = "item",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = args =>
                    args.ContainsKey("name") && args.ContainsKey("holderId") && !args.ContainsKey("item"),
                DeserializationHint =
                    "Required: id, name, description, holderId, coreCategory. coreCategory must be one of Weapon, Armor, "
                    + "Clothing, Container, Consumable, Tool, Material, Valuable, Document, Key, Other. To make an item "
                    + "equippable, set BOTH equipZones (Head, Face, Neck, Torso, Back, Waist, Hands, Wrists, Legs, Feet, "
                    + "MainHand, OffHand, Ring, Accessory) and equipLayer (Base, Armor, Outer, Held) — set once at "
                    + "creation, not on every equip. Set isEquipped:true only for starting gear worn at character "
                    + "creation; after creation, use commit's item_equip/item_unequip instead of re-calling this tool.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "item": {
                        "id": "items/chain-shirt",
                        "name": "Chain Shirt",
                        "description": "A shirt of interlocking metal rings.",
                        "holderId": "chars/valen",
                        "coreCategory": "Armor",
                        "equipZones": ["Torso"],
                        "equipLayer": "Armor"
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["upsert_creature"] = new ToolCallExample
            {
                ToolName = "upsert_creature",
                WrapperKey = "creature",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = args =>
                    args.ContainsKey("name") && args.ContainsKey("system") && !args.ContainsKey("creature"),
                DeserializationHint =
                    "Required: id, name, system (Dnd5e, Pathfinder2e, or Narrative). This defines a reusable homebrew "
                    + "stat-block TEMPLATE that overrides SRD creatures by name when queried via get_rules_reference (kind:'creatures') — it is "
                    + "distinct from a live NPC/monster instance in a scene, which is created via world_build's "
                    + "characters[], not this tool.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "creature": {
                        "id": "creatures/dire-wolf-alpha",
                        "name": "Dire Wolf Alpha",
                        "system": "Dnd5e",
                        "hp": 45,
                        "defense": 14
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["upsert_faction"] = new ToolCallExample
            {
                ToolName = "upsert_faction",
                WrapperKey = "faction",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = args =>
                    args.ContainsKey("name") && args.ContainsKey("factionType") && !args.ContainsKey("faction"),
                DeserializationHint =
                    "Required: id, name. factionType (Guild, Kingdom, Cult, MerchantHouse, MilitaryOrder, Criminal, "
                    + "Religious) defaults to Guild if omitted. For reputation or stance changes to an existing faction, "
                    + "use commit's faction_reputation/faction_state instead of re-calling this tool.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "faction": {
                        "id": "factions/thieves-guild",
                        "name": "The Thieves' Guild",
                        "factionType": "Criminal"
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["upsert_quest"] = new ToolCallExample
            {
                ToolName = "upsert_quest",
                WrapperKey = "quest",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = args =>
                    args.ContainsKey("title") && args.ContainsKey("objectives") && !args.ContainsKey("quest"),
                DeserializationHint =
                    "Required: id, title. urgency (Low, Normal, Urgent, Critical) defaults to Normal. objectives[] only "
                    + "needs a 'description' per entry (plus optional rewardHint/deadlineDay) — objective STATE is "
                    + "advanced later via commit's quest_progress, not by re-sending objectives here. For narrative "
                    + "progress on an existing quest, prefer quest_progress over re-calling this tool.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "quest": {
                        "id": "quests/stop-nightshade",
                        "title": "Stop the Nightshade Gang",
                        "urgency": "Normal",
                        "objectives": [ { "description": "Find the gang's hideout" } ]
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["upsert_lore"] = new ToolCallExample
            {
                ToolName = "upsert_lore",
                WrapperKey = "lore",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = args =>
                    args.ContainsKey("title") && args.ContainsKey("content") && !args.ContainsKey("lore"),
                DeserializationHint =
                    "Required: id, title, content. Call search_world first to check whether similar lore already exists "
                    + "before creating a duplicate entry.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "lore": {
                        "id": "lore/founding-of-the-city",
                        "title": "The Founding of the City",
                        "content": "Three centuries ago, refugees fleeing the war founded this port on the ashes of an older ruin."
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["upsert_rumor"] = new ToolCallExample
            {
                ToolName = "upsert_rumor",
                WrapperKey = "rumor",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = args =>
                    args.ContainsKey("subject") && args.ContainsKey("currentText") && !args.ContainsKey("rumor"),
                DeserializationHint =
                    "Required: id, subject, currentText; regionLocationId is required when CREATING a new rumor. state "
                    + "(Nascent, Spreading, Peak, Fading, Resolved, Forgotten) defaults to Nascent — there is no 'Active' "
                    + "value. truthValue (True, False, PartiallyTrue, Misleading, Unknown) defaults to True. For rumor evolution over time on "
                    + "an existing rumor, prefer commit's 'rumor' type over re-calling this tool.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "rumor": {
                        "id": "rumors/nightshade-gang",
                        "regionLocationId": "locations/docks-district",
                        "subject": "Nightshade Gang",
                        "currentText": "They say the Nightshade Gang has been paying off the harbor guards."
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["upsert_plot_thread"] = new ToolCallExample
            {
                ToolName = "upsert_plot_thread",
                WrapperKey = "plotThread",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = args =>
                    args.ContainsKey("title") && args.ContainsKey("tensionLevel") && !args.ContainsKey("plotThread"),
                DeserializationHint =
                    "Required: id, title. state (Dormant, Active, Escalating, Climax, Resolved, Abandoned) defaults to Active. "
                    + "Usually DM-scaffolding, not player-visible (isPlayerVisible defaults to false). When seeding new threads, "
                    + "MUST include: foreshadowingHooks (2-4 narratable teasers), clues (2-4 discoverable entries), "
                    + "resolutionCondition (testable end state), involvedEntityIds (NPCs/factions). Omitting these fields on an "
                    + "existing thread preserves the stored value — use this to bump tensionLevel or evolve state without re-sending the whole arc.",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "plotThread": {
                        "id": "plot-threads/guild-infiltration",
                        "title": "Infiltrate the Thieves' Guild",
                        "summary": "The guild leadership demands internal sabotage before offering refuge.",
                        "state": "Dormant",
                        "tensionLevel": 0,
                        "foreshadowingHooks": [
                          "A hooded figure watching from the rooftops when the party meets with guild contacts",
                          "Rumors of 'the job' — whispered in taverns, quickly silenced when outsiders draw near"
                        ],
                        "clues": [
                          {
                            "id": "clue-contact-letter",
                            "description": "A sealed letter from a guild intermediary, outlining the required sabotage",
                            "involvedEntityIds": ["chars/contact"]
                          },
                          {
                            "id": "clue-vault-location",
                            "description": "Overheard conversation between guild members about the vault's location and security",
                            "involvedEntityIds": ["chars/guard-captain"]
                          }
                        ],
                        "resolutionCondition": "Party presents evidence of completed sabotage to the guild leadership and gains their backing, or the job is abandoned and the guild becomes permanently hostile.",
                        "involvedEntityIds": ["chars/guild-master", "factions/thieves-guild"],
                        "dmNotes": "The guild leadership is testing party loyalty before granting them sanctuary. Their true endgame is political leverage against the city guard.",
                        "isPlayerVisible": false
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["upsert_spell"] = new ToolCallExample
            {
                ToolName = "upsert_spell",
                WrapperKey = "spell",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = args =>
                    args.ContainsKey("name") && args.ContainsKey("system") && !args.ContainsKey("spell"),
                DeserializationHint =
                    "Required: id, name, system (Dnd5e, Pathfinder2e, or Narrative). Homebrew spells override SRD spells "
                    + "by name when queried via get_rules_reference (kind:'spells').",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "spell": {
                        "id": "spells/frostbolt",
                        "name": "Frostbolt",
                        "system": "Dnd5e",
                        "level": 2
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
            ["upsert_feat"] = new ToolCallExample
            {
                ToolName = "upsert_feat",
                WrapperKey = "feat",
                AllowFlattenedWrapper = true,
                FlattenedFieldDetector = args =>
                    args.ContainsKey("name") && args.ContainsKey("system") && !args.ContainsKey("feat"),
                DeserializationHint =
                    "Required: id, name, system (Dnd5e, Pathfinder2e, or Narrative). Homebrew feats/perks override SRD "
                    + "ones by name when queried via get_rules_reference (kind:'handbook').",
                ArgumentsTemplate = JsonNode.Parse(
                    """
                    {
                      "feat": {
                        "id": "feats/river-runner",
                        "name": "River Runner",
                        "system": "Dnd5e"
                      },
                      "campaignName": "dragon-heist"
                    }
                    """)!.AsObject(),
            },
        };
    }
}