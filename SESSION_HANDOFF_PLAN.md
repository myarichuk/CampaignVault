# Session handoff plan

`end_session` asks the model for a structured handoff (a summary like a context compaction). `start_session` returns that handoff plus live engine facts, instead of today's growing dump. Supersedes Round 4 items 5 and 6 in `TOKEN_SURFACE_PLAN.md` for `start_session`. `get_entity` and Full `includeParty` still need the memory cap separately.

## Measured (2026-09-24, scratch campaign)

Scratch SFW campaign `harbor-lantern-2`: empty DB, 1 PC and 1 companion, 4 NPCs, 5 locations. Three sessions of 20 scripted beats: travel with `fullDetailLocationId`, memory beats, skill checks, social beats and `includeParty`. 50 of 60 beats committed; 10 hit the commit rate limit because the script outpaces any model. Worktree server, compact JSON chars.

**`start_session` grows about 2.4k per session:**

| After session | 0 | 1 | 2 | 3 |
|---|---|---|---|---|
| `start_session` | 6,321 | 9,440 | 12,125 | 13,502 |

Real long campaigns on the local DB (measured earlier, same code): 31k and 35k.

**Composition after session 3 (13.5k):**

| Part | Chars | Needed by the model? |
|---|---|---|
| PC `psychology.memories` (10 memories, ~375 each) | 3,749 | Rarely all of them. Grows every session. Fetchable on demand (`take_turn memoriesOnlyCharacterId`, works for PCs). |
| raw campaign doc: `initiativeSurfaced` 2,417 + `pressureCooldowns` 564 + ids | ~3,050 | No: engine bookkeeping. |
| party `systemStats` + `needs` (×2 members) | ~1,800 | AC, level and conditions only. Needs only when high. |
| `worldState.recentEvents` | 997 | Replaced by the handoff. |
| `worldPressure` | 463 | Yes. |
| `lastSessionRecap` | 310 | Yes, but only the *latest* session's recap survives. Sessions 1–2 are gone except as scattered memories. |

**Prototype (offline, same data):** handoff (1.0k) + posture + time + pressure + compact party + fingerprint = **2,610 chars (−81%)**, and flat: it grows with party size and the handoff cap, not with sessions played. Party entry, for example:

```json
{"id":"chars/tamsin","name":"Tamsin","hp":"21/21","ac":14,"level":3,"locationId":"locations/lighthouse",
 "conditions":["Exhaustion 1"],"equipped":["Shortsword"],"carried":["Thieves' Tools"],
 "memoryCount":10,"keyMemories":["night tide (s3)","Oda's debt (s3)","gull feathers (s2)"]}
```

The prototype showed the risk directly. The hand-written recap said the party rested at the Gull Tavern, while the engine had Tamsin at the lighthouse with Exhaustion 1. **The handoff is narrative only; engine facts (HP, location, gear, conditions) always come from the DB.**

## Design

**`end_session(campaignName, handoff)`** (`recapText` kept as a deprecated alias → `lastSession`):

| Field | Cap | Notes |
|---|---|---|
| `storySoFar` | 800 | Rolling fold, as in compaction: the model merges the previous `storySoFar` (it has it from `start_session`) with this session. Keeps sessions 1..N-1 alive at a fixed size. |
| `lastSession` | 600 | What happened this session. |
| `openThreads[]` | 6 × 120 | Unresolved hooks, in the model's words. |
| `npcsInPlay[]` | 8 × `{id, stance ≤80}` | Ids validated against the DB. An unknown id rejects with the list of close matches (no invented NPCs). |
| `partyIntent` | 200 | What the players said they'd do next. |
| `tone` | 120 | Optional; keeps the voice consistent across sessions and models. |

Over a cap: reject with the overage per field (one retry, cheap), not silent truncation.

**`end_session(..., checkpoint: true)`** stores the handoff and keeps the session open. The model calls it before its own context is compacted, or midway through a long session. A resumed `start_session` returns the newest checkpoint. No new tool, so `tools/list` doesn't grow.

**`start_session` returns:**
- `handoff`: the latest stored handoff (checkpoint or closed session).
- `campaign`: posture only (slug, name, system, PCs, focus, entry hint). The raw campaign doc is gone.
- `time`: the formatted string.
- `worldPressure`: unchanged.
- `party[]`: compact, from the DB: hp, AC, level, location, conditions, equipped and carried names, needs ≥ threshold, `memoryCount`, top-3 `keyMemories` topics.
- `partyFingerprint`, so the first `take_turn` can echo it.
- `summary`: "Load the scene: take_turn fullDetailLocationId=<PC location>". The id is filled in from the DB, and the stale "call get_entity" hint is gone.

