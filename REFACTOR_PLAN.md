# CampaignVault Refactor Plan

> Groomed, task-level plan. Every task names the exact files to touch and what "done" means.
> Scope: `src/CampaignVault` only. `src/CampaignVault.Authoring` is out of scope throughout.

---

## Context

CampaignVault works. It is expensive to run and awkward to extend. Three problems, in priority order:

**1. Token waste.** `tools/list` costs ~35–45k tokens. `take_turn` alone is ~17–19k of that, because its `changes` parameter is a `WorldChange[]` with 40 `[JsonDerivedType]` variants and System.Text.Json inlines every one with no `$ref` reuse. `MinutesElapsed` lives on the abstract base, so it is inlined 40× — 10,920 chars of identical prose. Three middlewares exist only to post-process the damage; one ships with `//TODO: evaluate - not sure its helpful` (`Program.cs:200`), another makes the advertised schema lie about what the server accepts.

**2. Guidance is pull-based, so the model over-fetches and still errors.** `get_help` is advertised "CALL THIS FIRST"; the default costs ~2,700 tokens, `topic=patterns` costs ~8,300. The model has no signal for which topic is relevant, so it speculatively fetches combat guidance when no combat is imminent. The same guidance is maintained in three drifting places: `Tools/DmHelpManual.cs` (83KB), `claude_skills/` (84KB), `recommended-system-prompt.md` (13KB).

**3. Plugin support is blocked; code quality is uneven.** `RulesetSystem` is a closed C# enum used in 53 files and checked for exhaustiveness at startup. `SystemExtension` is a closed `[JsonDerivedType]` hierarchy hard-cast in ~15 places outside `Rulesets/`. No assembly loading exists. Separately: `CampaignRepository` is 3,155 lines / 67 public methods with `IAsyncDocumentSession` as parameter #1 on 74 signatures; the change dispatcher does an O(48) scan up to 3× per change; ~560 lines of `Suggest*` exist twice, divergently; `Order` has 13 collision groups resolved by undefined reflection order.

### Decisions taken

| Decision | Choice |
|---|---|
| Rewrite vs refactor | **Rewrite the MCP boundary** (`Tools/`, `Middleware/`); **refactor the core in place**. The simulation engine, pressure system, ruleset resolvers, and 796 YAML files are the asset and stay. |
| Existing campaign data | **Preserve.** Persisted-shape changes get migrations via the existing `RavenStartup.RunDataMigrationsAsync` hook (`Program.cs:214`). |
| Sequencing | **Token + guidance first** (Phases 1–3), then plugins (Phase 4), then core quality (Phase 5). |

### Ground rules

- Per `CLAUDE.md`: **one build at a time**, never overlapping or backgrounded. If a build hangs, stop and ask.
- **Every phase ends on a full green suite** — not build-only. Pre-existing failures get reported explicitly, not absorbed.
- Phases are independently shippable. Do not start the next until the current is green.
- Phases 1–3 and 4–5 are largely independent after Phase 2; the only hard coupling is Task 1.4 (`get_commit_schema(type:)`), which Phase 3 error messages depend on.

---

# Phase 0 — Bug fixes to land immediately

Small, independent, no design debate. Do these first so they are not entangled with later work.

### 0.1 — Fix stdout corruption in stdio mode

**File:** `Data/Pressure/Contributors/LocationConnectivityPressureContributor.cs:42` and `:71`

Both sites are `catch (Exception ex) { Console.WriteLine($"Pressure check error: {ex.Message}"); }`.

In `MCP_STDIO=1` mode **stdout is the JSON-RPC channel** — this writes protocol-corrupting garbage into it. `Program.cs:212-219` establishes the correct pattern (`Console.Error`). Both blocks also swallow the exception with no logger and no stack trace.

**Change:** inject `ILogger<LocationConnectivityPressureContributor>?` (optional, matching the pattern in `MutationTools`), replace both with `_logger?.LogWarning(ex, "Location connectivity pressure check failed")`. Do not use `Console` at all.

**Done when:** zero `Console.WriteLine` outside `Program.cs` — `grep -rn "Console.WriteLine" --include="*.cs" src/CampaignVault | grep -v Program.cs` returns nothing.

### 0.2 — Delete dead code

| File | Why |
|---|---|
| `Tools/CommitTypesReference.cs` | `SupportedTypesList` (34 entries vs 40 actual) and `SupportedTypesBullet` have zero consumers outside the file. |
| `Tools/SemanticallyRequiredAttribute.cs` | Defined, never applied to anything, never read. |
| `Tools/CommitRumorHelpExamples.cs` | No consumers. |
| `CommitEnumCheatSheet.Compact` | Only `.Full` is referenced (`MetaTools.cs:121`). Delete the `Compact` member, keep the file. |

**Also:** `Models/V3Dtos.cs` `SessionBriefingView` is a hand-copied clone of `WorldStateView` (9 identical fields + `Party`), reachable only from the `internal`, unused `ExplorationTools.GetSessionBriefing`. `start_session` returns `SessionStartView` instead. Verify no test references it, then delete both the view and the method.

**Done when:** build is green and `CommitEnumCheatSheetTests` still passes.

---

# Phase 1 — Single source of truth for commit metadata

No user-visible change. This is the foundation Phases 2 and 3 both project from, and it kills an entire class of drift bug.

**The problem being solved:** the 40 `$type` variants are described in four hand-maintained places that have already diverged.

| Source | Count | Status |
|---|---|---|
| `[JsonDerivedType]` on `Models/WorldChanges.cs:14-53` | **40** | ground truth |
| `Tools/CommitSchemaRegistry.cs` `BuildAll()` | 35 | missing `item_persistence_surfaced`, `memory_decay`, `rest_recovery_ack`, `scene_setup`, `xp_grant` |
| `Tools/CommitTypesReference.cs` | 34 | dead — deleted in 0.2 |
| `WorldChange`'s own `[Description]` | "15 named, and 30+ others" | vague by construction |

### 1.1 — Add metadata attributes

**New file:** `Models/CommitMetadataAttributes.cs`

```csharp
[AttributeUsage(AttributeTargets.Class)]
internal sealed class CommitCategoryAttribute(string category) : Attribute
{
    public string Category { get; } = category;   // "Combat" | "Narrative" | "World" | "PlotThread"
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class CommitSideEffectsAttribute(params string[] types) : Attribute
{
    public string[] Types { get; } = types;       // discriminators this change auto-applies
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class CommitCoCommitAttribute(params string[] types) : Attribute
{
    public string[] Types { get; } = types;
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class CommitExampleAttribute(string json) : Attribute
{
    public string Json { get; } = json;
}

/// <summary>Marks a variant for full-detail treatment in the emitted tool schema.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class CommitHotTierAttribute : Attribute;
```

**Then apply them** to the variant classes in `Models/WorldChanges.cs`. This is a mechanical port of the 35 existing `CommitSchemaRegistry` entries onto their own classes, plus 5 newly authored ones.

Mark exactly these 10 with `[CommitHotTier]` — the high-frequency narrative path:

