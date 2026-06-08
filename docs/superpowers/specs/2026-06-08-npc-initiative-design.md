# NPC Initiative & Reactive Behavior — Design Addendum

**Version:** 1.1  
**Date:** 2026-06-08  
**Status:** Approved for implementation  
**Builds on:** Phase 8.3 (memories), Phase 9 (pressure pipeline / extensibility)

**v1.1 changes:** Tunable tension weights in `CampaignConfig` with rationale; fully specified `dispositionStress` matching; `IInitiativeSuppressionStore` contract; `NeedConflictRule` ↔ read-side test case.

---

## Overview & Vision

The MCP provides **rich context** and **targeted, high-quality initiative framing** around NPC behavior. **Grok** remains responsible for rules, adjudication, and all final decisions about what NPCs actually do.

NPCs should feel reactive based on:

- Personality (traits, openness, resilience, wants, fears)
- Current needs vs activity
- Memories (witnessed, heard, traumatic, conditioned)
- Relational beats (gratitude, affection, resentment, trust toward PCs or scene entities)

This addendum refines the original initiative spec with locked decisions from design review. It **does not** add five overlapping memory pressure contributors to the global `IPressureOrchestrator`. Instead it introduces a dedicated **read-side initiative layer** attached to NPC views, with optional urgent mirroring into `WorldPressure`.

---

## Locked Decisions

| # | Topic | Decision | Rationale |
|---|-------|----------|-----------|
| 1 | Personality data | **Extend `PsychologyProfile` in place** (no separate `PersonalityProfile`) | `Wants`, `Fears`, `Memories`, `CurrentMood` already live here |
| 2 | Memory on `knowledge_update` | **Enrich auto-created nodes** + `createMemory: false` opt-out | Handler already creates/updates `MemoryNode`; work is metadata + event pipeline |
| 3 | When initiatives surface | **Next relevant read** = first of `get_scene` or `get_npc_context` | Matches DM flow: notice NPCs in scene or drill into one |
| 4 | Initiative signal shape | **Four `INpcInitiativeSignalProvider`s**, not memory subsets | Gratitude, affection, needs, trauma share relational/mnemonic drivers |
| 5 | Where initiatives appear | **View-first** (`ActiveInitiatives` on NPC views); **mirror `Urgent`+ to `WorldPressure`** | Structured context for Grok; existing cap/cooldown for emergencies |
| 6 | `BehavioralTension` | **Deterministic 0–100 score**; Grok interprets, engine does not act | Reproducible, testable; framing text is suggestive not prescriptive |
| 7 | Gratitude / gift detection (v1) | **Heuristic + structured override** | Heuristics catch lazy commits; explicit fields win when LLM provides them |
| 8 | Raven queries | **No collection LINQ on hot paths** | Use static indexes + in-memory scan of NPCs already loaded for scene/context (Phase 9 lesson) |
| 9 | Tension tuning | **Weights + valence multipliers in `CampaignConfig`** | Same extensibility pattern as Phase 9; defaults documented with rationale, not magic numbers in code |
| 10 | Disposition matching | **Deterministic token match + configurable expansions** | Substring-on-tokens (min length gate), not fuzzy LLM guesswork |
| 11 | Suppression persistence | **`IInitiativeSuppressionStore` on `Campaign.InitiativeSurfaced`** | Explicit contract before implementation; mirrors `PressureManager` + `Campaign` co-location |

---

## 1. Data Model Changes

### 1.1 `PsychologyProfile` (extended)

Merge personality into existing profile:

```csharp
public class PsychologyProfile
{
    public Dictionary<string, MemoryNode> Memories { get; set; } = [];
    public List<string> Wants { get; set; } = [];
    public List<string> Fears { get; set; } = [];
    public string? CurrentMood { get; set; }

    // Phase 10: personality (merged — no separate PersonalityProfile)
    public List<string> Traits { get; set; } = [];
    public double Openness { get; set; } = 0.5;      // 0–1, willingness to initiate
    public double Resilience { get; set; } = 0.5;    // 0–1, dampens trauma spikes
}
```

### 1.2 `MemoryNode` (enhanced)

