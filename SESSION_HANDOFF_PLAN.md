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

- [ ] H1. `SessionHandoff` record on `SessionLog.SessionRecord` (+ `Checkpoint` slot). `end_session` params: `handoff`, `checkpoint`; `recapText` alias. Caps and id validation → structured error.
- [ ] H2. `SessionStartView` v2: posture-only campaign, compact `PartySessionView` built in the query (projection, per CLAUDE.md, not `[JsonIgnore]`), handoff, fingerprint, filled-in scene hint.
- [ ] H3. Fallback digest when no handoff exists.
- [ ] H4. Prompts and skills: system prompt END OF SESSION block (what to write, fold `storySoFar`), checkpoint before compaction, `memoriesOnlyCharacterId` for the PC's full memory when needed. Update `end_session` description (it rides in `tools/list`; keep it ≤300 chars, detail in `lookup kind=help topic=sessions`).
- [ ] H5. Tests: handoff round-trip, caps, unknown-NPC rejection, checkpoint resume, fallback digest, `start_session` size budget (≤3.5k for 2 PCs regardless of session count), engine facts from the DB even when the handoff contradicts them.
- [ ] H6. Replay the scratch script: before/after table. Then one played session with a real model (Grok on `/play` via `scripts/tunnel.sh`) to judge handoff quality. Tests can't.
- [ ] H7. Full suite green.

## Also found

- A rolled-back `take_turn` still says `Event logged (id: …)` under "NO CHANGES WERE SAVED". The model may cite an event that doesn't exist. Suppress per-change success lines when the batch rolls back.
- `take_turn` memory beats: `source: Witnessed` requires a client `eventId` on the paired event. That's correct, but it's a common first-try failure; worth a line in the `knowledge_update` commit_schema summary.

## Measurement data policy

- Use scratch campaigns on an empty `CAMPAIGN_DB_PATH` (the harness lives in the job scratch dir; `scratch.py` seeds and plays a campaign). Don't read the user's existing campaigns for measurement.
- No MITM proxy needed. The measurements call the server's MCP endpoint directly, so the JSON-RPC bytes are exact. To see what a real client sends (does Grok re-fetch `tools/list`? how big are its requests?), the ngrok agent's inspector at `http://127.0.0.1:4040` already shows raw request and response bodies for a `tunnel.sh` session. A MITM only adds value for clients that talk to a remote HTTPS server we don't control.
