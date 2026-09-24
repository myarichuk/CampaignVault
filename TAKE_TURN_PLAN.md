# take_turn response plan

`take_turn` results are almost all of what a session puts into the model's context. This plan trims them without dropping anything the model acts on, and fixes six engine bugs the measurement turned up. It follows `SESSION_HANDOFF_PLAN.md` and carries on from Round 4 items 1–4 in `TOKEN_SURFACE_PLAN.md`, which now have scratch numbers.

## Is take_turn the elephant? Yes

Scratch campaign, one scripted session each (after the session handoff landed):

| Per session | Session 1 | Session 2 |
|---|---|---|
| All tool results | 96.9k chars | 116.9k chars |
| `take_turn` | **86.2k (89%)** | **106.5k (91%)** |
| `start_session` (after handoff) | 1.8k | 2.4k |
| `tools/list` `/play` (once, cacheable) | 13k | 13k |

A 33-turn session puts about 22k tokens of `take_turn` output into context, and more as the campaign ages. `start_session` is solved; `take_turn` is the remaining cost.

## Measured (2026-09-24, scratch campaign)

`scripts/measure/take_turn_replay.py` on an empty DB builds SFW `saltmarsh-bell`: 1 PC, 1 companion, 13 NPCs over 6 locations (tavern 4, harbor 3, warehouse 3). Each session is one scripted run of 33 `take_turn` calls, plus `start_session`, `combat`, `advance_world` and `end_session`. Its turns:

- 8 arrivals (travel + `fullDetailLocationId`);
- 11 conversation beats: event + mood/relationship, or `knowledge_update` on both sides;
- 2 skill checks;
- a fight: 6 attacks alternating with `combat next`;
- 2 `includeParty`;
- 1 `extraCharacterIds` look and 1 `fullDetailCharacterId`;
- `includeWorldState`, then a rest, then one more beat.

Calls are paced under the commit limiter (the old replay silently lost turns to it). Session 3 is excluded: the party split mid-run from bug F1, which corrupted the remaining turns.

**By turn kind (compact JSON chars, average):**

| Kind | n/session | S1 | S2 | Share of S1 total |
|---|---|---|---|---|
| Arrival (Full, `fullScene`) | 7–8 | 7,099 | 8,099 | 58% |
| After rest (Full reseed, `scenes[]`) | 1 | 7,085 | 7,437 | 8% |
| Scene load (first turn after `start_session`) | 1 | 4,199 | 7,497 | 5% |
| `fullDetailCharacterId` | 1 | 2,519 | 4,056 | 3% |
| Combat attack | 6 | 1,148 | 1,201 | 8% |
| Conversation beat | 11 | ~890 | ~990 | 11% |
| Skill check / `includeParty` / look / `includeWorldState` | 6 | 550–2,100 | same | 7% |

Full-scene turns are 9 of 33 calls and **71% of the chars**. Beats are already lean (~1k).

**Why arrivals are Full and fat:**
- Every arrival is Full: `DetectAndApplyReseedTriggersAsync` (`MutationTools.cs` ~L764) escalates a party location change to Full by design.
- `fullScene` ignores mode. `IncludeFullSceneDetailAsync` (~L2558) always builds the complete scene, so returning to the harbor re-sends identical cards for Oda, Fenn and Hal.

**S1 `fullScene` composition (42k over 9 turns):** NPC cards 30.2k, scene events 6.2k, location 3.8k. Per NPC card, about 1.1k:

| Field | S1 total | What it is |
|---|---|---|
| `systemStats` | 8.1k | 6 ability scores, hitDie, proficiency, passive Perception, plus engine internals `willpower`, `morale`, `temperature`, `warmthRating`, `movementModifier`. The model needs AC and level; the engine rolls. |
| `needDescriptors` | 7.2k | The same stress/fatigue definitions (~260 chars) on **every** NPC. `SceneNpcPresenceFactory` says campaign-wide text goes once into a scene legend, but it arrives per NPC (bug F4). |
| `knownNeeds` | 2.8k | Six floats like `36.04166`; zeros included. |
| `behavioralSummary` | 1.3k | Restates activity and mood ("Oda is mood: content."). |
| `keepAlive` / `isPc` / `isPartyCompanion` / `notesTruncated` | 0.5k | `false` defaults. |
| `behavioralTension` | 0.5k | `12.65999984741211`. |

