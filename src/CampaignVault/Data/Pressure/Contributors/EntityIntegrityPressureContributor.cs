using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

/// <summary>
/// Startup integrity scan + guard-and-hint self-healing (bug_fixes_plan P0-3).
///
/// Legacy/malformed docs can carry null profile objects, null collections, blank names, null
/// memory topics, or dangling location refs that read-side guards (P0-1) paper over. This
/// contributor pairs with those guards: beat 1 surfaces an ENGINE warning with an exact fix,
/// beat 2 the client commits it. Cases where the engine would have to invent information
/// (identity, intent) are nudge-only and never auto-fixed.
///
/// Split (mirrors the plan):
/// - Auto-normalize, silently (no warning): null → []/new() for VisualTags, Psychology, Social,
///   Needs, SystemStats, Wants/Fears/Traits, Relationships, Memories (+ null entries/Details
///   inside Memories). Zero information invented; RavenDB session tracking persists on the
///   caller's existing save.
/// - Nudge-only (EngineWarning + fix JSON where a commit shape exists): null/blank character
///   Name (character_update cannot rename — points at world_build upsert, no SuggestedCommitJson),
///   null/blank memory Topic (knowledge_update fix), dangling CurrentLocationId (travel fix when
///   the requested location resolves, else world_build-or-travel guidance), null/blank location
///   Name on anchored locations (location_update fix).
///
/// Scope.World + convention registration (ConventionRegistration.RegisterCollection) means it
/// fires on both start_session and take_turn includeWorldState:true with no extra wiring —
/// same as IncompleteSystemStatsPressureContributor / CharacterDistressPressureContributor.
/// </summary>
public sealed class EntityIntegrityPressureContributor : IPressureContributor
{
    public const string CharacterNameGroupingKey = "Character:Integrity:Name";
    public const string CharacterMemoryTopicGroupingKey = "Character:Integrity:MemoryTopic";
    public const string CharacterLocationGroupingKey = "Character:Integrity:Location";
    public const string LocationNameGroupingKey = "Location:Integrity:Name";

    public PressureScope Scope => PressureScope.World;
    public int Order => 12;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var combatants = await PressureQueryHelper.QueryCombatantCharactersAsync(ctx.Session, ctx.CampaignName, 100, ct);
        var keepAlive = await PressureQueryHelper.QueryKeepAliveCharactersAsync(ctx.Session, ctx.CampaignName, 100, ct);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Character>();
        foreach (var c in combatants.Concat(keepAlive))
        {
            if (c is not null && !string.IsNullOrEmpty(c.Id) && seen.Add(c.Id))
            {
                candidates.Add(c);
            }
        }

        // Party always: PCs/companions are usually inside the sweeps above, but an explicit batch
        // load closes the stale-index gap for zero discovery cost (IDs already known).
        if (ctx.PartyCharacterIds is { Count: > 0 })
        {
            var partyIds = ctx.PartyCharacterIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (partyIds.Count > 0)
            {
                var partyDocs = await ctx.Session.LoadAsync<Character>(partyIds, ct);
                foreach (var kvp in partyDocs)
                {
                    if (kvp.Value is not null && !string.IsNullOrEmpty(kvp.Value.Id) && seen.Add(kvp.Value.Id))
                    {
                        candidates.Add(kvp.Value);
                    }
                }
            }
        }

        // Silent auto-normalization first: null → []/new() invents no information.
        foreach (var c in candidates)
        {
            NormalizeCharacter(c);
        }

        // Batch-load every anchored location once (characters' positions + requested location).
        var locationIds = candidates
            .Select(c => c.CurrentLocationId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(ctx.RequestedLocationId)
            && !locationIds.Contains(ctx.RequestedLocationId, StringComparer.OrdinalIgnoreCase))
        {
            locationIds.Add(ctx.RequestedLocationId);
        }

        var locations = new Dictionary<string, Location>(StringComparer.OrdinalIgnoreCase);
        if (locationIds.Count > 0)
        {
            var loaded = await ctx.Session.LoadAsync<Location>(locationIds, ct);
            foreach (var kvp in loaded)
            {
                if (kvp.Value is not null)
                {
                    locations[kvp.Key] = kvp.Value;
                }
            }
        }

        var pressures = new List<WorldPressureItem>();

        foreach (var c in candidates)
        {
            EvaluateCharacter(c, ctx, locations, pressures);
        }

        // Per-location checks cover anchored locations only (referenced above) — never a full scan.
        foreach (var loc in locations.Values)
        {
            EvaluateLocation(loc, pressures);
        }

