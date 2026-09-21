# Semantic Memory Triggering — Implementation Plan

Groomed plan. Goal: NPC memories surface when the *current beat's action or conversation*
semantically resembles something they remember — not just when a location/entity name string-matches
or an LLM-authored `TriggerCondition` substring hits. Example: an ex-indentured-servant NPC watches a
horse get whipped → their corporal-punishment memory should be eligible to surface, even though
nothing in that memory's text says "horse" or "whip".

Design decisions already made (with user, across this conversation):
- Trigger sources: both recent conversation topics AND action/narration descriptions.
- Integration point: additive scoring term, not a replacement of the existing substring/salience logic.
- Embedding timing: at write time (`knowledge_update` commit), mirroring the existing
  `IHasSemanticVector` pattern used by `Event`/`Character`/`Item`/etc.
- Vectors must never reach `get_entity`/`take_turn` responses — verified the existing
  `[JsonIgnore]` + `McpResponseCleaner` generic key-strip already covers nested dict values, no new
  leak-prevention work needed, just follow the existing pattern exactly.
- No RavenDB vector index (Corax) needed. The comparison is always "one current-turn trigger vector
  vs. one already-in-memory NPC's ~5-20 memories" — never an unbounded corpus scan — so Corax
  (used by `Event_Search`/`Lore_Search`/etc. for `recall_history`/`search_world`'s genuinely unbounded
  searches) doesn't apply here and would add latency/staleness for no benefit.
- Backfill runs at startup alongside the existing `SemanticVectorBootstrap`, not as a manual step.

**Critical finding from research (changes the naive design):** `NpcInitiativeContext.RecentEvents` is
populated from an indexed RavenDB query and is NOT reliably fresh for the very turn that just
committed an event — in fact at the `take_turn` call site
(`MutationTools.SelectAndEnrichInitiativeAsync` → `CampaignRepository.EnrichNpcInitiativeAsync`,
`MutationTools.cs:1057-1058`) `recentEvents` isn't even passed (defaults to `null` → empty list). So
relying on `RecentEvents` would silently miss the flagship "watches it happen right now" case. Fix:
embed the current turn's own committed narrative text once per `take_turn` call (from
`ctx.AppliedChanges.OfType<EventOccurred>().Select(e => e.Summary)` + `ctx.Request?.Narrative`,
already in memory, zero extra DB reads) and thread that single vector through
`NpcInitiativeContext.TriggerVector`. This also transparently covers NPC-NPc conversation content,
since `ConversationInvolvedResolver` already auto-tags any `event` with `Category: Conversation` +
`involved: [...]` — no PC required.

---

## Phase 1 — `MemoryNode` implements `IHasSemanticVector`

**File:** `src/CampaignVault/Models/Character.cs:181-214` (the `MemoryNode` class)

Current:
```csharp
public class MemoryNode
{
    public string Topic { get; set; } = null!;
    public string Details { get; set; } = null!;
    ...
```

Desired — add fields + interface, following the exact pattern already used by `Character` itself
(`Character.cs:3-12`):
```csharp
public class MemoryNode : IHasSemanticVector
{
    public string Topic { get; set; } = null!;
    public string Details { get; set; } = null!;

    [System.Text.Json.Serialization.JsonIgnore]
    public float[]? SemanticVector { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText() => $"{Topic}\n{Details}";

    ... (rest unchanged)
```

**Done when:** builds clean; `MemoryNode` satisfies `IHasSemanticVector`.

---

## Phase 2 — Embed on write (`knowledge_update`)

**File:** `src/CampaignVault/Data/ChangeHandlers/CharacterChangeHandlers.cs`

- Change the class declaration at line 672 from
  `public class KnowledgeUpdateHandler : IWorldChangeHandler`
  to a primary-constructor DI pattern identical to `ItemUpdateHandler`
  (`src/CampaignVault/Data/ChangeHandlers/ItemChangeHandlers.cs:8`):
  ```csharp
  public class KnowledgeUpdateHandler(ILocalEmbeddingService embeddingService) : IWorldChangeHandler
  ```
  No DI registration changes needed — `AutofacModules/ConventionRegistration.cs:84-88` assembly-scans
  every `IWorldChangeHandler` and registers it `.AsSelf().As<IWorldChangeHandler>()` automatically.

