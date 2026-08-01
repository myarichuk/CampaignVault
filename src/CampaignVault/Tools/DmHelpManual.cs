namespace CampaignVault.Tools;

/// <summary>
/// Canonical DM manual, split into focused sections.
/// Large topical sections (patterns, combat, spells, world-pressure, visual-sandbox, quickstart)
/// are now delivered as push-based guidance hints on tool responses. This manual carries only
/// session-0 procedural guidance (onboarding, world-building), FAQ, and the commit type enum reference.
/// </summary>
internal static class DmHelpManual
{
    internal const string CommitEnumSection = @"# Change Type Enum Reference

When calling `take_turn`, each change in the array must specify a `$type` discriminator. Here is the complete cheat sheet of valid types and their canonical usage:

{{COMMIT_ENUM_VALUES}}

## Key Rules

- All `$type` values are strings (exact case-sensitive match).
- `characterId`, `locationId`, `questId`, etc. are required where indicated — omitting them will hard-fail the batch.
- Omitting a field that is optional means the engine preserves its current value (no blank-out).
- Some `$type`s have automatic side effects (marked in `get_commit_schema`) — do not duplicate them in a single batch (e.g., do not include both `rest` and a separate `hp` for HP recovery; `rest` auto-applies).

For more details, call `get_commit_schema` (optional category filter: Combat, Narrative, World, PlotThread).
";

    internal const string FaqSection = @"# FAQ & Laziness Traps

## Common Mistakes

**Narrating a whole new dungeon level without creates**
→ Next scene fetch on a room ID: instant hallucination pressure + exact create JSON. Paste it.

**Creating a cellar via create but forgetting the back exit**
→ Pressure on entry: ENGINE WARNING with `location_update` JSON to add the missing exit.

**Spawning 40 named sailors for one scene**
→ Bloat; use ambient + 1-2 creates only for interactables; GC cleans the rest.

**Forgetting to `activity` change after a scene**
→ The next scene view shows stale locations/activities. Update it.

**Ignoring an aging ""Unresolved"" event for 10 days**
→ Pressure in the next world-state refresh with resolution hint. Fix it.

**Not committing `knowledge_update` after a discovery**
→ NPC will forget or confabulate the facts. Set `sourceEventIds` so you can verify truth later via `recall_history`.

**Confusing `relationship` (durable opinion) with `engagement_relation` (momentary state)**
→ Relationship is ±20 per significant event; engagement is ""grappling right now"". Use the right one.

**Re-querying after every mutation**
→ Unnecessary: `take_turn`'s response already echoes fresh summaries for every touched NPC/scene (plus optional party/world-state/full-detail sections). Check its `warnings` array if an expected section came back null.

## Tips & Tricks

**Leverage `ambientCrowd`:** Don't create 15 NPCs for a tavern. Use ambientCrowd: ""8-12 rough sailors"" and promote only the one the party talks to.

**Use `schedule_change` to make transients permanent:** A transient spawned via crowd interrupt can be `schedule_change`'d to persist across `advance_world` (it now has a routine and won't auto-GC).

**Batch related mutations:** Travel + quest progress + faction shifts + activity changes in one `take_turn` ensures consistency.

**Read `Suggested Commit Examples` in pressures:** The engine gives you copy-paste JSON. Use it.

**Call `recall_history` to verify ground truth:** If you're unsure whether an NPC was present for an event, search for the event, not for the NPC's memory of it.

**Check remaining pools before spending:** Before a spell or resource-heavy action, fetch the character's full detail (get_entity, or bundled via take_turn's fullDetailCharacterId) to see available slots/pools. Spending below 0 HARD-FAILS.

**Use `recordingMode: Deliberate` + `importance: Core` for player-initiated acts:** When the party *deliberately* does something they mark as important (marking the map, making a vow, burning a bridge), set these flags so the event survives all retrieval budgets.

**Transients created with `schedule: null` + `keepAlive: false` auto-GC:** If you create an NPC for a single scene and don't want to keep them around, leave schedule unset and keepAlive false. Engine cleans up when the area goes cold.

**Location state persists:** After combat, vandalism, or major events, use `location_update` to record the state. `pointsOfInterest` evolve over time; narrate the decay realistically.

**Engage visual tags early:** Persist visual state (bloodied, disheveled, wanted) via `character_update` early so crowd interrupt and faction pressure can react naturally.

**Watch your WorldPressure — it's your co-DM:** Never ignore ENGINE WARNING or NARRATIVE PROMPT. If you see the same pressure twice, you didn't commit the fix.
";

    internal const string OnboardingSection = @"# Guided Campaign Onboarding (Session 0 Q&A)

`start_campaign_onboarding` / `submit_onboarding_answer` / `finalize_campaign_onboarding` are an OPTIONAL guided alternative to calling `create_campaign` directly. They exist to prevent hallucination when the user has NOT yet told you their setting, tone, ruleset, or plot — the tool asks one structured question at a time (system, tone, starting era/year, solo-vs-party, plot source, factions, etc.) instead of you inventing answers.

## When to use it vs going straight to `create_campaign`

**Use onboarding** when the user's opening message is short/vague (""let's start a new campaign"", ""set up a D&D game for me"") and you don't yet know the ruleset, tone, or starting point. Call `start_campaign_onboarding(campaignName)`, then `submit_onboarding_answer` for each question it returns, then `finalize_campaign_onboarding` once it reports `ready_to_build` — that call creates the locked `Campaign` doc (system, narrative focus, starting lore/year) from the collected answers. Follow it with `world_build` as usual (see `get_help topic=world-building`).