```csharp
public class MemoryNode
{
    public string Topic { get; set; } = default!;
    public string Details { get; set; } = default!;
    public int DayAcquired { get; set; }
    public MemoryImportance Importance { get; set; } = MemoryImportance.Important;

    public MemorySource Source { get; set; } = MemorySource.Told;
    public EmotionalValence Valence { get; set; } = EmotionalValence.Neutral;
    public double Salience { get; set; } = 0.5;              // 0.0 – 1.0
    public List<string> RelatedEntityIds { get; set; } = [];
    public string? TriggerCondition { get; set; }            // optional scene hook hint
    public MemoryUrgency Urgency { get; set; } = MemoryUrgency.Normal;
}
```

**New enums:**

```csharp
public enum MemorySource { Witnessed, Heard, Told, Experienced, Trauma, Conditioned }
public enum EmotionalValence { Positive, Negative, Neutral, Traumatic }
public enum MemoryUrgency { Low, Normal, High, Urgent }
```

**Migration defaults** for existing documents:

| Field | Default |
|-------|---------|
| `Source` | `Told` |
| `Valence` | `Neutral` |
| `Salience` | `0.5` |
| `Urgency` | `Normal` |
| `RelatedEntityIds` | `[]` |

### 1.3 `KnowledgeUpdate` (extended)

```csharp
public class KnowledgeUpdate : WorldChange
{
    // existing: CharacterId, Topic, Details, Importance?

    /// <summary>Default true. Set false to update details without touching memory graph.</summary>
    public bool CreateMemory { get; set; } = true;

    // Optional structured enrichment (override inference)
    public MemorySource? Source { get; set; }
    public EmotionalValence? Valence { get; set; }
    public double? Salience { get; set; }
    public MemoryUrgency? Urgency { get; set; }
    public List<string>? RelatedEntityIds { get; set; }
}
```

When `CreateMemory == false`, handler updates no memory (future: may still log an event).

### 1.4 `EventOccurred` / emotional beats (gratitude v1 — structured override)

Add optional fields for reliable relational signals:

```csharp
public class EventOccurred : WorldChange
{
    // existing: Summary, Category, Involved?

    /// <summary>Optional. e.g. "gratitude", "affection", "betrayal", "gift_received".</summary>
    public string? EmotionalBeat { get; set; }

    /// <summary>Optional. Item, character, or location ID this beat relates to.</summary>
    public string? RelatedEntityId { get; set; }
}
```

When `EmotionalBeat` is set, `EventOccurredHandler` (or a post-handler hook) may create/enrich memories for involved NPCs.

### 1.5 Initiative view types

```csharp
public enum InitiativeDriver { Relational, Memory, Need, Disposition }

public record InitiativeCandidate(
    string Key,                    // stable suppression key, e.g. "gratitude:chars/x:item/y"
    string NpcId,
    InitiativeDriver Driver,
    MemoryUrgency Urgency,
    string FramingPrompt,          // LLM-facing suggestion, not a command
    double Weight                  // ranking within NPC (higher = more salient)
);

public record TensionBreakdown(
    float NeedStress,
    float MemoryStress,
    float RelationalStress,
    float DispositionStress
);

// Added to NpcPresenceSummary and NpcContextView
public double BehavioralTension { get; init; }                    // 0–100
public List<InitiativeCandidate> ActiveInitiatives { get; init; } = [];
public List<MemoryNode> RelevantMemories { get; init; } = [];     // top 3, pre-filtered

// NpcContextView only (deep dive)
public TensionBreakdown? TensionComponents { get; init; }
```

### 1.6 Initiative suppression state

Track consumed initiatives per campaign (similar to `PressureCooldowns`):

```csharp
// On Campaign document
public Dictionary<string, InitiativeSurfacedState> InitiativeSurfaced { get; set; } = [];

public record InitiativeSurfacedState(
    int SurfacedDay,
    string SurfacedViaTool,    // "get_scene" | "get_npc_context"
    bool Consumed = true
);
```

**Key format:** `initiative:{npcId}:{initiativeKey}`

**Semantics — "next relevant read":**

| Tool | NPCs evaluated |
|------|----------------|
| `get_scene` | All `PresentNPCs` at location |
| `get_npc_context` | Requested character only |

When an initiative surfaces for NPC X:

1. Attach to that NPC's view (`ActiveInitiatives`).
2. Record `InitiativeSurfaced[key]` as consumed.
3. Do **not** surface again until **re-armed** by a simulation event (see §4).

Initiative is **consumed on first surface**, not day-cooled. Persistent feelings (high relationship band) may **re-arm** on new positive interaction or each N days via simulation rule.