- In `ApplyAsync` (line 676), add the enrich call **after** the existing Witnessed/Experienced
  `sourceEventIds` validation block (currently the last check before `return ChangeHandlerResult.Ok;`
  at line 743-753) — so a batch that's about to fail validation and roll back doesn't pay for an
  embedding first:
  ```csharp
  ApplyEnrichment(memory, ku, isNew);

  if ((memory.Source is MemorySource.Witnessed or MemorySource.Experienced)
      && (memory.SourceEventIds == null || memory.SourceEventIds.Count == 0))
  {
      return ChangeHandlerResult.Failure(...); // existing, unchanged
  }

  await SemanticEnrichmentHelper.EnrichAsync(memory, embeddingService, context.Logger, ct);

  return ChangeHandlerResult.Ok;
  ```
  Mirrors the exact call shape already used at `ItemChangeHandlers.cs:175-176`.

**Done when:** creating/updating a memory via `knowledge_update` populates `MemoryNode.SemanticVector`
in RavenDB (verify via a direct RavenDB read in a test, never via a tool response — the field is
`[JsonIgnore]`d).

---

## Phase 3 — Thread this turn's trigger text into `NpcInitiativeContext`

**File:** `src/CampaignVault/Data/Initiative/NpcInitiativeContext.cs` (currently 16 lines)

Add one property:
```csharp
public float[]? TriggerVector { get; init; }
```

**File:** `src/CampaignVault/Data/CampaignRepository.cs`

- `EnrichNpcInitiativeAsync` (line 495-502): add an optional parameter `float[]? triggerVector = null`
  and set it on the `NpcInitiativeContext` constructed at line 528-542 (`TriggerVector = triggerVector`).
- Add a small wrapper method near the existing `EnrichSemanticVectorAsync` (line 72-73) so callers
  outside `CampaignRepository` don't need `ILocalEmbeddingService` injected directly:
  ```csharp
  public Task<float[]> EmbedTriggerTextAsync(string text, CancellationToken ct = default)
      => _embeddingService.GenerateEmbeddingAsync(text, ct);
  ```

**File:** `src/CampaignVault/Tools/MutationTools.cs`

- In `SelectAndEnrichInitiativeAsync` (starts line 955 as of `5befec8`, drifted +1 from a prior
  doc-string trim earlier in the file — reverify before editing), before the per-NPC loop that calls
  `EnrichNpcInitiativeAsync` (line 1058), compute the trigger text once:
  ```csharp
  var triggerText = string.Join("\n",
      ctx.AppliedChanges.OfType<EventOccurred>().Select(e => e.Summary)
          .Concat(ctx.Request?.Narrative is { } n ? [n] : []));

  float[]? triggerVector = null;
  if (!string.IsNullOrWhiteSpace(triggerText))
  {
      triggerVector = await _repository.EmbedTriggerTextAsync(triggerText);
  }
  ```
  Pass `triggerVector` into every `EnrichNpcInitiativeAsync(...)` call in the loop (line 1057-1058):
  ```csharp
  var enrichment = await _repository.EnrichNpcInitiativeAsync(
      ctx.Session, npc, ctx.Campaign, "take_turn", includeTensionBreakdown: false,
      triggerVector: triggerVector);
  ```
  One embedding call per `take_turn`, reused across every NPC candidate in the loop — not
  one-per-NPC.

- `SceneNpcPresenceFactory.cs:34-48` (the `get_scene` construction site) is a pure-query path with no
  "just committed" text available — leave `TriggerVector` unset there (defaults to `null`, scoring
  degrades gracefully to the existing `RecentEvents`/`NpcRecentEvents` vectors, see Phase 4). Explicit
  non-goal for this phase; note it in a comment so it isn't mistaken for an oversight later.

**Done when:** a `take_turn` call that commits an `EventOccurred` produces a non-null
`ctx.TriggerVector`-equivalent reaching `NpcInitiativeContext` for every NPC considered that turn.

---

## Phase 4 — Semantic term in scoring (gate + ranking), keyword-first ordering, and a GM-facing "why" hint

**Trauma-trigger framing (folded in here, not a separate phase, and more already exists than
originally assumed):** re-reading `MemoryInitiativeProvider.cs` against `HEAD` (post `0746a19`/
`5befec8`) shows Traumatic-valence handling is **already implemented**, not just implied by adjacent
fields:
- `GetCandidates` (lines 37-47) already floors `Urgency` to `High` and adds a flat `+20` initiative
  weight whenever `memory.Valence == EmotionalValence.Traumatic` — a distinct enum value from
  `Negative` (see `Models/Character.cs:163-169`), not a proxy for it.