**What grows between sessions (S1 → S2 `fullScene`: 42k → 60k):**
- `relevantMemories` on scene NPCs: 0 → 5.2k. About 350 chars each: float salience, default valence/urgency, sourceEventIds.
- `recentEventSummaries`: 6.2k → 10k. Each carries a UUID id, category, `involved`, `locationId` (= the scene) and importance. Each party member's travel is its own "X traveled to Harbor" event.
- `scenePressure`: 0.4k → 4.7k. A "looks vulnerable in a populated scene" SUGGESTION of about 1k **per character**, NPCs included. Each carries a canned two-change example that tags the target `"Covered in blood and road dust"`, `bloody`, `wanted`. It fired for Vess and the thugs, and for Tamsin, whose equipped Shortsword has no category (bug F5).

**Beats (~1k):**
- A novelty `Hint:` of about 230 chars per event, including engine travel events: 5.1k per session.
- `Event logged (id: …)` repeats `committedIds`.
- `npcs[]` echoes `behavioralSummary` and float tension.
- `knownCharacterIds`, `rateLimitTokensRemaining` at 49 of 50, the envelope's `"World updated with N changes and fresh state echoed."` and `tokensEst`.

## What-if (offline, same captures)

`scripts/measure/take_turn_whatif.py` applies each trim to the captured responses. Trims are applied cumulatively in table order; each row is its marginal saving.

| Lever | S1 saves | S2 saves | Accuracy risk |
|---|---|---|---|
| A1 NPC card projection: `systemStats` → AC and level (HP when known), needs → rounded ints without zeros, drop `needDescriptors` (fix the legend, F4), `behavioralSummary`, false defaults; round tension; item names only | 25.4k | 29.4k | Low. Ability scores and engine internals are never narrated; the engine rolls. |
| A2 Seen-this-session NPC stub: `{id, name, mood, seen:true}` when the card is unchanged since last sent | 2.9k | 3.5k | **Medium.** Relies on the model still having the card. Must reset on `start_session`, `forceFullReseed` and the reseed interval. |
| A3 Party companion as a stub in scenes (its state rides `party`/fingerprint) | 0.7k | 0.6k | Low |
| A4 Scene events: drop engine travel events, `"d0: summary"` strings, cap 4 | 5.7k | 9.3k | Low. Ids are fetchable via `recall_history`. |
| A5 Scene chrome: default location fields, exit descriptions, `seededNpcIds`, `lastKnownTravel` | 3.0k | 3.3k | None |
| A6 One "vulnerable" line naming everyone; the example moves to `lookup help` (plus F5) | 0 | 4.1k | Low, and an accuracy gain: the canned example is wrong for most scenes. |
| A7 `relevantMemories` → `"d0 topic: details"`, max 2 per NPC | 1.6k | 4.0k | Low–medium. The full set is behind `fullDetailCharacterId`. |
| B1 HP-only fingerprint drift → `partyDelta`, not a full scene reseed; `advance_world` returns `partyFingerprint` (F6) | 1.2k (7.1k alone) | 1.3k (7.4k alone) | None. It's a correctness fix. |
| C1 Novelty hint: not for engine events, one per turn, short; drop `Event logged` when the id is in `committedIds` | 5.1k | 5.4k | Low |
| C2 Echo noise: `npcs[]` summary and floats, `knownCharacterIds`, `rateLimitTokensRemaining` unless < 10 | 3.9k | 3.9k | None |
| C3 `partyFingerprint` → 8-char hash | 2.1k | 2.2k | Low. Deferred: the readable string is also a cheap HP/location readout. |
| D1 Envelope: drop `tokensEst` and the generic summary sentence | 2.4k | 2.5k | None (nothing reads `tokensEst`) |
| **All** | **86.2k → 32.2k (−62%)** | **106.5k → 37.0k (−65%)** | |