### 1.7 `IInitiativeSuppressionStore`

Persistence contract for `Campaign.InitiativeSurfaced`. Operates on the in-memory `Campaign` document already loaded by the read path; caller persists via `session.SaveChangesAsync` (same pattern as `IPressureManager.FilterAndCapAsync` writing `PressureCooldowns`).

```csharp
public interface IInitiativeSuppressionStore
{
    /// <summary>True when key exists and Consumed == true.</summary>
    bool IsConsumed(Campaign campaign, string initiativeKey);

    /// <summary>
    /// Record a surfaced initiative. Mutates campaign.InitiativeSurfaced[key].
    /// Idempotent: re-marking same key updates SurfacedDay / SurfacedViaTool only.
    /// </summary>
    void MarkConsumed(
        Campaign campaign,
        string initiativeKey,
        int surfacedDay,
        string surfacedViaTool);   // "get_scene" | "get_npc_context"

    /// <summary>
    /// Clear suppression so the initiative may surface again.
    /// Called by sim rules (re-arm) or structured beats that explicitly reset a key.
    /// No-op if key absent.
    /// </summary>
    void ReArm(Campaign campaign, string initiativeKey);

    /// <summary>
    /// Optional hygiene: remove entries older than retention window (config-driven).
    /// Does not affect keys re-armed within the window.
    /// </summary>
    void PruneStale(Campaign campaign, int currentDay, int retentionDays);
}
```

**Key format:** `initiative:{npcId}:{initiativeKey}` (provider-local `initiativeKey` is the stable suffix, e.g. `gratitude:chars/x:item/y`).

**Semantics:**

| Method | When |
|--------|------|
| `IsConsumed` | Before ranking candidates in `NpcInitiativeService` |
| `MarkConsumed` | After attaching top initiatives to the view for this read |
| `ReArm` | `RelationalRearmRule`, new `EmotionalBeat`, or explicit handler hook |
| `PruneStale` | Optional pass during `advance_world` or campaign maintenance |

Default implementation: `CampaignInitiativeSuppressionStore` (stateless singleton; all state on `Campaign`).

### 1.8 `CampaignConfig` tuning (initiative + tension)

Add to `CampaignConfig` in PR 2 (defaults below). `DefaultBehavioralTensionCalculator` reads config on every call — no compiled-in magic numbers.

```csharp
// ── BehavioralTension component weights (should sum to ~1.0; clamp if drift) ──
public float TensionWeightNeed { get; set; } = 0.30f;
public float TensionWeightMemory { get; set; } = 0.25f;
public float TensionWeightRelational { get; set; } = 0.25f;
public float TensionWeightDisposition { get; set; } = 0.20f;

// ── Memory valence multipliers (0–1, applied before resilience dampening) ──
public float TensionValencePositive { get; set; } = 0.3f;
public float TensionValenceNeutral { get; set; } = 0.5f;
public float TensionValenceNegative { get; set; } = 0.8f;
public float TensionValenceTraumatic { get; set; } = 1.0f;

// ── Need ↔ activity conflict (sim rule + tension boost) ──
public float NeedConflictThreshold { get; set; } = 70f;       // ActiveNeeds value
public float NeedConflictTensionBoost { get; set; } = 15f;    // added to needStress when flag set

// ── Disposition keyword matching ──
public int DispositionMinTokenLength { get; set; } = 3;
/// <summary>Maps fear/want phrase → scene-tag synonyms, e.g. "crowds" → ["crowded","busy","market"].</summary>
public Dictionary<string, List<string>> DispositionKeywordExpansions { get; set; } = [];

// ── Gratitude heuristic (§2.3) ──
public List<string> GratitudeHeuristicTokens { get; set; } =
    ["gift", "gave", "granted", "necklace", "reward", "favor", "saved", "rescued"];

// ── Suppression hygiene ──
public int InitiativeSuppressionRetentionDays { get; set; } = 30;
```

**Default weight rationale** (documented so testers know what to tune):

| Weight | Default | Why |
|--------|---------|-----|
| Need | 0.30 | Physiological discomfort is the most immediate, scene-visible stressor ("Barliman is exhausted *right now*"). |
| Memory | 0.25 | Salient memories drive reactions but are often latent until scene overlap triggers them. |
| Relational | 0.25 | Social bonds (gratitude, resentment, trust) are as behaviorally important as memory for initiative framing. |
| Disposition | 0.20 | Personality/fears are **contextual amplifiers** — they spike tension when the scene matches, but should not dominate baseline stress. |