- `BuildFraming` (lines 119-134) already emits differentiated phrasing per valence: Traumatic →
  `"Painful memory tied to {location} — may tense, withdraw, or react sharply..."`, Negative →
  `"Unsettling memory about \"{topic}\" — may become guarded..."`, else a generic "salient memory"
  line. **This is the exact mechanism that becomes the GM-visible reason text** — see the "why" hint
  below; do not build a parallel surface for it.
- `KnowledgeUpdateHandler.InferDefaultsFromDetails` (`CharacterChangeHandlers.cs`) already infers
  `Source: Trauma` + `Valence: Traumatic` + `Urgency: High` + `Salience: 0.85` from passive-mode text
  containing "trauma"/"traumatic"/"nightmare"/"ptsd" — unaffected by `5befec8`'s inference cleanup
  (that commit only removed the Witnessed/Heard/Experienced text-sniffing, not the Trauma one).

So "trauma" needs **zero new fields and zero new gate/scoring logic** — it's fully live today for the
entity/location/`TriggerCondition`-match path. What this phase actually adds is: (a) a **semantic**
match path so a mocking-by-orcs memory also fires on "greenskin" or "tusked brute" (paraphrases a
literal `TriggerCondition` keyword would miss), with the existing keyword check running first as the
cheap/precise fast path; and (b) extending the same `BuildFraming` output — for the semantic-match
case specifically, since entity/location/keyword hits already read naturally without an id — to name
*which* memory and source event fired it, per the "why" hint below.

**Write-time note (from `5befec8`):** `KnowledgeUpdateHandler` now requires `ku.SourceEventIds`
whenever `ku.Source` is `Witnessed` or `Experienced` (checked against the incoming request, not the
merged memory, and no longer inferred from phrases like "saw"/"survived"). That means memories with
those two sources are now reliably carrying a `SourceEventIds` value by the time this phase's gate
runs — good for the "why" hint's source-event-id half. `Trauma`-sourced memories are **not** covered
by that enforcement, so `SourceEventIds` may still be empty there; the hint stays conditional on it
being present, as already written below.

**File:** `src/CampaignVault/Data/Initiative/MemoryInitiativeProvider.cs` (the boolean gate — decides
whether a memory becomes an initiative candidate at all, i.e. whether the NPC *acts* on it)

Add a semantic check inside `MemoryMatchesScene` (lines 62-117), **after** the existing
entity/location/`TriggerCondition` checks (keyword match stays first — it's near-free and more
precise when the author bothered to write one; only fall through to the embedding comparison if
nothing cheap matched). Change the return type from `bool` to a small result so the caller can report
*what* matched, not just *that* something did:
```csharp
private const double SemanticMatchThreshold = 0.55; // calibrate against real data before merging —
    // see ItemDetailSemanticMatchThreshold (0.86) in ItemChangeHandlers.cs and
    // EventNoveltyAdvisor's novelty/echo cutoffs as reference points from the same embedding model.

private readonly record struct MemoryMatchResult(bool Matched, string? MatchReason);

private static MemoryMatchResult MemoryMatchesScene(MemoryNode memory, ..., float[]? triggerVector,
    IReadOnlyList<Event> recentEvents, IReadOnlyList<Event> npcRecentEvents)
{
    ... existing entity/location checks ...
    // existing TriggerCondition substring check stays exactly where it is (cheap, precise, first):
    if (...TriggerCondition substring hit...)
    {
        return new MemoryMatchResult(true, $"trigger phrase \"{memory.TriggerCondition}\" matched");
    }

    if (memory.SemanticVector is { Length: > 0 } memVec)
    {
        if (triggerVector is { Length: > 0 }
            && SemanticEnrichmentHelper.CosineSimilarity(memVec, triggerVector) >= SemanticMatchThreshold)
        {
            return new MemoryMatchResult(true, $"reminded of \"{memory.Topic}\"");
        }

        var bestEvent = recentEvents.Concat(npcRecentEvents)
            .Where(e => e.SemanticVector is { Length: > 0 })
            .Select(e => (Event: e, Sim: SemanticEnrichmentHelper.CosineSimilarity(memVec, e.SemanticVector!)))
            .OrderByDescending(x => x.Sim)
            .FirstOrDefault();
        if (bestEvent.Event != null && bestEvent.Sim >= SemanticMatchThreshold)
        {
            return new MemoryMatchResult(true, $"reminded of \"{memory.Topic}\" by {bestEvent.Event.Id}");
        }
    }

    return new MemoryMatchResult(false, null);
}
```
`GetCandidates` (line 7-60) needs to pass `ctx.TriggerVector`, `ctx.RecentEvents`, `ctx.NpcRecentEvents`
into `MemoryMatchesScene` (currently only passes presence/location args), and thread `MatchReason`
through into whatever candidate DTO it builds (e.g. add `MatchedMemoryId`/`MatchReason` fields
alongside the existing candidate fields) so it survives into the initiative signal.