**Fallbacks:**
- No handoff (older campaigns, or a crashed client that never called `end_session`): a server-built digest of the last N important events + the old `recapText`, capped at ~1.5k. It is never the unbounded dump.
- `start_session(..., full: true)` for a deliberate full reseed. Or keep that on `take_turn forceFullReseed`, which already exists.

## Tasks

- [x] H1. `SessionHandoff` on `SessionLog.SessionRecord` (+ `HandoffIsCheckpoint`, `HandoffWrittenAtUtc`). `end_session(campaignName, handoff, checkpoint)`; `recapText` binds as an alias for `lastSession`. Caps and NPC-id validation reject with per-field overage and close matches; nothing is saved on rejection. An omitted `storySoFar` keeps the previous fold.
- [x] H2. `SessionStartView` v2: `handoff`, posture-only `campaign` (+ narrativeFocus, systemOptions), `time`, `activeQuests`, `seedCoverage` only while gaps remain, compact `PartySessionView` (item names projected in the query), `partyFingerprint`, and a summary that names `take_turn fullDetailLocationId=<PC location>`. `start_session` also primes the turn cursor: the next `take_turn` is a full reseed (a new conversation has no delta baseline, and delta mode sends already-surfaced NPCs as stubs), and the kickoff fingerprint is stored so echoing it isn't drift.
- [x] H3. `SessionDigestBuilder`: latest legacy recap + importance-ranked recent events, hard cap 1.5k. Used when no handoff exists, or when a later session closed without one.
- [x] H4. `lookup kind=help topic=sessions`; SESSIONS block in `recommended-system-prompt.md`; STARTUP/SESSION END in the opencode prompt; README. `end_session` gets a hand-written wire schema (`McpSchemaInstaller`, 625 chars vs 2.1k reflected) so `/play` stays within its 13k budget. opencode plugin reads `campaign.slug` from the new payload.
- [x] H5. `SessionHandoffTests` (11): round-trip, caps, required lastSession, unknown NPC with suggestions, checkpoint resume and overwrite, recapText alias + storySoFar carry-over, legacy digest, DB-over-handoff engine facts, size budget (<=3.5k for 2 PCs over 5 sessions, flat within 100 chars), first take_turn Full with no false drift, rolled-back take_turn summary.
- [x] H6 (replay). Scratch replay on an empty DB, `harbor-lantern-3`, same script with handoffs: see table below.
- [ ] H6 (real model). One played session with Grok on `/play` via `scripts/tunnel.sh` to judge handoff quality. Left to Michael: tests can't judge prose.
- [x] H7. Unit suite 1,572 passed / 0 failed / 2 skipped (pre-existing skips); integration project 4 skipped (pre-existing, env-gated); opencode plugin 29/29.

**After (2026-09-24, scratch replay):**

| After session | 0 | 1 | 2 | 3 |
|---|---|---|---|---|
| `start_session` before | 6,321 | 9,440 | 12,125 | 13,502 |
| `start_session` after | 2,215 | 2,738 | 2,895 | 2,947 |

Session 3 composition: handoff 1,020, party 567 (both members), worldPressure 463, campaign 238, seedCoverage 157, time 48, fingerprint 79. The 2,738 → 2,947 creep is the script growing `storySoFar` toward its 800 cap; the worst case with every handoff field at its cap is about 3.3k of handoff plus about 1.5k of the rest.

## Also found (done)

- [x] A rolled-back `take_turn` no longer echoes the success lines of changes that validated (`Event logged (id: …)`). `WorldChangeDispatcher` drops them, keeps WARNING/ERROR lines, and adds one "rolled back with the batch" note.
- [x] `knowledge_update`'s schema summary now says `source=Witnessed/Experienced` needs `sourceEventIds` citing an `eventId` on the paired event.

## Measurement data policy

- Use scratch campaigns on an empty `CAMPAIGN_DB_PATH` (the harness lives in the job scratch dir; `scratch.py` seeds and plays a campaign). Don't read the user's existing campaigns for measurement.
- No MITM proxy needed. The measurements call the server's MCP endpoint directly, so the JSON-RPC bytes are exact. To see what a real client sends (does Grok re-fetch `tools/list`? how big are its requests?), the ngrok agent's inspector at `http://127.0.0.1:4040` already shows raw request and response bodies for a `tunnel.sh` session. A MITM only adds value for clients that talk to a remote HTTPS server we don't control.
