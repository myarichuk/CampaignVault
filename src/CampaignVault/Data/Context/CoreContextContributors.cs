using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.Context;

/// <summary>Shared loaders for the core contributors (all first-level-cache hits in the turn's session).</summary>
internal static class ContextTurnReads
{
    public static async Task<List<Character>> InvolvedCharactersAsync(ContextTurn turn, CancellationToken ct)
    {
        var ids = turn.InvolvedEntityIds
            .Where(id => id.StartsWith(CanonicalId.Characters, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var loaded = await turn.Session.LoadAsync<Character>(ids, ct);
        return loaded.Values.Where(c => c != null).ToList()!;
    }

    public static bool IsSkillCheck(WorldChange change, params string[] skills) =>
        change is RulesetAction { ActionType: RulesetActionType.SkillCheck or RulesetActionType.ContestedCheck } ra
        && skills.Any(s => ra.ActionName?.Contains(s, StringComparison.OrdinalIgnoreCase) == true
                           || ra.Parameters.GetValueOrDefault("skill")?.Contains(s, StringComparison.OrdinalIgnoreCase) == true);

    public static int PassivePerception(Character c) =>
        c.SystemStats?.Attributes?.TryGetValue("passivePerception", out var pp) == true ? (int)Math.Round(pp) : 10;
}

/// <summary>Anticipated memories: when the narrative of a commit is about something an involved character
/// remembers, push that memory (at most two per turn, each once per session). "Oda remembers Tamsin asked
/// about the ledger yesterday", sent only when the ledger comes up.</summary>
internal sealed class MemoryRecallContextContributor : IContextContributor
{
    internal const double MatchThreshold = 0.55;
    private const int MaxPerTurn = 2;

    public async Task<IEnumerable<ContextItem>> ContributeAsync(ContextTurn turn, CancellationToken ct = default)
    {
        if (turn.NarrativeVector is not { Length: > 0 } vector)
        {
            return [];
        }

        var matches = new List<(double Score, ContextItem Item)>();
        foreach (var character in await ContextTurnReads.InvolvedCharactersAsync(turn, ct))
        {
            foreach (var (key, memory) in character.Psychology?.Memories ?? [])
            {
                if (memory.SemanticVector is not { Length: > 0 } memoryVector)
                {
                    continue;
                }

                var score = SemanticEnrichmentHelper.CosineSimilarity(vector, memoryVector);
                var line = NpcCardFactory.Line(memory);
                if (score >= MatchThreshold && !turn.MemoryLinesInCards.Contains(line))
                {
                    matches.Add((score, new ContextItem($"mem:{character.Id}:{key}", $"{character.Name} recalls — {line}", 50)));
                }
            }
        }

        return matches.OrderByDescending(m => m.Score).Take(MaxPerTurn).Select(m => m.Item).ToList();
    }
}

/// <summary>Edge trigger: a relationship crossed into a new tier this commit.</summary>
internal sealed class RelationshipTierContextContributor : IContextContributor
{
    public async Task<IEnumerable<ContextItem>> ContributeAsync(ContextTurn turn, CancellationToken ct = default)
    {
        var items = new List<ContextItem>();
        foreach (var rel in turn.AppliedChanges.OfType<RelationshipChange>())
        {
            if (!turn.RelationshipBaselines.TryGetValue((rel.CharacterId, rel.TargetId), out var before))
            {
                continue;
            }

            var character = await turn.Session.LoadAsync<Character>(rel.CharacterId, ct);
            var target = await turn.Session.LoadAsync<Character>(rel.TargetId, ct);
            var after = character?.Social?.Relationships?.GetValueOrDefault(rel.TargetId) ?? before;
            var oldTier = RelationshipModifierHelper.TierName(before);
            var newTier = RelationshipModifierHelper.TierName(after);
            if (character == null || oldTier == newTier)
            {
                continue;
            }

            items.Add(new ContextItem($"rel:{rel.CharacterId}:{rel.TargetId}:{newTier}",
                $"{character.Name} now regards {target?.Name ?? rel.TargetId} as {newTier} ({oldTier} → {newTier}, {after}).", 80));
        }

        return items;
    }
}

/// <summary>Edge trigger: an involved or present NPC's need is pressing (≥ 60), once per need per session.</summary>
internal sealed class PressingNeedContextContributor : IContextContributor
{
    private const int MaxLines = 3;

    public async Task<IEnumerable<ContextItem>> ContributeAsync(ContextTurn turn, CancellationToken ct = default)
    {
        var ids = turn.PresentNpcIds
            .Concat(turn.InvolvedEntityIds.Where(id => id.StartsWith(CanonicalId.Characters, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var characters = await turn.Session.LoadAsync<Character>(ids, ct);
        return characters.Values
            .Where(c => c is { IsPc: false })
            .SelectMany(c => (c!.Needs?.ActiveNeeds ?? [])
                .Where(kv => kv.Value >= NpcCardFactory.PressingNeedThreshold)
                .Select(kv => new ContextItem($"need:{c.Id}:{kv.Key}",
                    $"{c.Name}: {kv.Key} {Math.Round(kv.Value)} (pressing)", 40)))
            .Take(MaxLines)
            .ToList();
    }
}

/// <summary>Trade beat (an item changed hands, or gold moved): the party's gold, and the wares of any NPC
/// in the trade. Saves the includeParty the model would otherwise need to price anything.</summary>
internal sealed class TradeContextContributor : IContextContributor
{
    private const int MaxWares = 8;

    public async Task<IEnumerable<ContextItem>> ContributeAsync(ContextTurn turn, CancellationToken ct = default)
    {
        var isTrade = turn.AppliedChanges.Any(c => c is ItemTransfer
            || c is ResourceChange rc && rc.PoolName.Equals("gold", StringComparison.OrdinalIgnoreCase));
        if (!isTrade)
        {
            return [];
        }

        var items = new List<ContextItem>();
        var gold = turn.Party
            .Select(p => (p.Name, Gold: p.SystemStats?.ResourcePools?.TryGetValue("gold", out var pool) == true ? pool.Current : (int?)null))
            .Where(x => x.Gold != null)
            .ToList();
        if (gold.Count > 0)
        {
            var text = "Party gold: " + string.Join(", ", gold.Select(g => $"{g.Name} {g.Gold}"));
            items.Add(new ContextItem("gold:" + text, text, 60));
        }

        var partyIds = new HashSet<string>(turn.PartyCharacterIds, StringComparer.OrdinalIgnoreCase);
        foreach (var npc in (await ContextTurnReads.InvolvedCharactersAsync(turn, ct)).Where(c => !partyIds.Contains(c.Id)))
        {
            var wares = await turn.Session.Query<Item>()
                .Where(i => i.HolderId == npc.Id && !i.IsArchived)
                .Take(MaxWares + 1)
                .ToListAsync(ct);
            if (wares.Count == 0)
            {
                continue;
            }

            var names = wares.Take(MaxWares).Select(i => i.Quantity > 1 ? $"{i.Name} x{i.Quantity}" : i.Name).ToList();
            var text = $"{npc.Name} holds: {string.Join(", ", names)}{(wares.Count > MaxWares ? ", …" : "")}";
            items.Add(new ContextItem("wares:" + text, text, 55));
        }

        return items;
    }
}

/// <summary>Stealth beat: the passive Perception the roll has to beat, for NPCs present here.</summary>
internal sealed class StealthContextContributor : IContextContributor
{
    public async Task<IEnumerable<ContextItem>> ContributeAsync(ContextTurn turn, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(turn.PartyLocationId)
            || !turn.AppliedChanges.Any(c => ContextTurnReads.IsSkillCheck(c, "stealth")))
        {
            return [];
        }

        // Who is here right now, not who this response happened to carry a scene for.
        var npcs = await turn.Session.Query<Character>()
            .Where(c => c.CampaignName == turn.CampaignName && c.CurrentLocationId == turn.PartyLocationId
                        && !c.IsPc && !c.IsPartyCompanion)
            .Take(12)
            .ToListAsync(ct);
        var watchers = npcs
            .Select(c => $"{c.Name} {ContextTurnReads.PassivePerception(c)}")
            .ToList();
        if (watchers.Count == 0)
        {
            return [];
        }

        var text = "Passive Perception here: " + string.Join(", ", watchers);
        return [new ContextItem($"pp:{turn.PartyLocationId}:{text}", text, 70)];
    }
}

/// <summary>Search beat (Investigation/Perception): what the location itself holds, by name. Hidden things
/// are resolved by the engine (discovery), not listed here.</summary>
internal sealed class SearchContextContributor : IContextContributor
{
    private const int MaxItems = 8;

    public async Task<IEnumerable<ContextItem>> ContributeAsync(ContextTurn turn, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(turn.PartyLocationId)
            || !turn.AppliedChanges.Any(c => ContextTurnReads.IsSkillCheck(c, "investigation", "perception", "search")))
        {
            return [];
        }

        var fixtures = await turn.Session.Query<Item>()
            .Where(i => i.HolderId == turn.PartyLocationId && !i.IsArchived && !i.Hidden)
            .Take(MaxItems)
            .ToListAsync(ct);
        if (fixtures.Count == 0)
        {
            return [];
        }

        var text = "Here: " + string.Join(", ", fixtures.Select(f => $"{f.Name} [{f.Id}]"));
        return [new ContextItem("search:" + text, text, 60)];
    }
}

/// <summary>An entity tied to an open quest objective was touched: the objective's state, once.</summary>
internal sealed class QuestLinkContextContributor : IContextContributor
{
    private const int MaxLines = 2;

    public async Task<IEnumerable<ContextItem>> ContributeAsync(ContextTurn turn, CancellationToken ct = default)
    {
        var touched = new HashSet<string>(turn.InvolvedEntityIds, StringComparer.OrdinalIgnoreCase);
        if (touched.Count == 0 || turn.AppliedChanges.Any(c => c is QuestProgress))
        {
            return []; // A quest change already says what happened to the quest.
        }

        var quests = await turn.Session.Query<Quest>()
            .Where(q => q.CampaignName == turn.CampaignName && q.OverallState == QuestState.Open && !q.IsArchived)
            .Take(50)
            .ToListAsync(ct);

        var items = new List<ContextItem>();
        foreach (var quest in quests)
        {
            foreach (var objective in quest.Objectives.Where(o => o.State == QuestState.Open))
            {
                var link = (objective.InvolvedIds ?? []).FirstOrDefault(touched.Contains)
                           ?? (quest.GiverId != null && touched.Contains(quest.GiverId) ? quest.GiverId : null);
                if (link == null)
                {
                    continue;
                }

                items.Add(new ContextItem($"quest:{quest.Id}:{objective.Description}",
                    $"Quest '{quest.Title}' ({quest.Id}): open objective '{objective.Description}' involves {link}.", 55));
                break;
            }
        }

        return items.Take(MaxLines).ToList();
    }
}