If playtest feedback says "too stressed," tune **down** the dominant component in the breakdown (`get_npc_context` exposes `TensionComponents` for this). If "flat," raise `TensionWeightRelational` or `Disposition` first.

### 1.9 Need ↔ activity conflict flag (sim → read bridge)

`NeedConflictRule` writes durable state that read-side providers and tension both consume:

```csharp
// On NeedsProfile (or Character.Needs)
public bool ActivityConflictActive { get; set; }
public string? ActivityConflictNeed { get; set; }   // e.g. "tiredness", "hunger"
```

Cleared when the conflicting need drops below `NeedConflictThreshold` or `CurrentActivity` changes to a compatible activity (evaluated each sim tick).

---

## 2. Architecture

### 2.1 Read-side: `NpcInitiativeService`

**Not** a global `IPressureContributor`. Invoked from:

- `CampaignRepository.GetSceneAsync` (per present NPC)
- `CampaignTools.GetNpcContext` (single NPC)

```
NpcInitiativeService
  ├── INpcInitiativeSignalProvider[] (DI-registered)
  ├── IRelevantMemorySelector
  ├── IBehavioralTensionCalculator
  ├── IInitiativeSuppressionStore (reads/writes Campaign.InitiativeSurfaced)
  └── optional mirror: IUrgentInitiativeMirror → WorldPressureItem[]
```

Flow:

1. Build `NpcInitiativeContext` (npc, scene entities present, time, config, recent events for npc).
2. Each provider returns `InitiativeCandidate[]`.
3. Filter out suppressed keys.
4. Rank by `Weight`, take top **3 per NPC** for `ActiveInitiatives`.
5. Compute `BehavioralTension` + breakdown.
6. Select top **3** `RelevantMemories` via `IRelevantMemorySelector`.
7. Mark surfaced initiatives consumed; persist on save.
8. Mirror `Urgency >= High` candidates to `WorldPressure` (optional pass to `PressureOrchestrator` or direct append before cap).

### 2.2 Signal providers (four meaningful drivers)

| Provider | Detects | Example framing (PG-default) |
|----------|---------|------------------------------|
| **`RelationalInitiativeProvider`** | Gratitude, affection, resentment, trust toward someone present or PC | "Recently received kindness — may want to reciprocate (meal, favor, kind word)." / "Deep trust toward {name} — may seek a private moment to connect." |
| **`MemoryInitiativeProvider`** | Salient memories matching scene (location, entities present, topic overlap); routes trauma/urgent internally | "Witnessed violence here — may tense if party mentions it." |
| **`NeedActivityConflictProvider`** | High need vs `CurrentActivity` mismatch | "Exhausted but still on duty — may slip, snap, or ask for help." |
| **`DispositionInitiativeProvider`** | Traits, fears, wants vs scene tags / crowd | "Fear of crowds + busy market — may withdraw or cling to familiar face." |

**Removed as separate contributors:** `MemoryInitiativeContributor`, `ReactiveMemoryContributor`, `TraumaTriggerContributor`, `PersonalityBiasContributor` (personality **weights** scores instead).

**Personality modulation (not a provider):**

- `Openness` scales initiative `Weight` for social/relational candidates.
- `Resilience` dampens `MemoryStress` from `Valence == Traumatic` sources.

### 2.3 Gratitude & gift detection (v1 — heuristic + structured)

**Structured (preferred when present):**

- `EventOccurred.EmotionalBeat` in `{ "gratitude", "gift_received", "favor_received" }`
- `EventOccurred.RelatedEntityId` → item or giver character
- `KnowledgeUpdate` with `Valence = Positive`, `Source = Experienced`, optional `RelatedEntityIds`

**Heuristic fallback** (recent `SceneCommit` / `event` for involved NPC, last 2 in-game days):

- Summary contains tokens from `CampaignConfig.GratitudeHeuristicTokens` (case-insensitive word boundary match)
- Recent `item_transfer` where NPC is `newHolderId` and item has `Tags` or `CoreCategory` suggesting gift

Heuristic hits create a **pending relational initiative** keyed `gratitude:{npcId}:{relatedEntity}` without auto-writing a memory (unless structured beat also fired). Structured beats may auto-create a `MemoryNode`.

### 2.4 Affection / persistent feelings