**GM-facing hint — exact chain (verified against `HEAD`):** `MemoryInitiativeProvider.GetCandidates`
line 49 calls `BuildFraming(memory, ctx.Location?.Name)` → result becomes `InitiativeCandidate
.FramingPrompt` (`Models/InitiativeTypes.cs:17`) → the winning candidate's `FramingPrompt` becomes
`TurnIntentSignal`'s reason in `NpcInitiativeService.cs:46`
(`new TurnIntentSignal("npc", topCandidate.FramingPrompt, topCandidate.Urgency)`). This is the one
and only path to the GM-visible `TurnIntent` for scheduler-originated (non-nudge) wins — no separate
"call site that sets reason" to hunt for.

Extend `BuildFraming`'s signature to also take the `MemoryMatchResult` (or just the semantic-match
flag + matched event, since keyword/entity/location hits don't need it) so that **only when the win
came from the new semantic path**, it appends the memory id and, if present, `SourceEventIds`, e.g.
`"Painful memory tied to the market — may tense, withdraw, or react sharply if it comes up. (memory
chars/npc-1/mem/orc-mockery, source event evt-042)"`. Entity/location/`TriggerCondition` wins keep
their existing phrasing unchanged — they're already self-explanatory without an id. This reuses the
existing `TurnIntent`/reason surface — no new response field, no vector exposure risk (only the
memory's `Topic`/id/event-id strings travel, never `SemanticVector` itself). Note `npc_initiative_nudge`
-originated wins are a separate path (`MutationTools.cs:1068`, `FramingPrompt: nudgeReason`, LLM-
authored) and are out of scope here — they already carry their own GM-authored reason.

**File:** `src/CampaignVault/Data/Initiative/DefaultRelevantMemorySelector.cs` (the ranking term — which
memories get surfaced into `CompressedMemories`/NPC summary, separate from whether they trigger
initiative)

Add a smaller **additive** term (not a gate) to `ScoreMemory` (lines 34-79), same fallback pattern
(compute best similarity against `TriggerVector` and `ctx.RecentEvents`/`NpcRecentEvents`, skip
silently if either side has no vector yet):
```csharp
if (memory.SemanticVector is { Length: > 0 } memVec)
{
    var bestSim = BestSimilarity(memVec, ctx); // shared helper, see below
    if (bestSim >= SemanticMatchThreshold)
    {
        score += 0.2; // deliberately smaller than the +0.35 present-entity bonus — semantic
                       // match is corroborating evidence, not as strong a signal as "this NPC
                       // this memory is literally about is standing right here"
    }
}
```
Factor the "best similarity across TriggerVector + RecentEvents + NpcRecentEvents" computation into
one shared static helper (e.g. `SemanticTriggerMatcher.BestSimilarity(...)` in a new small file
`src/CampaignVault/Data/Initiative/SemanticTriggerMatcher.cs`) so `MemoryInitiativeProvider` and
`DefaultRelevantMemorySelector` don't duplicate the same fallback-chain logic — mirrors how both
files already independently duplicate the "Details is typed non-nullable" null-guard comment today
(worth fixing both at once rather than adding a third copy).

**Done when:** a memory with no related entity/location string match but a high-similarity
`SemanticVector` still gets surfaced (gate) and ranked (score) correctly; a memory with only a weak/
below-threshold similarity does not; a keyword (`TriggerCondition`) match still short-circuits before
any embedding comparison runs; and a gate-triggered initiative win carries a human-readable
`MatchReason` (memory id + source event id where available) through to the GM-visible
`TurnIntent`/reason string.

---

## Phase 5 — Backfill existing memories at startup

**File:** `src/CampaignVault/Data/SemanticVectorBootstrap.cs`

`EnrichMissingVectorsAsync<T>` (lines 58-107) only works on top-level RavenDB documents
(`session.Query<T>().Where(x => x.SemanticVector == null)`) — `MemoryNode` is nested inside
`Character.Psychology.Memories`, so it needs its own loop, added as one more step inside `RunAsync`
(after line 35's `EnrichMissingVectorsAsync<Character>` call, since it also touches `Character`
documents):
```csharp
private async Task EnrichMissingMemoryVectorsAsync(CancellationToken cancellationToken)
{
    const int batchSize = 50;
    var totalEnriched = 0;
    var totalCharacters = 0;
    string? lastId = null;

    while (true)
    {
        using var session = _store.OpenAsyncSession();
        var batch = await session.Query<Character>()
            .Where(c => lastId == null || c.Id.CompareTo(lastId) > 0)
            .OrderBy(c => c.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0) break;
        lastId = batch[^1].Id;
        totalCharacters += batch.Count;

        var touched = false;
        foreach (var character in batch)
        {
            foreach (var memory in character.Psychology?.Memories.Values ?? [])
            {
                if (memory.SemanticVector != null) continue;
                await SemanticEnrichmentHelper.EnrichAsync(memory, _embeddingService, _logger, cancellationToken);
                totalEnriched++;
                touched = true;
            }
        }

        if (touched) await session.SaveChangesAsync(cancellationToken);
        if (batch.Count < batchSize) break;
    }

    if (totalEnriched > 0)
    {
        _logger.LogInformation("MemoryNode: enriched {Count} memories across {Chars} characters", totalEnriched, totalCharacters);
        Console.Error.WriteLine($"  MemoryNode: enriched {totalEnriched} memories.");
    }
}
```
Call it from `RunAsync` after the `Character` line (35):
```csharp
await EnrichMissingVectorsAsync<Character>(cancellationToken);
await EnrichMissingMemoryVectorsAsync(cancellationToken);
```
(Id-range pagination instead of `Skip`, matching the existing "re-query rather than Skip over a
mutating predicate" rationale in the doc comment at line 64-67 — except here the predicate is
per-memory, not per-document, so `Skip` over documents is actually safe; using id-range keeps it
simple and consistent regardless.)

**Done when:** on a fresh startup against a campaign seeded before this feature shipped, every
existing `MemoryNode` ends up with a populated `SemanticVector` without any manual step.

---

## Phase 6 — Test-expectation check

**Files:**
- `tests/CampaignVault.UnitTests/Phase10InitiativeCoreTests.cs` (lines 42, 84, 308 — constructs
  `DefaultRelevantMemorySelector` directly)
- `tests/CampaignVault.UnitTests/Phase10InitiativeProviderTests.cs` (lines 115, 147, 180 — constructs
  `MemoryInitiativeProvider` directly)
- `tests/CampaignVault.UnitTests/NpcInitiativeTurnIntentTests.cs` (line 20)

Widening `MemoryMatchesScene` to return `true` on a semantic match can flip existing test
expectations that assert a memory does NOT surface for a given scene. Read all three files fully
before touching scoring code; any test that constructs a `MemoryNode` with a real embeddable
`Topic`/`Details` (rather than placeholder text) is a candidate for an unexpected pass/fail flip once
`SemanticVector` starts getting populated by these same tests' fixtures — check whether fixtures set
`SemanticVector` explicitly or leave it null (the latter is safe: Phase 4's guards skip the semantic
term entirely when either vector is null).

**Done when:** `dotnet test` is green with no new failures, no skipped/deleted tests to make it pass.

---

## Phase 7 — Guidance update (one line)

**Files:** `claude_skills/dnd-npc-interaction/SKILL.md`, `claude_skills/grok-playtest/SKILL.md`

Add one sentence near the existing "Memory & Salience" (`dnd-npc-interaction/SKILL.md:105-109`)
section: semantic memory surfacing only sees what's committed — narrate-only banter or action
description that never becomes an `event`/`knowledge_update` commit doesn't feed it. NPC-NPC
conversation specifically: only recorded if the GM commits an `event` with
`Category: Conversation` + `involved: [npc1, npc2]` (already true today per
`ConversationInvolvedResolver.cs` — no PC required — just needs stating so it isn't assumed
automatic).

**Done when:** both skill files mention the commit-dependency caveat.

---

## Phase gate

Each phase should build + pass `dotnet test` before moving to the next, per project convention
(one build at a time, full green suite before claiming done, pre-existing failures reported
separately and confirmed).