After all levers, arrivals are about **2.1k in both sessions** (7.1k and 8.1k before). The session 1 → 2 growth goes, and so do the after-rest reseed (7.1k → 0.4k) and scene load (7.5k → 2.0k). Beats go from about 0.9–1.2k to 0.5–0.8k. In tokens, a 33-turn session drops from about 22–27k to about 8–9k.

Not simulated: the Round 4 guidance-hint repeat (item 1; no guidance hint fired in the scratch run) and `fullDetailCharacterId` (4k, growing, one call per session here).

## Need-driven responses (beyond the trims)

Today every turn echoes state that happens to be nearby. The next step is for each turn to answer only "what does the model need for *this* beat?", plus a per-session ledger so nothing it already has gets re-sent. Pull stays available for everything else (`fullDetailCharacterId`, `includeParty`, `includeWorldState`, `recall_history`).

The order is commit, then narrate (system prompt: "Commit via take_turn, then narrate"), so anything a commit returns arrives before the prose for that beat. The catch is that the model authors the NPC's reaction (mood, knowledge, relationship) *in* the commit, so the card has to arrive one step earlier than the first commit that involves the NPC.

**Offline simulation** (`scripts/measure/take_turn_needs.py`, starting from the trims minus A2/C3). Scratch NPCs have no traits, so every card gets a synthetic ~120-char traits/wants/fears set:

| | S1 | S2 |
|---|---|---|
| Baseline | 86.2k | 106.5k |
| Trims A–D | 35.1k | 40.1k |
| N1: roster + cards on arrival, once per session | 25.0k (−71%) | 28.1k (−74%) |
| N2: roster on arrival, card on first commit involving the NPC | 25.4k | 28.1k |

Arrivals go to 1.0–1.3k and beats to 0.4–0.7k; a first-contact beat costs about +250 in N2. Totals are close because the script talks to most NPCs present. In real play, crowds are mostly never engaged, which favors N2, but N2 has the timing problem above.

**Recommended shape (N3 hybrid):**
- **Scene:** a roster line per NPC: `"Oda the Harbormaster [chars/oda]: nervous, mending nets | <note ≤80>"`. Description and events on the first visit this session only; pressure and plot threads always.
- **Cards** (traits, wants, fears, mood, relationship to the PC, AC/level, needs ≥60, top memories) are sent once per session:
  - on arrival, for *spotlight* NPCs: quest/plot-linked, active initiative, non-zero relationship with the party, or `keepAlive`;
  - on the first commit that involves any other NPC. Prompt guidance: open a first contact with an approach beat (event only) and author reactions after the card arrives.
- **Anticipated memories, not routine ones:** on a commit involving NPC X (or the PC on a knowledge beat), match the event summary against X's memories by semantic vector (`MemoryNode` has one; `EventNoveltyAdvisor` already embeds summaries). Push ≤2 memories above a similarity threshold that haven't been delivered this session. That's the "Oda remembers Tamsin asked about the ledger yesterday" moment, sent only when the topic comes up.
- **Other edge triggers, one line each and only on the crossing:**
  - relationship tier change;
  - a need crossing 60;
  - combat start (combatant AC/HP roster, once);
  - an NPC or item linked to an open quest objective.
- **Echo:** `npcs[]` only for involved NPCs whose mood or activity changed. No bystander tension echoes and no party entries (the fingerprint/`partyDelta` covers them).
- **Ledger:** keys on `TurnCursor` (`card:<id>@<hash>`, `mem:<id>`, `loc:<id>`), generalizing the existing `SurfacedMemoryHintTopicsByEntityId`. Cleared by `start_session`, `forceFullReseed` and the reseed interval. The opencode plugin should send `forceFullReseed` after it compacts, since the model loses the cards then.

Risk: medium, since a stale ledger means a model without the card. Mitigations: roster lines always name everyone, a card changes hash when the NPC changes, and the clearing triggers above. Replaces A2.

### Context contributors: generalizing beyond conversation

Conversation → psychology + memories is one case of a general rule: **the commit's change types say what the beat is about, so they say what context the next prose needs.** Build it the way pressure is built. Each `IContextContributor` watches the applied changes (and the refresh flags) and offers a small item keyed for the ledger. The turn assembles items under a budget of about 800 chars; overflow becomes one pointer line ("more: fullDetailCharacterId chars/oda"). Plugins register contributors through the SDK, as they do guidance contributors, so a crafting mode can push recipe state on `crafting_step`.