`hp`, `status`, `event`, `relationship`, `mood`, `activity`, `item`, `ruleset_action`, `engagement_relation`, `travel`

> Note the actual discriminators are `item` (not `item_transfer`) and `event` (not `event_occurred`) — see `WorldChanges.cs:15` and `:18`.

**Why attributes rather than a lookup table:** the metadata ends up next to the type it describes, so adding a variant without its metadata is visible in review and caught by 1.5.

### 1.2 — Reflection-derived model

**New file:** `Schema/CommitSchemaModel.cs`

```csharp
internal sealed record CommitFieldModel(
    string JsonName,
    Type ClrType,
    bool IsRequired,
    string? Description,
    IReadOnlyList<string>? EnumValues);

internal sealed record CommitVariantModel(
    string Discriminator,
    Type ClrType,
    string Category,
    string Summary,
    IReadOnlyList<CommitFieldModel> Fields,
    bool IsHotTier,
    IReadOnlyList<string> SideEffects,
    IReadOnlyList<string> CoCommitHints,
    string? Example);

internal static class CommitSchemaModel
{
    public static IReadOnlyList<CommitVariantModel> Variants { get; }   // static readonly Lazy<>
    public static CommitVariantModel? Find(string discriminator);
}
```

Build it by:
1. Walking `typeof(WorldChange).GetCustomAttributes<JsonDerivedTypeAttribute>()` — this is the ground truth for both the discriminator string and the CLR type.
2. For each derived type, reading `JsonSerializerOptions.GetTypeInfo(t).Properties` rather than raw `GetProperties()` — this respects `[JsonIgnore]` and the naming policy for free, so `JsonName` is always what actually goes on the wire.
3. Joining back to `PropertyInfo` via `JsonPropertyInfo.AttributeProvider` to read `[Description]`.
4. `IsRequired` from `new NullabilityInfoContext().Create(pi).WriteState == NullabilityState.NotNull` for reference types; `Nullable.GetUnderlyingType(t) is null` for value types.

> **Finding worth knowing:** `JsonSchemaExporter` emits `required` only for C# `required` / `[JsonRequired]` members. `WorldChanges.cs` has 2 such across 251 properties; `UpsertRequests.cs` has 0. **The current schema is effectively required-free** — it does not tell the model that `hp` needs `characterId` and `delta`. Deriving `required` from nullability makes Phase 2's schema strictly *stronger* than today's, not just smaller.

### 1.3 — Reduce the registry to a projection

**File:** `Tools/CommitSchemaRegistry.cs` — 386 lines collapse to ~25.

Keep the `public record CommitTypeSchema` (`:3-13`) exactly as-is so `MetaTools.GetCommitSchema`'s wire contract is unchanged. Replace `BuildAll()` with a projection over `CommitSchemaModel.Variants`, mapping `Fields.Where(f => f.IsRequired)` → `RequiredFields` and the rest → `OptionalFields`.

**File:** `Tools/CommitEnumCheatSheet.cs` — `Full` becomes a projection over the same model.

### 1.4 — Single-variant lookup

**File:** `Tools/CommitSchemaRegistry.cs` — add `GetAll(string? category, string? type)`.
**File:** `Tools/MetaTools.cs:63` — add a `string? type = null` parameter to `GetCommitSchema` and thread it through.

**This is load-bearing for both later phases.** Phase 2's cold tier tells the model "call `get_commit_schema(type: "faction_state")`" — that call must cost ~150 tokens, not the ~5,000 that returning all 35 costs today.

### 1.5 — Drift tests

**New file:** `tests/CampaignVault.UnitTests/CommitSchemaDriftTests.cs`

| Test | Asserts |
|---|---|
| `EveryDerivedType_HasARegistryEntry` | Set-equality between `[JsonDerivedType]` discriminators and `CommitSchemaRegistry.GetAll()`. **This is the test that would have caught 35-vs-40.** |
| `EveryVariant_HasCategoryAndSummary` | No empty `[CommitCategory]` or class `[Description]`. |
| `DeclaredSideEffects_MatchRegisteredHandlers` | Every string in `[CommitSideEffects]` is a real discriminator. |
| `RequiredFields_AreNonNullable` | Every field the registry reports required is non-nullable in CLR, so schema `required` and registry `RequiredFields` cannot disagree. |
| `HotTier_IsExactlyTen` | Guards the Phase 2 budget from silent growth. |

**Phase 1 done when:** full suite green; `get_commit_schema` output is byte-comparable to the pre-refactor baseline for the 35 shared entries, plus 5 new ones. Capture that baseline **before** starting 1.3.

---

# Phase 2 — Own the tool schema

**Target:** `tools/list` from 35–45k tokens down to 12–16k.

### The SDK hook (verified against ModelContextProtocol 1.3.0 in the nuget cache)

- `McpServerToolCreateOptions` has an `OutputSchema` but **no `InputSchema`** — `McpServerTool.Create(...)` cannot be handed one.
- **`Tool.InputSchema` has a public setter**, and `McpServerTool.ProtocolTool` returns a **mutable** `Tool`. The setter's only validation is that the schema is `"type":"object"`.
- `McpServerOptionsSetup : IConfigureOptions<McpServerOptions>` fills `McpServerOptions.ToolCollection` from DI. So **`services.PostConfigure<McpServerOptions>(...)` runs after every tool is materialised** and can install a hand-built schema **once at startup** — not per `tools/list` request, which is what the three current filters do.

**Also verified:** `Microsoft.Extensions.AI.Abstractions` contains no `$defs` or `$ref` literal anywhere — it does no hoisting and no pointer rewriting. A parameter's schema is generated in isolation then nested under `properties/<paramName>`, so any `$ref` emitted from `TransformSchemaNode` would dangle. **Generation-time `$ref` cannot work without a post-hoc rewrite pass** — meaning it would not actually retire the middleware it claims to replace. Owning the document outright is the only approach that does.

### The design: tiered `$defs`, generated not hand-authored

All 40 variants keep a `$def` and stay in the `anyOf`, so `$type` validation and the closed discriminator set never weaken. What is tiered is **detail depth**:

| Tier | Count | Contents |
|---|---|---|
| **Hot** | 10 (`[CommitHotTier]`) | Full properties, types, enum value lists, a real `required` array, one **≤80-char** description per property. |
| **Cold** | 30 | Property names and types listed; **prose dropped**; `additionalProperties: true`; one **≤60-char** description pointing at `get_commit_schema(type: "x")`. |

Plus two shared `$defs`:
- `minutesElapsed` — referenced 40× instead of inlined 40×. **10,920 chars → ~1,600.**
- `systemExtension` — one honest def listing the three `$system` branches' field names, replacing today's `{"type":"object"}` stub.

**Why not the alternatives:** structure alone has a floor of ~20,000 chars ≈ 5k tokens (251 props × ~38 chars + 23 enum lists + 42 def wrappers + 40 `$type` consts + the `anyOf`). Stripping descriptions or adding `$ref` cannot get under 4k. Tiering is arithmetically required. Routing the cold tail through a free-form escape hatch would get there too, but bifurcates the wire format and loses `$type` validation for 30 types — this design keeps both.