Handled by **`RelationalInitiativeProvider`**, not a separate system.

| Signal | Condition | Framing (examples) |
|--------|-----------|-------------------|
| Warm attachment | `Relationships[pcId] >= 60` | "Feels warmly toward {name} — may check in, share news, offer comfort." |
| Deep bond | `Relationships[pcId] >= 80` | "Strong bond — may seek meaningful connection (conversation, shared activity)." |
| Re-arm | New positive `relationship` delta or `EmotionalBeat: affection` | Resets suppression for `affection:{npcId}:{targetId}` |

Framing is intentionally **non-prescriptive and PG-neutral**. Grok chooses tone and physicality. Engine never emits inherently NSFW prompts.

---

## 3. BehavioralTension (deterministic)

Computed by `IBehavioralTensionCalculator`. **Engine scores; Grok interprets.** All weights and valence multipliers come from `CampaignConfig` (§1.8).

```csharp
var w = config; // TensionWeight* properties
tension = clamp(0, 100,
    needStress        * w.TensionWeightNeed
  + memoryStress      * w.TensionWeightMemory
  + relationalStress  * w.TensionWeightRelational
  + dispositionStress * w.TensionWeightDisposition
);
```

If the four weights do not sum to 1.0, normalize at runtime (log once in debug) so partial retuning does not require arithmetic.

### 3.1 Component definitions

| Component | Range | Inputs |
|-----------|-------|--------|
| `needStress` | 0–100 | `max(ActiveNeeds) / 100 × 100`; if `ActivityConflictActive`, add `NeedConflictTensionBoost` (clamped to 100) |
| `memoryStress` | 0–100 | Σ (`Salience` × `valenceWeight` × 100) for top 3 relevant memories; `Traumatic` additionally × `(1 - Resilience)` |
| `relationalStress` | 0–100 | Extreme relationship values (±80+ → 40 base each, cap 100), unresolved gratitude flags (+25), recent negative `EmotionalBeat` (+20) |
| `dispositionStress` | 0–100 | Fear/want ↔ scene tag matches (§3.2); trait amplifiers |

**Valence weights:** `TensionValencePositive/Neutral/Negative/Traumatic` from config (defaults 0.3 / 0.5 / 0.8 / 1.0), applied before resilience dampening.

`TensionBreakdown` exposed on `get_npc_context` only. `get_scene` shows score without breakdown to limit payload.

### 3.2 `dispositionStress` — keyword matching (fully specified)

**Goal:** Deterministic, debuggable matching from `PsychologyProfile.Fears` / `Wants` to scene context. No LLM, no Raven queries.

**Step 1 — Build scene tag corpus** (all lowercased):

| Source | Extraction |
|--------|------------|
| `Location.VisualTags` | Each tag as-is (already short tokens, e.g. `"crowded"`, `"market"`) |
| `Location.AmbientCrowd` | Tokenize on whitespace + punctuation; drop tokens shorter than `DispositionMinTokenLength` |
| Present NPC `VisualTags` | Union of all present characters' tags |
| Present NPC names | Tokenize `Name` (same min-length filter) |

**Step 2 — Normalize fear/want phrases:**

- Each entry in `Fears` / `Wants` → lowercase, trim, tokenize to keywords (min length `DispositionMinTokenLength`).
- Apply `DispositionKeywordExpansions`: for each keyword, union configured synonym tokens into the phrase's match set.

**Step 3 — Match algorithm** (bidirectional substring on tokens):

For each fear keyword `f` and each scene token `s`:

```
match if (f.Length >= minLen && s.Contains(f)) OR (s.Length >= minLen && f.Contains(s))
```

Same for wants. **Not** fuzzy Levenshtein; **not** whole-string `AmbientCrowd` substring without tokenization (that caused false positives in early drafts).

**Step 4 — Score:**

```
fearHits  = count of distinct fears with ≥1 match
wantHits  = count of distinct wants with ≥1 match
base      = fearHits * 25 - wantHits * 10        // wants partially relieve stress
traitMult = 1.0 + (0.15 * count(Traits in {"anxious","timid","paranoid"}))
dispositionStress = clamp(0, 100, base * traitMult)
```

**Step 5 — Initiative link:** `DispositionInitiativeProvider` uses the **same** match function. Any `fearHits > 0` with `dispositionStress ≥ 20` may emit a `Disposition` driver candidate (framing references matched tag, e.g. `"crowded"`).