**Skip straight to `create_campaign` + `world_build`** when the user has ALREADY given you the substance in their own words — a plot outline, a PC character sheet, a named antagonist, an inciting incident, a specific ruleset. Onboarding's questions only collect abstract preference flags (system/tone/era/solo-vs-party/plot-source); it has no field for verbatim content like a full stat block or a named villain. Re-asking questions the user already answered unprompted is a laziness trap, not thoroughness — extract the ruleset/tone/era straight from what they gave you, call `create_campaign` with it, then seed the PCs/antagonist/plot via `world_build` directly. If they mentioned some things but left real gaps (e.g. gave you a plot but not a ruleset), ask only about the gaps in plain conversation, or run onboarding only for the unanswered questions — don't restart the full Q&A sequence over information already on the table.

Onboarding state is per-campaign-slug and resumable: calling `start_campaign_onboarding` again on an in-progress slug resumes where it left off instead of restarting.
";

    internal const string WorldBuildingSection = @"# Initial World-Building (Session 0)

Seeding a fresh campaign — the starting region, key NPCs, opening quest — is a one-time batch job. Use `world_build` instead of many individual entity calls: it accepts arrays for every entity kind in one atomic call (all-or-nothing — a bad entry rolls back the whole batch and tells you which one failed).

## Before you seed

0. If the user hasn't told you the ruleset/tone/setting yet, consider the guided `start_campaign_onboarding` flow first instead of guessing — see `get_help topic=onboarding` for when it's worth it vs going straight to step 1 below.
1. `create_campaign` — pass `initialSystem` (locks the ruleset immediately; bootstrap HP/AC derivation for `world_build`'s `characters[]` depends on it) and `narrativeFocus` (steers `importance` defaults on later `event` changes; update later via a `campaign_update` change in take_turn). Skip this step if `finalize_campaign_onboarding` already created the campaign.

## Recommended seeding order (matches world_build's own dispatch order)

1. **locations** — the starting hub/region first, then anywhere it links to (set `connectedFromLocationId` for auto-linked exits, or `exits` directly). Set `dangerModifier` (-50 to +50) on each one based on plausible in-fiction threat — it has no automatic/inferred value, defaults to 0 (perfectly safe) if omitted, and directly feeds the probability of `rest`/`travel`/`scene_interrupt_check` encounter rolls there. A guarded inn room might be -20; an unpatrolled wilderness saddle at night, +15 to +25.
2. **factions** — any powers already active in the region.
3. **creatures / spells / feats** — only if this campaign has homebrew content; skip otherwise.
4. **characters** — PCs first (`isPc: true`), then the handful of named NPCs the opening scene actually needs. Don't pre-create a whole cast — most NPCs should stay ambient (`ambientCrowd` on the location) until the party interacts with them.
5. **items** — starting gear, set `holderId` to the owning character. **Characters have no inline equipment fields** — a guard, soldier, crime boss, or any combat-capable NPC you seed in step 4 is unarmed/unarmored until you ALSO give them a matching `items[]` entry here in the SAME batch. It's easy to seed a rich cast of NPCs and forget this step entirely since nothing about the character record itself hints at it — `world_build` emits a non-blocking warning per newly-seeded character with no items[] entry (in this batch or already on file) specifically to catch that.
6. **quests** — the opening hook, if you have one ready.
7. **plotThreads** — DM-only scaffolding for arcs you're seeding in advance.
8. **lore** — background/history entries worth being searchable.
9. **rumors** — seed sparingly; most rumors should emerge from play, not from a pre-written list.
10. **needDescriptors** — human-readable explanations for any custom needs your NPCs track (e.g. ""homesickness"": ""Longing for home and family. High values cause distraction, poor rest, and risk of emotional outbursts.""). Merged into NPC full-detail views automatically (per-NPC descriptors override).

Forward references are fine — a quest's `giverId` pointing at a character earlier in the same batch resolves normally since `characters` dispatches before `quests`; a reference to something NOT in this batch at all just produces a non-blocking warning (create it later).

## Copy-paste example

```json
{
  ""batch"": {
    ""locations"": [
      { ""id"": ""locations/dragon-heist-yawning-portal"", ""name"": ""The Yawning Portal"", ""description"": ""A famous tavern built around a deep well leading to Undermountain."", ""type"": ""Building"", ""climateZone"": ""Temperate"", ""ambientCrowd"": ""a dozen adventurers and regulars"" }
    ],
    ""characters"": [
      { ""id"": ""chars/valen"", ""name"": ""Valen"", ""isPc"": true, ""currentLocationId"": ""locations/dragon-heist-yawning-portal"", ""systemStats"": { ""$system"": ""dnd5e"", ""hitDie"": ""d10"", ""level"": 1, ""constitution"": 14 } },
      { ""id"": ""chars/durnan"", ""name"": ""Durnan"", ""currentLocationId"": ""locations/dragon-heist-yawning-portal"", ""currentActivity"": ""Tending the bar"", ""notes"": ""Owner of the Yawning Portal, retired adventurer."" }
    ],
    ""quests"": [
      { ""id"": ""quests/find-floon"", ""title"": ""Where's Floon?"", ""giverId"": ""chars/durnan"", ""objectives"": [ { ""description"": ""Track down Floon Blagmaar's last known whereabouts"" } ] }
    ]
  },
  ""campaignName"": ""dragon-heist""
}
```

## After seeding

Call `start_session` — its world state's `seedCoverage` block reports counts (locations, PC characters, factions, open quests, active plot threads) plus a short `gaps` hint list (e.g. ""no PC characters yet"", ""starting location has no climateZone""). Use it to spot what's still missing before you start the session; the gaps shrink as you seed more.

For the full field-level schema of each entity kind, see `get_help topic=commit-enum` for enum values, or inspect the `world_build` tool's own input schema (every field mirrors what `character_update`/`location_update`/etc. accept during play).
";
}