### 2.1 — Schema builder

**New file:** `Schema/TakeTurnSchemaBuilder.cs`

```csharp
internal static class TakeTurnSchemaBuilder
{
    public static JsonElement Build(JsonSerializerOptions options);
}
```

Per variant, call `JsonSchemaExporter.GetJsonSchemaAsNode(options, variant.ClrType, exporterOptions)` on the **derived type in isolation**. STJ only emits the discriminator when exporting the polymorphic *base*, so this neither emits `$type` nor recurses into the other 39 — which is exactly what makes the per-variant `$def` cheap.

In `exporterOptions.TransformSchemaNode`:
- Strip every `description`.
- Replace any `MinutesElapsed` / `SystemExtension` subtree with `{"$ref": "#/$defs/..."}`.
- For hot-tier types only, re-attach the truncated description read from `ctx.PropertyInfo!.AttributeProvider`.

Then inject `"$type": {"const": discriminator}` and the `required` array computed in 1.2.

**Put `$defs` at the tool-schema root** (`#/$defs/hp`). `$ref` resolves against the whole document; since we author the entire document, root placement is unambiguous. Do not nest `$defs` under `properties/request/...`.

**Escape hatch:** honour `CAMPAIGNVAULT_INLINE_SCHEMA=1` by emitting the fully-inlined tiered schema (~28k chars) for a client that cannot resolve `$ref`. One branch, cheap insurance — see Risks.

### 2.2 — Same treatment for `world_build`

**New file:** `Schema/WorldBuildSchemaBuilder.cs`

`WorldBuildBatch` (`Models/WorldBuildModels.cs:12`) is 13 arrays over the 12 `*UpsertRequest` types in `Models/UpsertRequests.cs` (158 props, 7,760 description chars). Same generator, same `$defs` document — the upsert shapes mirror the commit types closely enough to share `systemExtension`.

**Expected: ~9–10k tokens → ~2.5k.**

### 2.3 — Install at startup

**New file:** `Schema/McpSchemaInstaller.cs`

```csharp
internal static class McpSchemaInstaller
{
    public static IServiceCollection AddCampaignVaultToolSchemas(this IServiceCollection services) =>
        services.PostConfigure<McpServerOptions>(o =>
        {
            if (o.ToolCollection?.TryGetPrimitive("take_turn", out var t) == true)
                t.ProtocolTool.InputSchema = TakeTurnSchemaBuilder.Build(McpJsonOptions);
            if (o.ToolCollection?.TryGetPrimitive("world_build", out var wb) == true)
                wb.ProtocolTool.InputSchema = WorldBuildSchemaBuilder.Build(McpJsonOptions);
        });
}
```

**File:** `Program.cs` — call `builder.Services.AddCampaignVaultToolSchemas();` immediately after the `.WithToolsFromAssembly()` block (currently ends `Program.cs:201`).

**Fallback if `PostConfigure` ordering surprises us:** the identical `Build()` output installs from a `filters.AddListToolsFilter` (same shape as the current `SystemStatsSchemaSimplifier.Register`), guarded by a `static readonly Lazy<JsonElement>` so it is still computed once. Same result, one dictionary lookup per `tools/list`.

### 2.4 — Delete the compensating middleware

**File:** `Program.cs:194-201` — remove two lines from the `WithRequestFilters` block, including the `//TODO: evaluate - not sure its helpful`.

| Delete | Lines | Why it is now unnecessary |
|---|---|---|
| `Middleware/McpSchemaDeduplicator.cs` + its tests | 244 | Hoisting repeated subtrees to `$defs` now happens at generation time with stable readable names (`hp`, `ruleset_action`) instead of `dedup_A3F9…`. Its `ContainsKey("properties")` gate at `:127` — which is exactly why the 40× scalar `minutesElapsed` was never deduped — becomes moot. |
| `Middleware/SystemStatsSchemaSimplifier.cs` + its tests | 139 | Replaced by a truthful `#/$defs/systemExtension`. **The schema stops lying.** |

**Keep** `Middleware/McpNormalizationMiddleware.cs` — it rewrites *inbound* param synonyms at the raw HTTP layer and is orthogonal to schema emission. It shrinks in Task 3.6 for an unrelated reason.

**Net: −383 lines of middleware, +~350 lines of generator, and a schema that is more accurate than today's.**

### 2.5 — Budget tests

**New file:** `tests/CampaignVault.UnitTests/ToolSchemaBudgetTests.cs`

| Test | Asserts |
|---|---|
| `TakeTurnInputSchema_StaysUnderBudget` | `Build(opts).GetRawText().Length < 18_000`, and **prints the actual count via `ITestOutputHelper`**. |
| `WorldBuildInputSchema_StaysUnderBudget` | `< 11_000`, same output. |
| `EveryDefIsReferenced_AndEveryRefResolves` | Walk the built schema: no dangling `$ref`, no orphan `$def`. |
| `HotTierDescriptions_AreUnder80Chars` | Guards the only soft part of the budget. |

> **This is the honest enforcement of the target.** The ~17–19k current figure is from static analysis — nobody has captured a live `tools/list`. This test replaces the estimate with a measurement on day one and holds the line in CI thereafter.

### Risks

- **`$ref` becomes load-bearing for the entire changes array.** `McpSchemaDeduplicator` already bets on client `$ref` support in production, so the bet is not new — but it is bigger. Mitigated by `CAMPAIGNVAULT_INLINE_SCHEMA=1` (2.1).
- **Shared-instance mutation.** Unverified whether `ListToolsResult.Tools` hands back singleton `Tool` objects. If it does, the three existing filters have been mutating server-global state and re-parsing ~45k chars per `tools/list` — an argument for `PostConfigure` independent of size.
- **`PostConfigure` ordering** relies on all `IConfigureOptions` running before all `IPostConfigureOptions`. That is the documented `Microsoft.Extensions.Options` contract but is unobserved in this app. 2.3's fallback covers it.

**Phase 2 done when:** full suite green; budget tests pass and their printed numbers are recorded in the PR; a live `MCP_STDIO=1` `tools/list` capture confirms the emitted schema matches the built one; a real `take_turn` succeeds with both a hot-tier and a cold-tier `$type`, and **fails correctly** when a hot-tier required field is omitted.

---

# Phase 3 — Push guidance instead of serving a manual

This is the fix for *"get_help → combat when nothing indicates imminent combat."*

**The insight:** the server knows whether combat is active; the model does not know what it does not know. Invert the direction. And the infrastructure already exists — the pressure system is push-based, state-triggered, and already solves dedup, cooldowns, escalation, capping, and content-signature suppression. **Guidance is pressure with a different lifetime: once-per-campaign instead of once-per-cooldown-window.**

### 3.1 — The seam

**New file:** `Data/Guidance/GuidanceTypes.cs`