**Example:** Fear `"crowds"`, location tags `["market","busy"]`, expansion `"crowds" → ["crowded","busy"]` → `busy` matches → `fearHits = 1` → `dispositionStress = 25` (before trait mult).

**First-bug guard:** Unit tests must cover: exact tag match, expansion match, `AmbientCrowd` tokenization, min-length rejection (`"in"` vs `"inn"`), want-hit reduction.

---

## 4. Simulation Rules (tick-time)

Run during `advance_world`. Feed read-side via mutated state, not LLM calls.

| Rule | Order (suggested) | Behavior |
|------|-------------------|----------|
| `MemorySalienceDecayRule` | ~45 | Decay `Salience` per `Importance`; never below floor for `Core`. Bump `Urgency` when salience high but stale. |
| `NeedConflictRule` | ~36 (after needs accumulation) | If any `ActiveNeeds` value ≥ `NeedConflictThreshold` **and** `CurrentActivity` incompatible (see below), set `ActivityConflictActive` + `ActivityConflictNeed`; optional `MoodChange` when crossing threshold |

**Activity incompatibility table** (v1 static defaults; activity match = case-insensitive substring on `CurrentActivity`):

| Need | Incompatible activity keywords |
|------|-------------------------------|
| `tiredness` | `duty`, `guard`, `tend`, `serve`, `patrol`, `work` |
| `hunger` | `cook`, `serve`, `feast`, `bake` (irony / distraction) |
| `thirst` | `speak`, `perform`, `preach`, `negotiate` |

`NeedActivityConflictProvider` reads `ActivityConflictActive` (primary) and may also compute live mismatch using the same table for candidates not yet flagged by sim.
| *(optional)* `RelationalRearmRule` | ~50 | Re-arm persistent affection/grudge initiatives every N days if relationship still in band |

**No `ReactiveMemoryRule` sim rule** unless it writes concrete state (e.g. `PendingReaction` on character). Read-side reactivity stays in `MemoryInitiativeProvider`.

---

## 5. Integration with Phase 9 Pressure System

| Concern | Owner |
|---------|-------|
| Location integrity, travel stuck, dangling items | Existing `IPressureContributor`s |
| NPC initiative framing | `NpcInitiativeService` on views |
| Urgent NPC initiative | Mirror to `WorldPressure` → `IPressureManager.FilterAndCapAsync` |
| Memory decay nag (epistemic drift) | Existing `MemoryDecayPressureContributor` — **keep**; distinct from initiative framing |

Avoid duplicate text: `MemoryDecayPressureContributor` tells Grok to *distort/forget* old memories. `MemoryInitiativeProvider` tells Grok the NPC may *react in scene* to salient memory.

---

## 6. Raven / Performance

- **Scene path:** Initiative runs on `PresentNPCs` already materialized — scan `Psychology`, `Social`, `Needs` in memory.
- **Recent events for relational heuristics:** `Event_Search` static index — `CampaignName`, `DayLogged >= minDay`, filter `Involved` in memory.
- **Item gift heuristic:** `Item_Search` by `HolderId` for present NPCs (already used in faction economy pattern).
- **Do not** add `session.Query<Character>()` filters on psychology fields (auto-index risk).

---

## 7. `IRelevantMemorySelector`

Pure function/service used by initiative + views:

```csharp
public interface IRelevantMemorySelector
{
    IReadOnlyList<MemoryNode> Select(
        Character npc,
        NpcInitiativeContext ctx,
        int maxCount = 3);
}
```

**Relevance scoring:**

- Entity overlap: `RelatedEntityIds` ∩ scene present IDs
- Location match: topic/details contains location name/id
- Salience × urgency multiplier
- Recency: boost memories within last 7 days

---

## 8. DI Registration (sketch)

```csharp
builder.Services.AddSingleton<INpcInitiativeSignalProvider, RelationalInitiativeProvider>();
builder.Services.AddSingleton<INpcInitiativeSignalProvider, MemoryInitiativeProvider>();
builder.Services.AddSingleton<INpcInitiativeSignalProvider, NeedActivityConflictProvider>();
builder.Services.AddSingleton<INpcInitiativeSignalProvider, DispositionInitiativeProvider>();
builder.Services.AddSingleton<IRelevantMemorySelector, DefaultRelevantMemorySelector>();
builder.Services.AddSingleton<IBehavioralTensionCalculator, DefaultBehavioralTensionCalculator>();
builder.Services.AddSingleton<IInitiativeSuppressionStore, CampaignInitiativeSuppressionStore>();
builder.Services.AddSingleton<INpcInitiativeService, NpcInitiativeService>();
```