| Beat (detected from) | Pushed once, or when it changes | Not pushed |
|---|---|---|
| First contact / conversation (`event` with NPC, `mood`, `relationship`) | Card: traits, wants, fears, stance toward PC, relationship tier, faction and party standing; ≤2 topic-matched memories; rumors the NPC holds that match the topic | Stats, needs < 60 |
| Trade (`item` transfer, gold, `UseItem` on a merchant) | Party gold; the merchant's carried items (names, prices if set) | Merchant psychology beyond mood |
| Search / investigate (`SkillCheck` Investigation/Perception at a location) | Persisted details of this location, items held by the location (hidden ones on success), plot threads anchored here | Roster |
| Stealth (`SkillCheck` Stealth) | Passive Perception of present NPCs, lighting/time | Cards |
| Spell (`ruleset_action` Spell) | Caster's remaining slots, concentration | |
| Combat (`combat start`, attacks) | Combatant AC/HP/conditions roster once, turn order; after that only changes | Memories, cards |
| Knowledge / recall (`knowledge_update` on the PC, lore question) | Topic-matched PC memories and lore entries (≤2 each) | |
| Quest-linked entity touched | Objective state line | |
| Travel / arrival | Roster, first-visit description, spotlight cards, pressure | Revisit description, events |
| Rest (`advance_world`, rest) | HP/slots restored, needs crossing thresholds, overnight events | |

Mostly this *moves* chars from every turn to the turns that use them. Its value is accuracy as much as size: the trade beat gets gold without an `includeParty`, and the stealth beat gets passive Perception without a `get_entity`.

### Remove points of interest; hidden content becomes real entities

Probe (same street with and without 6 POI names): the arrival goes from **1,362 to 3,228 chars**. The names cost 130. The rest is a ~1.1k "these PoIs have no materialized details… use location_update" SUGGESTION on every arrival at that location. It lists the placeholders, includes a JSON example, and has a doubled `SUGGESTION: SUGGESTION:` prefix.

**Why remove them:**
- A POI name adds nothing the description doesn't ("a market street of vendors: fish, barrels, herbs, candles").
- It pulls the model toward the seeded names instead of answering the player ("I look for an alchemist").
- Every job a POI does already has a real entity that does it better:

| A POI stood for | Real home | Exists? |
|---|---|---|
| A place you can enter (back room, cellar, the alchemist's shop) | Child `Location` plus an exit | Yes |
| Furniture or objects you interact with (desk, notice board, well) | `Item` held by the location (`HolderId` accepts a location) | Yes |
| Contents (a key in the drawer, posters on the board) | `Item` held by that item (items hold items) | Yes |
| Secret compartment, carved glyph, scorch mark | `ItemDetail` on the fixture; its DM-only `intent` holds the DC or discovery condition | Yes |
| Changed fixture state ("tavern cleaned after the brawl") | `ItemDetail` update, or the location description | Yes |
| Hidden door or passage | Exit with `hidden` + `discoverDc`/`intent` | **No**: `LocationExit` has `lockCondition` only |
| Hidden object | Item `hidden` + `discoverDc` | **No**: `IsArchived` is soft delete, not concealment |
| Trap | A `hazard` on an exit, item or location: trigger, `detectDc`, `disarmDc`, effect as a `ruleset_action` | **No**: there's no trap concept |

The **"is there an alchemist?"** flow then needs no placeholder. The model decides from the settlement and the description (or rolls the optional existence oracle) and narrates. It creates the entity only when the party engages: a child location and an NPC if they go in, an item if they pick something up. Next visit it's there because it's real.

**Seeding hidden things: yes, with purpose, and enforced by the engine:**
- **Guidance, not quota.** `world_build` guidance (the `dnd-world-building` skill, `lookup help`) asks for each location that matters to a plot thread, quest or NPC secret: "What's hidden here, and who hid it?" Zero to two things, each tied to a reason. Random secrets everywhere are noise, and they cost seeding tokens.
- **Hidden means hidden on the wire.** Hidden exits, items, details and hazards stay out of scene payloads, which saves tokens and stops accidental leaks. On the first visit this session the model gets one DM-only line: the `intent` (e.g. "desk: false bottom, DC 15; someone searched it recently"). That's enough to foreshadow without the content.
- **The engine resolves discovery, as it does dice.** An Investigation/Perception `ruleset_action` at the location is checked against the hidden DCs. The result names what was found and un-hides it; that's the "search" row of the context-contributor table. Passive Perception on arrival catches the obvious ones. A hazard's trigger (entering, opening) fires the trap's `ruleset_action`, unless it was detected and disarmed.
- **Migration.** POIs with details become fixture items held by the location, with their details as `ItemDetail`s. Name-only POIs are dropped, or folded into the description if it's empty. `world_build` still accepts `pointsOfInterest` for older prompts and converts names to fixture items only when details come with them. Remove the POI pressure contributors, `LocationPoiMaterializer` and the POI fields on `location_update`.

**Optional: an existence oracle.** For genuinely uncertain "is there one?" questions, the model sets a likelihood (likely/even/unlikely from town size and context) and the engine rolls. This keeps the DM from always saying yes, the same honesty `ruleset_action` gives rolls. Worth a played test before building.

## Functional bugs found while measuring

- **F1 Accidental party splits on travel.** Losing a party member can be a good feature: rare, with a stated reason (dense fog, a storm, a night crossing of a marsh). Today it isn't that feature; it's an accident:
  - Each traveler rolls its own encounter (`TravelChangeHandler` → `_resolver.EvaluateAsync` per change).
  - `EncounterResolver` rolls each 6-hour bucket at the full chance, even when it is partial, so a 15-minute hop through town rolls like a 6-hour road.
  - There is no weather or visibility state, so nothing gives a split a reason.

  In 3 sessions, one member was "interrupted" twice; the other arrived alone. The model got one generic line, and from there every travel failed with "No LocationExit from <other location>".

  Fix:
  - Group same-destination travel changes in a batch into one party move: one roll, shared outcome.
  - Prorate the chance for partial buckets.
  - Make separation an explicit, rare outcome of that roll. Gate it on a reason: wilderness terrain, night, or an optional model-supplied `hazard` on travel ("dense fog"). Never on in-town exits.
  - Report it plainly: `"SEPARATED: Bram lost the party in the fog; he is at <route/origin>. Reunite by traveling there or waiting."` The lost member lands somewhere findable, not silently at the old location.
- **F2 Party travel advances the clock once per traveler.** `time.AdvanceHours(hoursTraveled)` runs per change. Verified: a 1-hour exit, PC plus companion, went 06:00 → 08:00. Fix: advance once per group (same grouping as F1).
- **F3 The companion's activity stays "Traveling" after arrival** (`NewActivity = tc.Narrative ?? "Traveling"`). Seen in every later scene card. Fix: "Arrived" / keep the prior activity.
- **F4 `needDescriptors` repeat on every NPC** instead of the scene legend `SceneNpcPresenceFactory` describes. Check how descriptors get stamped onto NPC docs (world_build defaults?) and filter out the campaign-wide keys.
- **F5 The vulnerability heuristic misfires.** `SceneVulnerabilityHeuristics.ScoreEquipment` only accepts `CoreCategory == Weapon` or a weapon tag, so an uncategorized "Shortsword" reads as unarmed. It also fires for NPCs, not just the party, and its example (`bloody`, `wanted`) is fixed text. Fix: match on item name too; party only by default; example via help.
- **F6 `advance_world` doesn't return `partyFingerprint`.** HP healed during the rest reads as drift on the next `take_turn`. That forces a full reseed, and the advisory wrongly tells the model it "may have missed or misread a prior delta". Fix: return the fingerprint and update the cursor.

## Tasks

- [x] T1 **Scene NPC card projection** (A1, F4; done: compact `stats` wire line with full stats kept in-process, needs rounded and zero-free, campaign-wide descriptors filtered, behavioralSummary dropped, tension rounded, memories capped at 2; A3 companion stub and the A7 memory string format move to T6). Build a wire view in `SceneNpcPresenceFactory` / the `NpcPresenceSummary` projection, not `[JsonIgnore]` (CLAUDE.md). `get_entity` location shares the view, so it gets smaller too. Size test: a 4-NPC arrival ≤ 2.5k.
- [x] T2 (partial) **Scene events, pressure** (A4 travel events dropped and capped at 4; A6/F5 one party-only vulnerability line, name-based weapon match, example moved to `help faq`). Still open: A5 scene chrome (exit descriptions, defaults), folded into T5b because `LocationDetailView` is reshaped there. `SceneView`, `SceneVulnerabilityPressureContributor`, `SceneVulnerabilityHeuristics`; example text moves to `lookup kind=help`.
- [x] T3 (partial) **Beat trims**: shorter novelty hints and no hint for Travel/Arrival events (C1), `tokensEst` removed (D1). Skipped: rate-limit field and summary sentence (tests read them; ~50 chars). `knownCharacterIds` stays until T6 roster lines exist. Round 4 item 1 not touched. `EventNoveltyAdvisor` (skip engine-generated events, one per turn), commit echo, `McpResponseCleaner` `tokensEst`. Also Round 4 item 1 (guidance ledger write), since that path is being touched anyway.
- [x] T4 **Reseed correctness** (B1, F6): `advance_world` returns/stores `partyFingerprint`; HP-only drift resends the party block instead of a Full reseed. `advance_world` returns and stores `partyFingerprint`; HP-only drift sends `partyDelta`, location drift stays Full.
- [x] T5 (partial) **Travel** (F1 grouping + prorated buckets, F2 one clock advance per group, F3 "Arrived"). Still open: separation as a deliberate, reasoned outcome (optional `hazard` on travel, explicit report). Party move as one group: one roll, one clock advance. Prorated buckets. Separation as a rare outcome that needs a reason (optional `hazard`), reported explicitly.
- [ ] T5b **Remove POIs** (feature work, not just trimming):
  - Startup data migration (decided: remove them from existing data, don't just ignore them). An idempotent, versioned step that runs once per campaign at startup: POIs with details → fixture items + `ItemDetail`s held by the location; name-only POIs dropped (folded into an empty description); then clears `PointsOfInterest`, `PointOfInterestDetails`, `PoisUsedByActivity`. Tested on a scratch DB only; never run against real campaign data by me.
  - `world_build` compatibility shim.
  - Remove the POI contributors, materializer and `location_update` POI fields.
  - Update prompts, skills and help.
- [ ] T5c **Hidden content and hazards:**
  - `hidden` + `discoverDc`/`intent` on exits and items; hidden items and details stay off the wire.
  - First-visit DM-only intent line.
  - Engine-resolved discovery on Investigation/Perception and passive Perception on arrival.
  - A minimal `hazard` (trigger, detect/disarm DC, effect as `ruleset_action`).
  - `world_build` guidance: 0–2 purposeful secrets per plot-relevant location.
  - Existence oracle only after a played test.
- [ ] T6 **Need-driven responses** (N3 + context contributors table, replaces A2). Delivery ledger on `TurnCursor`; roster lines; spotlight cards on arrival; first-commit briefing; semantic memory push ≤2; edge-trigger lines; echo only for changed, involved NPCs. Prompt: open a first contact with an approach beat. Plugin: `forceFullReseed` after compaction. Done after T1–T4 so the card format is settled.
- [ ] T7 Response-size budget tests like `ToolListBudgetTests`: arrival, beat, after-rest; plus the full suite green.
- [ ] T8 Rerun `scripts/measure/take_turn_replay.py` and compare against this page.
- [ ] T9 (user) One played session on `/play`: does the DM still use NPC stats, memories and pressure correctly with the lean cards?

Skipped: C3 fingerprint hash (small, and loses a readable readout).

## Harness

`scripts/measure/`:
- `take_turn_replay.py`: scratch world plus 3 scripted sessions; writes `out/`.
- `take_turn_breakdown.py <s1|s2>`: field composition.
- `take_turn_whatif.py <s1|s2>`: the lever table above.
- `take_turn_needs.py <s1|s2> [turn N1|N2]`: the need-driven simulation.

It only talks to a server you start on an empty `CAMPAIGN_DB_PATH`. Session 3 of the replay will stay unreliable until F1 is fixed.