```csharp
public sealed record GuidanceHint(
    string Key,                   // stable ledger key, e.g. "combat.first-encounter"
    string Text,                  // <= 240 chars, imperative, one idea
    GuidanceTrigger Trigger,      // enum, telemetry only
    int Priority = 0)
{
    public string? Example { get; init; }        // <= 200 chars copy-paste JSON
    public int? RepeatAfterDays { get; init; }   // null = strictly once
}

public interface IGuidanceContributor
{
    PressureScope Scope { get; }
    int Order { get; }
    Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default);
}

public interface IGuidanceOrchestrator
{
    Task<IReadOnlyList<GuidanceHint>> CollectAsync(
        PressureScope scope, PressureContext ctx, bool ignoreLedger = false, CancellationToken ct = default);
}
```

**Reuse `PressureContext` verbatim** (`Data/Pressure/PressureTypes.cs:17-28`). It already carries `CampaignName`, `Time`, `Config`, `Session`, `Scene`, `PartyPresent`, `ActiveRumors`, `RecentEvents`, `QuestDeadlines`, `DaysAdvanced`, `DisableCooldowns`. Do not introduce a parallel context type.

**New file:** `Data/Guidance/GuidanceOrchestrator.cs` — mirrors `PressureOrchestrator.CollectAndCapAsync`: run contributors in `Order` filtered by `Scope`; drop keys already in the ledger (unless `ignoreLedger`); sort by `Priority` desc; take while under budget; record survivors.

### 3.2 — The ledger

**New file:** `Models/GuidanceLedger.cs`

```csharp
public class GuidanceLedger
{
    public string Id { get; set; } = null!;
    public string CampaignName { get; set; } = null!;
    public Dictionary<string, GuidanceDelivery> Delivered { get; set; } = [];
    public int TokensDeliveredTotal { get; set; }
}
public record GuidanceDelivery(int Day, DateTime AtUtc, string ToolName);
```

**File:** `Data/CampaignDocumentKeys.cs` — add alongside `StateOnboarding`:

```csharp
public string StateGuidance(string campaignName) =>
    $"campaigns/{Normalize(campaignName)}/state/guidance";
```

**Deliberately a separate document, not `Campaign`.** `Models/Campaign.cs` already carries `PressureCooldowns` and `InitiativeSurfaced`, and that doc is loaded and saved on essentially every mutating call. Guidance keys are write-once and never re-read after the first sessions — piling ~60 more dictionary entries into the hot document is the wrong trade. Load lazily (only when a contributor fires), save on the existing `CampaignToolBase.ExecuteAsync` save path.

`RepeatAfterDays` is checked against `GuidanceDelivery.Day` using the same `currentDay - lastDay < cooldownDays` idiom already in `PressureManager`.

### 3.3 — Response surface and budget

**File:** `Models/V3Dtos.cs:6-25` — add to `ToolResult<T>`, next to `WorldPressure`:

```csharp
public string[]? Guidance { get; set; }
```

Add it to the positional constructor too, defaulted to `null`, so existing call sites compile unchanged.

**Keep it distinct from `WorldPressure`.** Pressure is "the world is nagging you"; guidance is "here is how this tool works". Merging them would subject guidance to `PressureCooldowns`' day-based cooldown, which is wrong for once-only delivery, and would make both harder to tune.

**File:** `Models/CampaignConfig.cs` — add next to `MaxPressuresPerResponse`:

```csharp
public int MaxGuidanceCharsPerResponse { get; set; } = 600;   // ~150 tokens
public int MaxGuidanceHintsPerResponse { get; set; } = 2;
public bool GuidanceEnabled { get; set; } = true;             // off-switch for veteran campaigns
```

Enforce in `GuidanceOrchestrator`, **not negotiable by contributors** — accumulate `Text.Length + (Example?.Length ?? 0)`, stop at the cap, truncate an over-long hint at a sentence boundary and log a warning.

**File:** `AutofacModules/ConventionRegistration.cs:43-46` — add `RegisterCollection<IGuidanceContributor>(builder, assembly);` to `RegisterMarkerCollections`. Register `GuidanceOrchestrator` explicitly in `RegisterApplicationCore` (it will not match the `Manager|Orchestrator|Selector|Store|Service` name convention unless its namespace is added to `NameMatchedNamespaces` — pick one, explicitly is simpler).

### 3.4 — The contributors

**New directory:** `Data/Guidance/Contributors/` — each file 30–60 lines, mirroring `Data/Pressure/Contributors/TimeStalenessPressureContributor.cs`.

| Contributor | Trigger (read off real state) | Replaces |
|---|---|---|
| `FirstCommitGuidanceContributor` | `Campaign.CreatedAt` recent AND no `Event` docs | Quickstart golden rules |
| `FirstWorldBuildGuidanceContributor` | `SeedCoverage.Locations == 0` | WorldBuilding seeding order |
| `CombatStartedGuidanceContributor` | `keys.CombatCurrent(campaign)` exists AND `Round == 1` | Combat: "resolve every check through `ruleset_action`; do **not** add separate hp/status — the engine applies them" |
| `SpellcastingGuidanceContributor` | party has a spell-slot `ResourcePool` **and** a `Spell`-category `ruleset_action` was just committed | Spells routing |
| `ItemDamageGuidanceContributor` | an `item_update`/`ruleset_action` degraded an item's `State` | VisualSandbox: `upsertItemDetail` |
| `PlotThreadStalenessGuidanceContributor` | reuse the detection already in `Data/Pressure/Contributors/PlotThreadStalenessContributor.cs` | Patterns: quest lifecycle |
| `SystemStatsGuidanceContributor` | reuse `IncompleteSystemStatsPressureContributor`'s detection | Combat: bootstrap fields |
| `RestAndTravelGuidanceContributor` | first `rest` or `travel` commit | Patterns: wilderness/transients |
| `NarrativeFocusGuidanceContributor` | `Campaign.NarrativeFocus` empty after N commits | Faq: importance calibration |
| `TimeRecordingGuidanceContributor` | `CommitsSinceTimeRecorded` past threshold AND `minutesElapsed` never used | Quickstart time rules |

**~30 hints × ~200 chars ≈ 6KB total**, versus 83KB pulled on demand.

> Several of these reuse detection that pressure contributors already compute. **Do not duplicate the detection** — extract the predicate into a shared helper and have both call it.

### 3.5 — Shrink `get_help`

**File:** `Tools/DmHelpManual.cs` — 83KB → ~13KB.

Measured section sizes: Patterns 33,353 / VisualSandbox 12,444 / Quickstart 10,885 / Combat 9,624 / WorldBuilding 5,158 / WorldPressure 3,566 / Faq 3,374 / Onboarding 2,216 / Spells 866 / CommitEnum 840.

**Delete** `patterns`, `combat`, `spells`, `visual-sandbox`, `world-pressure`, `quickstart` — **70,738 of 83,000 chars.** The 33KB Patterns section (the single most expensive call in the server) decomposes into triggered hints plus the existing `WorldPressureItem.SuggestedCommitJson` mechanism that `Data/Pressure/Contributors/PressureHintEnricher.cs` already uses. That is the template.