---

## 9. Testing

| Test | Assert |
|------|--------|
| `knowledge_update` enrichment | New fields populated; `createMemory: false` skips write |
| Gratitude heuristic | Recent gift event → relational initiative on next `get_npc_context` |
| Gratitude structured | `EmotionalBeat: gift_received` beats heuristic ambiguity |
| Suppression | Initiative surfaces once; second read same day is empty until re-arm |
| Scene vs context | `get_scene` surfaces for present NPC; `get_npc_context` for absent NPC not evaluated in scene pass |
| Urgent mirror | `Urgency=Urgent` appears in `WorldPressure` + view |
| Tension determinism | Same NPC state → same score ±0 tolerance |
| Tension config tuning | Override `TensionWeightDisposition` to 0.35 → `dispositionStress` contribution rises measurably for fear-match scene |
| Disposition token match | Fear `"crowds"` + tag `"busy"` via expansion → `dispositionStress > 0`; `"in"` alone does not match `"inn"` (min length) |
| **Need conflict sim → initiative** | NPC A: `tiredness=85`, `CurrentActivity="tending bar"` → after `advance_world`, `ActivityConflictActive=true`; `get_npc_context` has `Need` driver in `ActiveInitiatives` and `needStress` ≥ baseline + `NeedConflictTensionBoost`. NPC B: same tiredness, `CurrentActivity="resting"` → no conflict flag, no `Need` initiative, lower `needStress` |
| Affection band | Relationship 85 → relational framing; Grok-facing text is PG-neutral |
| Suppression store | `MarkConsumed` then `IsConsumed` true; `ReArm` clears; persists on `Campaign` after save/reload |
| Raven | No new auto-index names after initiative tool calls (manual/stat check in integration test) |

---

## 10. Execution Plan

### PR 1: Model + handler enrichment (low risk)
- Extend `PsychologyProfile`, `MemoryNode`, enums
- Extend `KnowledgeUpdate`, `EventOccurred` optional fields
- Update `KnowledgeUpdateHandler` inference defaults
- Migration defaults for load; Phase 8 tests updated

### PR 2: Initiative core (read-side)
- `InitiativeCandidate`, `NpcInitiativeContext`, `NpcInitiativeService`
- `IRelevantMemorySelector`, `IBehavioralTensionCalculator`, `IInitiativeSuppressionStore`
- `Campaign.InitiativeSurfaced` + `CampaignConfig` tension/disposition/gratitude properties
- Wire into `GetSceneAsync` + `GetNpcContext`

### PR 3: Signal providers
- `RelationalInitiativeProvider` (gratitude heuristic + structured + affection bands)
- `MemoryInitiativeProvider`
- `NeedActivityConflictProvider`
- `DispositionInitiativeProvider`

### PR 4: Simulation + urgent mirror
- `MemorySalienceDecayRule`, `NeedConflictRule`
- Mirror `Urgent`/`High` to `WorldPressure`
- Integration tests (LazyLlm-style scenarios)

---

## 11. Future (out of scope)

- LLM-computed tension scores (rejected for v1)
- NPC-initiated `WorldChange` auto-emission (engine acting without Grok)
- Per-NPC initiative LLM micro-calls for framing text
- Full `item_transfer` graph analytics for gift detection beyond holder heuristic

---

## Appendix: Example Grok-facing output

**`get_npc_context` for Barliman after receiving a necklace:**

```json
{
  "behavioralTension": 58,
  "tensionComponents": {
    "needStress": 12,
    "memoryStress": 18,
    "relationalStress": 22,
    "dispositionStress": 6
  },
  "activeInitiatives": [
    {
      "driver": "Relational",
      "urgency": "Normal",
      "framingPrompt": "Recently received a personal gift from the party — may want to reciprocate with hospitality, a favor, or a sincere thank-you."
    }
  ],
  "relevantMemories": [
    {
      "topic": "Party's gift",
      "details": "They gave me a silver necklace after I warned them about the road.",
      "valence": "Positive",
      "salience": 0.8
    }
  ]
}
```

Grok reads this and decides Barliman offers dinner, a room discount, or a quiet word — not the engine.