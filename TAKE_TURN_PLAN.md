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

## Functional bugs found while measuring

- **F1 Party splits on travel.** Each traveler rolls its own encounter (`TravelChangeHandler` → `_resolver.EvaluateAsync` per change). Twice in 3 sessions, even on 15-minute hops, one member was "interrupted" and the other arrived alone. The model sees one summary line; from there every travel fails with "No LocationExit from <other location>". Fix: treat all travel changes in one batch with the same destination as one group, with one roll and a shared outcome.
- **F2 Party travel advances the clock once per traveler.** `time.AdvanceHours(hoursTraveled)` runs per change. Verified: a 1-hour exit, PC plus companion, went 06:00 → 08:00. Fix: advance once per group (same grouping as F1).
- **F3 The companion's activity stays "Traveling" after arrival** (`NewActivity = tc.Narrative ?? "Traveling"`). Seen in every later scene card. Fix: "Arrived" / keep the prior activity.
- **F4 `needDescriptors` repeat on every NPC** instead of the scene legend `SceneNpcPresenceFactory` describes. Check how descriptors get stamped onto NPC docs (world_build defaults?) and filter out the campaign-wide keys.
- **F5 The vulnerability heuristic misfires.** `SceneVulnerabilityHeuristics.ScoreEquipment` only accepts `CoreCategory == Weapon` or a weapon tag, so an uncategorized "Shortsword" reads as unarmed. It also fires for NPCs, not just the party, and its example (`bloody`, `wanted`) is fixed text. Fix: match on item name too; party only by default; example via help.
- **F6 `advance_world` doesn't return `partyFingerprint`.** HP healed during the rest reads as drift on the next `take_turn`. That forces a full reseed, and the advisory wrongly tells the model it "may have missed or misread a prior delta". Fix: return the fingerprint and update the cursor.

## Tasks

- [ ] T1 **Scene NPC card projection** (A1, A3, A7, F4). Build a wire view in `SceneNpcPresenceFactory` / the `NpcPresenceSummary` projection, not `[JsonIgnore]` (CLAUDE.md). `get_entity` location shares the view, so it gets smaller too. Size test: a 4-NPC arrival ≤ 2.5k.
- [ ] T2 **Scene chrome, events, pressure** (A4, A5, A6, F5). `SceneView`, `SceneVulnerabilityPressureContributor`, `SceneVulnerabilityHeuristics`; example text moves to `lookup kind=help`.
- [ ] T3 **Beat trims** (C1, C2, D1). `EventNoveltyAdvisor` (skip engine-generated events, one per turn), commit echo, `McpResponseCleaner` `tokensEst`. Also Round 4 item 1 (guidance ledger write), since that path is being touched anyway.
- [ ] T4 **Reseed correctness** (B1, F6). `advance_world` returns and stores `partyFingerprint`; HP-only drift sends `partyDelta`, location drift stays Full.
- [ ] T5 **Travel bugs** (F1, F2, F3). Group same-destination travel changes in one batch.
- [ ] T6 **Seen-NPC stubs** (A2). Card hashes on `TurnCursor`, cleared by `start_session` (`PrimeTurnCursorAsync`), `forceFullReseed` and the reseed interval. Last, because it's the only lever with medium risk and the smallest saving after T1.
- [ ] T7 Response-size budget tests like `ToolListBudgetTests`: arrival, beat, after-rest; plus the full suite green.
- [ ] T8 Rerun `scripts/measure/take_turn_replay.py` and compare against this page.
- [ ] T9 (user) One played session on `/play`: does the DM still use NPC stats, memories and pressure correctly with the lean cards?

Skipped: C3 fingerprint hash (small, and loses a readable readout).

## Harness

`scripts/measure/`:
- `take_turn_replay.py`: scratch world plus 3 scripted sessions; writes `out/`.
- `take_turn_breakdown.py <s1|s2>`: field composition.
- `take_turn_whatif.py <s1|s2>`: the lever table above.

It only talks to a server you start on an empty `CAMPAIGN_DB_PATH`. Session 3 of the replay will stay unreliable until F1 is fixed.