        return pressures;
    }

    internal static void NormalizeCharacter(Character c)
    {
        c.VisualTags ??= [];
        if (c.VisualTags.Count > 0)
        {
            c.VisualTags.RemoveAll(string.IsNullOrWhiteSpace);
        }

        c.Psychology ??= new PsychologyProfile();
        c.Psychology.Wants ??= [];
        c.Psychology.Fears ??= [];
        c.Psychology.Traits ??= [];
        c.Psychology.Memories ??= [];

        c.Social ??= new SocialProfile();
        c.Social.Relationships ??= [];
        c.Social.FactionReputations ??= [];

        c.Needs ??= new NeedsProfile();
        c.Needs.ActiveNeeds ??= [];
        c.Needs.NeedDescriptors ??= [];
        c.Needs.AccumulationRates ??= [];

        c.SystemStats ??= new SystemExtension();

        if (c.Psychology.Memories.Count > 0)
        {
            // Null-valued entries carry zero information — drop them; null Details reads as "".
            var nullKeys = c.Psychology.Memories
                .Where(kvp => kvp.Value is null)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in nullKeys)
            {
                c.Psychology.Memories.Remove(key);
            }

            foreach (var node in c.Psychology.Memories.Values)
            {
                node.Details ??= string.Empty;
            }
        }
    }

    private static void EvaluateCharacter(
        Character c,
        PressureContext ctx,
        Dictionary<string, Location> locations,
        List<WorldPressureItem> pressures)
    {
        var hasName = !string.IsNullOrWhiteSpace(c.Name);
        var label = hasName ? c.Name : $"(unnamed character '{c.Id}')";

        // Nudge-only: the engine must never invent identity. character_update cannot rename,
        // so there is no commit-shaped fix — point at world_build upsert instead.
        if (!hasName)
        {
            pressures.Add(new WorldPressureItem(
                PressureSeverity.EngineWarning,
                c.Id,
                $"[ENGINE] Character '{c.Id}' has a null/blank Name. The engine must not invent identity — set the real name via world_build characters[] upsert ({{ \"id\": \"{c.Id}\", \"name\": \"ACTUAL NAME HERE\" }}). character_update cannot rename.",
                CharacterNameGroupingKey));
        }

        if (c.Psychology?.Memories != null)
        {
            foreach (var kvp in c.Psychology.Memories)
            {
                var node = kvp.Value;
                if (node is null || !string.IsNullOrWhiteSpace(node.Topic))
                {
                    continue;
                }

                // Nudge-only: Topic is the memory's identity. When the dict key is usable it
                // doubles as the repair topic; otherwise only world_build can rebuild the entry.
                string? fix = null;
                string repair;
                if (!string.IsNullOrWhiteSpace(kvp.Key))
                {
                    fix = "[ { \"$type\": \"knowledge_update\", \"characterId\": \"" + c.Id
                        + "\", \"topic\": \"" + Escape(kvp.Key)
                        + "\", \"details\": \"" + Escape(node.Details ?? string.Empty) + "\" } ]";
                    repair = "Repair with the real topic via knowledge_update: " + fix;
                }
                else
                {
                    repair = "Neither the memory key nor Topic identifies this memory — repair via world_build characters[] upsert with a corrected psychology.memories entry.";
                }

                pressures.Add(new WorldPressureItem(
                    PressureSeverity.EngineWarning,
                    c.Id,
                    $"[ENGINE] {label} has a memory with null/blank Topic (memory key '{kvp.Key}'). Topic is the memory's identity — the engine must not invent it. {repair}",
                    CharacterMemoryTopicGroupingKey)
                {
                    EntityName = hasName ? c.Name : null,
                    SuggestedCommitJson = fix,
                });
            }
        }

        // Nudge-only: only travel (or creating the missing location) can re-anchor them.
        if (!string.IsNullOrWhiteSpace(c.CurrentLocationId) && !locations.ContainsKey(c.CurrentLocationId))
        {
            string? fix = null;
            var repair = $"Either world_build the missing location '{c.CurrentLocationId}' first, or move {label} somewhere real.";
            if (!string.IsNullOrWhiteSpace(ctx.RequestedLocationId) && locations.ContainsKey(ctx.RequestedLocationId))
            {
                fix = "[ { \"$type\": \"travel\", \"characterId\": \"" + c.Id
                    + "\", \"destinationLocationId\": \"" + ctx.RequestedLocationId
                    + "\", \"narrative\": \"They arrive at the party's current location.\" } ]";
                repair = "Move them to the party's current location: " + fix;
            }

            pressures.Add(new WorldPressureItem(
                PressureSeverity.EngineWarning,
                c.Id,
                $"[ENGINE] {label} has a dangling CurrentLocationId '{c.CurrentLocationId}' (no such location exists). {repair}",
                CharacterLocationGroupingKey)
            {
                EntityName = hasName ? c.Name : null,
                SuggestedCommitJson = fix,
            });
        }
    }

    private static void EvaluateLocation(Location loc, List<WorldPressureItem> pressures)
    {
        loc.VisualTags ??= [];
        if (loc.VisualTags.Count > 0)
        {
            loc.VisualTags.RemoveAll(string.IsNullOrWhiteSpace);
        }

        if (!string.IsNullOrWhiteSpace(loc.Name))
        {
            return;
        }

        // Nudge-only: placeholder "..." follows the LocationHallucinationPressureContributor
        // convention — the caller must replace it with the real name, never commit it verbatim.
        var fix = "[ { \"$type\": \"location_update\", \"locationId\": \"" + loc.Id + "\", \"name\": \"...\" } ]";
        pressures.Add(new WorldPressureItem(
            PressureSeverity.EngineWarning,
            loc.Id,
            $"[ENGINE] Location '{loc.Id}' has a null/blank Name. Set the real name via location_update (replace ... below, never commit it verbatim): {fix}",
            LocationNameGroupingKey)
        {
            SuggestedCommitJson = fix,
        });
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
}