**Keep:** `tools` (cheap, a genuine lookup), `world-building` + `onboarding` (7,374 chars; genuinely session-0 procedural, and the model asks at exactly the right moment), `commit-enum` (reimplemented as a `CommitSchemaModel` projection in 1.3 — it doubles as Phase 2's cold-tier escape valve).

**File:** `Tools/MetaTools.cs:10-55` — remove the deleted members from the `HelpTopic` enum and the corresponding `switch` arms at `:101-128`.

**File:** `Tools/MetaTools.cs:84` — **rewrite the `[Description]`.** Drop `"CALL THIS FIRST"`. Replace with:

> `"Reference lookup. The server pushes what you need automatically on tool responses; call this only to look up something you were not told."`

**That one edit removes the incentive for the speculative call.** It is the highest-leverage single line in this phase.

### 3.6 — Trim error-time guidance

`Middleware/McpToolErrorFilter.cs` already has the right shape — error-time, targeted, structured. Only its inputs are wrong.

**File:** `Tools/ToolCallExamples.cs` — 879 lines / 42KB, of which **11 of 15 registry entries (lines 579-878, ~17,000 chars) target retired `upsert_*` tools** that are no longer exposed over MCP. `McpNormalizationMiddleware` walks those dead branches on **every tool call**.

- Delete the 11 dead entries and their `WrapperKey`/`LegacyWrapperKey` normalization branches.
- **Keep** the 4 live entries (`take_turn`, `world_build`, `get_entity`, `combat`) and the `$type`-aware `TryNormalize` logic — the `changes[]` walking, `$type`/`type` legacy fallback, and `parameters`/`actionType`/`newState` coercion are genuinely valuable and have no replacement.
- **New test** `Registry_OnlyContainsLiveTools` — every key is in `ToolCatalog.GetByCategory(null)`. **This is the exact test that would have caught the 41% dead code.**

**File:** `Middleware/McpToolErrorFilter.cs:55-84` — replace the hand-written 14-case `(tool, param)` switch (same drift class as `CommitSchemaRegistry`; 4 of its 14 cases now duplicate `CommitSchemaModel`) with `ToolParameterGuidance.For(tool, param)`, reading from:
1. `ToolCatalog` for the tool's own description,
2. `CommitSchemaModel` for anything `$type`-shaped,
3. a residual 5-entry table for genuinely tool-specific cases: `combat.action`, `combat.locationId`, `combat.combatantIds`, `create_campaign.initialSystem`, `get_rules_reference.kind`.

Everything else falls back to a generated message naming the tool's actual required parameters.

**Wire guidance into errors:** when the filter builds a `ToolResult` for a failed `take_turn`, attach the matching `GuidanceHint` for that `$type` with **`ignoreLedger: true`** — an error is the highest-signal moment to spend 150 tokens, and a repeated mistake deserves a repeated correction.

**File:** `Tools/ModelEnumErrorHints.cs` — keep it (it does the right thing for enum values), but make its enum tables a projection of `CommitFieldModel.EnumValues`. Third instance of the same drift class.

### 3.7 — Split the prose corpus three ways

Split by **who can observe the trigger.**

| Destination | What | Size |
|---|---|---|
| **Into the server** (3.4 contributors) | Anything conditioned on campaign state: most of `dnd-world-change`, `dnd-combat`, `dnd-campaign-events`, and the "when to commit what" half of `recommended-system-prompt.md`. The server sees combat, spell slots, item damage, plot threads; a skill file cannot. | ~25KB migrates |
| **`recommended-system-prompt.md`**, trimmed | Things true *before* any tool call: persona, tone, the "you are the DM, the server is the world" framing — plus the load-bearing line: *"the server pushes guidance on tool responses under `guidance`; follow it and don't call `get_help` speculatively."* | 13KB → ~4KB |
| **`claude_skills/`**, kept | Narrative craft with no server-observable trigger: `dnd-narration`, `dnd-conversation`, `dnd-social`, `dnd-npc-interaction`. The server has no signal for "your prose is flat." | unchanged |

**Also:** move `claude_skills/grok-playtest` and `claude_skills/dnd-bundling` to `docs/`. They are dev/QA artifacts, not runtime guidance, and their presence makes the skills directory look like runtime config.

**New file:** `tests/CampaignVault.UnitTests/GuidanceCorpusTests.cs`

| Test | Asserts |
|---|---|
| `TotalHintChars_UnderBudget` | Sum of all `GuidanceHint.Text` < 8,000. |
| `HintKeys_AreUnique` | No duplicate ledger keys. |
| `DmHelpManual_UnderSizeCap` | Total chars < 15,000. |
| `SkillFiles_ReferenceOnlyLiveTools` | No `claude_skills/*/SKILL.md` names a tool absent from `ToolCatalog`. **This is the failure mode that produced the 41% dead `ToolCallExamples`.** |

**Phase 3 done when:** full suite green; **and** a short session is played end-to-end confirming guidance appears on the *first* combat and not before, does *not* reappear on the second, and `get_help` is no longer called speculatively. This phase cannot be validated by tests alone.

---

# Phase 4 — Open the system model for plugins

**Ship data-only plugins first.** A system defined purely by YAML plus the existing narrative resolver (SWADE, Fate, OSR) is the useful milestone and lands well before code plugins.

### 4.1 — `RulesetSystem` enum → open string id

**File:** `Models/RulesetEnums.cs:10-15` — `enum RulesetSystem { Dnd5e, Pathfinder2e, Narrative }`.

Referenced in **53 files**. Used as: a module-registration dictionary key, a key in all 9 YAML providers, **5 persisted document fields** (`Campaign.System`, `CampaignConfig.ActiveSystem`, `CustomSpell.System`, `CustomFeat.System`, `CustomCreature.System`), and — critically — **an MCP tool parameter type** on `CampaignManagementTools.CreateCampaign` (`:84`), where it is emitted as a **closed `enum` in the tools/list schema**. A plugin system would be rejected at schema validation before reaching any C# code.

**Change:** to a string id (or a `readonly record struct SystemId(string Value)`). Add a `list_systems` discovery path so the model can still learn the valid set. `Models/RulesetSystemExtensions.cs:8-14` `ToSlug()` becomes the identity.

**Migration required** — data is being preserved. Add a step to `RavenStartup.RunDataMigrationsAsync` (`Program.cs:214`) converting the 5 persisted fields. Enum values already serialize as strings via `[JsonConverter(typeof(JsonStringEnumConverter))]`, so the on-disk representation is likely already compatible; **verify against a real database copy before assuming it**.

### 4.2 — Delete the exhaustiveness check

**File:** `Rulesets/IRulesetModuleSelector.cs:19-23`

```csharp
foreach (var system in Enum.GetValues<RulesetSystem>())
    if (!_modules.ContainsKey(system))
        throw new InvalidOperationException($"Startup Validation Failed: No IRulesetModule registered for system: {system}");
```

This structurally forbids a sparse registry — it is the plugin blocker in one statement. Delete it; replace with a check that the *campaign's configured* system resolves, raised at use time with a helpful message listing registered systems.

### 4.3 — `SystemExtension` → runtime type resolver

**File:** `Models/Character.cs:234-237` — the closed `[JsonPolymorphic("$system")]` + two `[JsonDerivedType]` attributes.

Replace with an `IJsonTypeInfoResolver` populated from registered modules at startup.

**Fix a silent data-loss path while here:** `UnknownDerivedTypeHandling.FallBackToBaseType` (`:234`) degrades an unknown `$system` to a bare `SystemExtension`, and then `Rulesets/SystemStatsMerger.cs:44` (`_ => Dnd5e`) plus `CampaignRepository.UpgradeSystemStatsIfNeededAsync` (`:906-919`) **actively rewrite it into a `Dnd5eExtension` on load.** Harmless today; silent data loss the moment a third system exists.

### 4.4 — Move the leak sites behind `IRulesetModule`

Highest value first. Each is a hard cast to `Dnd5eExtension`/`Pf2eExtension` outside `Rulesets/`:

| File | Leak |
|---|---|
| `Rulesets/ArmorParameterResolver.cs:47-66` | **Start here.** A static 5e/PF2e switch computing `10 + dex + acBonus`, called from 4 sites in `Data/ChangeHandlers/` (`ItemEquipHandler.cs:115,281`, `ItemTransferHandler.cs:95-113`, `ItemChangeHandlers.cs:135-136`) — bypasses the module seam entirely. |
| `Data/ChangeHandlers/HpChangeHandler.cs:83-104` | Concentration-break save; `Constitution`/`"CON"` vs `ConstitutionMod`/`"Fortitude"`. |
| `Data/ChangeHandlers/CharacterChangeHandlers.cs:404-431` | Ability-score increase with a literal `case "strength": … "charisma":` switch. |
| `Data/SpellSlotValidator.cs` + `Data/ChangeHandlers/ResourceChangeHandler.cs:94-132` | Called unconditionally regardless of active system. |
| `Services/ResourcePoolInitializer.cs:53,159-193` | |
| `Services/XpThresholdCalculator.cs:73-75` | `_ => Dnd5eStandardXp // Default to 5e for Narrative/other systems` |
| `Data/Scenes/SceneRecognitionHintFactory.cs:85-90` | Twice: skills, background. |
| `Services/CharacterClassResolver.cs:16` | |
| `Data/RulesetSystemResolver.cs:12-20` | Throws `NotSupportedException` on anything else. |

### 4.5 — Audit the default-to-5e fallbacks

~15 sites of `?? RulesetSystem.Dnd5e` or `_ => Dnd5e`. Each becomes a silent-wrong-behaviour bug the moment a third system exists.

Worst offenders: **`Tools/WorldBuilderTools.cs:313,342` *persists* a 5e config when none exists.** Also `Models/Campaign.cs:35`, `Models/CampaignConfig.cs:31`, `CampaignRepository.cs:914`, `CharacterChangeHandlers.cs:659,664`, `Tools/OnboardingTools.cs:238-241`, `Rulesets/SocialSkillGating.cs:29`, `Rulesets/SystemStatsUpgradeHelper.cs:31`.

Each becomes either an explicit error or a genuinely system-neutral default. **No silent coercion.**

### 4.6 — Directory-driven providers

9 near-identical provider classes in `Services/` each hardcode `Register(RulesetSystem.Dnd5e, …, "dnd5e", …)` and `Register(RulesetSystem.Pathfinder2e, …, "pf2e", …)`. `RaceDefinitionProvider.cs:21-22` even hardcodes the `races` vs `ancestries` folder-name difference.

**Change:** scan `RulesetData/*/` for system directories instead of enumerating a fixed list.

**Reuse `Data/Templates/RulesetTemplateLoader.cs` unchanged.** Its embedded→disk extraction with hash-based homebrew detection and disk-wins override (`:70-127`) is already exactly the mechanism a plugin's data wants — it only needs to stop being fed a fixed system list.

> **This is the milestone.** After 4.1–4.6, a data-only plugin works. Ship it and validate before starting 4.7.

### 4.7 — Code plugins

An `AssemblyLoadContext` scanning a plugin directory, feeding assemblies into `ConventionRegistration.Register` (`AutofacModules/ConventionRegistration.cs:33`).

`RegisterCollection<IRulesetModule>` (`:47`) already scans by interface and would pick up a plugin module unchanged — **the only reason it cannot today is that nothing ever loads the assembly.** There is zero `AssemblyLoadContext` / `Assembly.LoadFrom` / MEF anywhere in the project.

`IBootstrapStep` (`Rulesets/Bootstrap/IBootstrapStep.cs`) is a clean contract but pipelines are constructed with inline `new` in `Dnd5eRulesetResolver.cs:22-30` and `Pf2eRulesetResolver.cs`. A plugin cannot inject a step into an existing pipeline — make step registration DI-driven if that turns out to matter.

### Explicitly deferred

The wire vocabulary is d20-shaped and baked into core `Models/`: `AdvantageState` (`RulesetEnums.cs:51-58`), the **closed** `RulesetActionType` (`:22-48`, 8 values, `default:` → `NotImplemented`), `RecoveryType`/`RestType` (`:134-160`), `LevelUpChange.AbilityScoreIncreases` (`WorldChanges.cs:817-819`, a 5e-only field on a core change), `PendingLevelUpChoicesResponse.Pf2eBudget` (`LevelUpModels.cs:75,80`, a PF2e-named type on a core DTO), `ClassDefinition.CasterType.Warlock` (a 5e class name in a core enum).

Plugin-contributed action types have no mechanism at all. **This is a Phase 5+ conversation, not a blocker for data-only plugins.** Note it, do not solve it here.

**Phase 4 done when:** full suite green; a campaign on a fabricated third system id backed only by YAML round-trips without being rewritten to 5e; the migration runs against a **copy** of real campaign data with a before/after diff reviewed.

---

# Phase 5 — Core code quality

Ordered by value. Each item is independently shippable.

### 5.1 — Split `CampaignRepository`

3,155 lines, 67 public methods, 11 injected dependencies, 3 organizational comments. Mixes at minimum: session factory, commit orchestration, simulation driving, scene assembly, hybrid search, CRUD for 13 entity types, fuzzy-ID suggestion, JSON sanitisation, view/DTO assembly, schema migration, NPC enrichment, validation.

- **Introduce a `CampaignSession` unit-of-work** that owns the Raven session, so it stops being parameter #1 on **74 signatures**. Today `OpenSession()` is at `:74` but the transaction boundary lives in `Tools/CampaignToolBase.ExecuteAsync:64` — mutation logic and transaction control are split across two types with nothing enforcing the contract.
- **Delete `SanitizeEvent`/`SanitizeLocation`/`SanitizeItem`/`SanitizeEntity`/`SanitizeForToolResponse`** (`:1187-1217`) — five public methods that delegate straight to `JsonSanitizer`, existing only so callers holding a repository can reach a static.
- Split the rest by concern. Longest methods first: `UpsertItemAsync` 142 lines (`:1629`), `BuildWorldStateAsync` 138 (`:2388`), `UpsertLocationAsync` 108 (`:1311`), `UpsertCharacterAsync` 96 (`:924`).
- **`BuildWorldStateAsync` takes `IPressureOrchestrator` as a method parameter** (`:2392`) though every caller already injects it — almost certainly breaking a DI cycle. Find the cycle, document or fix it.

### 5.2 — Dispatcher: dictionary, not linear scan

**File:** `Data/ChangeHandlers/WorldChangeDispatcher.cs:230-240`

All 48 `ShouldHandle` implementations are a plain type test (only `StatusChangeHandler.cs:31` uses `is StatusChange or StatusRemove`). Yet the loop scans **all 48 without breaking on first match**, purely to detect a duplicate claim that is structurally impossible.

Worse, the scan runs multiple times per change: `ExtractInvolvedIds` (`:517`) calls `FindHandler`; `DispatchMutationAsync` calls `FindHandler` (`:472`) then `TrackInvolvedEntities` **twice** (`:481`, `:486`), the second a no-op. **Cost per commit of N changes: ≥ 2N × 48 virtual calls, plus 3 × 48 per engine-generated child delta.**

**Change:** build a `Dictionary<Type, IWorldChangeHandler>` once at construction; keep the duplicate-claim detection as a **startup** validation instead of a per-change one.

**Also:** cache the per-`Type` reflection. `IWorldChangeHandler.ExtractInvolvedEntities` (`:47`) calls `GetProperties()` on every change, and `WorldChangeHandlerHelpers.NormalizeIdFields` (`:78-113`) does `GetProperties` + `GetValue`/`SetValue` per property per change. Nothing is cached.

### 5.3 — Delete the test-only null-session path

`Data/ChangeHandlers/ChangeContext.cs:112-142` is a test-only constructor doing `Session = sessionForTests!` against a property declared non-nullable (`:15`). It forces **43 `context.Session != null` checks across 24 production files**, plus a parallel "no session" branch in `WorldChangeDispatcher.cs:192-212`.

**This is testability bought by degrading production.** Use the embedded store in those tests instead, then delete the constructor, the 43 checks, and the parallel branch.

### 5.4 — Fix the ID-prefix classifier

`Data/ChangeHandlers/IWorldChangeHandler.cs` `ProcessExtractedString`:

```csharp
bool hasPrefix = val.StartsWith("chars/") || val.StartsWith("loc") ||
                 val.StartsWith("fac") || val.StartsWith("que") || val.StartsWith("item");
```

Applied to **every string property** of every change. `CharacterUpdate.Notes = "items were scattered"` starts with `"item"` → added to `itemIds` (bogus preload) and to `InvolvedEntities`, which drives `PressureCooldowns` eviction at `CampaignRepository.cs:145`.

Match full canonical prefixes with explicit `StringComparison`. The same prefix cascade is duplicated at `CampaignRepository.cs:2725-2745` and `Data/EventDataRepair.cs:141-153` — extract one helper.

**Related:** `CampaignDocumentKeys` covers only per-campaign singletons. Entity collections have **266 raw `"chars/"`/`"locations/"`/`"items/"`/… literals** across `Data/`, `Services/`, `Tools/`, `Models/` (e.g. `CampaignRepository.cs:602` builds `"events/" + Guid.NewGuid()`). Three overlapping abstractions coexist: `CampaignDocumentKeys`, `CanonicalId.NormalizeAlias`, and ad-hoc literals. Consolidate.

### 5.5 — Dedupe `Suggest*`

~560 lines implementing the same feature twice, divergently:
- `ChangeContext.cs:190-441` — 252 lines, five near-identical methods, each doing two index queries with `WaitForNonStaleResults(5s)`. **A typo in a commit can therefore cost 10 seconds.**
- `CampaignRepository.cs:1999-2307` — ~310 lines; adds vector search and a `TimeoutException` catch that the other copy lacks.

Extract one `IEntityResolver`. Note this is also a query concern sitting on a per-change data carrier — moving it out shrinks `ChangeContext` from 441 lines to ~190.

### 5.6 — Validate rule ordering at startup

`ISimulationRule.Order` (`Data/ISimulationRule.cs:24`) and `IPressureContributor.Order` (`Data/Pressure/PressureTypes.cs:35`) are hardcoded ints, sorted at `DefaultSimulationEngine.cs:25` and `PressureOrchestrator.cs:30`.

**13 collision groups confirmed:**
- Simulation rules: `36` (`NeedConflictRule` + `ScheduleNeedSatisfactionRule`), `50` (`PlotThreadEvolutionRule` + `RelationalRearmRule`).
- Pressure contributors: 3-way at `15`, `35`, `40`; 2-way at `5`, `10`, `20`, `25`, `27`, `30`, `45`, `50`.

`OrderBy` is stable, so ties resolve to DI registration order = `Assembly.GetTypes()` order, **which the CLR does not contractually specify.** Two collided contributors writing the same `merged[key]` in `PressureOrchestrator.cs:38` are non-deterministic last-writer-wins.

Ordering intent is currently written as prose that nothing enforces: `// after QuestStalenessRule (45), before TransientEviction (100)`, `// Order = 45 as per plan`, `// after RumorDecayRule (20)`.

**Minimum:** fail fast on duplicates in `RegisterStartupValidation`. **Better:** a phase enum plus declared before/after edges. Also replace `PressureHintEnricher.cs:8`'s `Order => 1000` sentinel with a real terminal phase.

### 5.7 — Fix the unsafe retry loop

`Tools/CampaignToolBase.cs:58-120` re-runs the whole action on `ConcurrencyException`. **Actions are not idempotent** — `RulesetActionHandler` rolls dice and `LogEventAsync` stores events, so a retry re-rolls and can duplicate narrative events. Make the action idempotent or scope the retry to genuinely safe operations.

### 5.8 — Query performance

- **84 `WaitForNonStaleResults` call sites** in production code (RavenDB documents this as test-only): `CampaignRepository.cs` 27, `SimulationQueryHelper.cs` 13, `ChangeContext.cs` 10, `CampaignSyncService.cs` 8, `WorldStateScopeResolver.cs` 7. `GetSceneAsync` alone crosses ~6, several with a 5-second budget — worst-case multi-second scene reads that scale with index staleness, not data size.
- **Sequential independent awaits:** `LoadSceneAssemblyContextAsync` (`:193-268`) does ~18; `RunSimulationTickAsync` (`:573-580`) does 6, all passing `CancellationToken.None`.
- **`UnifiedSearchAsync` (`:632-695`)** does 8 blocks × 2 queries = 16 sequential round-trips with leading-wildcard `$"*{query}*"`. The comment at `:637-639` says they were deliberately de-parallelised to dodge a *"Disposing session with active async task is forbidden"* error — **a workaround for a session-lifetime bug rather than a fix.** Fix the lifetime bug (5.1 helps), then re-parallelise.
- **Explicit N+1 loops:** `ValidateClueEntityReferencesAsync:2721-2751` (one `LoadAsync` per ID, trivially a single batched `LoadAsync(ids)`), `CampaignRepository.cs:1761,2290`, `TransientEvictionRule.cs:126`, `ContainerResolver.cs:109`, `WorldStateScopeResolver.cs:126`, `ExplorationTools.cs:185,188,272,278`, `SessionTools.cs:112`. Note `ExplorationTools.cs:185` calls `GetGlobalNeedDescriptorsAsync` **once per NPC inside a loop**.
- **No `CancellationToken`** on any of the 60 `public async Task` methods on `CampaignRepository`, while `IWorldChangeHandler.ApplyAsync`, `ISimulationRule.ApplyAsync`, and `IPressureContributor.EvaluateAsync` all take one. `BuildWorldStateAsync` takes one and ignores it across ~14 awaits, forwarding it only at `:2483`.

### 5.9 — Response-shape redundancy

- **`NpcPresenceSummary` (`Models/V4Views.cs:186-244`)** is documented as "lightweight … without dumping entire documents" and carries **21 fields**, including full `SystemStats`, and both `TopNeeds` **and** `KnownNeeds` (a strict superset). Worst: `NeedDescriptors` is built at `Data/Scenes/SceneNpcPresenceFactory.cs:31-35` by copying the **full global descriptor dictionary into every present NPC** — a tavern with 8 NPCs ships the same prose 8×.
- **`SceneView.PresentNPCs` is uncapped** while `TurnResult.Npcs` is capped at 6.
- **`take_turn` can serialize the same character 3×** — `TurnResult.Npcs`, `.Party`, and `.FullNpcContext` all carry `CharacterDetailView`, with no cross-list dedup.
- **`advance_world` triple-ships pressures** (`Tools/MutationTools.cs:806-829`): `AdvanceResult.SimulatorEvents`, `AdvanceResult.WorldPressure` (full structured records — **not** `[JsonIgnore]`d, unlike the equivalents on `WorldStateView`/`SceneView`), and `ToolResult.WorldPressure`. `dedupedSimulatorEvents` is folded into `rawPressures` at `:805`, so the same text appears in all three. Same file ships `EvictedNpcIds` **and** `EvictedNpcs`.
- **`NpcContextView` (`Models/NpcViews.cs:66-99`)** claims in its XML doc to avoid shipping needs twice, then does exactly that — `Character.Needs` plus top-level `KnownNeeds` and `NeedDescriptors`, populated identically at `ExplorationTools.cs:239-240`.

Apply the `[JsonIgnore]` "internal full copy + wire summary" pattern consistently; it already exists on `WorldStateView.WorldPressureItems` and `SceneView.RecentEvents`.

### 5.10 — Fix test isolation

`tests/CampaignVault.UnitTests/RavenDbTestEnvironment.cs`

- **Two incompatible modes.** With Docker: one fresh DB **per test class** (`:83-108`). Without Docker: **every class shares `"TestDB_Shared"`** (`:85-88`, `:198`). A state-leaking test passes in one mode and fails in the other. **This is the single biggest testability defect** — pick one and make it authoritative.
- `RavenDBFixture` — the `IClassFixture` used by all 56 Raven-bound classes — is defined **inside `CampaignRepositoryTests.cs:32-86`**. Move it to its own file.
- **`MaxNumberOfRequestsPerSession` was raised 30 → 200** (`:98`, `:203`) with a comment conceding *"AdvanceWorld's growing set of independent per-rule/per-contributor queries adds up quickly."* The N+1 guard was disabled rather than the N+1 fixed. **Re-lower it toward 30 as 5.8 lands** and let it do its job.
- 56 classes join the single `[CollectionDefinition("RavenDB")]`, so ~40% of the 952-fact suite is serialized. Tests contain 80 `Thread.Sleep`/`Task.Delay`/`WaitForIndexesAfterSaveChanges` calls, 23 in `CampaignRepositoryTests.cs` alone.
- The project is named `UnitTests` but is an integration suite; the actual `CampaignVault.IntegrationTests` has two files. Consider renaming or resplitting.

### 5.11 — Delete the legacy facade

`Tools/CampaignTools.cs` — 202 lines in the production assembly, **zero `[McpServerTool]` methods**, self-described as existing only to keep tests compiling, hardcoding `TestDefaultCampaignSlug = "test-campaign"`. Move what tests need into `tests/CampaignVault.UnitTests/TestCampaignToolsFactory.cs` and delete it.

### 5.12 — DI cleanups

- **Static mutable state set post-build:** `Middleware/McpToolTelemetryFilter.cs:15` is assigned at `Program.cs:222`, *after* `app.Build()` and after migrations, while the filter registers at `:197`. **Any tool call in that window silently drops telemetry.** Inject properly.
- **Non-thread-safe lazy static with a filesystem side effect:** `Services/Pf2eCasterClasses.cs:9-14` does `_defaultProvider ??= new ClassDefinitionProvider(Path.Combine(Path.GetTempPath(), "cv_classdef_embedded"), …)`, bypassing DI entirely.
- **Registration-order hack:** `AutofacModules/ConventionRegistration.cs:49-58` registers `ExplorationTools` explicitly then excludes it by name from the assembly scan, to force an `IEnumerable<T>` resolution order between two tools. Find the real dependency and express it directly.
- **Two containers:** `IDocumentStore` and the embedding services go into MS DI (`Program.cs:144-146`); everything else into Autofac (`:148-149`). Consolidate or document why not.
- `SemanticVectorBootstrap` is manually `new`'d at `Program.cs:218` as a top-level statement; make it a hosted service.

**Phase 5 done when:** full suite green; startup validation rejects `Order` collisions; `MaxNumberOfRequestsPerSession` is lowered and the suite still passes.

---

## Verification

Build once, wait for it, never overlap or background (`CLAUDE.md`). `./build.sh`, then `dotnet test` across both test projects (952 facts).

| Phase | Beyond a green suite |
|---|---|
| **0** | `grep` confirms no `Console.WriteLine` outside `Program.cs`. |
| **1** | `get_commit_schema` output byte-comparable to the baseline captured *before* 1.3, plus the 5 new entries. |
| **2** | Budget test numbers recorded in the PR. Live `MCP_STDIO=1` `tools/list` capture confirms emitted == built and `$ref`s resolve in the real client. A real `take_turn` succeeds with a hot-tier and a cold-tier `$type`, and **fails correctly** on a missing hot-tier required field. |
| **3** | **Play a session.** Guidance appears on the first combat and not before; does not reappear on the second; `get_help` is not called speculatively. Not testable in CI alone. |
| **4** | A campaign on a fabricated third system id, backed only by YAML, round-trips without being rewritten to 5e. Migration runs against a **copy** of real data; before/after diff reviewed. |
| **5** | `Order`-collision startup validation fires on a deliberately duplicated order. `MaxNumberOfRequestsPerSession` lowered toward 30 with the suite still green. |

## Open questions

1. **Does the target MCP client resolve `$ref`?** Phase 2 makes this load-bearing. `McpSchemaDeduplicator` already bets on it, so the risk is not new — but confirm against the real client before deleting the deduplicator, and keep `CAMPAIGNVAULT_INLINE_SCHEMA=1` regardless.
2. **Is the on-disk `RulesetSystem` representation already a string?** `[JsonStringEnumConverter]` suggests yes, which would make the 4.1 migration a no-op. **Verify against a real database copy** rather than assuming.
3. **Should `get_help` survive at all** once guidance is pushed? This plan keeps 4 topics. If telemetry after Phase 3 shows it is never called, delete it entirely.
